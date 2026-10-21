using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Convertino.Input;

/// <summary>
/// System-wide shortcuts through RegisterHotKey on a message-only window: Windows itself delivers WM_HOTKEY,
/// swallows the keys (so Win+… doesn't open Start), and says when another app already owns a combination.
/// </summary>
public sealed class Hotkeys : IDisposable
{
    const int WM_HOTKEY = 0x0312, ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    readonly HwndSource _window;
    readonly Dictionary<int, Action> _actions = [];
    int _nextId = 0x0C0D;

    public Hotkeys()
    {
        // HWND_MESSAGE parent: never shown, no taskbar entry, only messages
        _window = new HwndSource(new HwndSourceParameters("Convertino shortcuts") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _window.AddHook(WndProc);
    }

    /// <summary>Register a shortcut; null when it worked, otherwise why not (shown in the panel and a card).</summary>
    public string? Register(Hotkey key, Action action)
    {
        if (VirtualKey(key.Key) is not { } vk) return "unknown key";
        uint mods = MOD_NOREPEAT;
        if (key.Mods.HasFlag(HotMods.Ctrl)) mods |= MOD_CONTROL;
        if (key.Mods.HasFlag(HotMods.Alt)) mods |= MOD_ALT;
        if (key.Mods.HasFlag(HotMods.Shift)) mods |= MOD_SHIFT;
        if (key.Mods.HasFlag(HotMods.Super)) mods |= MOD_WIN;
        int id = _nextId++;
        if (!RegisterHotKey(_window.Handle, id, mods, vk))
        {
            int err = Marshal.GetLastWin32Error();
            Log.Info($"hotkey {key.Label} not registered: {err}");
            return err == ERROR_HOTKEY_ALREADY_REGISTERED ? "another app uses it" : $"Windows refused it ({err})";
        }
        _actions[id] = action;
        Log.Info($"hotkey {key.Label} registered");
        return null;
    }

    public void Clear()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_window.Handle, id);
        _actions.Clear();
    }

    static uint? VirtualKey(string key)
    {
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) return char.ToUpperInvariant(key[0]); // VK_A… and VK_0… are their ASCII codes
        if (key.Length > 1 && key[0] == 'F' && int.TryParse(key[1..], out int f) && f is >= 1 and <= 24) return (uint)(0x70 + f - 1);
        return null;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            try { action(); } catch (Exception ex) { Log.Error("hotkey", ex); }
        }
        return IntPtr.Zero;
    }

    // ---- dev (Convertino.exe --clip-selftest): Windows' own WM_HOTKEY for a registered shortcut, sent straight to the window ----

    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    internal void DevPress(int nth) => SendMessage(_window.Handle, WM_HOTKEY, new IntPtr(_actions.Keys.ElementAt(nth)), IntPtr.Zero);

    public void Dispose()
    {
        Clear();
        _window.Dispose();
    }
}
