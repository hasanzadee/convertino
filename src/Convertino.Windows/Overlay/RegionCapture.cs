using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Convertino.Native;
using Convertino.Ui;

namespace Convertino.Overlay;

/// <summary>
/// "Capture an area": the whole desktop is frozen first (one BitBlt of the virtual screen, in physical pixels), then
/// every monitor gets its own window showing its part dimmed, so mixed DPI just works: each window knows its own
/// scale, the selection lives in physical pixels. Drag a rectangle (live W × H), click for the whole screen,
/// Esc or right-click to cancel. The picture comes from the frozen pixels, so the overlay itself is never in it.
/// </summary>
public sealed class RegionCapture
{
    /// <summary>The picked area as top-down BGRX pixels (PNG-encode them off the UI thread), and where the button came up.</summary>
    public sealed record Result(byte[] Pixels, int Width, int Height, POINT Release, RECT Area)
    {
        public byte[] Png() => Services.Captures.BgraToPng(Pixels, Width, Height);
    }

    /// <summary>The capture on screen right now, if any (the global Esc cancels it).</summary>
    public static RegionCapture? Active { get; private set; }

    readonly RECT _virt;
    readonly byte[] _px; // top-down BGRX of the virtual screen
    readonly BitmapSource _frozen;
    readonly List<RECT> _monitors;
    readonly List<RegionWindow> _windows = [];
    readonly TaskCompletionSource<Result?> _done = new();
    POINT _cursor;
    POINT? _start;

    RegionCapture(RECT virt, byte[] px, List<RECT> monitors)
    {
        _virt = virt;
        _px = px;
        _monitors = monitors;
        _frozen = BitmapSource.Create(virt.Width, virt.Height, 96, 96, PixelFormats.Bgr32, null, px, virt.Width * 4);
        _frozen.Freeze();
    }

    /// <summary>Selection so far in physical pixels (null before the first drag).</summary>
    internal RECT? Selection => _start is { } s ? Norm(s, _cursor) : null;
    internal POINT Cursor => _cursor;
    internal bool Dragging => _start != null;

    /// <summary>Freeze the screens and let the user pick an area; null when cancelled.</summary>
    public static Task<Result?> Run()
    {
        var virt = VirtualScreen();
        var px = Grab(virt);
        if (px == null) return Task.FromResult<Result?>(null);
        var rc = new RegionCapture(virt, px, Monitors());
        Active = rc;
        rc.Show();
        return rc._done.Task;
    }

    public void Cancel() => Finish(null);

    void Show()
    {
        _cursor = Win32.Cursor;
        foreach (var mon in _monitors)
        {
            var crop = new CroppedBitmap(_frozen, new Int32Rect(mon.Left - _virt.Left, mon.Top - _virt.Top, mon.Width, mon.Height));
            crop.Freeze();
            var w = new RegionWindow(this, mon, crop);
            _windows.Add(w);
            w.Place();
        }
        // keyboard focus for Esc on the screen the pointer is on (the global Esc hook works too)
        var here = _windows.FirstOrDefault(w => w.Monitor.Contains(_cursor)) ?? _windows.FirstOrDefault();
        if (here != null) { Win32.ForceForeground(here.Hwnd); here.Activate(); here.Focus(); }
        Log.Info($"capture: {_monitors.Count} monitor(s), virtual {_virt.Left},{_virt.Top} {_virt.Width}×{_virt.Height}");
    }

    internal void Down() { _cursor = Win32.Cursor; _start = _cursor; Redraw(); }

    internal void Move()
    {
        var c = Win32.Cursor;
        if (c.X == _cursor.X && c.Y == _cursor.Y) return;
        _cursor = c;
        Redraw();
    }

    internal void Up()
    {
        if (_start is not { } s) return;
        _cursor = Win32.Cursor;
        var area = Norm(s, _cursor);
        // a click without a drag takes the whole screen it is on
        if (area.Width < 4 && area.Height < 4) area = _monitors.FirstOrDefault(m => m.Contains(_cursor), _monitors[0]);
        var (px, w, h) = Crop(_px, _virt, area);
        Finish(new Result(px, w, h, _cursor, area));
    }

