using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Convertino.Native;
using Convertino.Services;
using Convertino.Ui;

namespace Convertino.Overlay;

/// <summary>
/// "Capture an area" on Linux. X11: the whole root window is frozen first (XGetImage), then shown dimmed in one
/// override-redirect window across all monitors; drag a rectangle (live W × H), click for the whole screen, Esc or
/// right-click cancels. Wayland can't be read that way, so the desktop's own picker does it: grim + slurp,
/// gnome-screenshot, Spectacle or Flameshot, whichever is installed.
/// </summary>
public sealed class RegionCapture
{
    /// <summary>The capture on screen right now (the global Esc cancels it).</summary>
    public static RegionCapture? Active { get; private set; }

    readonly PixelRect _virt;
    readonly byte[] _px; // tight top-down BGRA of _virt
    readonly List<PixelRect> _screens;
    readonly double _scale;
    readonly Window _win;
    readonly RegionView _view;
    readonly TaskCompletionSource<(string, ScreenPoint?)?> _done = new();
    PixelPoint _cursor;
    PixelPoint? _start;

    internal PixelRect? Selection => _start is { } s ? Norm(s, _cursor) : null;
    internal PixelPoint Cursor => _cursor;
    internal PixelRect Virtual => _virt;
    internal IReadOnlyList<PixelRect> Screens => _screens;

    public static bool Wayland => Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland"
        || (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")));

    /// <summary>Pick an area; the saved PNG (Pictures/Convertino) and where the pick ended, null when cancelled.</summary>
    public static Task<(string Png, ScreenPoint? At)?> Run(X11Input? input, TopLevel host, Action<Window>? unmanaged)
    {
        if (Wayland || input == null) return Task.Run(WithTool);
        var screens = host.Screens?.All.Select(s => s.Bounds).ToList() ?? [];
        if (screens.Count == 0) screens.Add(new PixelRect(0, 0, 1920, 1080));
        var virt = screens.Aggregate((a, b) => a.Union(b));
        byte[]? raw;
        int stride;
        lock (input) raw = X11.Capture(input.Display, virt.X, virt.Y, virt.Width, virt.Height, out stride);
        if (raw == null) return Task.Run(WithTool);
        var px = new byte[virt.Width * virt.Height * 4];
        for (int y = 0; y < virt.Height; y++) Buffer.BlockCopy(raw, y * stride, px, y * virt.Width * 4, virt.Width * 4);
        // XGetImage leaves the fourth byte 0: as alpha, a compositor would add the picture to the desktop instead of covering it
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        var rc = new RegionCapture(virt, px, screens, host.Screens?.Primary?.Scaling ?? 1, unmanaged);
        Active = rc;
        rc.Show(input.Pointer);
        return rc._done.Task;
    }

    RegionCapture(PixelRect virt, byte[] px, List<PixelRect> screens, double scale, Action<Window>? unmanaged)
    {
        _virt = virt;
        _px = px;
        _screens = screens;
        _scale = scale;
        var bmp = new WriteableBitmap(new PixelSize(virt.Width, virt.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = bmp.Lock())
            for (int y = 0; y < virt.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(px, y * virt.Width * 4, fb.Address + y * fb.RowBytes, virt.Width * 4);
        _view = new RegionView(this, bmp);
        _win = new Window
        {
            WindowDecorations = WindowDecorations.None, Topmost = true, ShowInTaskbar = false, ShowActivated = true, CanResize = false,
            Background = Brushes.Black, Title = "Convertino capture", Content = _view,
            Width = virt.Width / scale, Height = virt.Height / scale,
            Cursor = new Cursor(StandardCursorType.Cross),
        };
        unmanaged?.Invoke(_win); // no window-manager frame or placement: exactly over every screen
        _win.PointerPressed += (_, e) =>
        {
            var pt = e.GetCurrentPoint(_view);
            if (pt.Properties.IsRightButtonPressed) { Cancel(); return; }
            if (!pt.Properties.IsLeftButtonPressed) return;
            _cursor = ToScreen(pt.Position);
            _start = _cursor;
            _view.InvalidateVisual();
        };
        _win.PointerMoved += (_, e) => { _cursor = ToScreen(e.GetPosition(_view)); _view.InvalidateVisual(); };
        _win.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left || _start is not { } s) return;
            _cursor = ToScreen(e.GetPosition(_view));
            var area = Norm(s, _cursor);
            // a click without a drag takes the whole screen it is on
            if (area.Width < 4 && area.Height < 4) area = _screens.FirstOrDefault(m => m.Contains(_cursor), _screens[0]);
            Finish(area);
        };
        _win.KeyDown += (_, e) => { if (e.Key == Key.Escape) Cancel(); };
    }

    PixelPoint ToScreen(Point p) => new(_virt.X + (int)Math.Round(p.X * _scale), _virt.Y + (int)Math.Round(p.Y * _scale));

    void Show(ScreenPoint pointer)
    {
        _cursor = new PixelPoint(pointer.X, pointer.Y);
        _win.Position = new PixelPoint(_virt.X, _virt.Y);
        _win.Show();
        _win.Position = new PixelPoint(_virt.X, _virt.Y);
        _win.Activate();
        Log.Info($"capture: {_screens.Count} screen(s), {_virt.Width}×{_virt.Height} at {_virt.X},{_virt.Y}, scale {_scale}");
    }

    public void Cancel()
    {
        if (_done.Task.IsCompleted) return;
        Close();
        _done.TrySetResult(null);
    }

    void Close()
    {
        if (Active == this) Active = null;
        _win.Close();
    }

    async void Finish(PixelRect area)
    {
        if (_done.Task.IsCompleted) return;
        Close();
        try
        {
            var at = new ScreenPoint(_cursor.X, _cursor.Y);
            var path = await Task.Run(() =>
            {
                var (cut, w, h) = Crop(_px, _virt, area);
                var p = Captures.NewPath("Screenshot", "png");
                File.WriteAllBytes(p, Captures.BgraToPng(cut, w, h));
                return p;
            });
            Log.Info($"capture: {area.Width}×{area.Height} at {area.X},{area.Y}");
            _done.TrySetResult((path, at));
        }
        catch (Exception ex) { _done.TrySetException(ex); }
    }

    static PixelRect Norm(PixelPoint a, PixelPoint b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X) + 1, Math.Abs(a.Y - b.Y) + 1);

