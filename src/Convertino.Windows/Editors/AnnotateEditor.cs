using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using Orientation = System.Windows.Controls.Orientation;
using Convertino.Services;

namespace Convertino.Editors;

public enum Pen2 { Pen, Highlighter, Arrow, Box, Oval, Text, Step }

abstract record Mark(Color Color, double W);
sealed record InkMark(List<Point> Pts, Color Color, double W, bool Highlighter) : Mark(Color, W);
sealed record ArrowMark(Point A, Point B, Color Color, double W) : Mark(Color, W);
sealed record BoxMark(Rect R, Color Color, double W, bool Oval) : Mark(Color, W);
sealed record TextMark(Point P, string Text, Color Color, double W) : Mark(Color, W);
sealed record StepMark(Point P, int N, Color Color, double W) : Mark(Color, W);

static class MarkPainter
{
    static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public static void Draw(DrawingContext dc, Mark m, Func<Point, Point> map, double k, double ppd = 1)
    {
        var shadow = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
        switch (m)
        {
            case InkMark ink when ink.Pts.Count > 0:
            {
                var c = ink.Color;
                if (ink.Highlighter) c.A = 105;
                var pen = new Pen(new SolidColorBrush(c), ink.W * k * (ink.Highlighter ? 4.5 : 1))
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(map(ink.Pts[0]), false, false);
                    if (ink.Pts.Count == 1) ctx.LineTo(map(ink.Pts[0]) + new Vector(0.01, 0), true, true);
                    // smooth the polyline with midpoint quadratic curves
                    for (int i = 1; i < ink.Pts.Count - 1; i++)
                    {
                        var p = map(ink.Pts[i]);
                        var n = map(ink.Pts[i + 1]);
                        ctx.QuadraticBezierTo(p, new Point((p.X + n.X) / 2, (p.Y + n.Y) / 2), true, true);
                    }
                    if (ink.Pts.Count > 1) ctx.LineTo(map(ink.Pts[^1]), true, true);
                }
                dc.DrawGeometry(null, pen, geo);
                break;
            }
            case ArrowMark a:
            {
                var A = map(a.A); var B = map(a.B);
                var d = B - A;
                double len = d.Length;
                if (len < 1) return;
                d /= len;
                double t = a.W * k;
                double headL = Math.Min(len * 0.6, t * 4.2), headW = t * 3.4;
                var basePt = B - d * headL;
                var n = new Vector(-d.Y, d.X);
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    // tapered shaft + head as one filled shape
                    ctx.BeginFigure(A + n * t * 0.25, true, true);
                    ctx.LineTo(basePt + n * t * 0.55, true, true);
                    ctx.LineTo(basePt + n * headW / 2, true, true);
                    ctx.LineTo(B, true, true);
                    ctx.LineTo(basePt - n * headW / 2, true, true);
                    ctx.LineTo(basePt - n * t * 0.55, true, true);
                    ctx.LineTo(A - n * t * 0.25, true, true);
                }
                dc.PushTransform(new TranslateTransform(t * 0.25, t * 0.35));
                dc.DrawGeometry(shadow, null, geo);
                dc.Pop();
                dc.DrawGeometry(new SolidColorBrush(a.Color), new Pen(new SolidColorBrush(a.Color), t * 0.15) { LineJoin = PenLineJoin.Round }, geo);
                break;
            }
            case BoxMark b:
            {
                var r = new Rect(map(b.R.TopLeft), map(b.R.BottomRight));
                double t = b.W * k;
                var pen = new Pen(new SolidColorBrush(b.Color), t) { LineJoin = PenLineJoin.Round };
                var sp = new Pen(shadow, t);
                dc.PushTransform(new TranslateTransform(t * 0.25, t * 0.35));
                if (b.Oval) dc.DrawEllipse(null, sp, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
                else dc.DrawRoundedRectangle(null, sp, r, t * 1.2, t * 1.2);
                dc.Pop();
                if (b.Oval) dc.DrawEllipse(null, pen, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
                else dc.DrawRoundedRectangle(null, pen, r, t * 1.2, t * 1.2);
                break;
            }
            case TextMark tm:
            {
                double size = tm.W * k * 5.5;
                var ft = new FormattedText(tm.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, Math.Max(1, size), new SolidColorBrush(tm.Color), ppd);
                var p = map(tm.P);
                // outline makes text readable on any background
                var geo = ft.BuildGeometry(p);
                bool light = tm.Color.R + tm.Color.G + tm.Color.B > 600;
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(light ? Color.FromArgb(160, 0, 0, 0) : Color.FromArgb(220, 255, 255, 255)), size * 0.16) { LineJoin = PenLineJoin.Round }, geo);
                dc.DrawGeometry(new SolidColorBrush(tm.Color), null, geo);
                break;
            }
            case StepMark s:
            {
                double r = s.W * k * 4.2;
                var p = map(s.P);
                dc.DrawEllipse(shadow, null, p + new Vector(r * 0.08, r * 0.12), r, r);
                dc.DrawEllipse(new SolidColorBrush(s.Color), new Pen(Brushes.White, r * 0.14), p, r, r);
                bool light = s.Color.R + s.Color.G + s.Color.B > 600;
                var ft = new FormattedText(s.N.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, r * 1.15, light ? Brushes.Black : Brushes.White, ppd);
                dc.DrawText(ft, new Point(p.X - ft.Width / 2, p.Y - ft.Height / 2));
                break;
            }
        }
    }
}

