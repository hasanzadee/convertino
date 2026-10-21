using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageMagick;
using Orientation = System.Windows.Controls.Orientation;
using Convertino.Services;

namespace Convertino.Editors;

/// <summary>"Add BG": drop a screenshot onto a soft gradient with padding, rounded corners and shadow.</summary>
public sealed class BackgroundEditor : Editor
{
    public sealed record Backdrop(string Name, Func<Rect, BitmapSource?, Drawing> Paint);

    public sealed record Options(int Backdrop, double Padding, double Corners, double Shadow, double? Ratio, bool Frame, bool DarkFrame);

    readonly string _src;
    readonly MagickImage _img;
    readonly BitmapSource _previewSrc;
    readonly Image _preview = new() { Stretch = Stretch.Uniform, Margin = new Thickness(18) };
    readonly FrameworkElement _controls;
    readonly WrapPanel _swatches = new();
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(16) };
    readonly List<Slider> _sliders = [];
    Options _o = new(0, 9, 30, 55, null, false, true);
    static readonly Options Defaults = new(0, 9, 30, 55, null, false, true);

    static Color H(string s) => UiKit.Hex(s);

    static Drawing Linear(Rect r, params string[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        for (int i = 0; i < stops.Length; i++) b.GradientStops.Add(new GradientStop(H(stops[i]), i / (double)(stops.Length - 1)));
        return new GeometryDrawing(b, null, new RectangleGeometry(r));
    }

    /// <summary>Soft "mesh" look: a base gradient with big blurred colour blobs.</summary>
    static Drawing Mesh(Rect r, string baseA, string baseB, params (double x, double y, double rad, string c)[] blobs)
    {
        var g = new DrawingGroup();
        g.Children.Add(Linear(r, baseA, baseB));
        foreach (var (x, y, rad, c) in blobs)
        {
            var col = H(c);
            var rb = new RadialGradientBrush(col, Color.FromArgb(0, col.R, col.G, col.B));
            double R = rad * Math.Max(r.Width, r.Height);
            g.Children.Add(new GeometryDrawing(rb, null, new EllipseGeometry(new Point(r.X + x * r.Width, r.Y + y * r.Height), R, R)));
        }
        return g;
    }

    static Drawing Solid(Rect r, string c) => new GeometryDrawing(new SolidColorBrush(H(c)), null, new RectangleGeometry(r));

    public static readonly Backdrop[] Backdrops =
    [
        new("Sonoma", (r, _) => Mesh(r, "#ff9f45", "#e8436b", (0.15, 0.1, 0.6, "#ffd36e"), (0.9, 0.95, 0.65, "#8a6cf0"), (0.85, 0.15, 0.4, "#ff6a88"))),
        new("Mocha", (r, _) => Mesh(r, "#1e1e2e", "#313244", (0.1, 0.1, 0.6, "#cba6f7"), (0.95, 0.9, 0.6, "#f5c2e7"), (0.9, 0.1, 0.45, "#89b4fa"))),
        new("Aurora", (r, _) => Mesh(r, "#0f2027", "#2c5364", (0.2, 0.9, 0.7, "#2bd2b4"), (0.85, 0.1, 0.6, "#8f5cff"))),
        new("Peach", (r, _) => Linear(r, "#ffecd2", "#fcb69f")),
        new("Lavender", (r, _) => Linear(r, "#a18cd1", "#fbc2eb")),
        new("Ocean", (r, _) => Linear(r, "#4facfe", "#00f2fe")),
        new("Mint", (r, _) => Linear(r, "#d4fc79", "#96e6a1")),
        new("Candy", (r, _) => Linear(r, "#f093fb", "#f5576c")),
        new("Dusk", (r, _) => Linear(r, "#0f0c29", "#302b63", "#24243e")),
        new("Graphite", (r, _) => Linear(r, "#2b2b30", "#121214")),
        new("Paper", (r, _) => Solid(r, "#f4f1ea")),
        new("White", (r, _) => Solid(r, "#ffffff")),
        new("Blur", (r, img) =>
        {
            // the screenshot itself, enlarged and melted into a backdrop
            if (img == null) return Solid(r, "#888888");
            var g = new DrawingGroup();
            double s = Math.Max(r.Width / img.PixelWidth, r.Height / img.PixelHeight) * 1.3;
            double w = img.PixelWidth * s, h = img.PixelHeight * s;
            g.Children.Add(new ImageDrawing(img, new Rect(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h)));
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), null, new RectangleGeometry(r)));
            return g;
        }),
        new("None", (r, _) => new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(r))),
    ];

    public BackgroundEditor(string src, MagickImage img)
    {
        _src = src;
        _img = img;
        using (var p = (MagickImage)img.Clone())
        {
            if (Math.Max(p.Width, p.Height) > 1100) p.Resize(new MagickGeometry(1100, 1100));
            _previewSrc = Wpf.ToBitmapSource(p);
        }
        RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.HighQuality);
        _debounce.Tick += (_, _) => { _debounce.Stop(); RenderPreview(); };

        BuildSwatches();
        FrameworkElement S(string label, double max, Func<Options, double> get, Func<Options, double, Options> set)
        {
            var s = UiKit.Slider(0, max, get(_o), v => { _o = set(_o, v); Kick(); }, 150);
            _sliders.Add(s);
            var row = UiKit.LabeledSlider(label, s, out var val, 64);
            s.ValueChanged += (_, e) => val.Text = $"{e.NewValue:0}";
            val.Text = $"{get(_o):0}";
            return row;
        }
        var sliders = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
        sliders.Children.Add(S(L.T("Padding"), 30, o => o.Padding, (o, v) => o with { Padding = v }));
        sliders.Children.Add(S(L.T("Corners"), 100, o => o.Corners, (o, v) => o with { Corners = v }));
        sliders.Children.Add(S(L.T("Shadow"), 100, o => o.Shadow, (o, v) => o with { Shadow = v }));

        var ratio = new StackPanel { Orientation = Orientation.Horizontal };
        ratio.Children.Add(UiKit.Label(L.T("Ratio")));
        foreach (var (label, r) in new (string, double?)[] { ("Auto", null), ("1:1", 1), ("4:3", 4 / 3.0), ("16:9", 16 / 9.0), ("9:16", 9 / 16.0), ("4:5", 0.8) })
            ratio.Children.Add(UiKit.Chip(label, "bgratio", r == null, () => { _o = _o with { Ratio = r }; Kick(); }));
        ratio.Children.Add(new Border { Width = 12 });
        ratio.Children.Add(UiKit.Toggle(L.T("Window frame"), false, v => { _o = _o with { Frame = v }; Kick(); }, L.T("macOS-style title bar")));

        _controls = new StackPanel
        {
            Children =
            {
                new DockPanel { Children = { UiKit.Label(L.T("Background"), 74), _swatches } },
                sliders,
                ratio,
            },
        };
        RenderPreview();
    }

    void BuildSwatches()
    {
        _swatches.Children.Clear();
        for (int i = 0; i < Backdrops.Length; i++)
        {
            int idx = i;
            var b = Backdrops[i];
            var dv = b.Paint(new Rect(0, 0, 1, 1), _previewSrc);
            Brush fill = b.Name == "None" ? new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)) : new DrawingBrush(dv) { Stretch = Stretch.Fill };
            _swatches.Children.Add(UiKit.Swatch(fill, idx == _o.Backdrop, () => { _o = _o with { Backdrop = idx }; BuildSwatches(); Kick(); }, L.T(b.Name), 28));
        }
    }

    void Kick() { _debounce.Stop(); _debounce.Start(); }

    void RenderPreview() => _preview.Source = Compose(_previewSrc, _o, blurRadiusScale: 1);

    /// <summary>Build the final picture. Works at preview or full resolution.</summary>
    public static BitmapSource Compose(BitmapSource img, Options o, double blurRadiusScale)
    {
        double iw = img.PixelWidth, ih = img.PixelHeight;
        double unit = Math.Max(iw, ih);
        double bar = o.Frame ? Math.Round(unit * 0.045) : 0;
        double cw = iw, ch = ih + bar;
        double pad = unit * o.Padding / 100.0;
        double W = cw + pad * 2, Hh = ch + pad * 2;
        if (o.Ratio is double r)
        {
            if (W / Hh < r) W = Hh * r; else Hh = W / r;
        }
        W = Math.Round(W); Hh = Math.Round(Hh);
        var canvas = new Rect(0, 0, W, Hh);
        var content = new Rect((W - cw) / 2, (Hh - ch) / 2, cw, ch);
        double radius = o.Corners / 100.0 * Math.Min(cw, ch) * 0.08;

        var root = new ContainerVisual();
        var bg = new DrawingVisual();
        var backdrop = Backdrops[Math.Clamp(o.Backdrop, 0, Backdrops.Length - 1)];
        using (var dc = bg.RenderOpen()) dc.DrawDrawing(backdrop.Paint(canvas, img));
        if (backdrop.Name == "Blur") bg.Effect = new BlurEffect { Radius = Math.Min(250, unit * 0.06) * blurRadiusScale, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality };
        root.Children.Add(bg);

        var fg = new DrawingVisual();
        if (o.Shadow > 0)
            fg.Effect = new DropShadowEffect
            {
                BlurRadius = Math.Min(400, unit * 0.06 * o.Shadow / 50.0),
                ShadowDepth = unit * 0.012 * o.Shadow / 50.0,
                Direction = 270,
                Opacity = Math.Min(0.75, 0.25 + o.Shadow / 200.0),
                Color = Colors.Black,
                RenderingBias = RenderingBias.Quality,
            };
        using (var dc = fg.RenderOpen())
        {
            var clip = new RectangleGeometry(content, radius, radius);
            dc.PushClip(clip);
            if (o.Frame)
            {
                var barRect = new Rect(content.X, content.Y, cw, bar);
                dc.DrawRectangle(new SolidColorBrush(o.DarkFrame ? H("#2a2a32") : H("#ececf0")), null, new Rect(content.X, content.Y, cw, ch));
                double d = bar * 0.26, gap = bar * 0.42, x0 = content.X + bar * 0.55;
                string[] lights = ["#ff5f57", "#febc2e", "#28c840"];
                for (int i = 0; i < 3; i++)
                    dc.DrawEllipse(new SolidColorBrush(H(lights[i])), null, new Point(x0 + i * (d * 2 + gap * 0.5), barRect.Y + bar / 2), d, d);
            }
            dc.DrawImage(img, new Rect(content.X, content.Y + bar, iw, ih));
            dc.Pop();
            // hairline so light screenshots don't bleed into light backgrounds
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(28, 0, 0, 0)), Math.Max(1, unit / 1200)), content, radius, radius);
        }
        root.Children.Add(fg);

        var rtb = new RenderTargetBitmap((int)W, (int)Hh, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        rtb.Freeze();
        return rtb;
    }

    internal override void DevPrepare() => _o = _o with { Ratio = 16 / 9.0, Frame = true };

    public override string Title => L.T("Add Background");
    public override FrameworkElement Preview => _preview;
    public override FrameworkElement Controls => _controls;

    public override void Reset()
    {
        _o = Defaults;
        _sliders[0].Value = Defaults.Padding;
        _sliders[1].Value = Defaults.Corners;
        _sliders[2].Value = Defaults.Shadow;
        BuildSwatches();
        RenderPreview();
    }

    public override async Task<string?> Save()
    {
        var dst = Paths.Beside(_src, "png", "(with background)");
        var full = await Task.Run(() => Wpf.ToBitmapSource(_img));
        var o = _o;
        var result = Compose(full, o, blurRadiusScale: 1);
        await Task.Run(() =>
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(result));
            using var fs = System.IO.File.Create(dst);
            enc.Save(fs);
        });
        return dst;
    }
}
