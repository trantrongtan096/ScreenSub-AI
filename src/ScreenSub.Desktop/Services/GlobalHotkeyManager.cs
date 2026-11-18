using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace ScreenSub.Desktop.Services;

public sealed partial class GlobalHotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hWnd, int id);

    private readonly nint _hwnd;
    private readonly HwndSource? _source;
    private int _currentId;
    private readonly Dictionary<int, Action> _callbacks = [];

    public GlobalHotkeyManager(nint hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(HwndHook);
    }

    public int Register(ModifierKeys modifiers, Key key, Action callback)
    {
        return TryRegister(modifiers, key, callback, out int id) ? id : -1;
    }

    public bool TryRegister(ModifierKeys modifiers, Key key, Action callback, out int hotkeyId)
    {
        hotkeyId = -1;
        if (key == Key.None) return false;

        uint fsModifiers = 0;
        if ((modifiers & ModifierKeys.Control) != 0) fsModifiers |= MOD_CONTROL;
        if ((modifiers & ModifierKeys.Shift) != 0) fsModifiers |= MOD_SHIFT;
        if ((modifiers & ModifierKeys.Alt) != 0) fsModifiers |= MOD_ALT;
        if ((modifiers & ModifierKeys.Windows) != 0) fsModifiers |= MOD_WIN;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return false;

        int newId = ++_currentId;
        if (RegisterHotKey(_hwnd, newId, fsModifiers, vk))
        {
            _callbacks[newId] = callback;
            hotkeyId = newId;
            return true;
        }

        return false;
    }

    public bool Unregister(int id)
    {
        if (id <= 0) return false;
        if (_callbacks.Remove(id))
        {
            return UnregisterHotKey(_hwnd, id);
        }
        return false;
    }

    public void UnregisterAll()
    {
        foreach (var id in _callbacks.Keys.ToList())
        {
            UnregisterHotKey(_hwnd, id);
        }
        _callbacks.Clear();
    }

    private nint HwndHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_callbacks.TryGetValue(id, out var callback))
            {
                callback?.Invoke();
                handled = true;
            }
        }
        return nint.Zero;
    }

    public void Dispose()
    {
        _source?.RemoveHook(HwndHook);
        foreach (var id in _callbacks.Keys)
        {
            UnregisterHotKey(_hwnd, id);
        }
        _callbacks.Clear();
    }
}