sealed class AnnotateSurface : ImageSurface
{
    public List<Mark> Marks { get; } = [];
    public Stack<Mark> Redo { get; } = new();
    public Pen2 Tool = Pen2.Arrow;
    public Color Color = Color.FromRgb(0xff, 0x3b, 0x5c);
    public double Weight = 2;      // in "units"
    public double Unit = 1;        // image px per unit
    public Func<Point, Point, Task<string?>>? AskText; // view point → text

    Mark? _live;
    Point _anchor;

    public AnnotateSurface() { Cursor = Cursors.Cross; }

    protected override void DrawOverlay(DrawingContext dc, Rect r)
    {
        dc.PushClip(new RectangleGeometry(r));
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var m in Marks) MarkPainter.Draw(dc, m, ToView, Scale * Unit, ppd);
        if (_live != null) MarkPainter.Draw(dc, _live, ToView, Scale * Unit, ppd);
        dc.Pop();
    }

    int NextStep => Marks.OfType<StepMark>().Select(s => s.N).DefaultIfEmpty(0).Max() + 1;

    protected override async void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var vp = e.GetPosition(this);
        var ip = ToImage(vp);
        _anchor = ip;
        switch (Tool)
        {
            case Pen2.Pen:
            case Pen2.Highlighter:
                _live = new InkMark([ip], Color, Weight, Tool == Pen2.Highlighter);
                break;
            case Pen2.Arrow: _live = new ArrowMark(ip, ip, Color, Weight); break;
            case Pen2.Box: _live = new BoxMark(new Rect(ip, ip), Color, Weight, false); break;
            case Pen2.Oval: _live = new BoxMark(new Rect(ip, ip), Color, Weight, true); break;
            case Pen2.Step:
                Commit(new StepMark(ip, NextStep, Color, Weight));
                return;
            case Pen2.Text:
                if (AskText != null)
                {
                    var text = await AskText(vp, ip);
                    if (!string.IsNullOrWhiteSpace(text)) Commit(new TextMark(ip, text, Color, Weight));
                }
                return;
        }
        CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_live == null) return;
        var ip = ToImage(e.GetPosition(this));
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        _live = _live switch
        {
            InkMark ink => AddPoint(ink, ip),
            ArrowMark a => a with { B = shift ? Snap(a.A, ip) : ip },
            BoxMark b => b with { R = new Rect(_anchor, shift ? Square(_anchor, ip) : ip) },
            _ => _live,
        };
        InvalidateVisual();
    }

    InkMark AddPoint(InkMark ink, Point p)
    {
        if ((ink.Pts[^1] - p).Length * Scale > 1.5) ink.Pts.Add(p);
        return ink;
    }

    static Point Snap(Point a, Point b)
    {
        var d = b - a;
        double ang = Math.Round(Math.Atan2(d.Y, d.X) / (Math.PI / 4)) * (Math.PI / 4);
        return a + new Vector(Math.Cos(ang), Math.Sin(ang)) * d.Length;
    }

    static Point Square(Point a, Point b)
    {
        double s = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        return new Point(a.X + Math.Sign(b.X - a.X) * s, a.Y + Math.Sign(b.Y - a.Y) * s);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (_live == null) return;
        bool keep = _live switch
        {
            ArrowMark a => (a.B - a.A).Length * Scale > 6,
            BoxMark b => b.R.Width * Scale > 4 && b.R.Height * Scale > 4,
            _ => true,
        };
        if (keep) Commit(_live);
        _live = null;
        InvalidateVisual();
    }

    void Commit(Mark m)
    {
        Marks.Add(m);
        Redo.Clear();
        InvalidateVisual();
    }

    public void Undo()
    {
        if (Marks.Count == 0) return;
        Redo.Push(Marks[^1]);
        Marks.RemoveAt(Marks.Count - 1);
        InvalidateVisual();
    }

    public void RedoOne()
    {
        if (Redo.Count == 0) return;
        Marks.Add(Redo.Pop());
        InvalidateVisual();
    }
}

