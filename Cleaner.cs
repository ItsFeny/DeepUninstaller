using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DeepUninstaller;

public class DeleteResult
{
    public int Deleted;
    public int PendingReboot;
    public long FreedBytes;
    public string BackupDir = "";
    public List<string> Errors = new();
}

/// <summary>Ejecuta desinstaladores y borra los restos encontrados.</summary>
public static class Cleaner
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);
    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 4;

    public static readonly string BackupRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DeepUninstaller", "Respaldos");

    // ------------------------------------------------------------------ DESINSTALADOR OFICIAL

    /// <summary>Ejecuta el desinstalador del programa y espera a que termine. Devuelve un aviso o "" si todo fue normal.</summary>
    public static async Task<string> RunUninstallerAsync(InstalledApp app)
    {
        string cmd = (app.UninstallString.Length > 0 ? app.UninstallString : app.QuietUninstallString).Trim();
        if (cmd.Length == 0)
            return "Este programa no tiene desinstalador: se hará borrado forzado de sus archivos.";
        if (cmd.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
            return "Juego de Steam: se borra directamente sin pasar por Steam.";

        ProcessStartInfo psi;
        var guid = Regex.Match(cmd, @"\{[0-9A-Fa-f\-]{36}\}");
        if (cmd.Contains("msiexec", StringComparison.OrdinalIgnoreCase) && guid.Success)
        {
            psi = new ProcessStartInfo("msiexec.exe", $"/x {guid.Value}") { UseShellExecute = true };
        }
        else
        {
            var (exe, args) = SplitCommand(cmd);
            if (exe.Length > 0 && File.Exists(exe))
                psi = new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" };
            else if (exe.Length > 0)
                return "El desinstalador ya no existe en el disco: se hará borrado forzado.";
            else
                psi = new ProcessStartInfo("cmd.exe", $"/c \"{cmd}\"") { UseShellExecute = false, CreateNoWindow = true };
        }

        try
        {
            using var p = Process.Start(psi);
            if (p != null) await p.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            return "No se pudo ejecutar el desinstalador (" + ex.Message + "). Se hará borrado forzado.";
        }

        // Muchos desinstaladores (NSIS, Inno Setup) se copian a %TEMP% y siguen corriendo
        await WaitForUninstallerCopiesAsync();
        return "";
    }

    private static (string exe, string args) SplitCommand(string cmd)
    {
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            if (end > 0) return (Environment.ExpandEnvironmentVariables(cmd[1..end]), cmd[(end + 1)..].Trim());
        }
        int i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (i > 0) return (Environment.ExpandEnvironmentVariables(cmd[..(i + 4)]), cmd[(i + 4)..].Trim());
        return ("", "");
    }

    private static async Task WaitForUninstallerCopiesAsync()
    {
        var deadline = DateTime.Now.AddMinutes(30);
        await Task.Delay(1500);
        while (DateTime.Now < deadline)
        {
            bool running = Process.GetProcesses().Any(p =>
            {
                string n = p.ProcessName;
                return n.StartsWith("Au_", StringComparison.OrdinalIgnoreCase) ||
                       n.StartsWith("Un_A", StringComparison.OrdinalIgnoreCase) ||
                       n.StartsWith("_iu", StringComparison.OrdinalIgnoreCase) ||
                       n.StartsWith("unins", StringComparison.OrdinalIgnoreCase);
            });
            if (!running) return;
            await Task.Delay(1000);
        }
    }

    // ------------------------------------------------------------------ BORRADO DE RESTOS

    public static DeleteResult DeleteItems(IList<LeftoverItem> items, string appName, IProgress<string>? progress = null)
    {
        var r = new DeleteResult();
        string safeName = Regex.Replace(appName, @"[^\w\-]+", "_");
        r.BackupDir = Path.Combine(BackupRoot, $"{DateTime.Now:yyyyMMdd_HHmmss}_{safeName}");
        Directory.CreateDirectory(r.BackupDir);

        // Cerrar procesos que se estén ejecutando desde las carpetas a borrar
        var folders = items.Where(i => i.Kind == LeftoverKind.Carpeta).Select(i => i.Path).ToList();
        if (folders.Count > 0) { progress?.Report("Cerrando procesos del programa..."); KillProcessesIn(folders); }

        int n = 0;
        foreach (var item in items.OrderBy(i => Order(i.Kind)))
        {
            n++;
            progress?.Report($"[{n}/{items.Count}] {item.DisplayPath}");
            try
            {
                switch (item.Kind)
                {
                    case LeftoverKind.Servicio: DeleteService(item.Path, r); break;
                    case LeftoverKind.Tarea: DeleteTask(item.Path, r); break;
                    case LeftoverKind.ValorRegistro: DeleteRegValue(item.Path, item.ValueName, r); break;
                    case LeftoverKind.ClaveRegistro: DeleteRegKey(item.Path, r); break;
                    case LeftoverKind.Archivo: DeleteFile(item.Path, r); break;
                    case LeftoverKind.Carpeta: DeleteDir(item.Path, r); break;
                }
            }
            catch (Exception ex)
            {
                r.Errors.Add($"{item.DisplayPath}: {ex.Message}");
            }
        }

        // Borrar carpetas padre que quedaron vacías (p.ej. la del fabricante)
        foreach (var item in items.Where(i => i.Kind is LeftoverKind.Carpeta or LeftoverKind.Archivo))
            RemoveEmptyParents(item.Path);

        return r;
    }

    private static int Order(LeftoverKind k) => k switch
    {
        LeftoverKind.Servicio => 0,
        LeftoverKind.Tarea => 1,
        LeftoverKind.ValorRegistro => 2,
        LeftoverKind.ClaveRegistro => 3,
        LeftoverKind.Archivo => 4,
        _ => 5,
    };

    private static void KillProcessesIn(List<string> folders)
    {
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self) continue;
                string? exe = p.MainModule?.FileName;
                if (exe != null && folders.Any(f => SafePaths.IsUnder(exe, f)))
                {
                    p.Kill(true);
                    p.WaitForExit(5000);
                }
            }
            catch { /* procesos del sistema o sin acceso */ }
        }
    }

    private static void DeleteService(string name, DeleteResult r)
    {
        ExportReg($@"HKLM\SYSTEM\CurrentControlSet\Services\{name}", r);
        Run("sc.exe", $"stop \"{name}\"");
        Thread.Sleep(1000);
        int code = Run("sc.exe", $"delete \"{name}\"");
        if (code == 0 || code == 1072 /* ya marcado para borrar */ || code == 1060 /* ya no existe */) r.Deleted++;
        else r.Errors.Add($"Servicio {name}: sc delete devolvió {code}");
    }

    private static void DeleteTask(string taskName, DeleteResult r)
    {
        string xml = Path.Combine(Environment.SystemDirectory, "Tasks") + taskName;
        if (!File.Exists(xml)) { r.Deleted++; return; } // el desinstalador ya la quitó
        try { File.Copy(xml, Path.Combine(r.BackupDir, "Tarea_" + Path.GetFileName(taskName) + ".xml"), true); } catch { }
        int code = Run("schtasks.exe", $"/Delete /TN \"{taskName}\" /F");
        if (code == 0) r.Deleted++;
        else r.Errors.Add($"Tarea {taskName}: schtasks devolvió {code}");
    }

    private static void DeleteRegValue(string keyPath, string valueName, DeleteResult r)
    {
        if (!RegUtil.Exists(keyPath)) { r.Deleted++; return; }
        ExportReg(keyPath, r);
        using var k = RegUtil.Open(keyPath, writable: true) ?? throw new Exception("Sin permiso para modificar la clave");
        k.DeleteValue(valueName, false);
        r.Deleted++;
    }

    private static void DeleteRegKey(string keyPath, DeleteResult r)
    {
        if (!RegUtil.Exists(keyPath)) { r.Deleted++; return; }
        ExportReg(keyPath, r);
        var (hive, sub) = RegUtil.Split(keyPath);
        int i = sub.LastIndexOf('\\');
        if (i <= 0) throw new Exception("Clave raíz protegida");
        using var parent = RegUtil.Root(hive).OpenSubKey(sub[..i], writable: true) ?? throw new Exception("No se pudo abrir la clave padre");
        parent.DeleteSubKeyTree(sub[(i + 1)..], false);
        r.Deleted++;
    }

    private static readonly HashSet<string> Exported = new(StringComparer.OrdinalIgnoreCase);

    private static void ExportReg(string keyPath, DeleteResult r)
    {
        string id = r.BackupDir + "|" + keyPath;
        if (!Exported.Add(id) || !RegUtil.Exists(keyPath)) return;
        string file = Path.Combine(r.BackupDir, $"reg_{Exported.Count:000}.reg");
        Run("reg.exe", $"export \"{RegUtil.ToRegExePath(keyPath)}\" \"{file}\" /y /reg:64");
    }

    private static void DeleteFile(string path, DeleteResult r)
    {
        if (SafePaths.IsProtected(path)) throw new Exception("Ruta protegida");
        if (!File.Exists(path)) { r.Deleted++; return; }
        long size = new FileInfo(path).Length;
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            r.Deleted++;
            r.FreedBytes += size;
        }
        catch
        {
            if (MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT)) r.PendingReboot++;
            else throw;
        }
    }

    private static void DeleteDir(string path, DeleteResult r)
    {
        if (SafePaths.IsProtected(path)) throw new Exception("Ruta protegida");
        if (!Directory.Exists(path)) { r.Deleted++; return; }
        long size = Util.DirSize(path);

        try
        {
            ClearReadOnly(path);
            Directory.Delete(path, true);
            r.Deleted++;
            r.FreedBytes += size;
            return;
        }
        catch { /* hay archivos en uso: vamos archivo por archivo */ }

        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        int pending = 0;
        foreach (var f in Directory.EnumerateFiles(path, "*", opts).ToList())
        {
            try { File.Delete(f); }
            catch { if (MoveFileEx(f, null, MOVEFILE_DELAY_UNTIL_REBOOT)) pending++; }
        }
        foreach (var d in Directory.EnumerateDirectories(path, "*", opts).OrderByDescending(d => d.Length).Append(path).ToList())
        {
            try { Directory.Delete(d, false); }
            catch { if (MoveFileEx(d, null, MOVEFILE_DELAY_UNTIL_REBOOT)) pending++; }
        }
        long left = Directory.Exists(path) ? Util.DirSize(path) : 0;
        r.FreedBytes += size - left;
        if (pending > 0) r.PendingReboot++;
        else if (Directory.Exists(path)) r.Errors.Add($"{path}: no se pudo borrar por completo");
        else r.Deleted++;
    }

    private static void ClearReadOnly(string dir)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opts))
        {
            try { if (f.IsReadOnly) f.IsReadOnly = false; } catch { }
        }
    }

    private static void RemoveEmptyParents(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        for (int i = 0; i < 3 && parent != null; i++)
        {
            try
            {
                if (SafePaths.IsProtected(parent) || !Directory.Exists(parent) || Directory.EnumerateFileSystemEntries(parent).Any()) return;
                Directory.Delete(parent);
            }
            catch { return; }
            parent = Path.GetDirectoryName(parent);
        }
    }

    public static int Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false });
            if (p == null) return -1;
            p.WaitForExit(60000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch { return -1; }
    }
}
