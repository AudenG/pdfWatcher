using System.Collections.ObjectModel;

namespace PdfWatcher.Gui.ViewModels;

public class DashboardViewModel : ViewModelBase
{
    private readonly AppState _appState;
    private AppStatus _status;
    private int _queueDepth;
    private int _processedToday;
    private int _processedThisMonth;

    public AppStatus Status           { get => _status;           private set => Set(ref _status, value); }
    public string    StatusText       { get => StatusLabel(_status); }
    public int       QueueDepth       { get => _queueDepth;       private set => Set(ref _queueDepth, value); }
    public int       ProcessedToday   { get => _processedToday;   private set => Set(ref _processedToday, value); }
    public int       ProcessedThisMonth { get => _processedThisMonth; private set => Set(ref _processedThisMonth, value); }

    public ObservableCollection<ActivityEntry> RecentActivity => _appState.RecentActivity;

    public RelayCommand RescanCommand { get; }

    public DashboardViewModel(AppState appState, Action rescan)
    {
        _appState = appState;
        RescanCommand = new RelayCommand(rescan);
        appState.StateChanged += Refresh;
        Refresh();
    }

    private void Refresh()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        void Update()
        {
            var prev = _status;
            Status             = _appState.Status;
            QueueDepth         = _appState.QueueDepth;
            ProcessedToday     = _appState.ProcessedToday;
            ProcessedThisMonth = _appState.ProcessedThisMonth;
            if (prev != _status) OnPropertyChanged(nameof(StatusText));
        }
        if (dispatcher == null || dispatcher.CheckAccess()) Update();
        else dispatcher.BeginInvoke(Update);
    }

    private static string StatusLabel(AppStatus s) => s switch
    {
        AppStatus.Processing     => "Processing",
        AppStatus.QuotaExhausted => "Quota Exhausted",
        _                        => "Running"
    };
}
