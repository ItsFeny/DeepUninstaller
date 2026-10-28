using System.Text.Json;
using Microsoft.Win32;

namespace DeepUninstaller;

/// <summary>Obtiene la lista de programas y juegos instalados.</summary>
public static class AppScanner
{
    private static readonly (RegistryHive hive, string path)[] UninstallRoots =
    {
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
    };

    public static List<InstalledApp> GetInstalledApps()
    {
        var list = new List<InstalledApp>();

        foreach (var (hive, path) in UninstallRoots)
        {
            using var root = RegUtil.Root(hive).OpenSubKey(path);
            if (root == null) continue;

            foreach (var subName in root.GetSubKeyNames())
            {
                try
                {
                    using var k = root.OpenSubKey(subName);
                    if (k == null) continue;

                    string name = (k.GetValue("DisplayName") as string ?? "").Trim();
                    if (name.Length == 0) continue;
                    if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    if (k.GetValue("ParentKeyName") != null) continue; // actualizaciones / parches
                    string releaseType = k.GetValue("ReleaseType") as string ?? "";
                    if (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
                        releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)) continue;

                    string uninstall = k.GetValue("UninstallString") as string ?? "";
                    string quiet = k.GetValue("QuietUninstallString") as string ?? "";
                    bool isSteam = subName.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase);

                    list.Add(new InstalledApp
                    {
                        Name = name,
                        Publisher = (k.GetValue("Publisher") as string ?? "").Trim(),
                        Version = (k.GetValue("DisplayVersion") as string ?? "").Trim(),
                        InstallLocation = CleanPath(k.GetValue("InstallLocation") as string ?? ""),
                        UninstallString = uninstall,
                        QuietUninstallString = quiet,
                        DisplayIcon = k.GetValue("DisplayIcon") as string ?? "",
                        RegistryKey = $@"{RegUtil.Prefix(hive)}\{path}\{subName}",
                        SizeKB = k.GetValue("EstimatedSize") is int s ? s : 0,
                        InstallDate = ParseDate(k.GetValue("InstallDate") as string),
                        Source = isSteam ? "Juego Steam" : "Programa",
                    });
                }
                catch { /* entrada corrupta: la ignoramos */ }
            }
        }

        list.AddRange(GetEpicGames());

        // Quitar duplicados (mismo nombre, p.ej. registrado en HKLM y HKCU)
        var result = list
            .GroupBy(a => a.Name.ToLowerInvariant())
            .Select(g => g.OrderByDescending(a => a.InstallLocation.Length).First())
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var a in result)
        {
            a.Category = Categorizer.Classify(a);
            // Tamaño real de la carpeta cuando el registro no lo indica (juegos de Steam/Epic sobre todo)
            if (a.SizeKB <= 0 && a.Category == AppCategory.Juego && a.InstallLocation.Length > 0 && Directory.Exists(a.InstallLocation))
                a.SizeKB = Util.DirSize(a.InstallLocation) / 1024;
        }
        return result;
    }

    private static DateTime? ParseDate(string? s)
    {
        if (s != null && DateTime.TryParseExact(s.Trim(), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d)) return d;
        return null;
    }

    private static IEnumerable<InstalledApp> GetEpicGames()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(dir)) yield break;

        foreach (var file in Directory.EnumerateFiles(dir, "*.item"))
        {
            InstalledApp? app = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                string name = r.TryGetProperty("DisplayName", out var n) ? n.GetString() ?? "" : "";
                string loc = r.TryGetProperty("InstallLocation", out var l) ? l.GetString() ?? "" : "";
                string exe = r.TryGetProperty("LaunchExecutable", out var e) ? e.GetString() ?? "" : "";
                if (name.Length > 0)
                {
                    app = new InstalledApp
                    {
                        Name = name,
                        Publisher = "Epic Games",
                        InstallLocation = CleanPath(loc),
                        DisplayIcon = exe.Length > 0 && loc.Length > 0 ? Path.Combine(loc, exe) : "",
                        InstallDate = File.GetCreationTime(file),
                        Source = "Juego Epic",
                        // Para Epic no hay desinstalador: se borra la carpeta y el manifiesto
                        UninstallString = "",
                        ManifestFile = file,
                    };
                }
            }
            catch { }
            if (app != null) yield return app;
        }
    }

    public static string CleanPath(string p)
    {
        p = p.Trim().Trim('"').Trim();
        if (p.Length == 0) return "";
        try { p = Environment.ExpandEnvironmentVariables(p); } catch { }
        return p.TrimEnd('\\', '/');
    }
}