    /// <summary>The area (clipped to the frozen screen) as tight BGRA.</summary>
    internal static (byte[] px, int w, int h) Crop(byte[] px, PixelRect virt, PixelRect area)
    {
        var a = area.Intersect(virt);
        int w = Math.Max(1, a.Width), h = Math.Max(1, a.Height);
        var outPx = new byte[w * h * 4];
        if (a.Width > 0 && a.Height > 0)
            for (int y = 0; y < h; y++)
                Buffer.BlockCopy(px, ((a.Y - virt.Y + y) * virt.Width + (a.X - virt.X)) * 4, outPx, y * w * 4, w * 4);
        return (outPx, w, h);
    }

    // ---------- Wayland: the desktop's own picker ----------

    static (string Png, ScreenPoint? At)? WithTool()
    {
        var path = Captures.NewPath("Screenshot", "png");
        ScreenPoint? at = null;
        if (Paths.Which("slurp") is { } slurp && Paths.Which("grim") is { } grim)
        {
            var (code, geo) = Exec(slurp, []);
            geo = geo.Trim();
            if (code != 0 || geo.Length == 0) return null; // Esc in slurp
            Exec(grim, ["-g", geo, path]);
            // "x,y wxh": the flower blooms at the bottom-right corner, where the drag ended
            var n = geo.Split([',', ' ', 'x'], StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out int v) ? v : 0).ToArray();
            if (n.Length == 4) at = new ScreenPoint(n[0] + n[2], n[1] + n[3]);
        }
        else if (Paths.Which("gnome-screenshot") is { } gs) Exec(gs, ["-a", "-f", path]);
        else if (Paths.Which("spectacle") is { } sp) Exec(sp, ["-r", "-b", "-n", "-o", path]);
        else if (Paths.Which("flameshot") is { } fs)
        {
            var png = ExecBytes(fs, ["gui", "-r"]);
            if (png is { Length: > 8 }) File.WriteAllBytes(path, png);
        }
        else throw new InvalidOperationException("On Wayland, area capture needs grim + slurp, gnome-screenshot, Spectacle or Flameshot");
        return File.Exists(path) && new FileInfo(path).Length > 0 ? (path, at) : null;
    }

    static (int Code, string Out) Exec(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit(300_000);
        return (p.HasExited ? p.ExitCode : -1, stdout);
    }

    static byte[]? ExecBytes(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit(300_000);
        return p.HasExited && p.ExitCode == 0 ? ms.ToArray() : null;
    }
}

