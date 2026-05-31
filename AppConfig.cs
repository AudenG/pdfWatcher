namespace PdfWatcher;

public class AppConfig
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string[] WatchFolders { get; set; } = [];
    public string BackupFolder { get; set; } = "Backups";
    public int MaxConcurrentJobs { get; set; } = 2;
    public int MaxSearchDepth { get; set; } = 5;
    public int BackupRetentionDays { get; set; } = 30;
}
