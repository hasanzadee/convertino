using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Convertino.Services;
using Convertino.Ui;
using ImageMagick;
using Orientation = Avalonia.Layout.Orientation;
using FontWeight = Avalonia.Media.FontWeight;
using TextAlignment = Avalonia.Media.TextAlignment;
using FontStyle = Avalonia.Media.FontStyle;

namespace Convertino.Editors;

public static class EditorHost
{
    public static Func<Job, Task>? ById(string id) => id switch
    {
        "crop" => j => Open(j, (s, i) => new CropEditor(s, i)),
        "redact" => j => Open(j, (s, i) => new RedactEditor(s, i)),
        "adjust" => j => Open(j, (s, i) => new AdjustEditor(s, i)),
        "annotate" => j => Open(j, (s, i) => new AnnotateEditor(s, i)),
        "addbg" => j => Open(j, (s, i) => new BackgroundEditor(s, i)),
        _ => null,
    };

    public static async Task<List<string>> DevSaveAll(string src)
    {
        var made = new List<string>();
        foreach (var make in new Func<string, MagickImage, Editor>[]
        {
            (s, i) => new CropEditor(s, i), (s, i) => new RedactEditor(s, i), (s, i) => new AnnotateEditor(s, i),
            (s, i) => new AdjustEditor(s, i), (s, i) => new BackgroundEditor(s, i),
        })
        {
            using var img = Images.Load(src);
            var ed = make(src, img);
            ed.DevPrepare();
            try { made.Add(await ed.Save() ?? "(null)"); }
            catch (Exception ex) { made.Add($"{ed.Title} FAILED: {ex.Message}"); Log.Error("dev save " + ed.Title, ex); }
        }
        return made;
    }

    static async Task Open(Job job, Func<string, MagickImage, Editor> make)
    {
        var src = job.Inputs[0];
        MagickImage img;
        try { img = await Task.Run(() => Images.Load(src)); }
        catch (Exception ex) { throw new IOException(L.T("Couldn't open this image: {0}", ex.Message), ex); }
        using (img)
        {
            var editor = make(src, img);
            double aspect = img.Width / (double)img.Height;
            double chrome = 150 + 70;
            double h = Math.Clamp(Math.Min(img.Height, 820) + chrome, 520, 900);
            double w = Math.Clamp((h - chrome) * aspect + 32, 640, 1300);
            var win = new EditorWindow(editor, $"{Path.GetFileName(src)} · {img.Width}×{img.Height}", new Size(w, h));
            win.Show();
            win.Activate();
            var result = await win.Result;
            if (result != null)
            {
                job.Outputs.Add(result);
                job.Summary = $"{editor.Title} · {Paths.HumanSize(Paths.SizeOf(result))}";
            }
            else job.Silent = true;
        }
    }
}

static class Draw
{
    public static IBrush B(Color c) => new ImmutableSolidColorBrush(c);
    public static Rect R(RectD r) => new(r.X, r.Y, r.Width, r.Height);
    public static RectD D(Rect r) => new(r.X, r.Y, r.Width, r.Height);
    public static Rect Norm(Point a, Point b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}

// ======================================================================= crop

sealed class CropSurface : ImageSurface
{
    public Rect Crop;
    public double? Ratio;
    public event Action? Changed;

    enum Drag { None, Move, New, L, R, T, B, TL, TR, BL, BR }
    Drag _drag;
    Point _anchor;
    Rect _start;

    protected override void DrawOverlay(DrawingContext dc, Rect r)
    {
        var v = ToView(Crop);
        var dim = Draw.B(Color.FromArgb(150, 0, 0, 0));
        dc.DrawRectangle(dim, null, new Rect(r.Left, r.Top, r.Width, Math.Max(0, v.Top - r.Top)));
        dc.DrawRectangle(dim, null, new Rect(r.Left, v.Bottom, r.Width, Math.Max(0, r.Bottom - v.Bottom)));
        dc.DrawRectangle(dim, null, new Rect(r.Left, v.Top, Math.Max(0, v.Left - r.Left), v.Height));
        dc.DrawRectangle(dim, null, new Rect(v.Right, v.Top, Math.Max(0, r.Right - v.Right), v.Height));
        var thin = new Pen(Draw.B(Color.FromArgb(90, 255, 255, 255)), 1);
        for (int i = 1; i < 3; i++)
        {
            double x = v.Left + v.Width * i / 3, y = v.Top + v.Height * i / 3;
            dc.DrawLine(thin, new Point(x, v.Top), new Point(x, v.Bottom));
            dc.DrawLine(thin, new Point(v.Left, y), new Point(v.Right, y));
        }
        dc.DrawRectangle(null, new Pen(Brushes.White, 1.5), v);
        var bracket = new Pen(Brushes.White, 4, lineCap: PenLineCap.Round);
        double L = Math.Min(20, Math.Min(v.Width, v.Height) / 3);
        void Corner(Point p, double dx, double dy) { dc.DrawLine(bracket, p, new Point(p.X + dx * L, p.Y)); dc.DrawLine(bracket, p, new Point(p.X, p.Y + dy * L)); }
        Corner(v.TopLeft, 1, 1); Corner(v.TopRight, -1, 1); Corner(v.BottomLeft, 1, -1); Corner(v.BottomRight, -1, -1);
    }

