using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Adobe.PDFServicesSDK;
using Adobe.PDFServicesSDK.auth;
using Adobe.PDFServicesSDK.io;
using Adobe.PDFServicesSDK.pdfjobs.jobs;
using Adobe.PDFServicesSDK.pdfjobs.parameters.ocr;
using Adobe.PDFServicesSDK.pdfjobs.results;
using Serilog;
using UglyToad.PdfPig;
using PdfWatcher;

// ---- AppState (created first so the log sink can reference it) ----
var appState = new AppState();

// ---- Logging setup ----

const string LogTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";
var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logDir);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: LogTemplate)
    .WriteTo.File(
        Path.Combine(logDir, "pdfwatcher-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: LogTemplate)
    .WriteTo.Sink(new PdfWatcher.Gui.AppStateSink(appState))
    .CreateLogger();

// ---- Config ----

var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");

if (!File.Exists(configPath))
{
    var template = new AppConfig
    {
        ClientId = "YOUR_CLIENT_ID",
        ClientSecret = "YOUR_CLIENT_SECRET",
        WatchFolders = [@"C:\Users\YourName\Downloads"],
        BackupFolder = Path.Combine(AppContext.BaseDirectory, "Backups"),
        MaxConcurrentJobs = 2,
        MaxSearchDepth = 5
    };
    File.WriteAllText(configPath, JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true }));
    Log.Warning("No config.json found. Template created at {Path} — edit it and restart.", configPath);
    await Log.CloseAndFlushAsync();
    return;
}

var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath))!;

if (string.IsNullOrWhiteSpace(config.ClientId) || config.ClientId == "YOUR_CLIENT_ID")
{
    Log.Fatal("ClientId is not set in config.json.");
    await Log.CloseAndFlushAsync();
    return;
}

Directory.CreateDirectory(config.BackupFolder);
CleanupOldBackups(config);

// ---- Persistent skip list ----
// Files that return CORRUPT_DOCUMENT are added here and never retried across restarts.
// Session-failed tracks transient failures within the current run only.
var skipListPath = Path.Combine(AppContext.BaseDirectory, "skiplist.json");
var permanentSkip = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
if (File.Exists(skipListPath))
{
    foreach (var p in JsonSerializer.Deserialize<string[]>(File.ReadAllText(skipListPath)) ?? Array.Empty<string>())
        permanentSkip.TryAdd(p, 0);
    if (permanentSkip.Count > 0)
        Log.Information("Loaded {Count} permanently-skipped file(s) from skip list.", permanentSkip.Count);
}
var sessionFailed = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
var skipListLock = new object();

void AddToPermanentSkip(string filePath)
{
    if (permanentSkip.TryAdd(filePath, 0))
    {
        Log.Warning("Added to permanent skip list — will not retry on future starts (delete skiplist.json to reset): {File}", Path.GetFileName(filePath));
        lock (skipListLock)
            File.WriteAllText(skipListPath, JsonSerializer.Serialize(
                permanentSkip.Keys.OrderBy(p => p).ToArray(),
                new JsonSerializerOptions { WriteIndented = true }));
    }
}

// ---- Startup validations ----

