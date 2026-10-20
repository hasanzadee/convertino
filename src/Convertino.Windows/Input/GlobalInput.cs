using System.Runtime.InteropServices;
using System.Windows.Threading;
using Convertino.Native;
using static Convertino.Native.Win32;

namespace Convertino.Input;

/// <summary>
/// Low-level keyboard + mouse hooks on a dedicated thread (so a busy UI thread never lags the
/// system cursor). Interesting moments are marshalled to the UI dispatcher as events.
/// </summary>
public sealed class GlobalInput : IDisposable
{
    readonly Dispatcher _ui;
    readonly HookProc _kbProc;
    readonly HookProc _msProc;
    Thread? _thread;
    uint _threadId;
    IntPtr _kbHook, _msHook;

    // mouse state (hook thread only)
    bool _lDown;
    bool _dragging;
    POINT _downAt;
    int _dragThreshold = 6;

    // keyboard state (hook thread only)
    bool _triggerHeld;
    bool _otherKeyDuringTrigger;
    uint _triggerDownTime;
    uint _lastTapUpTime;

    /// <summary>The trigger key (Shift, Ctrl or Alt, from the settings) went down. Args: whether a left-button drag is in progress, cursor position.</summary>
    public event Action<bool, POINT>? TriggerDown;
    /// <summary>The trigger key was tapped twice quickly with no other key in between (and no mouse drag).</summary>
    public event Action<POINT>? TriggerDoubleTap;
    public event Action<POINT>? MouseDown;
    public event Action<POINT>? MouseUp;
    public event Action? Escape;

    public GlobalInput(Dispatcher ui)
    {
        _ui = ui;
        _kbProc = KeyboardProc;
        _msProc = MouseProc;
    }

    public void Start()
    {
        _dragThreshold = Math.Max(4, Math.Max(GetSystemMetrics(SM_CXDRAG), GetSystemMetrics(SM_CYDRAG)) + 2);
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            var mod = GetModuleHandle(null);
            _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, mod, 0);
            _msHook = SetWindowsHookEx(WH_MOUSE_LL, _msProc, mod, 0);
            Log.Info($"hooks installed kb={_kbHook != IntPtr.Zero} ms={_msHook != IntPtr.Zero}");
            ready.Set();
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
            if (_msHook != IntPtr.Zero) UnhookWindowsHookEx(_msHook);
        })
        {
            IsBackground = true,
            Name = "Convertino input hooks",
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
        ready.Wait(2000);
    }

    public bool IsLeftDown => _lDown;
    public bool IsDragging => _dragging;

    void Post(Action a) => _ui.BeginInvoke(DispatcherPriority.Send, a);

    IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = wParam.ToInt32();
            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
            bool isTrigger = Settings.Current.TriggerKey.Modifier() switch
            {
                ModifierKey.Ctrl => k.vkCode is VK_LCONTROL or VK_RCONTROL or VK_CONTROL,
                ModifierKey.Alt => k.vkCode is VK_LMENU or VK_RMENU or VK_MENU,
                _ => k.vkCode is VK_LSHIFT or VK_RSHIFT or VK_SHIFT,
            };

            if (isTrigger)
            {
                if (down && !_triggerHeld)
                {
                    _triggerHeld = true;
                    _otherKeyDuringTrigger = false;
                    _triggerDownTime = k.time;
                    bool dragging = _dragging && _lDown;
                    GetCursorPos(out var pt);
                    Post(() => TriggerDown?.Invoke(dragging, pt));
                }
                else if (up && _triggerHeld)
                {
                    _triggerHeld = false;
                    bool cleanTap = !_otherKeyDuringTrigger && !_lDown && k.time - _triggerDownTime < 350;
                    if (cleanTap)
                    {
                        if (_lastTapUpTime != 0 && k.time - _lastTapUpTime < 420)
                        {
                            _lastTapUpTime = 0;
                            GetCursorPos(out var pt);
                            Post(() => TriggerDoubleTap?.Invoke(pt));
                        }
                        else _lastTapUpTime = k.time;
                    }
                    else _lastTapUpTime = 0;
                }
            }
            else if (down)
            {
                _otherKeyDuringTrigger = true;
                _lastTapUpTime = 0;
                if (k.vkCode == VK_ESCAPE) Post(() => Escape?.Invoke());
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            switch (msg)
            {
                case WM_MOUSEMOVE:
                    if (_lDown && !_dragging)
                    {
                        var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        if (Math.Abs(m.pt.X - _downAt.X) > _dragThreshold || Math.Abs(m.pt.Y - _downAt.Y) > _dragThreshold)
                            _dragging = true;
                    }
                    break;
                case WM_LBUTTONDOWN:
                {
                    var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    _lDown = true;
                    _dragging = false;
                    _downAt = m.pt;
                    _lastTapUpTime = 0;
                    var pt = m.pt;
                    Post(() => MouseDown?.Invoke(pt));
                    break;
                }
                case WM_LBUTTONUP:
                {
                    var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    _lDown = false;
                    _dragging = false;
                    var pt = m.pt;
                    Post(() => MouseUp?.Invoke(pt));
                    break;
                }
                case WM_RBUTTONDOWN:
                {
                    var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    var pt = m.pt;
                    Post(() => MouseDown?.Invoke(pt));
                    break;
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(500);
    }
}
