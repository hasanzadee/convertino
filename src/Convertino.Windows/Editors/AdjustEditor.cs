using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ImageMagick;
using Orientation = System.Windows.Controls.Orientation;
using Convertino.Services;

namespace Convertino.Editors;

public sealed class AdjustEditor : Editor
{
    sealed record Params(double Brightness, double Contrast, double Saturation, double Warmth, double Sharpness, double Vignette, int Rotate, bool FlipH, bool FlipV, int Scale)
    {
        public static readonly Params Default = new(0, 0, 0, 0, 0, 0, 0, false, false, 100);
        public bool IsIdentity => this == Default;
    }

    readonly string _src;
    readonly MagickImage _img;
    readonly MagickImage _preview;
    readonly ImageSurface _surface = new();
    readonly FrameworkElement _controls;
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(35) };
    readonly List<(Slider s, TextBlock v, Func<double, string> fmt)> _sliders = [];
    readonly List<RadioButton> _scaleChips = [];
    Params _p = Params.Default;
    int _renderGen;

    public AdjustEditor(string src, MagickImage img)
    {
        _src = src;
        _img = img;
        _preview = (MagickImage)img.Clone();
        if (Math.Max(_preview.Width, _preview.Height) > 1400) _preview.Resize(new MagickGeometry(1400, 1400));
        _surface.Image = Wpf.ToBitmapSource(_preview);
        _surface.ImageSize = new Size(_preview.Width, _preview.Height);
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await Render(); };

        FrameworkElement S(string label, double min, double max, Func<Params, double> get, Func<Params, double, Params> set, Func<double, string> fmt)
        {
            TextBlock val = null!;
            var slider = UiKit.Slider(min, max, get(_p), v => { _p = set(_p, v); val.Text = fmt(v); Kick(); }, 150);
            var row = UiKit.LabeledSlider(label, slider, out val);
            val.Text = fmt(get(_p));
            _sliders.Add((slider, val, fmt));
            slider.MouseDoubleClick += (_, _) => slider.Value = 0;
            return row;
        }
        string Signed(double v) => v == 0 ? "0" : $"{(v > 0 ? "+" : "")}{v:0}";

        var grid = new UniformGrid3();
        grid.Children.Add(S(L.T("Brightness"), -60, 60, p => p.Brightness, (p, v) => p with { Brightness = v }, Signed));
        grid.Children.Add(S(L.T("Contrast"), -60, 60, p => p.Contrast, (p, v) => p with { Contrast = v }, Signed));
        grid.Children.Add(S(L.T("Saturation"), -100, 100, p => p.Saturation, (p, v) => p with { Saturation = v }, Signed));
        grid.Children.Add(S(L.T("Warmth"), -100, 100, p => p.Warmth, (p, v) => p with { Warmth = v }, Signed));
        grid.Children.Add(S(L.T("Sharpness"), 0, 100, p => p.Sharpness, (p, v) => p with { Sharpness = v }, v => $"{v:0}"));
        grid.Children.Add(S(L.T("Vignette"), 0, 100, p => p.Vignette, (p, v) => p with { Vignette = v }, v => $"{v:0}"));

        var scale = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        scale.Children.Add(UiKit.Label(L.T("Size")));
        foreach (var pct in new[] { 100, 75, 50, 25 })
        {
            var chip = UiKit.Chip(pct == 100 ? $"100% · {img.Width}×{img.Height}" : $"{pct}%", "scale", pct == 100, () => { _p = _p with { Scale = pct }; Kick(); });
            _scaleChips.Add(chip);
            scale.Children.Add(chip);
        }
        scale.Children.Add(new Border { Width = 14 });
        scale.Children.Add(UiKit.Icon("", L.T("Rotate left"), () => { _p = _p with { Rotate = (_p.Rotate + 270) % 360 }; Kick(); }));
        scale.Children.Add(UiKit.Icon("", L.T("Rotate right"), () => { _p = _p with { Rotate = (_p.Rotate + 90) % 360 }; Kick(); }));
        ((Button)scale.Children[^2]).RenderTransform = new System.Windows.Media.ScaleTransform(-1, 1, 17, 16);
        scale.Children.Add(UiKit.Icon("", L.T("Flip horizontal"), () => { _p = _p with { FlipH = !_p.FlipH }; Kick(); }));
        scale.Children.Add(UiKit.Icon("", L.T("Flip vertical"), () => { _p = _p with { FlipV = !_p.FlipV }; Kick(); }));
        ((Button)scale.Children[^1]).RenderTransform = new System.Windows.Media.RotateTransform(90, 17, 16);

        _controls = new StackPanel { Children = { grid, scale } };
    }

    sealed class UniformGrid3 : System.Windows.Controls.Primitives.UniformGrid { public UniformGrid3() { Columns = 3; } }

    void Kick() { _debounce.Stop(); _debounce.Start(); }

    async Task Render()
    {
        int gen = ++_renderGen;
        var p = _p;
        var bmp = await Task.Run(() =>
        {
            using var c = (MagickImage)_preview.Clone();
            Apply(c, p, preview: true);
            return Wpf.ToBitmapSource(c);
        });
        if (gen != _renderGen) return;
        _surface.Image = bmp;
        _surface.ImageSize = new Size(bmp.PixelWidth, bmp.PixelHeight);
        _surface.InvalidateVisual();
        var outW = (int)Math.Round((p.Rotate % 180 == 0 ? _img.Width : _img.Height) * p.Scale / 100.0);
        var outH = (int)Math.Round((p.Rotate % 180 == 0 ? _img.Height : _img.Width) * p.Scale / 100.0);
        Shell?.SetSubtitle($"{System.IO.Path.GetFileName(_src)} · {outW}×{outH}");
    }

    static void Apply(MagickImage img, Params p, bool preview)
    {
        if (p.Rotate != 0) img.Rotate(p.Rotate);
        if (p.FlipH) img.Flop();
        if (p.FlipV) img.Flip();
        if (!preview && p.Scale != 100) img.Resize(new Percentage(p.Scale));
        if (p.Brightness != 0 || p.Contrast != 0)
            img.BrightnessContrast(new Percentage(p.Brightness * 0.6), new Percentage(p.Contrast * 0.6));
        if (p.Saturation != 0) img.Modulate(new Percentage(100), new Percentage(100 + p.Saturation), new Percentage(100));
        if (p.Warmth != 0)
        {
            double k = p.Warmth / 100.0 * 0.12;
            img.Evaluate(Channels.Red, EvaluateOperator.Multiply, 1 + k);
            img.Evaluate(Channels.Blue, EvaluateOperator.Multiply, 1 - k);
        }
        if (p.Sharpness > 0)
        {
            double scale = Math.Max(img.Width, img.Height) / 1400.0;
            img.UnsharpMask(0, Math.Max(0.6, 1.2 * scale), p.Sharpness / 100.0 * 1.6, 0.01);
        }
        if (p.Vignette > 0)
        {
            using var mask = new MagickImage("radial-gradient:white-black", img.Width, img.Height);
            mask.Evaluate(Channels.All, EvaluateOperator.Pow, 2.2);
            mask.Evaluate(Channels.All, EvaluateOperator.Multiply, p.Vignette / 100.0 * 0.85);
            mask.Negate();
            img.Composite(mask, CompositeOperator.Multiply, Channels.RGB);
        }
    }

    internal override void DevPrepare() => _p = _p with { Brightness = 15, Contrast = 20, Saturation = 30, Warmth = 40, Sharpness = 50, Vignette = 60, Rotate = 90, Scale = 50 };

    public override string Title => L.T("Adjust");
    public override FrameworkElement Preview => _surface;
    public override FrameworkElement Controls => _controls;

    public override void Reset()
    {
        _p = Params.Default;
        foreach (var (s, v, fmt) in _sliders) { s.Value = 0; v.Text = fmt(0); }
        _scaleChips[0].IsChecked = true;
        _ = Render();
    }

    public override async Task<string?> Save()
    {
        var p = _p;
        var ext = CropEditor.Kinds(_src);
        var dst = Paths.Beside(_src, ext, "(edited)");
        await Task.Run(() =>
        {
            using var c = (MagickImage)_img.Clone();
            Apply(c, p, preview: false);
            Images.PrepareFor(c, ext, ext == "jpg" ? 94 : null);
            c.Write(dst, Images.FormatOf(ext));
        });
        return dst;
    }
}
