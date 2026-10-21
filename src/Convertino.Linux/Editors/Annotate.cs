using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Convertino.Services;
using Convertino.Ui;
using ImageMagick;
using Orientation = Avalonia.Layout.Orientation;
using FontWeight = Avalonia.Media.FontWeight;
using TextAlignment = Avalonia.Media.TextAlignment;
using FontStyle = Avalonia.Media.FontStyle;

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
    static readonly Typeface Face = new(new FontFamily("Inter, Noto Sans, DejaVu Sans, sans-serif"), FontStyle.Normal, FontWeight.Bold);

    public static void Paint(DrawingContext dc, Mark m, Func<Point, Point> map, double k)
    {
        var shadow = Draw.B(Color.FromArgb(70, 0, 0, 0));
        switch (m)
        {
            case InkMark ink when ink.Pts.Count > 0:
            {
                var c = ink.Color;
                if (ink.Highlighter) c = Color.FromArgb(105, c.R, c.G, c.B);
                var pen = new Pen(Draw.B(c), ink.W * k * (ink.Highlighter ? 4.5 : 1), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(map(ink.Pts[0]), false);
                    if (ink.Pts.Count == 1) ctx.LineTo(map(ink.Pts[0]) + new Vector(0.01, 0));
                    for (int i = 1; i < ink.Pts.Count - 1; i++)
                    {
                        var p = map(ink.Pts[i]); var n = map(ink.Pts[i + 1]);
                        ctx.QuadraticBezierTo(p, new Point((p.X + n.X) / 2, (p.Y + n.Y) / 2));
                    }
                    if (ink.Pts.Count > 1) ctx.LineTo(map(ink.Pts[^1]));
                    ctx.EndFigure(false);
                }
                dc.DrawGeometry(null, pen, g);
                break;
            }
            case ArrowMark a:
            {
                var A = map(a.A); var B = map(a.B);
                var d = B - A;
                double len = Math.Sqrt(d.X * d.X + d.Y * d.Y);
                if (len < 1) return;
                d /= len;
                double t = a.W * k, headL = Math.Min(len * 0.6, t * 4.2), headW = t * 3.4;
                var bp = B - d * headL;
                var n = new Vector(-d.Y, d.X);
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(A + n * t * 0.25, true);
                    ctx.LineTo(bp + n * t * 0.55); ctx.LineTo(bp + n * headW / 2); ctx.LineTo(B);
                    ctx.LineTo(bp - n * headW / 2); ctx.LineTo(bp - n * t * 0.55); ctx.LineTo(A - n * t * 0.25);
                    ctx.EndFigure(true);
                }
                using (dc.PushTransform(Matrix.CreateTranslation(t * 0.25, t * 0.35))) dc.DrawGeometry(shadow, null, g);
                dc.DrawGeometry(Draw.B(a.Color), new Pen(Draw.B(a.Color), t * 0.15, lineJoin: PenLineJoin.Round), g);
                break;
            }
            case BoxMark b:
            {
                var r = new Rect(map(b.R.TopLeft), map(b.R.BottomRight));
                double t = b.W * k;
                using (dc.PushTransform(Matrix.CreateTranslation(t * 0.25, t * 0.35)))
                    Shape(dc, b.Oval, r, new Pen(shadow, t), t);
                Shape(dc, b.Oval, r, new Pen(Draw.B(b.Color), t, lineJoin: PenLineJoin.Round), t);
                break;
            }
            case TextMark tm:
            {
                double size = Math.Max(1, tm.W * k * 5.5);
                var ft = new FormattedText(tm.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, Draw.B(tm.Color));
                var geo = ft.BuildGeometry(map(tm.P));
                if (geo == null) return;
                bool light = tm.Color.R + tm.Color.G + tm.Color.B > 600;
                dc.DrawGeometry(null, new Pen(Draw.B(light ? Color.FromArgb(160, 0, 0, 0) : Color.FromArgb(220, 255, 255, 255)), size * 0.16, lineJoin: PenLineJoin.Round), geo);
                dc.DrawGeometry(Draw.B(tm.Color), null, geo);
                break;
            }
            case StepMark s:
            {
                double r = s.W * k * 4.2;
                var p = map(s.P);
                dc.DrawEllipse(shadow, null, p + new Vector(r * 0.08, r * 0.12), r, r);
                dc.DrawEllipse(Draw.B(s.Color), new Pen(Brushes.White, r * 0.14), p, r, r);
                bool light = s.Color.R + s.Color.G + s.Color.B > 600;
                var ft = new FormattedText(s.N.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, r * 1.15, light ? Brushes.Black : Brushes.White);
                dc.DrawText(ft, new Point(p.X - ft.Width / 2, p.Y - ft.Height / 2));
                break;
            }
        }
    }

    static void Shape(DrawingContext dc, bool oval, Rect r, IPen pen, double t)
    {
        if (oval) dc.DrawEllipse(null, pen, r.Center, r.Width / 2, r.Height / 2);
        else dc.DrawRectangle(null, pen, new RoundedRect(r, t * 1.2));
    }
}

