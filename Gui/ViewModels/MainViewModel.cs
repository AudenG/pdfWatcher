using System.Collections.ObjectModel;

namespace PdfWatcher.Gui.ViewModels;

public record NavItem(string Icon, string Label, ViewModelBase ViewModel);

public class MainViewModel : ViewModelBase
{
    private NavItem _selectedNav;

    public ObservableCollection<NavItem> NavItems { get; }
    public SettingsViewModel             Settings { get; }

    public NavItem SelectedNav
    {
        get => _selectedNav;
        set { if (Set(ref _selectedNav, value)) OnPropertyChanged(nameof(CurrentView)); }
    }

    public ViewModelBase CurrentView => _selectedNav.ViewModel;

    public MainViewModel(AppState appState, AppConfig config, string configPath, Action rescan)
    {
        var dashboard = new DashboardViewModel(appState, rescan);
        Settings      = new SettingsViewModel(config, configPath);
        var logs      = new LogViewModel(appState);

        // Segoe MDL2 Assets glyphs: E80F = Home, E713 = Settings, E8A5 = BulletedList
        NavItems = new ObservableCollection<NavItem>
        {
            new("", "Dashboard", dashboard),
            new("", "Settings",  Settings),
            new("", "Logs",      logs)
        };

        _selectedNav = NavItems[0];
    }

    public void RefreshSettings(AppConfig config)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        void Update() => Settings.RefreshFromConfig(config);
        if (dispatcher == null || dispatcher.CheckAccess()) Update();
        else dispatcher.BeginInvoke(Update);
    }
}