public sealed class AnnotateEditor : Editor
{
    readonly string _src;
    readonly MagickImage _img;
    readonly AnnotateSurface _surface = new();
    readonly Grid _previewGrid = new();
    readonly Canvas _overlay = new();
    readonly FrameworkElement _controls;
    readonly WrapPanel _swatches = new();

    static readonly Color[] Palette =
    [
        UiKit.Hex("#ff3b5c"), UiKit.Hex("#ff8a3d"), UiKit.Hex("#ffd43b"), UiKit.Hex("#40c057"),
        UiKit.Hex("#339af0"), UiKit.Hex("#cba6f7"), UiKit.Hex("#ffffff"), UiKit.Hex("#111111"),
    ];

    public AnnotateEditor(string src, MagickImage img)
    {
        _src = src;
        _img = img;
        using var preview = (MagickImage)img.Clone();
        if (Math.Max(preview.Width, preview.Height) > 2400) preview.Resize(new MagickGeometry(2400, 2400));
        _surface.Image = Wpf.ToBitmapSource(preview);
        _surface.ImageSize = new Size(img.Width, img.Height);
        _surface.Unit = Math.Max(img.Width, img.Height) / 260.0;
        _surface.AskText = AskText;
        _previewGrid.Children.Add(_surface);
        _previewGrid.Children.Add(_overlay);
        _overlay.IsHitTestVisible = true;
        _overlay.Background = null;

        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        void T(string glyph, string tip, Pen2 tool, bool on = false)
        {
            var r = new RadioButton
            {
                Content = new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["Convertino.Icons"], FontSize = 15 },
                GroupName = "tool", IsChecked = on, ToolTip = tip, Style = (Style)Application.Current.Resources["Convertino.RadioChip"],
                Padding = new Thickness(10, 7, 10, 7),
            };
            r.Checked += (_, _) => _surface.Tool = tool;
            tools.Children.Add(r);
        }
        T("", L.T("Arrow (A)"), Pen2.Arrow, true);
        T("", L.T("Pen (P)"), Pen2.Pen);
        T("", L.T("Highlighter (H)"), Pen2.Highlighter);
        T("", L.T("Rectangle (R)"), Pen2.Box);
        T("", L.T("Ellipse (E)"), Pen2.Oval);
        T("", L.T("Text (T)"), Pen2.Text);
        T("", L.T("Numbered step (N)"), Pen2.Step);
        //  may be missing on older Segoe MDL2: fall back to a typographic arrow
        if (!HasGlyph('')) ((TextBlock)((RadioButton)tools.Children[0]).Content).Text = "↗";
        if (!HasGlyph('')) ((TextBlock)((RadioButton)tools.Children[6]).Content).Text = "①";

