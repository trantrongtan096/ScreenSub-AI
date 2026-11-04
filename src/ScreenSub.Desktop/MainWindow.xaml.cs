using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ScreenSub.Desktop.Services;
using ScreenSub.Desktop.ViewModels;

namespace ScreenSub.Desktop;

public partial class MainWindow : Window
{
    private GlobalHotkeyManager? _hotkeyManager;
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;
        InitializeComponent();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _hotkeyManager = new GlobalHotkeyManager(hwnd);

        // Register Global Hotkey Ctrl + Shift + S to trigger Region Selector anytime
        _hotkeyManager.Register(ModifierKeys.Control | ModifierKeys.Shift, Key.S, () =>
        {
            if (_viewModel.SelectRegionCommand.CanExecute(null))
            {
                _viewModel.SelectRegionCommand.Execute(null);
            }
        });
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _hotkeyManager?.Dispose();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