/// <summary>Draws the frozen screens dimmed, the selection clear with an accent frame and its W × H, or a crosshair before the drag.</summary>
sealed class RegionView(RegionCapture owner, Bitmap frozen) : Control
{
    static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
    static readonly IPen GuideLight = new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1);
    static readonly IPen GuideDark = new Pen(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), 1);

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        var virt = owner.Virtual;
        double scale = virt.Width / w;
        Point Local(int x, int y) => new((x - virt.X) / scale, (y - virt.Y) / scale);
        var t = Skin.Current;
        var full = new Rect(0, 0, w, h);
        dc.DrawImage(frozen, new Rect(0, 0, frozen.PixelSize.Width, frozen.PixelSize.Height), full);

        if (owner.Selection is { } s)
        {
            var r = new Rect(Local(s.X, s.Y), Local(s.Right, s.Bottom));
            // dim around the selection: four bands
            dc.DrawRectangle(Dim, null, new Rect(0, 0, w, Math.Max(0, r.Top)));
            dc.DrawRectangle(Dim, null, new Rect(0, r.Bottom, w, Math.Max(0, h - r.Bottom)));
            dc.DrawRectangle(Dim, null, new Rect(0, r.Top, Math.Max(0, r.Left), r.Height));
            dc.DrawRectangle(Dim, null, new Rect(r.Right, r.Top, Math.Max(0, w - r.Right), r.Height));
            var accent = t.AccentGradient(r);
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 3), r);
            dc.DrawRectangle(null, new Pen(accent, 1.5), r);
            var tick = new Pen(accent, 4, lineCap: PenLineCap.Round);
            double k = Math.Min(14, Math.Min(r.Width, r.Height) / 3);
            foreach (var (cx, cy, dx, dy) in new[] { (r.Left, r.Top, 1, 1), (r.Right, r.Top, -1, 1), (r.Left, r.Bottom, 1, -1), (r.Right, r.Bottom, -1, -1) })
            {
                dc.DrawLine(tick, new Point(cx, cy), new Point(cx + dx * k, cy));
                dc.DrawLine(tick, new Point(cx, cy), new Point(cx, cy + dy * k));
            }
            // live size in physical pixels: what the PNG will be
            Pill(dc, $"{s.Width} × {s.Height}", new Point(r.Right, r.Bottom + 10), full, t, alignRight: true);
        }
        else
        {
            dc.DrawRectangle(Dim, null, full);
            var c = Local(owner.Cursor.X, owner.Cursor.Y);
            dc.DrawLine(GuideDark, new Point(0, c.Y + 1.5), new Point(w, c.Y + 1.5));
            dc.DrawLine(GuideDark, new Point(c.X + 1.5, 0), new Point(c.X + 1.5, h));
            dc.DrawLine(GuideLight, new Point(0, c.Y + 0.5), new Point(w, c.Y + 0.5));
            dc.DrawLine(GuideLight, new Point(c.X + 0.5, 0), new Point(c.X + 0.5, h));
            // the hint at the top of the screen the pointer is on
            var screen = owner.Screens.FirstOrDefault(m => m.Contains(owner.Cursor), owner.Screens[0]);
            var top = Local(screen.X + screen.Width / 2, screen.Y);
            Pill(dc, "Drag to capture  ·  click: whole screen  ·  Esc: cancel", new Point(top.X, top.Y + 28), full, t, centre: true);
        }
    }

    static void Pill(DrawingContext dc, string text, Point at, Rect bounds, Skin t, bool alignRight = false, bool centre = false)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 12.5, new SolidColorBrush(t.Text));
        double pw = ft.Width + 20, ph = ft.Height + 10;
        double x = centre ? at.X - pw / 2 : alignRight ? at.X - pw : at.X, y = at.Y;
        if (y + ph > bounds.Bottom - 6) y = at.Y - ph - 30; // no room below the selection: just inside it
        x = Math.Clamp(x, bounds.Left + 6, Math.Max(bounds.Left + 6, bounds.Right - pw - 6));
        y = Math.Clamp(y, bounds.Top + 6, Math.Max(bounds.Top + 6, bounds.Bottom - ph - 6));
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(232, t.Window.R, t.Window.G, t.Window.B)), new Pen(new SolidColorBrush(t.Stroke), 1), new Rect(x, y, pw, ph), ph / 2, ph / 2);
        dc.DrawText(ft, new Point(x + 10, y + 5));
    }
}