    Drag Hit(Point p)
    {
        var v = ToView(Crop);
        const double k = 14;
        bool nl = Math.Abs(p.X - v.Left) < k, nr = Math.Abs(p.X - v.Right) < k, nt = Math.Abs(p.Y - v.Top) < k, nb = Math.Abs(p.Y - v.Bottom) < k;
        bool inX = p.X > v.Left - k && p.X < v.Right + k, inY = p.Y > v.Top - k && p.Y < v.Bottom + k;
        if (nl && nt) return Drag.TL; if (nr && nt) return Drag.TR; if (nl && nb) return Drag.BL; if (nr && nb) return Drag.BR;
        if (nl && inY) return Drag.L; if (nr && inY) return Drag.R; if (nt && inX) return Drag.T; if (nb && inX) return Drag.B;
        return v.Contains(p) ? Drag.Move : Drag.New;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        _drag = Hit(p);
        _anchor = ToImage(p);
        _start = Crop;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) { _drag = Drag.None; e.Pointer.Capture(null); }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_drag == Drag.None)
        {
            Cursor = new Cursor(Hit(p) switch
            {
                Drag.TL or Drag.BR => StandardCursorType.TopLeftCorner,
                Drag.TR or Drag.BL => StandardCursorType.TopRightCorner,
                Drag.L or Drag.R => StandardCursorType.SizeWestEast,
                Drag.T or Drag.B => StandardCursorType.SizeNorthSouth,
                Drag.Move => StandardCursorType.SizeAll,
                _ => StandardCursorType.Cross,
            });
            return;
        }
        double W = ImageSize.Width, H = ImageSize.Height;
        var ip = ToImage(p);
        ip = new Point(Math.Clamp(ip.X, 0, W), Math.Clamp(ip.Y, 0, H));
        var c = _start;
        if (_drag == Drag.Move)
        {
            Crop = new Rect(Math.Clamp(c.X + ip.X - _anchor.X, 0, W - c.Width), Math.Clamp(c.Y + ip.Y - _anchor.Y, 0, H - c.Height), c.Width, c.Height);
            Changed?.Invoke(); InvalidateVisual(); return;
        }
        double l = c.Left, t = c.Top, r = c.Right, b = c.Bottom;
        switch (_drag)
        {
            case Drag.New: l = _anchor.X; t = _anchor.Y; r = ip.X; b = ip.Y; break;
            case Drag.L: l = ip.X; break;
            case Drag.R: r = ip.X; break;
            case Drag.T: t = ip.Y; break;
            case Drag.B: b = ip.Y; break;
            case Drag.TL: l = ip.X; t = ip.Y; break;
            case Drag.TR: r = ip.X; t = ip.Y; break;
            case Drag.BL: l = ip.X; b = ip.Y; break;
            case Drag.BR: r = ip.X; b = ip.Y; break;
        }
        var rect = Draw.Norm(new Point(l, t), new Point(r, b));
        if (Ratio is double ratio && rect.Width > 0 && rect.Height > 0)
        {
            double w = rect.Width, h = rect.Height;
            if (_drag is Drag.T or Drag.B || (_drag is not (Drag.L or Drag.R) && h * ratio > w)) w = h * ratio; else h = w / ratio;
            double ax = (_drag is Drag.L or Drag.TL or Drag.BL) || (_drag == Drag.New && ip.X < _anchor.X) ? rect.Right - w : rect.Left;
            double ay = (_drag is Drag.T or Drag.TL or Drag.TR) || (_drag == Drag.New && ip.Y < _anchor.Y) ? rect.Bottom - h : rect.Top;
            rect = new Rect(ax, ay, w, h);
            var bounded = rect.Intersect(new Rect(0, 0, W, H));
            if (bounded != rect) { double w2 = Math.Min(bounded.Width, bounded.Height * ratio); rect = new Rect(bounded.X, bounded.Y, w2, w2 / ratio); }
        }
        rect = rect.Intersect(new Rect(0, 0, W, H));
        if (rect.Width >= 4 && rect.Height >= 4) Crop = rect;
        Changed?.Invoke();
        InvalidateVisual();
    }

    public void ApplyRatio(double? ratio)
    {
        Ratio = ratio;
        if (ratio is double r)
        {
            double W = ImageSize.Width, H = ImageSize.Height, w = W, h = W / r;
            if (h > H) { h = H; w = H * r; }
            w *= 0.9; h *= 0.9;
            var cc = Crop.Center;
            Crop = new Rect(Math.Clamp(cc.X - w / 2, 0, W - w), Math.Clamp(cc.Y - h / 2, 0, H - h), w, h);
        }
        Changed?.Invoke();
        InvalidateVisual();
    }
}