sealed class AnnotateSurface : ImageSurface
{
    public List<Mark> Marks { get; } = [];
    public Stack<Mark> Redo { get; } = new();
    public Pen2 Tool = Pen2.Arrow;
    public Color Color = Kit.Hex("#ff3b5c");
    public double Weight = 2, Unit = 1;
    public Func<Point, Task<string?>>? AskText;
    Mark? _live;
    Point _anchor;

    protected override void DrawOverlay(DrawingContext dc, Rect r)
    {
        using (dc.PushClip(r))
        {
            foreach (var m in Marks) MarkPainter.Paint(dc, m, ToView, ViewScale * Unit);
            if (_live != null) MarkPainter.Paint(dc, _live, ToView, ViewScale * Unit);
        }
    }

    int NextStep => Marks.OfType<StepMark>().Select(s => s.N).DefaultIfEmpty(0).Max() + 1;

    protected override async void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var vp = e.GetPosition(this);
        var ip = ToImage(vp);
        _anchor = ip;
        switch (Tool)
        {
            case Pen2.Pen or Pen2.Highlighter: _live = new InkMark([ip], Color, Weight, Tool == Pen2.Highlighter); break;
            case Pen2.Arrow: _live = new ArrowMark(ip, ip, Color, Weight); break;
            case Pen2.Box: _live = new BoxMark(new Rect(ip, ip), Color, Weight, false); break;
            case Pen2.Oval: _live = new BoxMark(new Rect(ip, ip), Color, Weight, true); break;
            case Pen2.Step: Commit(new StepMark(ip, NextStep, Color, Weight)); return;
            case Pen2.Text:
                if (AskText != null && await AskText(vp) is { Length: > 0 } text) Commit(new TextMark(ip, text, Color, Weight));
                return;
        }
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_live == null) return;
        var ip = ToImage(e.GetPosition(this));
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _live = _live switch
        {
            InkMark ink => Add(ink, ip),
            ArrowMark a => a with { B = shift ? Snap(a.A, ip) : ip },
            BoxMark b => b with { R = Draw.Norm(_anchor, shift ? Square(_anchor, ip) : ip) },
            _ => _live,
        };
        InvalidateVisual();
    }

    InkMark Add(InkMark ink, Point p)
    {
        var d = ink.Pts[^1] - p;
        if (Math.Sqrt(d.X * d.X + d.Y * d.Y) * ViewScale > 1.5) ink.Pts.Add(p);
        return ink;
    }

    static Point Snap(Point a, Point b)
    {
        var d = b - a;
        double ang = Math.Round(Math.Atan2(d.Y, d.X) / (Math.PI / 4)) * (Math.PI / 4), len = Math.Sqrt(d.X * d.X + d.Y * d.Y);
        return a + new Vector(Math.Cos(ang), Math.Sin(ang)) * len;
    }

    static Point Square(Point a, Point b)
    {
        double s = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        return new Point(a.X + Math.Sign(b.X - a.X) * s, a.Y + Math.Sign(b.Y - a.Y) * s);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        if (_live == null) return;
        bool keep = _live switch
        {
            ArrowMark a => Math.Sqrt((a.B - a.A).X * (a.B - a.A).X + (a.B - a.A).Y * (a.B - a.A).Y) * ViewScale > 6,
            BoxMark b => b.R.Width * ViewScale > 4 && b.R.Height * ViewScale > 4,
            _ => true,
        };
        if (keep) Commit(_live);
        _live = null;
        InvalidateVisual();
    }

    void Commit(Mark m) { Marks.Add(m); Redo.Clear(); InvalidateVisual(); }
    public void Undo() { if (Marks.Count == 0) return; Redo.Push(Marks[^1]); Marks.RemoveAt(Marks.Count - 1); InvalidateVisual(); }
    public void RedoOne() { if (Redo.Count == 0) return; Marks.Add(Redo.Pop()); InvalidateVisual(); }
}