// Guard: backup folder must not be inside any watched folder (would cause infinite reprocessing)
foreach (var watchFolder in config.WatchFolders)
{
    var watch  = Path.GetFullPath(watchFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var backup = Path.GetFullPath(config.BackupFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (backup.StartsWith(watch, StringComparison.OrdinalIgnoreCase))
    {
        Log.Fatal("BackupFolder ({Backup}) is inside WatchFolder ({Watch}) — this would cause infinite reprocessing. Fix config.json and restart.", config.BackupFolder, watchFolder);
        await Log.CloseAndFlushAsync();
        return;
    }
}

// Clean up any .ocrtmp files left behind by a previous crash
foreach (var folder in config.WatchFolders.Where(Directory.Exists))
{
    foreach (var tmp in Directory.EnumerateFiles(folder, "*.ocrtmp",
        new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
    {
        try { File.Delete(tmp); Log.Information("Cleaned up leftover temp file: {File}", tmp); }
        catch { }
    }
}

Log.Information("PdfWatcher starting.");

// ---- Circuit breaker state ----
// Adobe returns a 0-byte file when the monthly quota is exhausted.
// After CircuitBreakerLimit empty responses in a row, stop processing.
int emptyResponseCount = 0;
int activeWorkers = 0;
const int CircuitBreakerLimit = 3;
NotifyIcon? trayNotify = null;
ToolStripMenuItem? restartItem = null;
Action<AppConfig>? onConfigReloaded = null;

// ---- Queue and workers ----

var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(1000)
{
    FullMode = BoundedChannelFullMode.DropOldest,
    SingleReader = false,
    SingleWriter = false
});

var inProgress = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

for (int i = 0; i < config.MaxConcurrentJobs; i++)
{
    _ = Task.Run(async () =>
    {
        await foreach (var path in channel.Reader.ReadAllAsync())
        {
            if (Volatile.Read(ref emptyResponseCount) >= CircuitBreakerLimit)
            {
                Log.Warning("Skipping {File} — circuit open, Adobe quota likely exhausted for this month.", Path.GetFileName(path));
                continue;
            }

            if (permanentSkip.ContainsKey(path) || sessionFailed.ContainsKey(path))
                continue;

            if (!inProgress.TryAdd(path, 0))
                continue;

            if (Interlocked.Increment(ref activeWorkers) == 1)
                Log.Information("Status: Active.");

            appState.IncrementQueue();
            try
            {
                await ProcessFileAsync(path, config);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Unhandled error processing {File}", Path.GetFileName(path));
                appState.RecordFailure(Path.GetFileName(path));
            }
            finally
            {
                inProgress.TryRemove(path, out _);
                appState.DecrementQueue();
                if (Interlocked.Decrement(ref activeWorkers) == 0 && channel.Reader.Count == 0)
                    Log.Information("Status: Waiting.");
            }
        }
    });
}

// ---- Initial scan ----

foreach (var folder in config.WatchFolders)
{
    if (!Directory.Exists(folder))
    {
        Log.Warning("Watch folder does not exist, skipping: {Folder}", folder);
        continue;
    }
    Log.Information("Scanning existing files in: {Folder}", folder);
    ScanExisting(folder, config.MaxSearchDepth, channel.Writer);
}

// ---- File system watchers ----

var watchers = new List<FileSystemWatcher>();

foreach (var folder in config.WatchFolders)
{
    if (!Directory.Exists(folder)) continue;

    var watcher = new FileSystemWatcher(folder, "*.pdf")
    {
        IncludeSubdirectories = true,
        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
        InternalBufferSize = 65536,
        EnableRaisingEvents = true
    };

    watcher.Created += (_, e) => channel.Writer.TryWrite(e.FullPath);
    watcher.Renamed += (_, e) =>
    {
        if (e.FullPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            channel.Writer.TryWrite(e.FullPath);
    };
    watcher.Error += (_, e) =>
    {
        Log.Error("FileSystemWatcher buffer overflow or error for {Folder}: {Message}", folder, e.GetException().Message);
        try
        {
            watcher.EnableRaisingEvents = false;
            Thread.Sleep(500);
            if (Directory.Exists(folder))
            {
                watcher.EnableRaisingEvents = true;
                Log.Information("FileSystemWatcher restarted for: {Folder}", folder);
            }
        }
        catch (Exception ex) { Log.Error("Failed to restart watcher for {Folder}: {Message}", folder, ex.Message); }
    };

    watchers.Add(watcher);
    Log.Information("Watching: {Folder}", folder);
}

// ---- Config hot-reload ----
// Watches config.json for changes while the app is running.
// BackupFolder and credentials apply immediately; anything that controls
// startup structure (WatchFolders, MaxConcurrentJobs, MaxSearchDepth) requires a restart.

var configFileWatcher = new FileSystemWatcher(AppContext.BaseDirectory, "config.json")
{
    NotifyFilter = NotifyFilters.LastWrite,
    EnableRaisingEvents = true
};

configFileWatcher.Changed += async (_, _) =>
{
    await Task.Delay(500); // wait for the editor to finish writing
    try
    {
        var newConfig = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath));
        if (newConfig == null) return;

        var old = config;
        config = newConfig;

        Log.Information("config.json reloaded.");

        if (newConfig.BackupFolder != old.BackupFolder)
            Log.Information("  BackupFolder updated to: {Folder}", newConfig.BackupFolder);
        if (newConfig.BackupRetentionDays != old.BackupRetentionDays)
        {
            Log.Information("  BackupRetentionDays updated to {Days} — running cleanup now.", newConfig.BackupRetentionDays);
            CleanupOldBackups(newConfig);
        }
        if (newConfig.ClientId != old.ClientId || newConfig.ClientSecret != old.ClientSecret)
            Log.Information("  Adobe credentials updated.");

        onConfigReloaded?.Invoke(newConfig);

        bool needsRestart =
            newConfig.MaxConcurrentJobs != old.MaxConcurrentJobs ||
            !newConfig.WatchFolders.SequenceEqual(old.WatchFolders, StringComparer.OrdinalIgnoreCase) ||
            newConfig.MaxSearchDepth != old.MaxSearchDepth;

        if (needsRestart)
        {
            Log.Warning("  One or more settings require a restart: MaxConcurrentJobs, WatchFolders, or MaxSearchDepth changed.");
            if (restartItem != null && !restartItem.Visible)
            {
                restartItem.Visible = true;
                trayNotify?.ShowBalloonTip(8000, "PdfWatcher — Restart Required",
                    "Config changes detected that require a restart. Right-click the tray icon to apply them.",
                    ToolTipIcon.Warning);
            }
        }
    }
    catch (Exception ex)
    {
        Log.Error("Failed to reload config.json: {Error}", ex.Message);
    }
};

Log.Information("PdfWatcher running. Look for the tray icon to exit.");
if (Volatile.Read(ref activeWorkers) == 0)
    Log.Information("Status: Waiting.");

// ---- System tray ----
// NotifyIcon must live on an STA thread; we share cts so Exit can cancel the main loop.
var cts = new CancellationTokenSource();
var trayReady = new TaskCompletionSource();

var trayThread = new Thread(() =>
{
    try
    {
        Application.EnableVisualStyles();

        // WPF infrastructure — no separate message loop needed; WinForms' Application.Run()
        // pumps Win32 messages for both frameworks on this STA thread.
        var wpfApp = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };

        // Load resources at Application level so every Window and UserControl can resolve
        // StaticResource lookups during InitializeComponent(), before they enter the visual tree.
        var appResources = new System.Windows.ResourceDictionary();
        appResources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
            { Source = new Uri("pack://application:,,,/Gui/Resources/Styles.xaml") });
        appResources["BoolToCheckConverter"]   = new PdfWatcher.Gui.BoolToCheckConverter();
        appResources["BoolToBrushConverter"]   = new PdfWatcher.Gui.BoolToBrushConverter();
        appResources["ZeroToVisibleConverter"] = new PdfWatcher.Gui.ZeroToVisibleConverter();
        appResources["BoolToVisibleConverter"] = new PdfWatcher.Gui.BoolToVisibleConverter();
        wpfApp.Resources.MergedDictionaries.Add(appResources);

        var viewModel = new PdfWatcher.Gui.ViewModels.MainViewModel(
            appState, config, configPath,
            () => { foreach (var f in config.WatchFolders.Where(Directory.Exists)) ScanExisting(f, config.MaxSearchDepth, channel.Writer); });

        var mainWindow = new PdfWatcher.Gui.MainWindow(viewModel);

        onConfigReloaded = newCfg => viewModel.RefreshSettings(newCfg);

        var logFolder = Path.Combine(AppContext.BaseDirectory, "logs");

        var startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = IsStartupEnabled(),
            CheckOnClick = true
        };
        startupItem.Click += (_, _) =>
        {
            SetStartup(startupItem.Checked);
            Log.Information("Start with Windows {State}.", startupItem.Checked ? "enabled" : "disabled");
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Re-scan Folders", null, (_, _) =>
        {
            Log.Information("Manual re-scan triggered from tray.");
            foreach (var folder in config.WatchFolders.Where(Directory.Exists))
                ScanExisting(folder, config.MaxSearchDepth, channel.Writer);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open Log Folder", null, (_, _) =>
        {
            if (Directory.Exists(logFolder))
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("explorer.exe", logFolder) { UseShellExecute = true });
        });
        restartItem = new ToolStripMenuItem("Apply Config Changes (Restart)")
        {
            Visible = false
        };
        restartItem.Click += (_, _) =>
        {
            Log.Information("Restarting to apply config changes.");
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
            cts.Cancel();
            Application.Exit();
        };
        menu.Items.Add(restartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { cts.Cancel(); Application.Exit(); });

        using var iconStream = typeof(Program).Assembly.GetManifestResourceStream("PdfWatcher.icon.ico");
        trayNotify = new NotifyIcon
        {
            Icon = iconStream != null ? new Icon(iconStream) : SystemIcons.Application,
            Text = "PdfWatcher — Running",
            ContextMenuStrip = menu,
            Visible = true
        };

        trayNotify.DoubleClick += (_, _) =>
        {
            if (mainWindow.IsVisible) { mainWindow.Activate(); mainWindow.Focus(); }
            else mainWindow.Show();
        };

        trayReady.SetResult();
        Application.Run();

        // WinForms loop exited — clean up WPF
        if (mainWindow.IsLoaded)
            mainWindow.Dispatcher.Invoke(() => { mainWindow.Closing -= null; mainWindow.Close(); });
        wpfApp.Dispatcher.InvokeShutdown();

        trayNotify.Visible = false;
    }
    catch (Exception ex)
    {
        Log.Fatal(ex, "Tray icon thread crashed — shutting down so the app does not become invisible.");
        trayReady.TrySetResult();
        cts.Cancel();
    }
});

