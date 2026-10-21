using System.Runtime.InteropServices;

namespace Convertino.Native;

/// <summary>The handful of Xlib / XShape calls Convertino needs (its own connection, separate from Avalonia's).</summary>
public static unsafe class X11
{
    const string Lib = "libX11.so.6";
    const string Ext = "libXext.so.6";

    [DllImport(Lib)] public static extern int XInitThreads();
    [DllImport(Lib)] public static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(Lib)] public static extern int XCloseDisplay(IntPtr dpy);
    [DllImport(Lib)] public static extern IntPtr XDefaultRootWindow(IntPtr dpy);
    [DllImport(Lib)] public static extern int XFlush(IntPtr dpy);
    [DllImport(Lib)] public static extern int XSync(IntPtr dpy, bool discard);

    [DllImport(Lib)]
    public static extern bool XQueryPointer(IntPtr dpy, IntPtr w, out IntPtr root, out IntPtr child,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);

    [DllImport(Lib)] public static extern int XQueryKeymap(IntPtr dpy, byte* keys32);
    [DllImport(Lib)] public static extern byte XKeysymToKeycode(IntPtr dpy, IntPtr keysym);

    [DllImport(Lib)]
    public static extern IntPtr XGetImage(IntPtr dpy, IntPtr drawable, int x, int y, uint width, uint height, IntPtr planeMask, int format);

    [DllImport(Lib)] public static extern int XFree(IntPtr data);

    // events (only the XInput2 raw events Convertino selects)
    [DllImport(Lib)] public static extern int XPending(IntPtr dpy);
    [DllImport(Lib)] public static extern int XNextEvent(IntPtr dpy, byte* ev);
    [DllImport(Lib)] public static extern bool XGetEventData(IntPtr dpy, byte* cookie);
    [DllImport(Lib)] public static extern void XFreeEventData(IntPtr dpy, byte* cookie);
    [DllImport(Lib)] public static extern bool XQueryExtension(IntPtr dpy, string name, out int opcode, out int firstEvent, out int firstError);

    // XInput2 (libXi): raw events reach every client that selects them on the root window, even while another
    // client holds a pointer grab (XI 2.1+) — which is how the wheel can be seen during a file manager's drag
    const string Xi = "libXi.so.6";

    [StructLayout(LayoutKind.Sequential)]
    public struct XIEventMask { public int deviceid, mask_len; public byte* mask; }

    [DllImport(Xi)] public static extern int XIQueryVersion(IntPtr dpy, ref int major, ref int minor);
    [DllImport(Xi)] public static extern int XISelectEvents(IntPtr dpy, IntPtr window, XIEventMask* masks, int n);

    public const int GenericEvent = 35, XIAllMasterDevices = 1, XI_RawButtonPress = 15;

    /// <summary>Ask for raw button presses on the root window. False if the server or libXi can't (no XI 2.1).</summary>
    public static bool SelectRawButtons(IntPtr dpy, out int xiOpcode)
    {
        xiOpcode = 0;
        try
        {
            if (!XQueryExtension(dpy, "XInputExtension", out xiOpcode, out _, out _)) return false;
            int major = 2, minor = 2;
            if (XIQueryVersion(dpy, ref major, ref minor) != 0 || major < 2 || (major == 2 && minor < 1)) return false;
            byte* bits = stackalloc byte[4];
            bits[XI_RawButtonPress >> 3] = (byte)(1 << (XI_RawButtonPress & 7));
            var m = new XIEventMask { deviceid = XIAllMasterDevices, mask_len = 4, mask = bits };
            XISelectEvents(dpy, XDefaultRootWindow(dpy), &m, 1);
            XFlush(dpy);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XSetWindowAttributes
    {
        public IntPtr background_pixmap, background_pixel, border_pixmap, border_pixel;
        public int bit_gravity, win_gravity, backing_store;
        public IntPtr backing_planes, backing_pixel;
        public int save_under;
        public IntPtr event_mask, do_not_propagate_mask;
        public int override_redirect;
        public IntPtr colormap, cursor;
    }

    public const uint CWOverrideRedirect = 1 << 9;

    [DllImport(Lib)]
    public static extern int XChangeWindowAttributes(IntPtr dpy, IntPtr w, uint valueMask, ref XSetWindowAttributes attrs);

    /// <summary>
    /// Take a window out of the window manager's hands (like menus and tooltips): exact position,
    /// no frame, no focus stealing, no taskbar entry. Must happen before the window is first mapped.
    /// </summary>
    public static void OverrideRedirect(IntPtr dpy, IntPtr window)
    {
        var a = new XSetWindowAttributes { override_redirect = 1 };
        XChangeWindowAttributes(dpy, window, CWOverrideRedirect, ref a);
        XSync(dpy, false);
    }

    [DllImport(Lib)] static extern int XSetInputFocus(IntPtr dpy, IntPtr window, int revertTo, IntPtr time);

    /// <summary>Give an override-redirect window the keyboard, which no window manager will: then its text boxes can be typed in.</summary>
    public static void Focus(IntPtr dpy, IntPtr window)
    {
        XSetInputFocus(dpy, window, 2 /* RevertToParent */, IntPtr.Zero /* CurrentTime */);
        XFlush(dpy);
    }

    // XShape
    [DllImport(Ext)] public static extern bool XShapeQueryExtension(IntPtr dpy, out int eventBase, out int errorBase);

    [DllImport(Ext)]
    public static extern void XShapeCombineRectangles(IntPtr dpy, IntPtr window, int destKind, int xOff, int yOff,
        XRectangle* rects, int n, int op, int ordering);

    [DllImport(Ext)]
    public static extern void XShapeCombineMask(IntPtr dpy, IntPtr window, int destKind, int xOff, int yOff, IntPtr pixmap, int op);

    public const int ShapeBounding = 0, ShapeInput = 2, ShapeSet = 0, Unsorted = 0, YXBanded = 3;
    public const int ZPixmap = 2;
    public const uint Button1Mask = 1 << 8, Button3Mask = 1 << 10, ShiftMask = 1;

    public const int XK_Shift_L = 0xffe1, XK_Shift_R = 0xffe2, XK_Escape = 0xff1b, XK_Tab = 0xff09;
    public const int XK_Control_L = 0xffe3, XK_Control_R = 0xffe4, XK_Alt_L = 0xffe9, XK_Alt_R = 0xffea;

    [StructLayout(LayoutKind.Sequential)]
    public struct XRectangle { public short x, y; public ushort width, height; }

    [StructLayout(LayoutKind.Sequential)]
    public struct XImage
    {
        public int width, height, xoffset, format;
        public IntPtr data;
        public int byte_order, bitmap_unit, bitmap_bit_order, bitmap_pad, depth, bytes_per_line, bits_per_pixel;
        public IntPtr red_mask, green_mask, blue_mask;
        public IntPtr obdata;
        public IntPtr create_image, destroy_image, get_pixel, put_pixel, sub_image, add_pixel;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int DestroyImage(IntPtr image);

    /// <summary>Grab a region of the root window as BGRA (null if unsupported).</summary>
    public static byte[]? Capture(IntPtr dpy, int x, int y, int w, int h, out int stride)
    {
        stride = 0;
        var img = XGetImage(dpy, XDefaultRootWindow(dpy), x, y, (uint)w, (uint)h, new IntPtr(-1), ZPixmap);
        if (img == IntPtr.Zero) return null;
        try
        {
            var xi = Marshal.PtrToStructure<XImage>(img);
            if (xi.bits_per_pixel != 32 || xi.data == IntPtr.Zero) return null;
            stride = xi.bytes_per_line;
            var buf = new byte[stride * xi.height];
            Marshal.Copy(xi.data, buf, 0, buf.Length);
            return buf;
        }
        finally
        {
            var xi = Marshal.PtrToStructure<XImage>(img);
            if (xi.destroy_image != IntPtr.Zero)
                Marshal.GetDelegateForFunctionPointer<DestroyImage>(xi.destroy_image)(img);
        }
    }

    /// <summary>Restrict a window's input (and optionally its visible shape) to a disc.</summary>
    public static void ShapeCircle(IntPtr dpy, IntPtr window, int size, int radius, bool bounding)
    {
        if (!XShapeQueryExtension(dpy, out _, out _)) return;
        var rects = new XRectangle[radius * 2];
        int c = size / 2, n = 0;
        for (int dy = -radius; dy < radius; dy++)
        {
            int half = (int)Math.Sqrt(Math.Max(0, radius * radius - (dy + 0.5) * (dy + 0.5)));
            rects[n++] = new XRectangle { x = (short)(c - half), y = (short)(c + dy), width = (ushort)(half * 2), height = 1 };
        }
        fixed (XRectangle* p = rects)
        {
            XShapeCombineRectangles(dpy, window, ShapeInput, 0, 0, p, n, ShapeSet, YXBanded);
            if (bounding) XShapeCombineRectangles(dpy, window, ShapeBounding, 0, 0, p, n, ShapeSet, YXBanded);
        }
        XFlush(dpy);
    }

    /// <summary>Remove any shape (back to the full rectangle).</summary>
    public static void Unshape(IntPtr dpy, IntPtr window)
    {
        if (!XShapeQueryExtension(dpy, out _, out _)) return;
        XShapeCombineMask(dpy, window, ShapeInput, 0, 0, IntPtr.Zero, ShapeSet);
        XShapeCombineMask(dpy, window, ShapeBounding, 0, 0, IntPtr.Zero, ShapeSet);
        XFlush(dpy);
    }
}

/// <summary>
/// Watches the pointer and keyboard on X11 by polling (no root, no extensions, works during other apps'
/// pointer grabs — e.g. while a file manager drags). Fires on the UI thread via <see cref="Post"/>.
/// </summary>
public sealed unsafe class X11Input : IDisposable
{
    readonly Action<Action> _post;
    Thread? _thread;
    volatile bool _run = true;
    public IntPtr Display { get; private set; }
    byte _shiftL, _shiftR, _ctrlL, _ctrlR, _altL, _altR, _esc, _tab;
    int _xiOpcode;

    /// <summary>The trigger key (Shift, Ctrl or Alt, from the settings) went down. Args: dragging?, where.</summary>
    public event Action<bool, ScreenPoint>? TriggerDown;
    /// <summary>The trigger key came back up.</summary>
    public event Action? TriggerUp;
    /// <summary>Tab went down (arg: Shift held → previous).</summary>
    public event Action<bool>? Tab;
    /// <summary>A wheel notch anywhere on screen (XInput2 raw button 4/5; arg &gt; 0 = down), and where the pointer was.</summary>
    public event Action<int, ScreenPoint>? Wheel;

    /// <summary>The server reports the wheel through XInput2 (otherwise only windows under the pointer see it).</summary>
    public bool RawWheel { get; private set; }
    public event Action<ScreenPoint>? ButtonDown;
    public event Action<ScreenPoint>? ButtonUp;
    public event Action? Escape;

    /// <summary>Poll faster while something interesting may happen.</summary>
    public volatile bool Hot;

    public X11Input(Action<Action> post) => _post = post;

    public bool Start()
    {
        X11.XInitThreads();
        Display = X11.XOpenDisplay(IntPtr.Zero);
        if (Display == IntPtr.Zero) return false;
        _shiftL = X11.XKeysymToKeycode(Display, X11.XK_Shift_L);
        _shiftR = X11.XKeysymToKeycode(Display, X11.XK_Shift_R);
        _ctrlL = X11.XKeysymToKeycode(Display, X11.XK_Control_L);
        _ctrlR = X11.XKeysymToKeycode(Display, X11.XK_Control_R);
        _altL = X11.XKeysymToKeycode(Display, X11.XK_Alt_L);
        _altR = X11.XKeysymToKeycode(Display, X11.XK_Alt_R);
        _esc = X11.XKeysymToKeycode(Display, X11.XK_Escape);
        _tab = X11.XKeysymToKeycode(Display, X11.XK_Tab);
        RawWheel = X11.SelectRawButtons(Display, out _xiOpcode);
        Log.Info($"x11 input: xinput2 wheel={RawWheel}");
        _thread = new Thread(Loop) { IsBackground = true, Name = "Convertino X11 input" };
        _thread.Start();
        return true;
    }

    public ScreenPoint Pointer
    {
        get
        {
            lock (this)
            {
                X11.XQueryPointer(Display, X11.XDefaultRootWindow(Display), out _, out _, out int x, out int y, out _, out _, out _);
                return new ScreenPoint(x, y);
            }
        }
    }

    void Loop()
    {
        var keys = stackalloc byte[32];
        var ev = stackalloc byte[192]; // sizeof(XEvent)
        bool lDown = false, rDown = false, dragging = false, trigger = false, esc = false, tab = false;
        int downX = 0, downY = 0;
        while (_run)
        {
            int x, y; uint mask;
            int wheel = 0;
            lock (this)
            {
                X11.XQueryPointer(Display, X11.XDefaultRootWindow(Display), out _, out _, out x, out y, out _, out _, out mask);
                X11.XQueryKeymap(Display, keys);
                if (RawWheel) wheel = DrainWheel(ev);
            }
            bool l = (mask & X11.Button1Mask) != 0;
            bool r = (mask & X11.Button3Mask) != 0;
            var (k1, k2) = Settings.Current.TriggerKey.Modifier() switch
            {
                ModifierKey.Ctrl => (_ctrlL, _ctrlR),
                ModifierKey.Alt => (_altL, _altR),
                _ => (_shiftL, _shiftR),
            };
            bool t = Key(keys, k1) || Key(keys, k2);
            bool e = Key(keys, _esc);
            bool tb = Key(keys, _tab);
            bool shift = Key(keys, _shiftL) || Key(keys, _shiftR);
            var pt = new ScreenPoint(x, y);

            if (l && !lDown) { downX = x; downY = y; dragging = false; _post(() => ButtonDown?.Invoke(pt)); if (Overlay.OverlayController.Diag) Log.Info($"x11 button down {pt}"); }
            if (!l && lDown) { dragging = false; _post(() => ButtonUp?.Invoke(pt)); }
            if (r && !rDown) _post(() => ButtonDown?.Invoke(pt));
            rDown = r;
            if (l && !dragging && (Math.Abs(x - downX) > 6 || Math.Abs(y - downY) > 6)) { dragging = true; if (Overlay.OverlayController.Diag) Log.Info("x11 dragging"); }
            lDown = l;

            if (t && !trigger) { bool d = dragging && l; _post(() => TriggerDown?.Invoke(d, pt)); if (Overlay.OverlayController.Diag) Log.Info($"x11 trigger d={d}"); }
            if (!t && trigger) _post(() => TriggerUp?.Invoke());
            trigger = t;
            if (e && !esc) _post(() => Escape?.Invoke());
            esc = e;
            if (tb && !tab) _post(() => Tab?.Invoke(shift));
            tab = tb;
            if (wheel != 0) { int w = wheel; _post(() => Wheel?.Invoke(w, pt)); if (Overlay.OverlayController.Diag) Log.Info($"x11 wheel {w}"); }

            Thread.Sleep(l || Hot ? 12 : 40);
        }
    }

    /// <summary>Read the queued raw button presses; wheel down (button 5) counts +1, up (4) −1.</summary>
    int DrainWheel(byte* ev)
    {
        int sum = 0;
        while (X11.XPending(Display) > 0)
        {
            X11.XNextEvent(Display, ev);
            // XGenericEventCookie: type @0, extension @32, evtype @36; XIRawEvent.detail @56 (LP64)
            if (*(int*)ev != X11.GenericEvent || *(int*)(ev + 32) != _xiOpcode || !X11.XGetEventData(Display, ev)) continue;
            try
            {
                var raw = *(byte**)(ev + 48);
                if (*(int*)(ev + 36) == X11.XI_RawButtonPress && raw != null)
                    sum += *(int*)(raw + 56) switch { 4 => -1, 5 => +1, _ => 0 };
            }
            finally { X11.XFreeEventData(Display, ev); }
        }
        return sum;
    }

    static bool Key(byte* keys, byte code) => code != 0 && (keys[code >> 3] & (1 << (code & 7))) != 0;

    public void Dispose()
    {
        _run = false;
        _thread?.Join(300);
        if (Display != IntPtr.Zero) X11.XCloseDisplay(Display);
    }
}
