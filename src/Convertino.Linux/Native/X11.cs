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

    public const int XK_Shift_L = 0xffe1, XK_Shift_R = 0xffe2, XK_Escape = 0xff1b;
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
    byte _shiftL, _shiftR, _ctrlL, _ctrlR, _altL, _altR, _esc;

    /// <summary>The trigger key (Shift, Ctrl or Alt, from the settings) went down. Args: dragging?, where.</summary>
    public event Action<bool, ScreenPoint>? TriggerDown;
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
        bool lDown = false, rDown = false, dragging = false, trigger = false, esc = false;
        int downX = 0, downY = 0;
        while (_run)
        {
            int x, y; uint mask;
            lock (this)
            {
                X11.XQueryPointer(Display, X11.XDefaultRootWindow(Display), out _, out _, out x, out y, out _, out _, out mask);
                X11.XQueryKeymap(Display, keys);
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
            var pt = new ScreenPoint(x, y);

            if (l && !lDown) { downX = x; downY = y; dragging = false; _post(() => ButtonDown?.Invoke(pt)); if (Overlay.OverlayController.Diag) Log.Info($"x11 button down {pt}"); }
            if (!l && lDown) { dragging = false; _post(() => ButtonUp?.Invoke(pt)); }
            if (r && !rDown) _post(() => ButtonDown?.Invoke(pt));
            rDown = r;
            if (l && !dragging && (Math.Abs(x - downX) > 6 || Math.Abs(y - downY) > 6)) { dragging = true; if (Overlay.OverlayController.Diag) Log.Info("x11 dragging"); }
            lDown = l;

            if (t && !trigger) { bool d = dragging && l; _post(() => TriggerDown?.Invoke(d, pt)); if (Overlay.OverlayController.Diag) Log.Info($"x11 trigger d={d}"); }
            trigger = t;
            if (e && !esc) _post(() => Escape?.Invoke());
            esc = e;

            Thread.Sleep(l || Hot ? 12 : 40);
        }
    }

    static bool Key(byte* keys, byte code) => code != 0 && (keys[code >> 3] & (1 << (code & 7))) != 0;

    public void Dispose()
    {
        _run = false;
        _thread?.Join(300);
        if (Display != IntPtr.Zero) X11.XCloseDisplay(Display);
    }
}