        BuildSwatches();
        var weight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                UiKit.Chip("S", "w", false, () => _surface.Weight = 1.2),
                UiKit.Chip("M", "w", true, () => _surface.Weight = 2),
                UiKit.Chip("L", "w", false, () => _surface.Weight = 3.4),
            },
        };
        var undo = UiKit.Icon("", L.T("Undo (Ctrl+Z)"), () => _surface.Undo());
        var redo = UiKit.Icon("", L.T("Redo (Ctrl+Y)"), () => _surface.RedoOne());
        var row1 = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var ur = new StackPanel { Orientation = Orientation.Horizontal, Children = { undo, redo } };
        DockPanel.SetDock(ur, Dock.Right);
        row1.Children.Add(ur);
        row1.Children.Add(tools);
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Children = { _swatches, new Border { Width = 10 }, weight } };
        _controls = new StackPanel { Children = { row1, row2 } };
    }

    static bool HasGlyph(char c)
    {
        foreach (var tf in new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets").GetTypefaces())
            if (tf.TryGetGlyphTypeface(out var g) && g.CharacterToGlyphMap.ContainsKey(c)) return true;
        return false;
    }

    void BuildSwatches()
    {
        _swatches.Children.Clear();
        foreach (var c in Palette)
            _swatches.Children.Add(UiKit.Swatch(new SolidColorBrush(c), c == _surface.Color, () => { _surface.Color = c; BuildSwatches(); }));
    }

    Task<string?> AskText(Point view, Point img)
    {
        var tcs = new TaskCompletionSource<string?>();
        double size = Math.Max(12, _surface.Weight * _surface.Unit * 5.5 * ViewScale());
        var box = new TextBox
        {
            MinWidth = 120,
            FontSize = size,
            FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            Foreground = new SolidColorBrush(_surface.Color),
            Background = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(_surface.Color),
            BorderThickness = new Thickness(1),
            CaretBrush = new SolidColorBrush(_surface.Color),
            AcceptsReturn = true,
            Padding = new Thickness(0),
        };
        Canvas.SetLeft(box, view.X);
        Canvas.SetTop(box, view.Y);
        _overlay.Children.Add(box);
        bool done = false;
        void Finish(bool ok)
        {
            if (done) return;
            done = true;
            var text = box.Text.TrimEnd();
            _overlay.Children.Remove(box);
            tcs.TrySetResult(ok ? text : null);
            _surface.Focus();
        }
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { e.Handled = true; Finish(true); }
            else if (e.Key == Key.Escape) { e.Handled = true; Finish(false); }
        };
        box.LostKeyboardFocus += (_, _) => Finish(true);
        box.Loaded += (_, _) => { box.Focus(); Keyboard.Focus(box); };
        return tcs.Task;
    }

    double ViewScale() => _surface.ToView(new Point(1, 0)).X - _surface.ToView(new Point(0, 0)).X;

    internal override void DevPrepare()
    {
        double w = _img.Width, h = _img.Height;
        _surface.Marks.Add(new ArrowMark(new Point(w * .15, h * .8), new Point(w * .45, h * .5), UiKit.Hex("#ff3b5c"), 2));
        _surface.Marks.Add(new BoxMark(new Rect(w * .5, h * .4, w * .3, h * .2), UiKit.Hex("#ffd43b"), 2, false));
        _surface.Marks.Add(new TextMark(new Point(w * .1, h * .1), "Convertino annotate", UiKit.Hex("#ffffff"), 2));
        _surface.Marks.Add(new StepMark(new Point(w * .8, h * .8), 1, UiKit.Hex("#339af0"), 2));
        _surface.Marks.Add(new InkMark([new(w * .2, h * .3), new(w * .3, h * .35), new(w * .35, h * .25), new(w * .45, h * .3)], UiKit.Hex("#40c057"), 2, false));
    }

    public override string Title => L.T("Annotate");
    public override FrameworkElement Preview => _previewGrid;
    public override FrameworkElement Controls => _controls;

    public override void Reset()
    {
        _surface.Marks.Clear();
        _surface.Redo.Clear();
        _surface.InvalidateVisual();
    }

    public override void OnKey(KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.Z) { _surface.Undo(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Y) { _surface.RedoOne(); e.Handled = true; return; }
        if (ctrl) return;
        int idx = e.Key switch { Key.A => 0, Key.P => 1, Key.H => 2, Key.R => 3, Key.E => 4, Key.T => 5, Key.N => 6, _ => -1 };
        if (idx >= 0 && _controls is StackPanel sp && sp.Children[0] is DockPanel dp && dp.Children[1] is StackPanel tools)
        {
            ((RadioButton)tools.Children[idx]).IsChecked = true;
            e.Handled = true;
        }
    }

    public override async Task<string?> Save()
    {
        if (_surface.Marks.Count == 0) return null;
        var ext = CropEditor.Kinds(_src);
        if (ext is "gif") ext = "png";
        var dst = Paths.Beside(_src, ext, "(annotated)");
        var full = await Task.Run(() => Wpf.ToBitmapSource(_img));
        int w = (int)_img.Width, h = (int)_img.Height;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(full, new Rect(0, 0, w, h));
            foreach (var m in _surface.Marks) MarkPainter.Draw(dc, m, p => p, _surface.Unit);
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        await Task.Run(() =>
        {
            using var outImg = Wpf.FromBitmapSource(rtb);
            var icc = _img.GetColorProfile();
            if (icc != null) outImg.SetProfile(icc);
            Images.PrepareFor(outImg, ext, ext == "jpg" ? 94 : null);
            outImg.Write(dst, Images.FormatOf(ext));
        });
        return dst;
    }
}
