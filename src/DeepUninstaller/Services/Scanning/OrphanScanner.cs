using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeepUninstaller.Services.Scanning;

/// <summary>
/// Busca carpetas "huérfanas": carpetas de primer nivel en Program Files, ProgramData y AppData
/// que no pertenecen a ningún programa instalado (restos de programas desinstalados hace tiempo).
/// Es conservador: ante cualquier señal de uso (programa, servicio, proceso, cambios recientes) la carpeta se omite.
/// </summary>
public static class OrphanScanner
{
    /// <summary>Una carpeta modificada en este plazo se considera en uso.</summary>
    private static readonly TimeSpan RecentUse = TimeSpan.FromDays(30);

    // Carpetas del sistema o compartidas que nunca se proponen
    private static readonly HashSet<string> SystemFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Common Files", "Internet Explorer", "Uninstall Information", "ModifiableWindowsApps", "WindowsApps", "dotnet",
        "Reference Assemblies", "MSBuild", "NuGet", "IIS", "IIS Express", "Package Cache", "Packages", "Temp", "Programs",
        "Application Data", "Desktop", "Documents", "Start Menu", "Templates", "Favorites", "ssh", "regid.1991-06.com.microsoft",
        "USOPrivate", "USOShared", "SoftwareDistribution", "Comms", "ConnectedDevicesPlatform", "CrashDumps", "D3DSCache",
        "History", "IsolatedStorage", "PeerDistRepub", "Publishers", "VirtualStore", "PlaceholderTileLogoFolder",
        "PackageManagement", "NVIDIA", "AMD", "Intel", "Apple", "Google", "Adobe Temp", "assembly", "Identities",
        // Componentes de Windows con nombre propio
        "RUXIM", "MsEdgeCrashpad", "DigitalEntitlements", "IdentityNexusIntegration", "ToastNotificationManagerCompat",
        // Herramientas de desarrollo de línea de comandos (no aparecen como programas instalados)
        "npm", "npm-cache", "pnpm", "pnpm-cache", "pnpm-state", "Yarn", "bower", "Pub", "pip", "pypa", "go", "kotlin",
        "fvm", "DartCLICompletion", "gradle", "cargo", "rustup", "fontconfig",
    };

    private static readonly Regex PathInText = new(@"[A-Za-z]:\\[^""<>|*?\r\n]+?\.(exe|dll|ico|sys|msi|bat|cmd)\b", RegexOptions.IgnoreCase);

    public static List<LeftoverItem> Scan(IEnumerable<InstalledApp> apps, IProgress<string>? progress = null)
    {
        progress?.Report("Leyendo programas, servicios y procesos...");
        var owners = CollectOwners(apps);
        var ownedPaths = owners.Paths.Select(SafePaths.Norm).Where(p => p.Length > 3).ToList();

        var result = new List<LeftoverItem>();
        foreach (var root in Roots())
        {
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root).ToList(); }
            catch { continue; }

            foreach (var dir in dirs)
            {
                try
                {
                    var info = new DirectoryInfo(dir);
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Attributes.HasFlag(FileAttributes.System)) continue;

                    string name = info.Name;
                    if (IsSystemFolder(name) || SafePaths.IsProtected(dir)) continue;
                    if (ownedPaths.Any(p => p.Equals(SafePaths.Norm(dir), StringComparison.OrdinalIgnoreCase) || SafePaths.IsUnder(p, dir))) continue;
                    if (MatchesName(name, owners.Names)) continue;

                    progress?.Report(dir);
                    var (size, lastWrite, files) = Measure(dir);
                    if (files > 0 && DateTime.UtcNow - lastWrite < RecentUse) continue;

                    // "Vacía" solo si no tiene ninguna entrada: el recuento de archivos ignora lo inaccesible
                    bool empty = !Directory.EnumerateFileSystemEntries(dir).Any();
                    result.Add(new LeftoverItem
                    {
                        Kind = LeftoverKind.Carpeta,
                        Path = dir,
                        SizeBytes = size,
                        HighConfidence = empty,
                        Reason = empty
                            ? "Carpeta vacía"
                            : $"Ningún programa instalado la usa · sin cambios desde {lastWrite.ToLocalTime():dd/MM/yyyy}",
                    });
                }
                catch { /* carpeta inaccesible: la ignoramos */ }
            }
        }
        return result.OrderByDescending(i => i.SizeBytes).ToList();
    }

    private static IEnumerable<string> Roots()
    {
        var roots = new[]
        {
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
        };
        return roots.Select(Environment.GetFolderPath)
            .Where(p => p.Length > 0 && Directory.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsSystemFolder(string name) =>
        SystemFolders.Contains(name) ||
        name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("{", StringComparison.Ordinal) ||
        name.StartsWith(".", StringComparison.Ordinal); // configuración de herramientas (.dart, .IdentityService...)

    // ------------------------------------------------------------------ QUIÉN USA CADA CARPETA

    private sealed class Owners
    {
        public readonly List<string> Paths = new();
        /// <summary>Nombres y fabricantes normalizados de todo lo instalado.</summary>
        public readonly List<string> Names = new();
    }

    private static Owners CollectOwners(IEnumerable<InstalledApp> apps)
    {
        var o = new Owners();

        foreach (var a in apps)
        {
            AddName(o, a.Name);
            AddName(o, a.Publisher);
            AddPath(o, a.InstallLocation);
            AddPathsFromText(o, a.DisplayIcon);
            AddPathsFromText(o, a.UninstallString);
        }

        // Todas las entradas de desinstalación, incluidos componentes del sistema que la lista de programas oculta
        foreach (var (hive, path) in new[]
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        })
        {
            try
            {
                using var root = RegUtil.Root(hive).OpenSubKey(path);
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    try
                    {
                        using var k = root.OpenSubKey(sub);
                        if (k == null) continue;
                        // Las entradas sin nombre son restos de desinstalaciones (p.ej. "Adobe Inc." sin programa):
                        // sus rutas cuentan, pero su fabricante no hace "dueña" a una carpeta.
                        string displayName = k.GetValue("DisplayName") as string ?? "";
                        if (displayName.Trim().Length > 0)
                        {
                            AddName(o, displayName);
                            AddName(o, k.GetValue("Publisher") as string);
                        }
                        AddPath(o, k.GetValue("InstallLocation") as string);
                        AddPathsFromText(o, k.GetValue("DisplayIcon") as string);
                        AddPathsFromText(o, k.GetValue("UninstallString") as string);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Servicios y controladores: su ejecutable no se puede tocar
        try
        {
            using var services = RegUtil.Root(RegistryHive.LocalMachine).OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            foreach (var sub in services?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                try
                {
                    using var k = services!.OpenSubKey(sub);
                    AddPathsFromText(o, Environment.ExpandEnvironmentVariables(k?.GetValue("ImagePath") as string ?? ""));
                }
                catch { }
            }
        }
        catch { }

        // Procesos en ejecución
        foreach (var p in Process.GetProcesses())
        {
            try { AddPath(o, Path.GetDirectoryName(p.MainModule?.FileName ?? "")); }
            catch { }
            finally { p.Dispose(); }
        }

        return o;
    }

    private static void AddName(Owners o, string? text)
    {
        string n = Norm(text ?? "");
        if (n.Length >= 3) o.Names.Add(n);
    }

    private static void AddPath(Owners o, string? path)
    {
        path = (path ?? "").Trim().Trim('"');
        if (path.Length > 3) o.Paths.Add(path);
    }

    private static void AddPathsFromText(Owners o, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        foreach (Match m in PathInText.Matches(text))
            AddPath(o, Path.GetDirectoryName(m.Value));
    }

    /// <summary>La carpeta se llama como algún programa o fabricante instalado ("Adobe" ↔ "Adobe Inc.").</summary>
    private static bool MatchesName(string folder, List<string> names)
    {
        string key = Norm(folder);
        if (key.Length < 3) return true; // demasiado corto para decidir: mejor no proponerla
        return names.Any(n => n.Contains(key) || (n.Length >= 4 && key.Contains(n)));
    }

    private static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");

    /// <summary>Tamaño, fecha del último cambio y número de archivos, en una sola pasada.</summary>
    private static (long size, DateTime lastWriteUtc, int files) Measure(string dir)
    {
        long size = 0;
        int files = 0;
        DateTime last = Directory.GetLastWriteTimeUtc(dir);
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opts))
        {
            try
            {
                size += f.Length;
                files++;
                if (f.LastWriteTimeUtc > last) last = f.LastWriteTimeUtc;
            }
            catch { }
        }
        return (size, last, files);
    }
}
