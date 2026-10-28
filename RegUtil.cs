using Microsoft.Win32;

namespace DeepUninstaller;

/// <summary>Ayudantes para trabajar con rutas de registro en texto ("HKLM\SOFTWARE\..."), siempre en vista 64 bits.</summary>
public static class RegUtil
{
    public static RegistryKey Root(RegistryHive hive) => RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

    public static (RegistryHive hive, string sub) Split(string fullPath)
    {
        int i = fullPath.IndexOf('\\');
        string root = i < 0 ? fullPath : fullPath[..i];
        string sub = i < 0 ? "" : fullPath[(i + 1)..];
        var hive = root.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKCR" or "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
            "HKU" or "HKEY_USERS" => RegistryHive.Users,
            _ => throw new ArgumentException("Raíz de registro desconocida: " + root)
        };
        return (hive, sub);
    }

    public static string Prefix(RegistryHive hive) => hive switch
    {
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.ClassesRoot => "HKCR",
        RegistryHive.Users => "HKU",
        _ => hive.ToString()
    };

    public static RegistryKey? Open(string fullPath, bool writable = false)
    {
        try
        {
            var (hive, sub) = Split(fullPath);
            return Root(hive).OpenSubKey(sub, writable);
        }
        catch { return null; }
    }

    public static bool Exists(string fullPath)
    {
        using var k = Open(fullPath);
        return k != null;
    }

    /// <summary>Ruta "larga" que entiende reg.exe.</summary>
    public static string ToRegExePath(string fullPath)
    {
        var (hive, sub) = Split(fullPath);
        string root = hive switch
        {
            RegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
            RegistryHive.CurrentUser => "HKEY_CURRENT_USER",
            RegistryHive.ClassesRoot => "HKEY_CLASSES_ROOT",
            RegistryHive.Users => "HKEY_USERS",
            _ => throw new ArgumentException()
        };
        return root + "\\" + sub;
    }
}
