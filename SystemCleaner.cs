using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DeepUninstaller;

public class CleanTarget : INotifyPropertyChanged
{
    private long _size = -1;
    private bool _isChecked = true;

    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Carpeta y patrón. Con patrón "*" se vacía la carpeta entera (sin borrar la carpeta).</summary>
    public List<(string Dir, string Pattern)> Locations { get; init; } = new();
    public string Glyph { get; init; } = Icons.Delete;
    public bool IsRecycleBin { get; init; }
    public bool StopWindowsUpdate { get; init; }

    public long SizeBytes { get => _size; set { _size = value; OnChanged(); OnChanged(nameof(SizeText)); } }
    public bool IsChecked { get => _isChecked; set { _isChecked = value; OnChanged(); } }
    public string SizeText => _size < 0 ? "…" : Util.FormatSize(_size);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public static class SystemCleaner
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, int dwFlags);

    public static List<CleanTarget> GetTargets()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string win = SafePaths.WinDir;

        return new List<CleanTarget>
        {
            new() { Glyph = Icons.Description, Name = "Archivos temporales del usuario", Description = SafePaths.UserTemp,
                    Locations = { (SafePaths.UserTemp, "*") } },
            new() { Glyph = Icons.Desktop, Name = "Archivos temporales de Windows", Description = SafePaths.WinTemp,
                    Locations = { (SafePaths.WinTemp, "*") } },
            new() { Glyph = Icons.Task, Name = "Prefetch", Description = "Rastros de programas ejecutados (Windows los regenera)",
                    Locations = { (SafePaths.Prefetch, "*.pf") } },
            new() { Glyph = Icons.Games, Name = "Caché de shaders DirectX", Description = "Se regenera al abrir los juegos",
                    Locations = { (Path.Combine(local, "D3DSCache"), "*") } },
            new() { Glyph = Icons.Memory, Name = "Caché de shaders NVIDIA", Description = "DXCache, GLCache y ComputeCache",
                    Locations = { (Path.Combine(local, @"NVIDIA\DXCache"), "*"), (Path.Combine(local, @"NVIDIA\GLCache"), "*"),
                                  (Path.Combine(roaming, @"NVIDIA\ComputeCache"), "*") } },
            new() { Glyph = Icons.Memory, Name = "Caché de shaders AMD", Description = "DxCache, DxcCache, GLCache y VkCache",
                    Locations = { (Path.Combine(local, @"AMD\DxCache"), "*"), (Path.Combine(local, @"AMD\DxcCache"), "*"),
                                  (Path.Combine(local, @"AMD\GLCache"), "*"), (Path.Combine(local, @"AMD\VkCache"), "*") } },
            new() { Glyph = Icons.Bug, Name = "Volcados de errores", Description = "Archivos .dmp de programas que se cerraron de golpe",
                    Locations = { (Path.Combine(local, "CrashDumps"), "*"), (Path.Combine(win, "Minidump"), "*") } },
            new() { Glyph = Icons.Bug, Name = "Informes de errores de Windows", Description = "Carpetas WER",
                    Locations = { (Path.Combine(pd, @"Microsoft\Windows\WER\ReportArchive"), "*"), (Path.Combine(pd, @"Microsoft\Windows\WER\ReportQueue"), "*"),
                                  (Path.Combine(local, @"Microsoft\Windows\WER\ReportArchive"), "*"), (Path.Combine(local, @"Microsoft\Windows\WER\ReportQueue"), "*") } },
            new() { Glyph = Icons.Image, Name = "Caché de miniaturas", Description = "thumbcache_*.db del Explorador (algunas pueden estar en uso)",
                    Locations = { (Path.Combine(local, @"Microsoft\Windows\Explorer"), "thumbcache_*.db") } },
            new() { Glyph = Icons.SystemUpdate, Name = "Descargas de Windows Update", Description = "Instaladores ya aplicados (SoftwareDistribution\\Download)",
                    Locations = { (Path.Combine(win, @"SoftwareDistribution\Download"), "*") }, StopWindowsUpdate = true },
            new() { Glyph = Icons.Delete, Name = "Papelera de reciclaje", Description = "Vacía la papelera de todas las unidades", IsRecycleBin = true },
        };
    }

    public static long Measure(CleanTarget t)
    {
        if (t.IsRecycleBin)
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
        }
        long total = 0;
        foreach (var (dir, pattern) in t.Locations)
        {
            if (!Directory.Exists(dir)) continue;
            var opts = new EnumerationOptions { RecurseSubdirectories = pattern == "*", IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            try
            {
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles(pattern, opts))
                    try { total += f.Length; } catch { }
            }
            catch { }
        }
        return total;
    }

    /// <summary>Limpia el objetivo y devuelve los bytes liberados.</summary>
    public static long Clean(CleanTarget t)
    {
        long before = Measure(t);
        if (t.IsRecycleBin)
        {
            SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4); // sin confirmación, sin progreso, sin sonido
            return before;
        }

        if (t.StopWindowsUpdate) { Cleaner.Run("net.exe", "stop wuauserv"); Cleaner.Run("net.exe", "stop bits"); }
        try
        {
            foreach (var (dir, pattern) in t.Locations)
            {
                if (!Directory.Exists(dir)) continue;
                if (pattern == "*") EmptyDir(dir);
                else
                {
                    foreach (var f in Directory.EnumerateFiles(dir, pattern).ToList())
                        try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { }
                }
            }
        }
        finally
        {
            if (t.StopWindowsUpdate) { Cleaner.Run("net.exe", "start bits"); Cleaner.Run("net.exe", "start wuauserv"); }
        }
        return Math.Max(0, before - Measure(t));
    }

    /// <summary>Borra todo el contenido de una carpeta (lo que esté en uso se salta).</summary>
    private static void EmptyDir(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir).ToList())
                try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { }
            foreach (var d in Directory.EnumerateDirectories(dir).ToList())
            {
                try
                {
                    var di = new DirectoryInfo(d);
                    if (di.Attributes.HasFlag(FileAttributes.ReparsePoint)) { di.Delete(); continue; }
                    Directory.Delete(d, true);
                }
                catch { EmptyDir(d); try { Directory.Delete(d); } catch { } }
            }
        }
        catch { }
    }
}