trayThread.SetApartmentState(ApartmentState.STA);
trayThread.IsBackground = true;
trayThread.Start();
await trayReady.Task;

// ---- Wait until exit ----
try { await Task.Delay(Timeout.Infinite, cts.Token); }
catch (OperationCanceledException) { }

Log.Information("Shutting down.");
foreach (var w in watchers) { w.EnableRaisingEvents = false; w.Dispose(); }
await Log.CloseAndFlushAsync();

// ---- Processing ----

async Task ProcessFileAsync(string path, AppConfig cfg)
{
    if (!await WaitForFileReadyAsync(path))
    {
        Log.Warning("File not accessible after retries, skipping: {File}", Path.GetFileName(path));
        return;
    }

    if (!NeedsOcr(path))
        return;

    Log.Information("Processing: {Path}", path);

    var originalSize = new FileInfo(path).Length;
    var backupPath = BuildBackupPath(path, cfg.BackupFolder);
    File.Copy(path, backupPath);
    File.SetLastWriteTime(backupPath, DateTime.Now); // stamp with copy time so retention cleanup works correctly
    Log.Information("  Backed up -> {Backup}", backupPath);

    var tempPath = path + ".ocrtmp";
    try
    {
        bool? ocrResult = await RunOcrWithRetryAsync(path, tempPath, cfg);
        if (ocrResult == null) { AddToPermanentSkip(path); appState.RecordFailure(Path.GetFileName(path)); return; }
        if (!ocrResult.Value) { sessionFailed.TryAdd(path, 0); appState.RecordFailure(Path.GetFileName(path)); return; }

        var resultSize = new FileInfo(tempPath).Length;

        // Empty file = Adobe quota exhausted
        if (resultSize == 0)
        {
            int count = Interlocked.Increment(ref emptyResponseCount);
            Log.Error("Adobe returned an empty (0-byte) file for {File} — monthly quota may be exhausted. ({Count}/{Limit} empty responses)",
                Path.GetFileName(path), count, CircuitBreakerLimit);
            if (count >= CircuitBreakerLimit)
            {
                Log.Fatal("CIRCUIT OPEN: Received {Limit} empty responses in a row. Adobe monthly quota is likely exhausted. Processing is paused until the app is restarted.", CircuitBreakerLimit);
                appState.SetQuotaExhausted();
                trayNotify?.ShowBalloonTip(10000, "PdfWatcher — Quota Exhausted",
                    "Adobe returned empty files 3 times in a row. Monthly quota is likely exhausted. No more files will be processed until the app is restarted.",
                    ToolTipIcon.Error);
            }
            return;
        }

        // Sanity check: result should not be drastically smaller than the original
        if (resultSize < originalSize * 0.3)
        {
            Log.Warning("OCR result is suspiciously small ({Result}KB vs {Original}KB original) — skipping overwrite to protect the original file.",
                resultSize / 1024, originalSize / 1024);
            appState.RecordFailure(Path.GetFileName(path));
            return;
        }

        Interlocked.Exchange(ref emptyResponseCount, 0);
        // File.Replace is atomic on NTFS — if it fails, the original is untouched.
        // Retry on IOException: OneDrive or another app may briefly hold a lock on the file.
        await ReplaceWithRetryAsync(tempPath, path);
        Log.Information("  Done: {File}", Path.GetFileName(path));
        appState.RecordSuccess(Path.GetFileName(path));
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Failed to process {File}", Path.GetFileName(path));
        appState.RecordFailure(Path.GetFileName(path));
    }
    finally
    {
        if (File.Exists(tempPath)) File.Delete(tempPath);
    }
}