    void Redraw() { foreach (var w in _windows) w.View.InvalidateVisual(); }

    void Finish(Result? r)
    {
        if (_done.Task.IsCompleted) return;
        if (Active == this) Active = null;
        foreach (var w in _windows) w.Close();
        _windows.Clear();
        if (r != null) Log.Info($"capture: {r.Area.Width}×{r.Area.Height} at {r.Area.Left},{r.Area.Top}");
        _done.TrySetResult(r);
    }

    static RECT Norm(POINT a, POINT b) => new()
    {
        Left = Math.Min(a.X, b.X), Top = Math.Min(a.Y, b.Y),
        Right = Math.Max(a.X, b.X) + 1, Bottom = Math.Max(a.Y, b.Y) + 1,
    };

    /// <summary>The area (physical pixels, clipped to the virtual screen) out of the frozen screen.</summary>
    internal static (byte[] px, int w, int h) Crop(byte[] px, RECT virt, RECT area)
    {
        int x0 = Math.Clamp(area.Left, virt.Left, virt.Right), y0 = Math.Clamp(area.Top, virt.Top, virt.Bottom);
        int x1 = Math.Clamp(area.Right, virt.Left, virt.Right), y1 = Math.Clamp(area.Bottom, virt.Top, virt.Bottom);
        int w = Math.Max(1, x1 - x0), h = Math.Max(1, y1 - y0);
        var outPx = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(px, ((y0 - virt.Top + y) * virt.Width + (x0 - virt.Left)) * 4, outPx, y * w * 4, w * 4);
        return (outPx, w, h);
    }

    // ---------- screen ----------

    const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

    delegate bool MonitorEnum(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnum callback, IntPtr data);

    static RECT VirtualScreen()
    {
        int x = Win32.GetSystemMetrics(SM_XVIRTUALSCREEN), y = Win32.GetSystemMetrics(SM_YVIRTUALSCREEN);
        return new RECT { Left = x, Top = y, Right = x + Win32.GetSystemMetrics(SM_CXVIRTUALSCREEN), Bottom = y + Win32.GetSystemMetrics(SM_CYVIRTUALSCREEN) };
    }

