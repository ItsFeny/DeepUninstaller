using Microsoft.Win32;

namespace DeepUninstaller.Services.Scanning;

public partial class LeftoverScanner
{
    // Claves de registro que jamás se tocan aunque coincidan por nombre
    private static readonly HashSet<string> ProtectedRegNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "classes", "policies", "wow6432node", "clients", "registeredapplications", "microsoft", "windows", "odbc", "intel", "nvidia",
    };

    private void ScanRegistry()
    {
        // 1. Entrada de desinstalación (si el desinstalador la dejó)
        if (_app.RegistryKey.Length > 0 && RegUtil.Exists(_app.RegistryKey))
            AddOther(LeftoverKind.ClaveRegistro, _app.RegistryKey, "", true, "Entrada de desinstalación");

        // 2. Configuración en Software\<Programa> y Software\<Fabricante>\<Programa>
        foreach (var root in new[] { @"HKCU\Software", @"HKLM\SOFTWARE", @"HKLM\SOFTWARE\WOW6432Node", @"HKCU\Software\WOW6432Node" })
        {
            using var k = RegUtil.Open(root);
            if (k == null) continue;
            foreach (var name in SafeSubKeys(k))
            {
                int m = ProtectedRegNames.Contains(name) ? 0 : Match(name);
                if (m > 0)
                {
                    AddOther(LeftoverKind.ClaveRegistro, $@"{root}\{name}", "", m == 2,
                        m == 2 ? "Configuración del programa en el registro" : "Clave con nombre parecido");
                }
                else if (_publisherKeys.Contains(Norm(name)))
                {
                    using var pk = k.OpenSubKey(name);
                    if (pk == null) continue;
                    foreach (var child in SafeSubKeys(pk))
                    {
                        if (ProtectedRegNames.Contains(child)) continue;
                        int cm = Match(child);
                        if (cm > 0)
                            AddOther(LeftoverKind.ClaveRegistro, $@"{root}\{name}\{child}", "", cm == 2, "Clave del fabricante en el registro");
                    }
                }
            }
        }

        // 3. App Paths (permite lanzar el .exe desde Ejecutar)
        foreach (var root in new[] { @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", @"HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths" })
        {
            using var k = RegUtil.Open(root);
            if (k == null) continue;
            foreach (var name in SafeSubKeys(k))
            {
                bool byExe = _exeNames.Contains(Path.GetFileNameWithoutExtension(name));
                bool byDir = false;
                if (!byExe && _installDir.Length > 0)
                {
                    using var sk = k.OpenSubKey(name);
                    byDir = PointsToInstallDir(sk?.GetValue("") as string);
                }
                if (byExe || byDir) AddOther(LeftoverKind.ClaveRegistro, $@"{root}\{name}", "", true, "Registro de ruta del ejecutable (App Paths)");
            }
        }

        // 4. Arranque automático (Run / RunOnce)
        foreach (var root in new[]
        {
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", @"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce",
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
            @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
        })
        {
            using var k = RegUtil.Open(root);
            if (k == null) continue;
            foreach (var v in k.GetValueNames())
            {
                string data = k.GetValue(v)?.ToString() ?? "";
                bool hit = PointsToInstallDir(data) || _exeNames.Any(e => data.Contains($@"\{e}.exe", StringComparison.OrdinalIgnoreCase));
                if (!hit && Match(v) == 2) hit = true;
                if (hit) AddOther(LeftoverKind.ValorRegistro, root, v, true, "Inicio automático con Windows");
            }
        }

        if (_installDir.Length == 0) return;

        // 5. Rastros de Windows que guardan la ruta del .exe en el nombre del valor
        foreach (var root in new[]
        {
            @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache",
            @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store",
            @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
            @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
        })
        {
            using var k = RegUtil.Open(root);
            if (k == null) continue;
            foreach (var v in k.GetValueNames())
                if (v.StartsWith(_installDir + "\\", StringComparison.OrdinalIgnoreCase))
                    AddOther(LeftoverKind.ValorRegistro, root, v, true, "Rastro de ejecución de Windows");
        }

        // 6. Reglas del Firewall de Windows
        const string fw = @"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";
        using (var k = RegUtil.Open(fw))
        {
            if (k != null)
            {
                foreach (var v in k.GetValueNames())
                {
                    string data = k.GetValue(v)?.ToString() ?? "";
                    int i = data.IndexOf("|App=", StringComparison.OrdinalIgnoreCase);
                    if (i < 0) continue;
                    string app = data[(i + 5)..].Split('|')[0];
                    if (PointsToInstallDir(app))
                        AddOther(LeftoverKind.ValorRegistro, fw, v, true, "Regla del Firewall de Windows");
                }
            }
        }
    }

    private void ScanServices()
    {
        using var k = RegUtil.Open(@"HKLM\SYSTEM\CurrentControlSet\Services");
        if (k == null) return;
        foreach (var name in SafeSubKeys(k))
        {
            using var sk = k.OpenSubKey(name);
            string image = sk?.GetValue("ImagePath")?.ToString() ?? "";
            if (image.Length == 0) continue;
            string expanded = Environment.ExpandEnvironmentVariables(image);
            if (PointsToInstallDir(expanded))
            {
                AddOther(LeftoverKind.Servicio, name, "", true, $"Servicio/driver: {expanded}");
                continue;
            }
            // Coincidencia por nombre solo si el ejecutable NO está en Windows
            if (expanded.Contains(SafePaths.WinDir, StringComparison.OrdinalIgnoreCase) ||
                expanded.Contains(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase) ||
                expanded.StartsWith("system32", StringComparison.OrdinalIgnoreCase)) continue;
            string display = sk?.GetValue("DisplayName")?.ToString() ?? "";
            int m = Math.Max(Match(name), display.StartsWith("@") ? 0 : Match(display));
            if (m > 0) AddOther(LeftoverKind.Servicio, name, "", false, $"Servicio con nombre parecido: {expanded}");
        }
    }

    private void ScanTasks()
    {
        string tasksDir = Path.Combine(Environment.SystemDirectory, "Tasks");
        if (!Directory.Exists(tasksDir)) return;
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var f in Directory.EnumerateFiles(tasksDir, "*", opts))
            {
                string taskName = f[tasksDir.Length..]; // "\Carpeta\Tarea"
                bool isMs = taskName.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);
                string xml = "";
                try { xml = File.ReadAllText(f); } catch { }

                if (_installDir.Length > 0 && xml.Contains(_installDir, StringComparison.OrdinalIgnoreCase))
                    AddOther(LeftoverKind.Tarea, taskName, "", true, "Tarea programada que ejecuta el programa");
                else if (!isMs)
                {
                    int m = Match(Path.GetFileName(f));
                    if (m > 0) AddOther(LeftoverKind.Tarea, taskName, "", m == 2, "Tarea programada con el nombre del programa");
                }
            }
        }
        catch { }
    }

    private bool PointsToInstallDir(string? s)
    {
        if (string.IsNullOrEmpty(s) || _installDir.Length == 0) return false;
        s = Environment.ExpandEnvironmentVariables(s).Trim('"');
        return s.Contains(_installDir + "\\", StringComparison.OrdinalIgnoreCase) ||
               s.Equals(_installDir, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] SafeSubKeys(RegistryKey k)
    {
        try { return k.GetSubKeyNames(); } catch { return Array.Empty<string>(); }
    }
}
