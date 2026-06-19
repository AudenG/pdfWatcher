using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;

namespace PdfWatcher.Gui.ViewModels;

public class LogViewModel : ViewModelBase
{
    private bool _showInf = true;
    private bool _showWrn = true;
    private bool _showErr = true;
    private bool _showFtl = true;

    public bool ShowInf { get => _showInf; set { Set(ref _showInf, value); Filter.Refresh(); } }
    public bool ShowWrn { get => _showWrn; set { Set(ref _showWrn, value); Filter.Refresh(); } }
    public bool ShowErr { get => _showErr; set { Set(ref _showErr, value); Filter.Refresh(); } }
    public bool ShowFtl { get => _showFtl; set { Set(ref _showFtl, value); Filter.Refresh(); } }

    public ICollectionView Filter { get; }

    public RelayCommand ClearCommand         { get; }
    public RelayCommand OpenLogFolderCommand { get; }

    public LogViewModel(AppState appState)
    {
        Filter = CollectionViewSource.GetDefaultView(appState.LogEntries);
        Filter.Filter = obj => obj is LogEntry e && e.Level switch
        {
            "INF" => ShowInf,
            "WRN" => ShowWrn,
            "ERR" => ShowErr,
            "FTL" => ShowFtl,
            _     => true
        };

        ClearCommand = new RelayCommand(() => appState.LogEntries.Clear());
        OpenLogFolderCommand = new RelayCommand(() =>
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            if (Directory.Exists(dir))
                Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        });
    }
}
