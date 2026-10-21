using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Convertino.Services;
using ImageMagick;
using Orientation = Avalonia.Layout.Orientation;
using FontWeight = Avalonia.Media.FontWeight;
using TextAlignment = Avalonia.Media.TextAlignment;
using FontStyle = Avalonia.Media.FontStyle;

namespace Convertino.Editors;

/// <summary>"Add BG": drop a screenshot onto a soft gradient with padding, rounded corners and shadow.</summary>
public sealed class BackgroundEditor : Editor
{
    public sealed record Options(int Backdrop, double Padding, double Corners, double Shadow, double? Ratio, bool Frame);
    sealed record Backdrop(string Name, Action<DrawingContext, Rect, Bitmap?> Paint);

    static readonly Options Defaults = new(0, 9, 30, 55, null, false);

    readonly string _src;
    readonly MagickImage _img;
    readonly Bitmap _previewImg, _previewBlur;
    readonly Image _preview = new() { Stretch = Stretch.Uniform, Margin = new Thickness(18) };
    readonly WrapPanel _swatches = new();
    readonly List<Slider> _sliders = [];
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(16) };
    readonly Control _controls;
    Options _o = Defaults;

    static Color H(string s) => Kit.Hex(s);

    static IBrush Linear(Rect r, params string[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = new RelativePoint(r.Left, r.Top, RelativeUnit.Absolute), EndPoint = new RelativePoint(r.Right, r.Bottom, RelativeUnit.Absolute) };
        for (int i = 0; i < stops.Length; i++) b.GradientStops.Add(new GradientStop(H(stops[i]), i / (double)(stops.Length - 1)));
        return b;
    }

    static void Mesh(DrawingContext dc, Rect r, string a, string b, params (double x, double y, double rad, string c)[] blobs)
    {
        dc.DrawRectangle(Linear(r, a, b), null, r);
        foreach (var (x, y, rad, c) in blobs)
        {
            var col = H(c);
            var rb = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative), GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative), RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
            };
            rb.GradientStops.Add(new GradientStop(col, 0));
            rb.GradientStops.Add(new GradientStop(Color.FromArgb(0, col.R, col.G, col.B), 1));
            double R = rad * Math.Max(r.Width, r.Height);
            dc.DrawEllipse(rb, null, new Point(r.X + x * r.Width, r.Y + y * r.Height), R, R);
        }
    }

    static readonly Backdrop[] Backdrops =
    [
        new("Sonoma", (dc, r, _) => Mesh(dc, r, "#ff9f45", "#e8436b", (0.15, 0.1, 0.6, "#ffd36e"), (0.9, 0.95, 0.65, "#8a6cf0"), (0.85, 0.15, 0.4, "#ff6a88"))),
        new("Mocha", (dc, r, _) => Mesh(dc, r, "#1e1e2e", "#313244", (0.1, 0.1, 0.6, "#cba6f7"), (0.95, 0.9, 0.6, "#f5c2e7"), (0.9, 0.1, 0.45, "#89b4fa"))),
        new("Aurora", (dc, r, _) => Mesh(dc, r, "#0f2027", "#2c5364", (0.2, 0.9, 0.7, "#2bd2b4"), (0.85, 0.1, 0.6, "#8f5cff"))),
        new("Peach", (dc, r, _) => dc.DrawRectangle(Linear(r, "#ffecd2", "#fcb69f"), null, r)),
        new("Lavender", (dc, r, _) => dc.DrawRectangle(Linear(r, "#a18cd1", "#fbc2eb"), null, r)),
        new("Ocean", (dc, r, _) => dc.DrawRectangle(Linear(r, "#4facfe", "#00f2fe"), null, r)),
        new("Mint", (dc, r, _) => dc.DrawRectangle(Linear(r, "#d4fc79", "#96e6a1"), null, r)),
        new("Candy", (dc, r, _) => dc.DrawRectangle(Linear(r, "#f093fb", "#f5576c"), null, r)),
        new("Dusk", (dc, r, _) => dc.DrawRectangle(Linear(r, "#0f0c29", "#302b63", "#24243e"), null, r)),
        new("Graphite", (dc, r, _) => dc.DrawRectangle(Linear(r, "#2b2b30", "#121214"), null, r)),
        new("Paper", (dc, r, _) => dc.DrawRectangle(Draw.B(H("#f4f1ea")), null, r)),
        new("White", (dc, r, _) => dc.DrawRectangle(Brushes.White, null, r)),
        new("Blur", (dc, r, blur) =>
        {
            if (blur == null) { dc.DrawRectangle(Brushes.Gray, null, r); return; }
            double s = Math.Max(r.Width / blur.Size.Width, r.Height / blur.Size.Height) * 1.3;
            double w = blur.Size.Width * s, h = blur.Size.Height * s;
            dc.DrawImage(blur, new Rect(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h));
            dc.DrawRectangle(Draw.B(Color.FromArgb(40, 0, 0, 0)), null, r);
        }),
        new("None", (_, _, _) => { }),
    ];

    public BackgroundEditor(string src, MagickImage img)
    {
        _src = src; _img = img;
        using (var p = EditOps.Downscaled(img, 1100)) _previewImg = Px.ToBitmap(p);
        _previewBlur = BlurredCopy(img);
        _debounce.Tick += (_, _) => { _debounce.Stop(); RenderPreview(); };
        BuildSwatches();

        Control S(string label, double max, Func<Options, double> get, Func<Options, double, Options> set)
        {
            TextBlock val = null!;
            var s = Kit.Slider(0, max, get(_o), v => { _o = set(_o, v); val.Text = $"{v:0}"; Kick(); });
            _sliders.Add(s);
            var row = Kit.LabeledSlider(label, s, out val, 64);
            val.Text = $"{get(_o):0}";
            return row;
        }
        var sliders = new Avalonia.Controls.Primitives.UniformGrid { Columns = 3 };
        sliders.Children.Add(S(L.T("Padding"), 30, o => o.Padding, (o, v) => o with { Padding = v }));
        sliders.Children.Add(S(L.T("Corners"), 100, o => o.Corners, (o, v) => o with { Corners = v }));
        sliders.Children.Add(S(L.T("Shadow"), 100, o => o.Shadow, (o, v) => o with { Shadow = v }));
        var ratio = new StackPanel { Orientation = Orientation.Horizontal };
        ratio.Children.Add(Kit.Label(L.T("Ratio")));
        foreach (var (label, r) in new (string, double?)[] { ("Auto", null), ("1:1", 1), ("4:3", 4 / 3.0), ("16:9", 16 / 9.0), ("9:16", 9 / 16.0), ("4:5", 0.8) })
            ratio.Children.Add(Kit.Chip(label, "bgratio", r == null, () => { _o = _o with { Ratio = r }; Kick(); }));
        ratio.Children.Add(Kit.Toggle(L.T("Window frame"), false, v => { _o = _o with { Frame = v }; Kick(); }));
        var bgRow = new DockPanel();
        bgRow.Children.Add(Kit.Label(L.T("Background"), 74));
        bgRow.Children.Add(_swatches);
        _controls = new StackPanel { Children = { bgRow, sliders, ratio } };
        RenderPreview();
    }

    static Bitmap BlurredCopy(MagickImage img)
    {
        using var b = EditOps.Downscaled(img, 360);
        b.Blur(0, 14);
        return Px.ToBitmap(b);
    }

    void BuildSwatches()
    {
        _swatches.Children.Clear();
        for (int i = 0; i < Backdrops.Length; i++)
        {
            int idx = i;
            var rtb = new RenderTargetBitmap(new PixelSize(28, 28), new Vector(96, 96));
            using (var dc = rtb.CreateDrawingContext(true)) Backdrops[i].Paint(dc, new Rect(0, 0, 28, 28), _previewBlur);
            IBrush fill = Backdrops[i].Name == "None" ? Draw.B(Color.FromArgb(40, 128, 128, 128)) : new ImageBrush(rtb) { Stretch = Stretch.Fill };
            _swatches.Children.Add(Kit.Swatch(fill, idx == _o.Backdrop, () => { _o = _o with { Backdrop = idx }; BuildSwatches(); Kick(); }, L.T(Backdrops[i].Name), 28));
        }
    }

    void Kick() { _debounce.Stop(); _debounce.Start(); }

    void RenderPreview() => _preview.Source = Compose(_previewImg, _previewBlur, _o);

    static RenderTargetBitmap Compose(Bitmap img, Bitmap blur, Options o)
    {
        double iw = img.PixelSize.Width, ih = img.PixelSize.Height, unit = Math.Max(iw, ih);
        double bar = o.Frame ? Math.Round(unit * 0.045) : 0;
        double cw = iw, ch = ih + bar, pad = unit * o.Padding / 100.0;
        double W = cw + pad * 2, Hh = ch + pad * 2;
        if (o.Ratio is double r) { if (W / Hh < r) W = Hh * r; else Hh = W / r; }
        W = Math.Round(W); Hh = Math.Round(Hh);
        var canvas = new Rect(0, 0, W, Hh);
        var content = new Rect((W - cw) / 2, (Hh - ch) / 2, cw, ch);
        double radius = o.Corners / 100.0 * Math.Min(cw, ch) * 0.08;
        var rtb = new RenderTargetBitmap(new PixelSize((int)W, (int)Hh), new Vector(96, 96));
        using (var dc = rtb.CreateDrawingContext(true))
        {
            Backdrops[Math.Clamp(o.Backdrop, 0, Backdrops.Length - 1)].Paint(dc, canvas, blur);
            var rr = new RoundedRect(content, radius);
            if (o.Shadow > 0)
            {
                var shadow = new BoxShadow
                {
                    Blur = Math.Min(400, unit * 0.06 * o.Shadow / 50.0),
                    OffsetY = unit * 0.012 * o.Shadow / 50.0,
                    Color = Color.FromArgb((byte)(Math.Min(0.75, 0.25 + o.Shadow / 200.0) * 255), 0, 0, 0),
                };
                dc.DrawRectangle(Brushes.Black, null, rr, new BoxShadows(shadow));
            }
            using (dc.PushClip(rr))
            {
                if (o.Frame)
                {
                    dc.DrawRectangle(Draw.B(H("#2a2a32")), null, content);
                    double d = bar * 0.26, gap = bar * 0.42, x0 = content.X + bar * 0.55;
                    string[] lights = ["#ff5f57", "#febc2e", "#28c840"];
                    for (int i = 0; i < 3; i++) dc.DrawEllipse(Draw.B(H(lights[i])), null, new Point(x0 + i * (d * 2 + gap * 0.5), content.Y + bar / 2), d, d);
                }
                dc.DrawImage(img, new Rect(content.X, content.Y + bar, iw, ih));
            }
            dc.DrawRectangle(null, new Pen(Draw.B(Color.FromArgb(28, 0, 0, 0)), Math.Max(1, unit / 1200)), rr);
        }
        return rtb;
    }

    internal override void DevPrepare() => _o = _o with { Ratio = 16 / 9.0, Frame = true };

    public override string Title => L.T("Add Background");
    public override Control Preview => _preview;
    public override Control Controls => _controls;

    public override void Reset()
    {
        _o = Defaults;
        _sliders[0].Value = Defaults.Padding; _sliders[1].Value = Defaults.Corners; _sliders[2].Value = Defaults.Shadow;
        BuildSwatches();
        RenderPreview();
    }

    public override async Task<string?> Save()
    {
        var full = await Task.Run(() => Px.ToBitmap(_img));
        using var result = Compose(full, _previewBlur, _o);
        var (px, w, h) = Px.Pixels(result);
        return await Task.Run(() => EditOps.SaveRendered(_src, null, px, w, h, "(with background)", "png"));
    }
}
