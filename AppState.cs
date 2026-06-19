using System.Collections.ObjectModel;

namespace PdfWatcher;

public enum AppStatus { Running, Processing, QuotaExhausted }

public record ActivityEntry(DateTime Timestamp, string FileName, bool Success);

public record LogEntry(DateTime Timestamp, string Level, string Message);

public class AppState
{
    private int _queueDepth;
    private int _processedToday;
    private int _processedThisMonth;
    private AppStatus _status = AppStatus.Running;
    private DateTime _today = DateTime.Today;

    public event Action? StateChanged;

    public AppStatus Status
    {
        get => _status;
        private set { _status = value; NotifyChanged(); }
    }

    public int QueueDepth        => Volatile.Read(ref _queueDepth);
    public int ProcessedToday    => Volatile.Read(ref _processedToday);
    public int ProcessedThisMonth => Volatile.Read(ref _processedThisMonth);

    public ObservableCollection<ActivityEntry> RecentActivity { get; } = new();
    public ObservableCollection<LogEntry>      LogEntries     { get; } = new();

    public void IncrementQueue()
    {
        Interlocked.Increment(ref _queueDepth);
        if (_status == AppStatus.Running) Status = AppStatus.Processing;
        NotifyChanged();
    }

    public void DecrementQueue()
    {
        var depth = Interlocked.Decrement(ref _queueDepth);
        if (depth <= 0 && _status == AppStatus.Processing) Status = AppStatus.Running;
        NotifyChanged();
    }

    public void RecordSuccess(string fileName)
    {
        RolloverIfNeeded();
        Interlocked.Increment(ref _processedToday);
        Interlocked.Increment(ref _processedThisMonth);
        AddActivity(new ActivityEntry(DateTime.Now, fileName, true));
        NotifyChanged();
    }

    public void RecordFailure(string fileName)
    {
        AddActivity(new ActivityEntry(DateTime.Now, fileName, false));
        NotifyChanged();
    }

    public void SetQuotaExhausted() => Status = AppStatus.QuotaExhausted;

    public void AddLogEntry(LogEntry entry)
    {
        DispatchToUi(() =>
        {
            LogEntries.Insert(0, entry);
            if (LogEntries.Count > 500) LogEntries.RemoveAt(LogEntries.Count - 1);
        });
    }

    private void AddActivity(ActivityEntry entry)
    {
        DispatchToUi(() =>
        {
            RecentActivity.Insert(0, entry);
            if (RecentActivity.Count > 50) RecentActivity.RemoveAt(RecentActivity.Count - 1);
        });
    }

    private void RolloverIfNeeded()
    {
        var today = DateTime.Today;
        if (today != _today)
        {
            Interlocked.Exchange(ref _processedToday, 0);
            _today = today;
        }
    }

    private static void DispatchToUi(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app == null) { action(); return; }
        if (app.Dispatcher.CheckAccess()) action();
        else app.Dispatcher.BeginInvoke(action);
    }

    private void NotifyChanged() => StateChanged?.Invoke();
}