async Task<bool?> RunOcrWithRetryAsync(string inputPath, string outputPath, AppConfig cfg, int maxAttempts = 3)
{
    for (int attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            await Task.Run(() => RunOcr(inputPath, outputPath, cfg));
            return true;
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            Log.Warning("OCR attempt {Attempt}/{Max} failed for {File}: {Message} — retrying in {Delay}s",
                attempt, maxAttempts, Path.GetFileName(inputPath), ex.Message, attempt * 3);
            await Task.Delay(attempt * 3000);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "OCR failed for {File} after {Max} attempts", Path.GetFileName(inputPath), maxAttempts);
            if (ex.ToString().Contains("CORRUPT_DOCUMENT", StringComparison.OrdinalIgnoreCase))
                return null;
            return false;
        }
    }
    return false;
}

void RunOcr(string inputPath, string outputPath, AppConfig cfg)
{
    var credentials = new ServicePrincipalCredentials(cfg.ClientId, cfg.ClientSecret);
    var pdfServices = new PDFServices(credentials);

    using var inputStream = File.OpenRead(inputPath);
    var asset = pdfServices.Upload(inputStream, PDFServicesMediaType.PDF.GetMIMETypeValue());

    var ocrParams = OCRParams.OCRParamsBuilder()
        .WithOcrLocale(OCRSupportedLocale.EN_US)
        .WithOcrType(OCRSupportedType.SEARCHABLE_IMAGE_EXACT)
        .Build();

    var job = new OCRJob(asset).SetParams(ocrParams);
    var location = pdfServices.Submit(job);
    var response = pdfServices.GetJobResult<OCRResult>(location, typeof(OCRResult));

    if (!string.Equals(response.Status, "done", StringComparison.OrdinalIgnoreCase))
        throw new Exception($"OCR job returned status '{response.Status}'");

    var resultStream = pdfServices.GetContent(response.Result.Asset);
    if (resultStream?.Stream == null)
        throw new Exception("Adobe returned null stream.");

    using var output = File.Create(outputPath);
    resultStream.Stream.CopyTo(output);
}

