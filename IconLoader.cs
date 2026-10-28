using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeepUninstaller;

/// <summary>Extrae el icono real de cada programa (de su .exe, .ico o DisplayIcon).</summary>
public static class IconLoader
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string szFileName, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[] phicon, int[] piconid, uint nIcons, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static readonly string[] SkipExe = { "unins", "uninst", "setup", "update", "crash", "helper", "install", "elevat", "notification" };

    public static ImageSource? Load(InstalledApp app)
    {
        foreach (var (file, index) in Candidates(app))
        {
            try
            {
                if (!File.Exists(file)) continue;
                var img = Extract(file, index, 48) ?? (index != 0 ? Extract(file, 0, 48) : null);
                if (img != null) return img;
            }
            catch { }
        }
        return null;
    }

    private static IEnumerable<(string file, int index)> Candidates(InstalledApp app)
    {
        if (app.DisplayIcon.Length > 0) yield return ParseIconPath(app.DisplayIcon);

        string msiIcon = MsiProductIcon(app);
        if (msiIcon.Length > 0) yield return ParseIconPath(msiIcon);

        // Carpetas donde buscar el .exe principal: la de instalación y las de encima del desinstalador
        // (p.ej. Chrome: ...\Application\131.0\Installer\setup.exe -> ...\Application\chrome.exe)
        var dirs = new List<string>();
        if (app.InstallLocation.Length > 0) dirs.Add(app.InstallLocation);
        string uninst = LeftoverScanner.ExtractExe(app.UninstallString);
        string? d = uninst.Length > 0 ? Path.GetDirectoryName(uninst) : null;
        for (int i = 0; i < 3 && d != null && !SafePaths.IsProtected(d); i++, d = Path.GetDirectoryName(d))
            dirs.Add(d);

        string norm = LeftoverScanner.Norm(app.Name);
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true };
        foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;
            List<string> exes;
            try { exes = Directory.EnumerateFiles(dir, "*.exe", opts).Take(400).ToList(); } catch { continue; }
            // El .exe cuyo nombre aparece en el nombre del programa (Discord -> app-1.0\Discord.exe)
            var best = exes.FirstOrDefault(e =>
            {
                string n = LeftoverScanner.Norm(Path.GetFileNameWithoutExtension(e));
                return n.Length >= 3 && norm.Contains(n) && !SkipExe.Any(s => n.Contains(s));
            });
            if (best != null) yield return (best, 0);
        }

        // Cualquier .exe de la carpeta de instalación que no sea un desinstalador/actualizador
        if (app.InstallLocation.Length > 0 && Directory.Exists(app.InstallLocation))
        {
            string[] top;
            try { top = Directory.GetFiles(app.InstallLocation, "*.exe"); } catch { top = Array.Empty<string>(); }
            var any = top.FirstOrDefault(e => !SkipExe.Any(s => Path.GetFileName(e).Contains(s, StringComparison.OrdinalIgnoreCase)));
            if (any != null) yield return (any, 0);
        }

        if (uninst.Length > 0) yield return (uninst, 0);
    }

    /// <summary>Los programas instalados con MSI guardan su icono en Installer\Products\{GUID empaquetado}\ProductIcon.</summary>
    private static string MsiProductIcon(InstalledApp app)
    {
        var m = System.Text.RegularExpressions.Regex.Match(app.UninstallString + " " + app.RegistryKey,
            @"\{([0-9A-Fa-f]{8})-([0-9A-Fa-f]{4})-([0-9A-Fa-f]{4})-([0-9A-Fa-f]{4})-([0-9A-Fa-f]{12})\}");
        if (!m.Success) return "";
        static string Rev(string s) => new(s.Reverse().ToArray());
        static string SwapPairs(string s) => string.Concat(Enumerable.Range(0, s.Length / 2).Select(i => $"{s[i * 2 + 1]}{s[i * 2]}"));
        string packed = Rev(m.Groups[1].Value) + Rev(m.Groups[2].Value) + Rev(m.Groups[3].Value)
                        + SwapPairs(m.Groups[4].Value) + SwapPairs(m.Groups[5].Value);
        foreach (var root in new[] { @"HKLM\SOFTWARE\Classes\Installer\Products", @"HKCU\Software\Microsoft\Installer\Products" })
        {
            using var k = RegUtil.Open($@"{root}\{packed.ToUpperInvariant()}");
            if (k?.GetValue("ProductIcon") is string icon && icon.Length > 0) return icon;
        }
        return "";
    }

    private static (string, int) ParseIconPath(string s)
    {
        s = s.Trim();
        int index = 0;
        int comma = s.LastIndexOf(',');
        if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), out int idx))
        {
            index = idx;
            s = s[..comma];
        }
        s = Environment.ExpandEnvironmentVariables(s.Trim().Trim('"'));
        return (s, index);
    }

    private static ImageSource? Extract(string file, int index, int size)
    {
        var handles = new IntPtr[1];
        var ids = new int[1];
        uint n = PrivateExtractIcons(file, index, size, size, handles, ids, 1, 0);
        if (n == 0 || n == 0xFFFFFFFF || handles[0] == IntPtr.Zero) return null;
        try
        {
            var bmp = Imaging.CreateBitmapSourceFromHIcon(handles[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        finally
        {
            DestroyIcon(handles[0]);
        }
    }
}