public sealed class CropEditor : Editor
{
    readonly string _src;
    readonly MagickImage _img;
    readonly CropSurface _surface = new();
    readonly TextBlock _size = Kit.Label("");
    readonly Control _controls;

    public CropEditor(string src, MagickImage img)
    {
        _src = src; _img = img;
        using (var p = EditOps.Downscaled(img, 2200)) _surface.Image = Px.ToBitmap(p);
        _surface.ImageSize = new Size(img.Width, img.Height);
        _surface.Crop = new Rect(0, 0, img.Width, img.Height);
        _surface.Changed += Update;
        Update();
        var chips = new WrapPanel();
        void R(string label, double? r, bool on = false) => chips.Children.Add(Kit.Chip(label, "ratio", on, () => _surface.ApplyRatio(r)));
        R(L.T("Free"), null, true); R(L.T("Original"), img.Width / (double)img.Height); R("1:1", 1); R("4:3", 4 / 3.0); R("3:2", 1.5); R("16:9", 16 / 9.0); R("9:16", 9 / 16.0);
        var d = new DockPanel();
        DockPanel.SetDock(_size, Dock.Right);
        d.Children.Add(_size);
        d.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { Kit.Label(L.T("Aspect")), chips } });
        _controls = d;
    }

    void Update() => _size.Text = $"{(int)Math.Round(_surface.Crop.Width)} × {(int)Math.Round(_surface.Crop.Height)} px";
    internal override void DevPrepare() => _surface.Crop = new Rect(_img.Width * .25, _img.Height * .25, _img.Width * .5, _img.Height * .4);
    public override string Title => L.T("Crop");
    public override Control Preview => _surface;
    public override Control Controls => _controls;
    public override void Reset() { _surface.Crop = new Rect(0, 0, _img.Width, _img.Height); _surface.Ratio = null; Update(); _surface.InvalidateVisual(); }
    public override Task<string?> Save() { var c = Draw.D(_surface.Crop); return Task.Run<string?>(() => EditOps.Crop(_src, _img, c)); }
}

// ======================================================================= redact

sealed class RedactSurface : ImageSurface
{
    public List<Rect> Boxes { get; } = [];
    public RedactStyle Mode = RedactStyle.Pixelate;
    public Bitmap? Pixelated, Blurred;
    public event Action? Changed;
    Point? _start;
    Rect _live;

    protected override void DrawOverlay(DrawingContext dc, Rect r)
    {
        var all = Boxes.ToList();
        if (_start != null && _live.Width > 0) all.Add(_live);
        foreach (var b in all)
        {
            var v = ToView(b).Intersect(r);
            if (v.Width <= 0) continue;
            var fx = Mode switch { RedactStyle.Pixelate => Pixelated, RedactStyle.Blur => Blurred, _ => null };
            if (fx != null) using (dc.PushClip(v)) dc.DrawImage(fx, r);
            else dc.DrawRectangle(Brushes.Black, null, v);
            dc.DrawRectangle(null, new Pen(Draw.B(Color.FromArgb(150, 255, 255, 255)), 1, dashStyle: DashStyle.Dash), v);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var ip = ToImage(e.GetPosition(this));
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            int hit = Boxes.FindLastIndex(b => b.Contains(ip));
            if (hit >= 0) { Boxes.RemoveAt(hit); Changed?.Invoke(); InvalidateVisual(); }
            return;
        }
        _start = Clamp(ip);
        _live = default;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_start == null) return;
        _live = Draw.Norm(_start.Value, Clamp(ToImage(e.GetPosition(this))));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        if (_start != null && _live.Width * ViewScale > 4 && _live.Height * ViewScale > 4) { Boxes.Add(_live); Changed?.Invoke(); }
        _start = null;
        InvalidateVisual();
    }

    Point Clamp(Point p) => new(Math.Clamp(p.X, 0, ImageSize.Width), Math.Clamp(p.Y, 0, ImageSize.Height));
}