// ---- Helpers ----

bool NeedsOcr(string pdfPath)
{
    try
    {
        using var doc = PdfDocument.Open(pdfPath);
        int totalWords = 0;
        foreach (var page in doc.GetPages().Take(5))
            totalWords += page.GetWords().Take(20).Count();
        return totalWords < 10;
    }
    catch (Exception ex)
    {
        // Encrypted or corrupt PDF — PdfPig can't open it.
        // Skip rather than waste an API call; log so it's visible.
        Log.Warning("Skipping {File} — could not read it to check for text ({Error}). If it is password-protected or corrupt it cannot be OCR'd.", Path.GetFileName(pdfPath), ex.Message);
        return false;
    }
}

void ScanExisting(string root, int maxDepth, ChannelWriter<string> writer)
{
    try
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = maxDepth,
            IgnoreInaccessible = true
        };
        foreach (var file in Directory.EnumerateFiles(root, "*.pdf", opts))
            writer.TryWrite(file);
    }
    catch (Exception ex)
    {
        Log.Error("Error scanning {Folder}: {Message}", root, ex.Message);
    }
}

async Task<bool> WaitForFileReadyAsync(string path, int maxAttempts = 6)
{
    for (int i = 0; i < maxAttempts; i++)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (IOException)
        {
            await Task.Delay(500 * (i + 1));
        }
    }
    return false;
}

async Task ReplaceWithRetryAsync(string source, string destination, int maxAttempts = 5)
{
    for (int i = 0; i < maxAttempts; i++)
    {
        try
        {
            File.Replace(source, destination, destinationBackupFileName: null);
            return;
        }
        catch (IOException) when (i < maxAttempts - 1)
        {
            await Task.Delay(500 * (i + 1));
        }
    }
}

string BuildBackupPath(string originalPath, string backupFolder)
{
    var stem = Path.GetFileNameWithoutExtension(originalPath);
    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
    return Path.Combine(backupFolder, $"{stem}_{timestamp}.pdf");
}

// ---- Backup cleanup ----

void CleanupOldBackups(AppConfig cfg)
{
    if (cfg.BackupRetentionDays <= 0) return; // 0 = keep forever
    if (!Directory.Exists(cfg.BackupFolder)) return;

    var cutoff = DateTime.Now.AddDays(-cfg.BackupRetentionDays);
    int deleted = 0;
    foreach (var file in Directory.EnumerateFiles(cfg.BackupFolder, "*.pdf"))
    {
        try
        {
            if (File.GetLastWriteTime(file) < cutoff)
            {
                File.Delete(file);
                deleted++;
            }
        }
        catch (Exception ex) { Log.Warning("Could not delete old backup {File}: {Error}", Path.GetFileName(file), ex.Message); }
    }
    if (deleted > 0)
        Log.Information("Deleted {Count} backup(s) older than {Days} days.", deleted, cfg.BackupRetentionDays);
}

// ---- Startup registry helpers ----

bool IsStartupEnabled()
{
    using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
    return key?.GetValue("PdfWatcher") != null;
}

void SetStartup(bool enable)
{
    using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
    if (key == null) return;
    if (enable)
        key.SetValue("PdfWatcher", $"\"{Environment.ProcessPath}\"");
    else
        key.DeleteValue("PdfWatcher", throwOnMissingValue: false);
}