public sealed class AnnotateEditor : Editor
{
    readonly string _src;
    readonly MagickImage _img;
    readonly AnnotateSurface _surface = new();
    readonly Panel _preview = new();
    readonly Canvas _overlay = new();
    readonly WrapPanel _swatches = new();
    readonly StackPanel _tools = new() { Orientation = Orientation.Horizontal };
    readonly Control _controls;

    static readonly Color[] Palette = new[] { "#ff3b5c", "#ff8a3d", "#ffd43b", "#40c057", "#339af0", "#cba6f7", "#ffffff", "#111111" }.Select(Kit.Hex).ToArray();

    public AnnotateEditor(string src, MagickImage img)
    {
        _src = src; _img = img;
        using (var p = EditOps.Downscaled(img, 2400)) _surface.Image = Px.ToBitmap(p);
        _surface.ImageSize = new Size(img.Width, img.Height);
        _surface.Unit = Math.Max(img.Width, img.Height) / 260.0;
        _surface.AskText = AskText;
        _preview.Children.Add(_surface);
        _preview.Children.Add(_overlay);

        void T(string icon, string tip, Pen2 tool, bool on = false)
        {
            var r = new RadioButton
            {
                GroupName = "tool", IsChecked = on, Margin = new Thickness(0, 0, 4, 0),
                Content = new IconView { Key = icon, Width = 18, Height = 18, Brush = Draw.B(Skin.Current.Text), Weight = 1.8 },
            };
            ToolTip.SetTip(r, tip);
            r.IsCheckedChanged += (_, _) => { if (r.IsChecked == true) _surface.Tool = tool; };
            _tools.Children.Add(r);
        }
        T("arrow", L.T("Arrow (A)"), Pen2.Arrow, true);
        T("pen", L.T("Pen (P)"), Pen2.Pen);
        T("marker", L.T("Highlighter (H)"), Pen2.Highlighter);
        T("square", L.T("Rectangle (R)"), Pen2.Box);
        T("circle", L.T("Ellipse (E)"), Pen2.Oval);
        T("text", L.T("Text (T)"), Pen2.Text);
        T("step", L.T("Numbered step (N)"), Pen2.Step);

        BuildSwatches();
        var weight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { Kit.Chip("S", "w", false, () => _surface.Weight = 1.2), Kit.Chip("M", "w", true, () => _surface.Weight = 2), Kit.Chip("L", "w", false, () => _surface.Weight = 3.4) },
        };
        var row1 = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var ur = new StackPanel { Orientation = Orientation.Horizontal, Children = { Kit.Icon("undo", L.T("Undo (Ctrl+Z)"), () => _surface.Undo()), Kit.Icon("redo", L.T("Redo (Ctrl+Y)"), () => _surface.RedoOne()) } };
        DockPanel.SetDock(ur, Dock.Right);
        row1.Children.Add(ur);
        row1.Children.Add(_tools);
        _controls = new StackPanel { Children = { row1, new StackPanel { Orientation = Orientation.Horizontal, Children = { _swatches, weight } } } };
    }

    void BuildSwatches()
    {
        _swatches.Children.Clear();
        foreach (var c in Palette)
            _swatches.Children.Add(Kit.Swatch(Draw.B(c), c == _surface.Color, () => { _surface.Color = c; BuildSwatches(); }));
    }

    Task<string?> AskText(Point view)
    {
        var tcs = new TaskCompletionSource<string?>();
        double size = Math.Max(12, _surface.Weight * _surface.Unit * 5.5 * _surface.ViewScale);
        var box = new TextBox
        {
            MinWidth = 120, FontSize = size, FontWeight = FontWeight.Bold, Foreground = Draw.B(_surface.Color),
            Background = Draw.B(Color.FromArgb(90, 0, 0, 0)), AcceptsReturn = true, Padding = new Thickness(2),
        };
        Canvas.SetLeft(box, view.X);
        Canvas.SetTop(box, view.Y);
        _overlay.Children.Add(box);
        bool done = false;
        void Finish(bool ok)
        {
            if (done) return;
            done = true;
            _overlay.Children.Remove(box);
            tcs.TrySetResult(ok ? box.Text?.TrimEnd() : null);
            _surface.Focus();
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { e.Handled = true; Finish(true); }
            else if (e.Key == Key.Escape) { e.Handled = true; Finish(false); }
        };
        box.LostFocus += (_, _) => Finish(true);
        box.AttachedToVisualTree += (_, _) => box.Focus();
        return tcs.Task;
    }

    internal override void DevPrepare()
    {
        double w = _img.Width, h = _img.Height;
        _surface.Marks.Add(new ArrowMark(new Point(w * .15, h * .8), new Point(w * .45, h * .5), Kit.Hex("#ff3b5c"), 2));
        _surface.Marks.Add(new BoxMark(new Rect(w * .5, h * .4, w * .3, h * .2), Kit.Hex("#ffd43b"), 2, false));
        _surface.Marks.Add(new TextMark(new Point(w * .1, h * .1), "Convertino annotate", Kit.Hex("#ffffff"), 2));
        _surface.Marks.Add(new StepMark(new Point(w * .8, h * .8), 1, Kit.Hex("#339af0"), 2));
    }

    public override string Title => L.T("Annotate");
    public override Control Preview => _preview;
    public override Control Controls => _controls;
    public override void Reset() { _surface.Marks.Clear(); _surface.Redo.Clear(); _surface.InvalidateVisual(); }

    public override void OnKey(KeyEventArgs e)
    {
        if (_overlay.Children.Count > 0) return;
        bool ctrl = Shortcut(e);
        if (ctrl && e.Key == Key.Z) { _surface.Undo(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Y) { _surface.RedoOne(); e.Handled = true; return; }
        int idx = e.Key switch { Key.A => 0, Key.P => 1, Key.H => 2, Key.R => 3, Key.E => 4, Key.T => 5, Key.N => 6, _ => -1 };
        if (idx >= 0 && !ctrl) { ((RadioButton)_tools.Children[idx]).IsChecked = true; e.Handled = true; }
    }

    public override async Task<string?> Save()
    {
        if (_surface.Marks.Count == 0) return null;
        int w = (int)_img.Width, h = (int)_img.Height;
        var full = await Task.Run(() => Px.ToBitmap(_img));
        using var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        using (var dc = rtb.CreateDrawingContext(true))
        {
            dc.DrawImage(full, new Rect(0, 0, w, h));
            foreach (var m in _surface.Marks) MarkPainter.Paint(dc, m, p => p, _surface.Unit);
        }
        var (px, pw, ph) = Px.Pixels(rtb);
        return await Task.Run(() => EditOps.SaveRendered(_src, _img, px, pw, ph, "(annotated)"));
    }
}