    static List<RECT> Monitors()
    {
        var list = new List<RECT>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (Win32.GetMonitorInfo(m, ref mi)) list.Add(mi.rcMonitor);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>One BitBlt of the whole virtual screen (all monitors, physical pixels: the process is per-monitor DPI aware).</summary>
    static byte[]? Grab(RECT r)
    {
        IntPtr screen = Win32.GetDC(IntPtr.Zero);
        IntPtr mem = Win32.CreateCompatibleDC(screen);
        var bih = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = r.Width, biHeight = -r.Height, biPlanes = 1, biBitCount = 32 };
        IntPtr dib = Win32.CreateDIBSection(screen, ref bih, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (dib == IntPtr.Zero) return null;
            IntPtr old = Win32.SelectObject(mem, dib);
            bool ok = BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.Left, r.Top, Win32.SRCCOPY | Win32.CAPTUREBLT);
            Win32.SelectObject(mem, old);
            if (!ok) { Log.Info("capture: BitBlt failed"); return null; }
            var px = new byte[r.Width * r.Height * 4];
            Marshal.Copy(bits, px, 0, px.Length);
            return px;
        }
        catch (Exception ex) { Log.Error("capture", ex); return null; }
        finally
        {
            if (dib != IntPtr.Zero) Win32.DeleteObject(dib);
            Win32.DeleteDC(mem);
            Win32.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // ---------- dev (Convertino.exe --capture-selftest out.png) ----------

    /// <summary>The overlay over a made-up desktop, mid-drag on a 1.5× monitor and idle on a 1× one, side by side; nothing on screen is read.</summary>
    internal static void DevRender(string outPath, BitmapSource desktop)
    {
        int w = desktop.PixelWidth, h = desktop.PixelHeight;
        var px = new byte[w * h * 4];
        new FormatConvertedBitmap(desktop, PixelFormats.Bgr32, null, 0).CopyPixels(px, w * 4, 0);
        var virt = new RECT { Left = 0, Top = 0, Right = w, Bottom = h };
        var rc = new RegionCapture(virt, px, [virt]) { _start = new POINT(w * 27 / 100, h * 26 / 100), _cursor = new POINT(w * 74 / 100, h * 70 / 100) };
        var idle = new RegionCapture(virt, px, [virt]) { _cursor = new POINT(w * 40 / 100, h * 55 / 100) };
        var shots = new[] { (rc, 1.5), (idle, 1.0) };
        var rtb = new RenderTargetBitmap(w * 2 + 24, h, 96, 96, PixelFormats.Pbgra32);
        double x = 0;
        foreach (var (cap, scale) in shots)
        {
            // a monitor of w×h physical pixels at this scale is w/scale × h/scale DIPs; render it back at full size
            var view = new RegionView(cap, virt, cap._frozen) { Width = w / scale, Height = h / scale };
            view.Measure(new Size(view.Width, view.Height));
            view.Arrange(new Rect(0, 0, view.Width, view.Height));
            var host = new System.Windows.Controls.Viewbox { Child = view, Width = w, Height = h };
            host.Measure(new Size(w, h));
            host.Arrange(new Rect(x, 0, w, h));
            host.UpdateLayout();
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(host), null, new Rect(x, 0, w, h));
            rtb.Render(dv);
            x += w + 24;
        }
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = System.IO.File.Create(outPath);
        enc.Save(fs);
    }
}

/// <summary>One monitor's part of the capture overlay.</summary>
sealed class RegionWindow : Window
{
    readonly RegionCapture _owner;
    public RECT Monitor { get; }
    public RegionView View { get; }
    public IntPtr Hwnd { get; }

    public RegionWindow(RegionCapture owner, RECT monitor, BitmapSource frozen)
    {
        _owner = owner;
        Monitor = monitor;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Title = "Convertino capture";
        Cursor = Cursors.Cross;
        Content = View = new RegionView(owner, monitor, frozen);
        Hwnd = new WindowInteropHelper(this).EnsureHandle();
        Win32.MakeToolWindow(Hwnd, noActivate: false);
        MouseLeftButtonDown += (_, _) => { CaptureMouse(); _owner.Down(); };
        MouseMove += (_, _) => _owner.Move();
        MouseLeftButtonUp += (_, _) => { ReleaseMouseCapture(); _owner.Up(); };
        MouseRightButtonUp += (_, _) => _owner.Cancel();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) _owner.Cancel(); };
        Deactivated += (_, _) => { if (!IsMouseCaptured) View.InvalidateVisual(); };
    }

    /// <summary>
    /// Cover the monitor exactly, in physical pixels. Twice: the first move to a monitor with another DPI makes WPF
    /// rescale the window to its suggested size; the second, now on that monitor, sticks.
    /// </summary>
    public void Place()
    {
        Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, Monitor.Left, Monitor.Top, Monitor.Width, Monitor.Height, Win32.SWP_NOACTIVATE);
        Show();
        Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, Monitor.Left, Monitor.Top, Monitor.Width, Monitor.Height, Win32.SWP_NOACTIVATE);
    }
}

