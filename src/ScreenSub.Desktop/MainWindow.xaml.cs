using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ScreenSub.Desktop.Services;
using ScreenSub.Desktop.ViewModels;

namespace ScreenSub.Desktop;

public partial class MainWindow : Window
{
    private GlobalHotkeyManager? _hotkeyManager;
    private SystemTrayManager? _trayManager;
    private readonly MainViewModel _viewModel;
    private int _registeredHotkeyId = -1;
    private ModifierKeys _currentModifiers = ModifierKeys.Control | ModifierKeys.Alt;
    private Key _currentKey = Key.Q;
    private bool _isExiting = false;
    private bool _hasShownTrayBalloon = false;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;
        InitializeComponent();

        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _hotkeyManager = new GlobalHotkeyManager(hwnd);
        _trayManager = new SystemTrayManager(hwnd)
        {
            OpenDashboardAction = () =>
            {
                Dispatcher.Invoke(() =>
                {
                    Show();
                    if (WindowState == WindowState.Minimized)
                    {
                        WindowState = WindowState.Normal;
                    }
                    Activate();
                    _viewModel.Navigate("Dashboard");
                });
            },
            SelectNewRegionAction = () =>
            {
                Dispatcher.Invoke(TriggerSelectRegion);
            },
            ToggleStartPauseAction = () =>
            {
                Dispatcher.Invoke(() => _viewModel.ToggleStartPauseCommand.Execute(null));
            },
            StopAction = () =>
            {
                Dispatcher.Invoke(() => _viewModel.StopTranslationCommand.Execute(null));
            },
            OpenSettingsAction = () =>
            {
                Dispatcher.Invoke(() =>
                {
                    Show();
                    if (WindowState == WindowState.Minimized)
                    {
                        WindowState = WindowState.Normal;
                    }
                    Activate();
                    _viewModel.Navigate("Cài đặt");
                });
            },
            ExitApplicationAction = () =>
            {
                Dispatcher.Invoke(ShutdownApplication);
            }
        };

        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.State))
            {
                _trayManager?.UpdateState(_viewModel.State);
            }
        };

        // Load configured hotkey (default: Ctrl + Alt + Q)
        var savedMods = _viewModel.CurrentHotkeyModifiers;
        var savedKey = _viewModel.CurrentHotkeyKey;

        _currentModifiers = savedMods != ModifierKeys.None ? savedMods : (ModifierKeys.Control | ModifierKeys.Alt);
        _currentKey = savedKey != Key.None ? savedKey : Key.Q;

        // Register Global Hotkey
        bool registered = _hotkeyManager.TryRegister(_currentModifiers, _currentKey, TriggerSelectRegion, out _registeredHotkeyId);
        if (!registered)
        {
            _viewModel.HotkeyStatusMessage = $"Không thể đăng ký phím tắt '{_viewModel.HotkeyDisplayText}' (có thể bị chiếm dụng bởi ứng dụng khác). Bạn vẫn có thể nhấn nút trên giao diện.";
            _viewModel.IsHotkeyError = true;
        }

        // Wire dynamic hotkey change requests
        _viewModel.RequestRegisterHotkey += (mods, key) =>
        {
            if (_hotkeyManager == null) return false;

            _hotkeyManager.UnregisterAll();
            bool ok = _hotkeyManager.TryRegister(mods, key, TriggerSelectRegion, out _registeredHotkeyId);
            if (!ok)
            {
                // Re-register previous active hotkey
                _hotkeyManager.TryRegister(_currentModifiers, _currentKey, TriggerSelectRegion, out _registeredHotkeyId);
                return false;
            }

            _currentModifiers = mods;
            _currentKey = key;
            return true;
        };
    }

    private void TriggerSelectRegion()
    {
        Dispatcher.Invoke(() =>
        {
            if (_viewModel.SelectRegionCommand.CanExecute(null))
            {
                _viewModel.SelectRegionCommand.Execute(null);
            }
        });
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel.IsRecordingHotkey)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape)
            {
                _viewModel.CancelRecordHotkey();
                e.Handled = true;
                return;
            }

            var modifiers = Keyboard.Modifiers;
            _viewModel.ApplyRecordedHotkey(modifiers, key);
            e.Handled = true;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExiting && _viewModel.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
            if (!_hasShownTrayBalloon)
            {
                _hasShownTrayBalloon = true;
                _trayManager?.ShowNotification(
                    "ScreenSub AI — Đang chạy nền",
                    $"Dashboard đã thu nhỏ xuống khay hệ thống. Nhấn {_viewModel.HotkeyDisplayText} để chọn vùng và dịch tự động!");
            }
            return;
        }

        ShutdownApplication();
    }

    public void ShutdownApplication()
    {
        if (_isExiting) return;
        _isExiting = true;

        _trayManager?.Dispose();
        _hotkeyManager?.Dispose();
        _viewModel.CloseOverlay();
        Application.Current.Shutdown();
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
