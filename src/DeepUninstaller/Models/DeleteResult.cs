namespace DeepUninstaller.Models;

public class DeleteResult
{
    public int Deleted;
    public int PendingReboot;
    public long FreedBytes;
    public string BackupDir = "";
    public List<string> Errors = new();
}