/// <summary>Draws one monitor: the frozen screen dimmed, the selection clear with an accent frame, W × H, a crosshair.</summary>
sealed class RegionView(RegionCapture owner, RECT monitor, BitmapSource frozen) : FrameworkElement
{
    static readonly Brush Dim = Freeze(new SolidColorBrush(Color.FromArgb(112, 0, 0, 0)));
    static readonly Pen GuideLight = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255))), 1));
    static readonly Pen GuideDark = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0))), 1));
    static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth > 0 ? ActualWidth : Width, h = ActualHeight > 0 ? ActualHeight : Height;
        if (w <= 0 || h <= 0) return;
        double scale = monitor.Width / w; // physical pixels per DIP on this monitor
        var full = new Rect(0, 0, w, h);
        Point Local(POINT p) => new((p.X - monitor.Left) / scale, (p.Y - monitor.Top) / scale);
        var t = Theme.Current;
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        dc.DrawImage(frozen, full);
        var sel = owner.Selection;
        if (sel is { } s && owner.Dragging)
        {
            var r = new Rect(Local(new POINT(s.Left, s.Top)), Local(new POINT(s.Right, s.Bottom)));
            dc.DrawGeometry(Dim, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(r)));
            if (r.IntersectsWith(full))
            {
                var accent = new LinearGradientBrush(t.AccentA, t.AccentB, 45);
                dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 3), r);
                dc.DrawRectangle(null, new Pen(accent, 1.5), r);
                // corner ticks
                var tick = new Pen(accent, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                double k = Math.Min(14, Math.Min(r.Width, r.Height) / 3);
                foreach (var (cx, cy, dx, dy) in new[] { (r.Left, r.Top, 1, 1), (r.Right, r.Top, -1, 1), (r.Left, r.Bottom, 1, -1), (r.Right, r.Bottom, -1, -1) })
                {
                    dc.DrawLine(tick, new Point(cx, cy), new Point(cx + dx * k, cy));
                    dc.DrawLine(tick, new Point(cx, cy), new Point(cx, cy + dy * k));
                }
                // live size, in physical pixels: what the PNG will be
                if (monitor.Contains(owner.Cursor))
                    Pill(dc, $"{s.Width} × {s.Height}", new Point(r.Right, r.Bottom + 10), full, ppd, t, alignRight: true);
            }
        }
        else
        {
            dc.DrawRectangle(Dim, null, full);
            if (monitor.Contains(owner.Cursor))
            {
                var c = Local(owner.Cursor);
                dc.DrawLine(GuideDark, new Point(0, c.Y + 1.5), new Point(w, c.Y + 1.5));
                dc.DrawLine(GuideDark, new Point(c.X + 1.5, 0), new Point(c.X + 1.5, h));
                dc.DrawLine(GuideLight, new Point(0, c.Y + 0.5), new Point(w, c.Y + 0.5));
                dc.DrawLine(GuideLight, new Point(c.X + 0.5, 0), new Point(c.X + 0.5, h));
                Pill(dc, L.T("Drag to capture  ·  click: whole screen  ·  Esc: cancel"), new Point(w / 2, 28), full, ppd, t, centre: true);
            }
        }
    }

    static void Pill(DrawingContext dc, string text, Point at, Rect bounds, double ppd, Theme t, bool alignRight = false, bool centre = false)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 12.5, new SolidColorBrush(t.Text), ppd);
        double pw = ft.Width + 20, ph = ft.Height + 10;
        double x = centre ? at.X - pw / 2 : alignRight ? at.X - pw : at.X, y = at.Y;
        if (y + ph > bounds.Bottom - 6) y = at.Y - ph - 30; // no room below the selection: just inside it
        x = Math.Clamp(x, bounds.Left + 6, Math.Max(bounds.Left + 6, bounds.Right - pw - 6));
        y = Math.Clamp(y, bounds.Top + 6, Math.Max(bounds.Top + 6, bounds.Bottom - ph - 6));
        var bg = new SolidColorBrush(Color.FromArgb(232, t.Window.R, t.Window.G, t.Window.B));
        dc.DrawRoundedRectangle(bg, new Pen(new SolidColorBrush(t.Stroke), 1), new Rect(x, y, pw, ph), ph / 2, ph / 2);
        dc.DrawText(ft, new Point(x + 10, y + 5));
    }
}
