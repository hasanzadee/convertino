using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Convertino.Ui;

namespace Convertino.Native;

/// <summary>
/// Global shortcuts on X11: XGrabKey on the root window (its own connection), once per lock-key state so they work
/// with NumLock / CapsLock on. The X server keeps the keys from other apps and refuses a combination another
/// client already grabbed (BadAccess), which becomes "another app uses it". Under XWayland it only sees keys while
/// an X11 window has focus: there the panel suggests binding "convertino menu --clipboard" in the desktop's settings.
/// </summary>
public sealed unsafe class X11Hotkeys : IHotkeys, IDisposable
{
    const string Lib = "libX11.so.6";
    [DllImport(Lib)] static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(Lib)] static extern int XCloseDisplay(IntPtr dpy);
    [DllImport(Lib)] static extern IntPtr XDefaultRootWindow(IntPtr dpy);
    [DllImport(Lib)] static extern IntPtr XStringToKeysym(string name);
    [DllImport(Lib)] static extern byte XKeysymToKeycode(IntPtr dpy, IntPtr keysym);
    [DllImport(Lib)] static extern int XGrabKey(IntPtr dpy, int keycode, uint modifiers, IntPtr grabWindow, int ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(Lib)] static extern int XUngrabKey(IntPtr dpy, int keycode, uint modifiers, IntPtr grabWindow);
    [DllImport(Lib)] static extern int XSync(IntPtr dpy, int discard);
    [DllImport(Lib)] static extern int XPending(IntPtr dpy);
    [DllImport(Lib)] static extern int XNextEvent(IntPtr dpy, byte* ev);
    [DllImport(Lib)] static extern IntPtr XSetErrorHandler(IntPtr handler);
    [DllImport(Lib)] static extern int XkbSetDetectableAutoRepeat(IntPtr dpy, int detectable, out int supported);

    const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8, Mod2Mask = 16, Mod4Mask = 64;
    const int KeyPress = 2, KeyRelease = 3, GrabModeAsync = 1, BadAccess = 10;
    static readonly uint[] LockVariants = [0, LockMask, Mod2Mask, LockMask | Mod2Mask];

    readonly Action<Action> _post;
    readonly object _gate = new();
    readonly List<(int Code, uint Mods, Action Action)> _keys = [];
    readonly Dictionary<int, (ulong Press, ulong Release)> _times = []; // X times of each grabbed key's last press and release (event thread only)
    IntPtr _dpy;
    Thread? _thread;
    volatile bool _run = true;

    public X11Hotkeys(Action<Action> post) => _post = post;

    public bool Start()
    {
        _dpy = XOpenDisplay(IntPtr.Zero);
        if (_dpy == IntPtr.Zero) return false;
        // a held shortcut auto-repeats: with this, as presses only (no fake releases between them), so one hold fires once
        try { XkbSetDetectableAutoRepeat(_dpy, 1, out _); } catch (EntryPointNotFoundException) { }
        _thread = new Thread(Loop) { IsBackground = true, Name = "Convertino X11 shortcuts" };
        _thread.Start();
        return true;
    }

    public string? Register(Hotkey key, Action action)
    {
        if (_dpy == IntPtr.Zero) return L.En("no X display");
        var name = key.Key.Length == 1 ? key.Key.ToLowerInvariant() : key.Key; // keysyms: "x", "5", "F12"
        int code = XKeysymToKeycode(_dpy, XStringToKeysym(name));
        if (code == 0) return L.En("no such key on this keyboard");
        uint mods = 0;
        if (key.Mods.HasFlag(HotMods.Shift)) mods |= ShiftMask;
        if (key.Mods.HasFlag(HotMods.Ctrl)) mods |= ControlMask;
        if (key.Mods.HasFlag(HotMods.Alt)) mods |= Mod1Mask;
        if (key.Mods.HasFlag(HotMods.Super)) mods |= Mod4Mask;
        lock (_gate)
        {
            var root = XDefaultRootWindow(_dpy);
            bool refused;
            lock (Errors)
            {
                // Xlib's error handler is process-wide (Avalonia has one too): ours only while these requests go out
                _refused = false;
                _ours = _dpy;
                var previous = XSetErrorHandler((IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)&OnError);
                _previous = previous;
                foreach (var v in LockVariants) XGrabKey(_dpy, code, mods | v, root, 0, GrabModeAsync, GrabModeAsync);
                XSync(_dpy, 0);
                XSetErrorHandler(previous);
                _ours = IntPtr.Zero;
                refused = _refused;
            }
            if (refused)
            {
                foreach (var v in LockVariants) XUngrabKey(_dpy, code, mods | v, root);
                XSync(_dpy, 0);
                Log.Info($"shortcut {key.Label} refused by the X server (taken)");
                return L.En("another app uses it");
            }
            _keys.Add((code, mods, action));
        }
        Log.Info($"shortcut {key.Label} grabbed");
        return null;
    }

    public void Clear()
    {
        if (_dpy == IntPtr.Zero) return;
        lock (_gate)
        {
            var root = XDefaultRootWindow(_dpy);
            foreach (var (code, mods, _) in _keys)
                foreach (var v in LockVariants) XUngrabKey(_dpy, code, mods | v, root);
            XSync(_dpy, 0);
            _keys.Clear();
        }
    }

    // ---------- errors ----------

    static readonly object Errors = new();
    static IntPtr _ours, _previous;
    static bool _refused;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnError(IntPtr dpy, IntPtr ev)
    {
        if (dpy == _ours)
        {
            // XErrorEvent: type, display, resourceid, serial, then error_code (unsigned char) at 32 on LP64
            if (((byte*)ev)[32] == BadAccess) _refused = true;
            return 0;
        }
        return _previous != IntPtr.Zero ? ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)_previous)(dpy, ev) : 0;
    }

    // ---------- events ----------

    void Loop()
    {
        var ev = stackalloc byte[192]; // sizeof(XEvent)
        while (_run)
        {
            Action? fire = null;
            lock (_gate)
            {
                while (_run && XPending(_dpy) > 0)
                {
                    XNextEvent(_dpy, ev);
                    int type = *(int*)ev;
                    // XKeyEvent on LP64: state at 80, keycode at 84
                    int code = (int)*(uint*)(ev + 84);
                    ulong time = *(ulong*)(ev + 56);
                    _times.TryGetValue(code, out var t);
                    if (type == KeyRelease) { _times[code] = (t.Press, time); continue; }
                    if (type != KeyPress) continue;
                    _times[code] = (time, t.Release);
                    // a held key auto-repeats: as presses only (detectable auto-repeat), or as a release + press at the same
                    // moment where the server can't. Either is the same keystroke; a lost release mutes it for 1.5 s at most.
                    bool repeat = t.Press != 0 && (t.Release > t.Press ? time - t.Release <= 30 : time - t.Press < 1500);
                    if (repeat) continue;
                    uint state = *(uint*)(ev + 80) & ~(LockMask | Mod2Mask);
                    foreach (var k in _keys)
                        if (k.Code == code && k.Mods == (state & (ShiftMask | ControlMask | Mod1Mask | Mod4Mask))) fire = k.Action;
                }
            }
            if (fire != null) { var a = fire; _post(a); }
            Thread.Sleep(25);
        }
    }

    public void Dispose()
    {
        _run = false;
        _thread?.Join(300);
        if (_dpy != IntPtr.Zero) { Clear(); XCloseDisplay(_dpy); _dpy = IntPtr.Zero; }
    }
}
