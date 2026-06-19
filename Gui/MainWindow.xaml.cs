using System.Windows;
using System.Windows.Input;
using PdfWatcher.Gui.ViewModels;

namespace PdfWatcher.Gui;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Hide instead of close — app keeps running in tray
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e)    => Hide();
}
