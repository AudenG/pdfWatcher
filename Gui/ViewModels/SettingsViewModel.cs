using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace PdfWatcher.Gui.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly string _configPath;
    private AppConfig _saved;

    private string _clientId          = "";
    private string _clientSecret      = "";
    private string _backupFolder      = "";
    private int    _maxConcurrentJobs = 2;
    private int    _maxSearchDepth    = 5;
    private int    _backupRetentionDays = 30;
    private bool   _hasUnsavedChanges;
    private string? _selectedFolder;

    public string ClientId   { get => _clientId;   set { Set(ref _clientId, value);   UpdateUnsaved(); } }
    public string ClientSecret { get => _clientSecret; set { Set(ref _clientSecret, value); UpdateUnsaved(); } }
    public string BackupFolder { get => _backupFolder; set { Set(ref _backupFolder, value); UpdateUnsaved(); } }
    public int MaxConcurrentJobs  { get => _maxConcurrentJobs;   set { Set(ref _maxConcurrentJobs, value);   UpdateUnsaved(); } }
    public int MaxSearchDepth     { get => _maxSearchDepth;      set { Set(ref _maxSearchDepth, value);      UpdateUnsaved(); } }
    public int BackupRetentionDays { get => _backupRetentionDays; set { Set(ref _backupRetentionDays, value); UpdateUnsaved(); } }
    public bool HasUnsavedChanges  { get => _hasUnsavedChanges;  private set => Set(ref _hasUnsavedChanges, value); }
    public string? SelectedFolder  { get => _selectedFolder;     set => Set(ref _selectedFolder, value); }

    public ObservableCollection<string> WatchFolders { get; } = new();

    public RelayCommand AddFolderCommand      { get; }
    public RelayCommand RemoveFolderCommand   { get; }
    public RelayCommand BrowseBackupCommand   { get; }
    public RelayCommand SaveCommand           { get; }
    public RelayCommand IncJobsCommand        { get; }
    public RelayCommand DecJobsCommand        { get; }
    public RelayCommand IncDepthCommand       { get; }
    public RelayCommand DecDepthCommand       { get; }
    public RelayCommand IncRetentionCommand   { get; }
    public RelayCommand DecRetentionCommand   { get; }

    public SettingsViewModel(AppConfig config, string configPath)
    {
        _configPath = configPath;
        _saved = config;
        LoadFromConfig(config);
        WatchFolders.CollectionChanged += (_, _) => UpdateUnsaved();

        AddFolderCommand    = new RelayCommand(AddFolder);
        RemoveFolderCommand = new RelayCommand(RemoveFolder, () => SelectedFolder != null);
        BrowseBackupCommand = new RelayCommand(BrowseBackup);
        SaveCommand         = new RelayCommand(Save, () => HasUnsavedChanges);
        IncJobsCommand      = new RelayCommand(() => MaxConcurrentJobs = Math.Min(8, MaxConcurrentJobs + 1));
        DecJobsCommand      = new RelayCommand(() => MaxConcurrentJobs = Math.Max(1, MaxConcurrentJobs - 1));
        IncDepthCommand     = new RelayCommand(() => MaxSearchDepth = Math.Min(10, MaxSearchDepth + 1));
        DecDepthCommand     = new RelayCommand(() => MaxSearchDepth = Math.Max(1, MaxSearchDepth - 1));
        IncRetentionCommand = new RelayCommand(() => BackupRetentionDays = Math.Min(365, BackupRetentionDays + 1));
        DecRetentionCommand = new RelayCommand(() => BackupRetentionDays = Math.Max(0, BackupRetentionDays - 1));
    }

    public void RefreshFromConfig(AppConfig config)
    {
        _saved = config;
        LoadFromConfig(config);
        HasUnsavedChanges = false;
    }

    private void LoadFromConfig(AppConfig config)
    {
        _clientId           = config.ClientId;
        _clientSecret       = config.ClientSecret;
        _backupFolder       = config.BackupFolder;
        _maxConcurrentJobs  = config.MaxConcurrentJobs;
        _maxSearchDepth     = config.MaxSearchDepth;
        _backupRetentionDays = config.BackupRetentionDays;
        OnPropertyChanged(nameof(ClientId));
        OnPropertyChanged(nameof(ClientSecret));
        OnPropertyChanged(nameof(BackupFolder));
        OnPropertyChanged(nameof(MaxConcurrentJobs));
        OnPropertyChanged(nameof(MaxSearchDepth));
        OnPropertyChanged(nameof(BackupRetentionDays));
        WatchFolders.Clear();
        foreach (var f in config.WatchFolders) WatchFolders.Add(f);
    }

    private void AddFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Select a folder to watch" };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        var selected = dlg.SelectedPath;

        if (WatchFolders.Contains(selected, StringComparer.OrdinalIgnoreCase)) return;

        // Backup folder must not sit inside this watch folder (would OCR its own backups forever)
        if (!string.IsNullOrWhiteSpace(BackupFolder) && IsInsideOrEqual(BackupFolder, selected))
        {
            Warn("Cannot add this watch folder.",
                $"The backup folder is inside it:\n  {BackupFolder}\n\n" +
                "PdfWatcher would repeatedly OCR its own backup files. " +
                "Choose a watch folder that does not contain the backup folder.");
            return;
        }

        // Watch folder must not sit inside the backup folder either
        if (!string.IsNullOrWhiteSpace(BackupFolder) && IsInsideOrEqual(selected, BackupFolder))
        {
            Warn("Cannot add this watch folder.",
                $"It is inside the backup folder:\n  {BackupFolder}\n\n" +
                "Watch folders must not be nested inside the backup folder.");
            return;
        }

        WatchFolders.Add(selected);
    }

    private void RemoveFolder()
    {
        if (SelectedFolder != null) WatchFolders.Remove(SelectedFolder);
    }

    private void BrowseBackup()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description  = "Select backup folder",
            SelectedPath = BackupFolder
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        var selected = dlg.SelectedPath;

        // Backup folder must not be inside (or equal to) any watch folder
        foreach (var watchFolder in WatchFolders)
        {
            if (IsInsideOrEqual(selected, watchFolder))
            {
                Warn("Cannot use this as the backup folder.",
                    $"It is inside a watched folder:\n  {watchFolder}\n\n" +
                    "PdfWatcher would repeatedly OCR its own backup files. " +
                    "Choose a backup folder outside all watched folders.");
                return;
            }
        }

        BackupFolder = selected;
    }

    private void Save()
    {
        // Final guard — catches manual edits to the BackupFolder text box
        foreach (var watchFolder in WatchFolders)
        {
            if (IsInsideOrEqual(BackupFolder, watchFolder))
            {
                Warn("Cannot save.",
                    $"The backup folder is inside a watched folder:\n  {watchFolder}\n\n" +
                    "Fix the backup folder path before saving.");
                return;
            }
        }

        var cfg = new AppConfig
        {
            ClientId           = ClientId,
            ClientSecret       = ClientSecret,
            WatchFolders       = WatchFolders.ToArray(),
            BackupFolder       = BackupFolder,
            MaxConcurrentJobs  = MaxConcurrentJobs,
            MaxSearchDepth     = MaxSearchDepth,
            BackupRetentionDays = BackupRetentionDays
        };
        File.WriteAllText(_configPath,
            JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        _saved = cfg;
        HasUnsavedChanges = false;
        SaveCommand.RaiseCanExecuteChanged();
    }

    // Returns true if 'path' is equal to or nested inside 'potentialParent'
    private static bool IsInsideOrEqual(string path, string potentialParent)
    {
        try
        {
            var child  = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var parent = Path.GetFullPath(potentialParent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void Warn(string title, string message) =>
        System.Windows.MessageBox.Show(message, title,
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);

    private void UpdateUnsaved()
    {
        HasUnsavedChanges =
            ClientId            != _saved.ClientId  ||
            ClientSecret        != _saved.ClientSecret ||
            BackupFolder        != _saved.BackupFolder ||
            MaxConcurrentJobs   != _saved.MaxConcurrentJobs ||
            MaxSearchDepth      != _saved.MaxSearchDepth ||
            BackupRetentionDays != _saved.BackupRetentionDays ||
            !WatchFolders.SequenceEqual(_saved.WatchFolders, StringComparer.OrdinalIgnoreCase);
        SaveCommand.RaiseCanExecuteChanged();
    }
}