public sealed class RedactEditor : Editor
{
    readonly string _src;
    readonly MagickImage _img;
    readonly RedactSurface _surface = new();
    readonly TextBlock _count = Kit.Label(L.T("Drag over anything private"));
    readonly Button _detect;
    readonly Control _controls;

    public RedactEditor(string src, MagickImage img)
    {
        _src = src; _img = img;
        using var preview = EditOps.Downscaled(img, 2200);
        _surface.Image = Px.ToBitmap(preview);
        _surface.ImageSize = new Size(img.Width, img.Height);
        using (var px = EditOps.Pixelate(preview, EditOps.BlockFor(preview))) _surface.Pixelated = Px.ToBitmap(px);
        using (var bl = EditOps.BlurAll(preview)) _surface.Blurred = Px.ToBitmap(bl);
        _surface.Changed += () => _count.Text = _surface.Boxes.Count == 0 ? L.T("Drag over anything private") : L.N(_surface.Boxes.Count, "{0} area", "{0} areas") + " · " + L.T("right-click removes");
        _detect = Kit.Btn(L.T("Find text"), async () => await Detect());
        var d = new DockPanel();
        DockPanel.SetDock(_count, Dock.Right);
        d.Children.Add(_count);
        d.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                Kit.Label(L.T("Style")),
                Kit.Chip(L.T("Pixelate"), "rs", true, () => { _surface.Mode = RedactStyle.Pixelate; _surface.InvalidateVisual(); }),
                Kit.Chip(L.T("Blur"), "rs", false, () => { _surface.Mode = RedactStyle.Blur; _surface.InvalidateVisual(); }),
                Kit.Chip(L.T("Black"), "rs", false, () => { _surface.Mode = RedactStyle.Black; _surface.InvalidateVisual(); }),
                _detect,
                Kit.Btn(L.T("Undo"), Undo),
            },
        });
        _controls = d;
    }

    void Undo() { if (_surface.Boxes.Count > 0) { _surface.Boxes.RemoveAt(_surface.Boxes.Count - 1); _surface.InvalidateVisual(); } }

    async Task Detect()
    {
        _detect.IsEnabled = false;
        _detect.Content = L.T("Reading…");
        try
        {
            double k = 1;
            byte[] png;
            using (var c = (MagickImage)_img.Clone())
            {
                if (Math.Max(c.Width, c.Height) < 1000) { k = 2; c.Resize(new Percentage(200)); }
                png = c.ToByteArray(MagickFormat.Png);
            }
            var res = await Platform.Current.Ocr(png);
            foreach (var line in res.Lines)
            {
                if (line.IsEmpty) continue;
                double pad = line.Height / k * 0.18;
                _surface.Boxes.Add(new Rect(line.X / k - pad, line.Y / k - pad, line.Width / k + pad * 2, line.Height / k + pad * 2));
            }
            _surface.InvalidateVisual();
            _count.Text = res.Lines.Count == 0 ? L.T("No text found") : L.N(_surface.Boxes.Count, "{0} area", "{0} areas") + " · " + L.T("right-click removes");
        }
        catch (Exception ex) { _count.Text = ex.Message; }
        _detect.Content = L.T("Find text");
        _detect.IsEnabled = true;
    }

    internal override void DevPrepare() => _surface.Boxes.Add(new Rect(_img.Width * .1, _img.Height * .1, _img.Width * .4, _img.Height * .2));
    public override string Title => L.T("Redact");
    public override Control Preview => _surface;
    public override Control Controls => _controls;
    public override void Reset() { _surface.Boxes.Clear(); _surface.InvalidateVisual(); }
    public override void OnKey(KeyEventArgs e) { if (e.Key == Key.Z && Shortcut(e)) { Undo(); e.Handled = true; } }

    public override Task<string?> Save()
    {
        if (_surface.Boxes.Count == 0) return Task.FromResult<string?>(null);
        var boxes = _surface.Boxes.Select(Draw.D).ToList();
        var mode = _surface.Mode;
        return Task.Run<string?>(() => EditOps.Redact(_src, _img, boxes, mode));
    }
}

