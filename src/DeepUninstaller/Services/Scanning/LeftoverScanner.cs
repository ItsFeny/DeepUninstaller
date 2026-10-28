using System.Text;
using System.Text.RegularExpressions;

namespace DeepUninstaller.Services.Scanning;

/// <summary>
/// Busca todo lo que deja un programa: carpetas, caché, temporales, accesos directos,
/// registro, servicios, tareas programadas, reglas de firewall, prefetch, volcados...
/// Uso: PreScan() ANTES de ejecutar el desinstalador y Scan() DESPUÉS.
/// </summary>
public partial class LeftoverScanner
{
    // Nombres demasiado genéricos para usarlos como coincidencia
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft","windows","nvidia","intel","amd","google","apple","adobe","app","apps","application","data",
        "program","programs","programfiles","software","system","system32","users","public","default","common","commonfiles",
        "shared","bin","bin64","bin32","tools","update","updater","setup","installer","install","uninstall","launcher","client",
        "game","games","mygames","savedgames","temp","tmp","cache","caches","logs","log","local","roaming","locallow",
        "packages","programdata","documents","desktop","downloads","music","pictures","videos","onedrive","config","settings",
        "x64","x86","win64","win32","64bit","32bit","plugins","runtime","redist","service","services","helper","driver","drivers",
        "crashdumps","crashreports","packagecache","appdata","user","edition","free","pro","beta","studio","the",
    };

    // Ejecutables genéricos (no se usan para buscar prefetch, Run, etc.)
    private static readonly HashSet<string> GenericExe = new(StringComparer.OrdinalIgnoreCase)
    {
        "unins000","unins001","uninstall","uninst","uninstaller","setup","install","installer","update","updater","launcher",
        "crashreporter","crashhandler","crashhandler64","unitycrashhandler64","unitycrashhandler32","helper","vc_redist.x64",
        "vc_redist.x86","vcredist_x64","vcredist_x86","dxsetup","dotnetfx","cleanup","start","run","app","main","game","client",
        "server","service","notification_helper","elevate","7z","7za","python","pythonw","node","java","javaw","cmd","powershell",
    };

    private readonly InstalledApp _app;
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _publisherKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _exeNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LeftoverItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private string _installDir = "";
    private string _steamAppId = "";

    public string InstallDir => _installDir;

    public LeftoverScanner(InstalledApp app) => _app = app;

    // ------------------------------------------------------------------ PRE-ESCANEO

    /// <summary>Recoge datos que desaparecen al desinstalar (carpeta, ejecutables).</summary>
    public void PreScan()
    {
        AddKey(_app.Name);
        AddKey(CleanName(_app.Name));
        string pubClean = CleanPublisher(_app.Publisher);
        if (pubClean.Length > 0)
        {
            _publisherKeys.Add(Norm(pubClean));
            string firstWord = pubClean.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (Norm(firstWord).Length >= 4 && !Generic.Contains(Norm(firstWord))) _publisherKeys.Add(Norm(firstWord));

            // "Mozilla Firefox" -> "Firefox"
            string cn = CleanName(_app.Name);
            foreach (var prefix in new[] { pubClean, firstWord })
                if (prefix.Length > 0 && cn.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase))
                    AddKey(cn[(prefix.Length + 1)..]);
        }
        // Las claves del nombre deben existir antes: ResolveInstallDir las usa para validar carpetas deducidas
        _installDir = ResolveInstallDir();
        if (_installDir.Length > 0) AddKey(Path.GetFileName(_installDir));

        var m = Regex.Match(_app.RegistryKey, @"Steam App (\d+)$", RegexOptions.IgnoreCase);
        if (m.Success) _steamAppId = m.Groups[1].Value;

        // Ejecutables de la carpeta de instalación (para prefetch, volcados, App Paths, Run...)
        if (_installDir.Length > 0 && Directory.Exists(_installDir))
        {
            try
            {
                var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var exe in Directory.EnumerateFiles(_installDir, "*.exe", opts).Take(300))
                {
                    var n = Path.GetFileNameWithoutExtension(exe);
                    if (n.Length >= 3 && !GenericExe.Contains(n)) _exeNames.Add(n);
                }
            }
            catch { }
        }
    }

    private string ResolveInstallDir()
    {
        string loc = _app.InstallLocation;
        if (loc.Length > 0 && Directory.Exists(loc) && !SafePaths.IsProtected(loc)) return SafePaths.Norm(loc);

        // Juegos de launchers: su desinstalador/icono es del launcher (steam.exe...). Si la carpeta
        // no existe (disco desconectado) NO se deduce nada, o acabaríamos apuntando a la carpeta de Steam.
        if (_app.Source.StartsWith("Juego")) return "";

        // Sin InstallLocation: lo deducimos del icono o del desinstalador
        foreach (var cmd in new[] { _app.DisplayIcon, _app.UninstallString })
        {
            string exe = ExtractExe(cmd);
            if (exe.Length == 0 || !File.Exists(exe)) continue;
            string? dir = Path.GetDirectoryName(exe);
            if (dir == null) continue;
            string dn = Norm(Path.GetFileName(dir));
            if (dn is "bin" or "bin64" or "bin32" or "x64" or "x86" or "win64" or "win32" || Regex.IsMatch(dn, @"^app\d"))
                dir = Path.GetDirectoryName(dir) ?? dir;
            if (dir.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase) ||
                dir.Contains(@"\Installer", StringComparison.OrdinalIgnoreCase) ||
                SafePaths.IsUnder(dir, SafePaths.UserTemp) || SafePaths.IsProtected(dir)) continue;
            // Solo se acepta si la carpeta se llama como el programa (Discord -> ...\Local\Discord)
            if (Match(Path.GetFileName(dir)) == 0) continue;
            return SafePaths.Norm(dir);
        }
        return "";
    }

    /// <summary>Saca la ruta del .exe de una línea de comandos o de un DisplayIcon ("C:\x.exe",0).</summary>
    public static string ExtractExe(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.Length == 0) return "";
        string path;
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            path = end > 0 ? cmd[1..end] : cmd.Trim('"');
        }
        else
        {
            int i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = i > 0 ? cmd[..(i + 4)] : cmd.Split(',')[0];
        }
        path = Environment.ExpandEnvironmentVariables(path.Trim());
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "";
        string fn = Path.GetFileName(path).ToLowerInvariant();
        if (fn is "msiexec.exe" or "rundll32.exe" or "cmd.exe") return "";
        return path;
    }

    // ------------------------------------------------------------------ NOMBRES

    public static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string CleanName(string name)
    {
        string s = Regex.Replace(name, @"\(.*?\)|\[.*?\]", " ");
        s = Regex.Replace(s, @"\bv?\d+(\.\d+)+\w*\b", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\b(x64|x86|amd64|win64|win32|64[- ]?bit|32[- ]?bit|version)\b", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"[™®©]", "");
        return Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '_', '.');
    }

    private static string CleanPublisher(string pub)
    {
        string s = Regex.Replace(pub, @"[,.]", " ");
        s = Regex.Replace(s, @"\b(inc|llc|ltd|limited|corp|corporation|gmbh|sa|s a|co|company|ag|bv|plc|srl|sl|sas|oy|ab|pty|kk)\b", " ", RegexOptions.IgnoreCase);
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private void AddKey(string s)
    {
        string n = Norm(s);
        if (n.Length >= 3 && !Generic.Contains(n)) _keys.Add(n);
    }

    /// <summary>0 = no coincide, 1 = posible, 2 = exacta.</summary>
    private int Match(string name)
    {
        string n = Norm(name);
        if (n.Length < 3 || Generic.Contains(n)) return 0;
        if (_keys.Contains(n)) return 2;
        foreach (var k in _keys)
        {
            if (k.Length >= 5 && n.StartsWith(k)) return 1;
            if (n.Length >= 6 && k.StartsWith(n)) return 1;
        }
        return 0;
    }

    // ------------------------------------------------------------------ ESCANEO PRINCIPAL

    public List<LeftoverItem> Scan()
    {
        _items.Clear();

        // 1. Carpeta de instalación
        if (_installDir.Length > 0 && Directory.Exists(_installDir))
            AddPath(_installDir, true, "Carpeta de instalación");

        // 2. Restos específicos de launchers de juegos
        ScanSteam();
        if (_app.ManifestFile.Length > 0 && File.Exists(_app.ManifestFile))
            AddPath(_app.ManifestFile, true, "Manifiesto de Epic Games");

        // 3. Carpetas típicas de datos, caché y configuración
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string pubDocs = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);

        ScanBase(pf, "Archivos de programa");
        ScanBase(pf86, "Archivos de programa (x86)");
        ScanBase(pd, "ProgramData");
        ScanBase(roaming, "AppData\\Roaming");
        ScanBase(local, "AppData\\Local");
        ScanBase(SafePaths.LocalLow, "AppData\\LocalLow");
        ScanBase(Path.Combine(local, "Programs"), "AppData\\Local\\Programs");
        ScanBase(docs, "Documentos");
        ScanBase(Path.Combine(docs, "My Games"), "Documentos\\My Games");
        ScanBase(SafePaths.SavedGames, "Partidas guardadas");
        ScanBase(pubDocs, "Documentos públicos");

        // 4. Temporales (carpetas y archivos sueltos)
        ScanBase(SafePaths.UserTemp, "Temporales del usuario", includeFiles: true);
        ScanBase(SafePaths.WinTemp, "Temporales de Windows", includeFiles: true);

        // 5. Menú inicio y escritorio
        ScanShortcuts();

        // 6. Prefetch, volcados de error e informes WER
        ScanTraces(local, pd);

        // 7. Registro, servicios, tareas, firewall
        ScanRegistry();
        ScanServices();
        ScanTasks();

        // Quitar elementos que ya están dentro de una carpeta que se va a borrar
        var folders = _items.Values.Where(i => i.Kind == LeftoverKind.Carpeta).Select(i => i.Path).ToList();
        var result = _items.Values
            .Where(i => !(i.Kind is LeftoverKind.Carpeta or LeftoverKind.Archivo) || !folders.Any(f => SafePaths.IsUnder(i.Path, f)))
            .OrderByDescending(i => i.HighConfidence).ThenBy(i => i.Kind).ThenBy(i => i.Path)
            .ToList();
        return result;
    }

    private void ScanBase(string baseDir, string label, bool includeFiles = false)
    {
        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir)) return;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(baseDir))
            {
                string name = Path.GetFileName(dir);
                int m = Match(name);
                if (m > 0)
                {
                    AddPath(dir, m == 2, m == 2 ? $"Mismo nombre del programa en {label}" : $"Nombre parecido en {label}");
                }
                else if (_publisherKeys.Contains(Norm(name)))
                {
                    // Carpeta del fabricante: buscamos dentro (p.ej. AppData\Local\Google\Chrome)
                    try
                    {
                        foreach (var child in Directory.EnumerateDirectories(dir))
                        {
                            int cm = Match(Path.GetFileName(child));
                            if (cm > 0)
                                AddPath(child, cm == 2, $"Carpeta del fabricante en {label}");
                        }
                    }
                    catch { }
                }
            }
            if (includeFiles)
            {
                foreach (var file in Directory.EnumerateFiles(baseDir))
                {
                    int m = Match(Path.GetFileNameWithoutExtension(file));
                    if (m > 0) AddPath(file, m == 2, $"Archivo temporal en {label}");
                }
            }
        }
        catch { }
    }

    private void ScanSteam()
    {
        if (_steamAppId.Length == 0 || _installDir.Length == 0) return;
        // ...\steamapps\common\Juego  ->  ...\steamapps
        string? steamapps = Path.GetDirectoryName(Path.GetDirectoryName(_installDir) ?? "");
        if (steamapps == null || !Path.GetFileName(steamapps).Equals("steamapps", StringComparison.OrdinalIgnoreCase)) return;

        string id = _steamAppId;
        foreach (var (p, why) in new[]
        {
            (Path.Combine(steamapps, $"appmanifest_{id}.acf"), "Manifiesto de Steam"),
            (Path.Combine(steamapps, "shadercache", id), "Caché de shaders de Steam"),
            (Path.Combine(steamapps, "compatdata", id), "Datos de compatibilidad de Steam"),
            (Path.Combine(steamapps, "workshop", "content", id), "Contenido de Workshop"),
            (Path.Combine(steamapps, "workshop", $"appworkshop_{id}.acf"), "Manifiesto de Workshop"),
            (Path.Combine(steamapps, "downloading", id), "Descarga incompleta de Steam"),
            (Path.Combine(steamapps, "temp", id), "Temporales de Steam"),
        })
        {
            if (File.Exists(p) || Directory.Exists(p)) AddPath(p, true, why);
        }
    }

    private void ScanShortcuts()
    {
        var dirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
        };
        foreach (var d in dirs.Where(Directory.Exists))
        {
            try
            {
                // Carpetas del menú inicio con el nombre del programa
                foreach (var sub in Directory.EnumerateDirectories(d))
                {
                    int m = Match(Path.GetFileName(sub));
                    if (m > 0) AddPath(sub, m == 2, "Carpeta en el menú Inicio");
                }
                var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true };
                foreach (var lnk in Directory.EnumerateFiles(d, "*.*", opts))
                {
                    string ext = Path.GetExtension(lnk).ToLowerInvariant();
                    if (ext is not (".lnk" or ".url")) continue;
                    if (ShortcutPointsToApp(lnk)) AddPath(lnk, true, "Acceso directo que apunta al programa");
                    else
                    {
                        int m = Match(Path.GetFileNameWithoutExtension(lnk));
                        if (m > 0) AddPath(lnk, m == 2, "Acceso directo con el nombre del programa");
                    }
                }
            }
            catch { }
        }
    }

    private bool ShortcutPointsToApp(string lnk)
    {
        try
        {
            var bytes = File.ReadAllBytes(lnk);
            if (bytes.Length > 64 * 1024) return false;
            string a = Encoding.Latin1.GetString(bytes);
            string u = Encoding.Unicode.GetString(bytes);
            if (_installDir.Length > 0 &&
                (a.Contains(_installDir, StringComparison.OrdinalIgnoreCase) || u.Contains(_installDir, StringComparison.OrdinalIgnoreCase)))
                return true;
            if (_steamAppId.Length > 0 && a.Contains($"steam://rungameid/{_steamAppId}", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch { }
        return false;
    }

    private void ScanTraces(string local, string pd)
    {
        foreach (var exe in _exeNames)
        {
            TryGlob(SafePaths.Prefetch, exe.ToUpperInvariant() + ".EXE-*.pf", "Rastro de Prefetch");
            TryGlob(Path.Combine(local, "CrashDumps"), exe + ".exe*.dmp", "Volcado de error");
            foreach (var wer in new[] { @"Microsoft\Windows\WER\ReportArchive", @"Microsoft\Windows\WER\ReportQueue" })
            {
                foreach (var root in new[] { pd, local })
                {
                    string dir = Path.Combine(root, wer);
                    if (!Directory.Exists(dir)) continue;
                    try
                    {
                        foreach (var d in Directory.EnumerateDirectories(dir, $"*_{exe}.exe_*"))
                            AddPath(d, true, "Informe de errores de Windows");
                    }
                    catch { }
                }
            }
        }
    }

    private void TryGlob(string dir, string pattern, string why)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, pattern))
                AddPath(f, true, why);
        }
        catch { }
    }

    private void AddPath(string path, bool high, string reason)
    {
        if (SafePaths.IsProtected(path)) return;
        string full = SafePaths.Norm(path);
        if (_items.TryGetValue(full, out var existing))
        {
            if (high && !existing.HighConfidence) { existing.HighConfidence = true; existing.Reason = reason; }
            return;
        }
        bool isDir = Directory.Exists(full);
        long size = 0;
        try { size = isDir ? Util.DirSize(full) : new FileInfo(full).Length; } catch { }
        _items[full] = new LeftoverItem
        {
            Kind = isDir ? LeftoverKind.Carpeta : LeftoverKind.Archivo,
            Path = full,
            HighConfidence = high,
            Reason = reason,
            SizeBytes = size,
        };
    }

    private void AddOther(LeftoverKind kind, string path, string valueName, bool high, string reason)
    {
        string id = $"{kind}|{path}|{valueName}";
        if (_items.ContainsKey(id)) return;
        _items[id] = new LeftoverItem { Kind = kind, Path = path, ValueName = valueName, HighConfidence = high, Reason = reason };
    }
}
