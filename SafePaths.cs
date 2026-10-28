namespace DeepUninstaller;

/// <summary>
/// Lista de rutas que NUNCA se borran (ni ellas ni sus carpetas padre).
/// Es la red de seguridad para que una coincidencia de nombre no se lleve por delante el sistema.
/// </summary>
public static class SafePaths
{
    public static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    public static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static readonly string LocalLow = Path.Combine(UserProfile, @"AppData\LocalLow");
    public static readonly string SavedGames = Path.Combine(UserProfile, "Saved Games");
    public static readonly string UserTemp = Path.GetTempPath().TrimEnd('\\');
    public static readonly string WinTemp = Path.Combine(WinDir, "Temp");
    public static readonly string Prefetch = Path.Combine(WinDir, "Prefetch");

    private static readonly HashSet<string> Protected = Build();

    private static HashSet<string> Build()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Environment.SpecialFolder f in Enum.GetValues(typeof(Environment.SpecialFolder)))
        {
            try
            {
                var p = Environment.GetFolderPath(f);
                if (!string.IsNullOrEmpty(p)) set.Add(Norm(p));
            }
            catch { }
        }
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string sysDrive = Path.GetPathRoot(WinDir) ?? @"C:\";

        foreach (var p in new[]
        {
            LocalLow, SavedGames, UserTemp, WinTemp, Prefetch,
            Path.Combine(UserProfile, "AppData"), Path.Combine(UserProfile, "Downloads"),
            Path.Combine(docs, "My Games"),
            Path.Combine(sysDrive, "Users"), Path.Combine(sysDrive, @"Users\Public"),
            Path.Combine(pf, "WindowsApps"), Path.Combine(pf, "Common Files"),
            Path.Combine(pd, "Microsoft"), Path.Combine(pd, "Package Cache"),
            Path.Combine(local, "Microsoft"), Path.Combine(local, "Packages"),
            Path.Combine(local, "Programs"), Path.Combine(local, "CrashDumps"),
            Path.Combine(local, "Temp"),
        })
            set.Add(Norm(p));

        return set;
    }

    public static string Norm(string p)
    {
        try { p = Path.GetFullPath(p); } catch { }
        return p.TrimEnd('\\', '/');
    }

    /// <summary>true si la ruta NO se debe borrar.</summary>
    public static bool IsProtected(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        string full = Norm(path);

        // Raíz de unidad (C:\, D:\...)
        var root = Path.GetPathRoot(full);
        if (root == null || full.Length <= root.TrimEnd('\\').Length) return true;

        if (Protected.Contains(full)) return true;

        // Es carpeta padre de algo protegido
        string withSlash = full + "\\";
        foreach (var p in Protected)
            if (p.StartsWith(withSlash, StringComparison.OrdinalIgnoreCase)) return true;

        // Dentro de Windows solo se permite Temp y Prefetch
        if (full.StartsWith(WinDir + "\\", StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(WinTemp + "\\", StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(Prefetch + "\\", StringComparison.OrdinalIgnoreCase))
            return true;

        // Raíces de bibliotecas de juegos
        string name = Path.GetFileName(full);
        string parent = Path.GetFileName(Path.GetDirectoryName(full) ?? "");
        if (name.Equals("steamapps", StringComparison.OrdinalIgnoreCase)) return true;
        if (parent.Equals("steamapps", StringComparison.OrdinalIgnoreCase) &&
            (name.Equals("common", StringComparison.OrdinalIgnoreCase) || name.Equals("shadercache", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("compatdata", StringComparison.OrdinalIgnoreCase) || name.Equals("workshop", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("downloading", StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    public static bool IsUnder(string path, string dir)
    {
        if (string.IsNullOrEmpty(dir)) return false;
        return Norm(path).StartsWith(Norm(dir) + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