// ======================================================================= adjust

public sealed class AdjustEditor : Editor
{
    readonly string _src;
    readonly MagickImage _img, _preview;
    readonly ImageSurface _surface = new() { Cursor = Cursor.Default };
    readonly Control _controls;
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(40) };
    readonly List<Slider> _sliders = [];
    AdjustParams _p = AdjustParams.Default;
    int _gen;

    public AdjustEditor(string src, MagickImage img)
    {
        _src = src; _img = img;
        _preview = EditOps.Downscaled(img, 1400);
        _surface.Image = Px.ToBitmap(_preview);
        _surface.ImageSize = new Size(_preview.Width, _preview.Height);
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await Render(); };

        Control S(string label, double min, double max, Func<AdjustParams, double> get, Func<AdjustParams, double, AdjustParams> set)
        {
            TextBlock val = null!;
            var s = Kit.Slider(min, max, get(_p), v => { _p = set(_p, v); val.Text = $"{v:0}"; Kick(); });
            _sliders.Add(s);
            var row = Kit.LabeledSlider(label, s, out val);
            val.Text = $"{get(_p):0}";
            return row;
        }
        var grid = new Avalonia.Controls.Primitives.UniformGrid { Columns = 3 };
        grid.Children.Add(S(L.T("Brightness"), -60, 60, p => p.Brightness, (p, v) => p with { Brightness = v }));
        grid.Children.Add(S(L.T("Contrast"), -60, 60, p => p.Contrast, (p, v) => p with { Contrast = v }));
        grid.Children.Add(S(L.T("Saturation"), -100, 100, p => p.Saturation, (p, v) => p with { Saturation = v }));
        grid.Children.Add(S(L.T("Warmth"), -100, 100, p => p.Warmth, (p, v) => p with { Warmth = v }));
        grid.Children.Add(S(L.T("Sharpness"), 0, 100, p => p.Sharpness, (p, v) => p with { Sharpness = v }));
        grid.Children.Add(S(L.T("Vignette"), 0, 100, p => p.Vignette, (p, v) => p with { Vignette = v }));
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        row2.Children.Add(Kit.Label(L.T("Size")));
        foreach (var pct in new[] { 100, 75, 50, 25 })
            row2.Children.Add(Kit.Chip(pct == 100 ? $"100% · {img.Width}×{img.Height}" : $"{pct}%", "scale", pct == 100, () => { _p = _p with { Scale = pct }; Kick(); }));
        row2.Children.Add(Kit.Icon("rotate", L.T("Rotate right"), () => { _p = _p with { Rotate = (_p.Rotate + 90) % 360 }; Kick(); }));
        row2.Children.Add(Kit.Icon("flip", L.T("Flip horizontal"), () => { _p = _p with { FlipH = !_p.FlipH }; Kick(); }));
        _controls = new StackPanel { Children = { grid, row2 } };
    }

    void Kick() { _debounce.Stop(); _debounce.Start(); }

    async Task Render()
    {
        int gen = ++_gen;
        var p = _p;
        var (px, w, h) = await Task.Run(() =>
        {
            using var c = (MagickImage)_preview.Clone();
            EditOps.Adjust(c, p, preview: true);
            return Images.ToBgra(c);
        });
        if (gen != _gen) return;
        _surface.Image = Px.ToBitmap(px, w, h);
        _surface.ImageSize = new Size(w, h);
        _surface.InvalidateVisual();
    }

    internal override void DevPrepare() => _p = _p with { Brightness = 15, Contrast = 20, Saturation = 30, Warmth = 40, Sharpness = 50, Vignette = 60, Rotate = 90, Scale = 50 };
    public override string Title => L.T("Adjust");
    public override Control Preview => _surface;
    public override Control Controls => _controls;
    public override void Reset() { _p = AdjustParams.Default; foreach (var s in _sliders) s.Value = 0; _ = Render(); }
    public override Task<string?> Save() { var p = _p; return Task.Run<string?>(() => EditOps.SaveAdjusted(_src, _img, p)); }
}
