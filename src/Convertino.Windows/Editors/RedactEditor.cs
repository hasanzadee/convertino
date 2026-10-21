using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using Orientation = System.Windows.Controls.Orientation;
using Convertino.Services;

namespace Convertino.Editors;

public enum RedactStyle { Pixelate, Blur, Black }

sealed class RedactSurface : ImageSurface
{
    public List<Rect> Boxes { get; } = [];
    public RedactStyle Mode = RedactStyle.Pixelate;
    public BitmapSource? Pixelated, Blurred;
    public int Selected = -1;
    public event Action? Changed;

    Point? _start;
    Rect _live;

    public RedactSurface() { Cursor = Cursors.Cross; }

    protected override void DrawOverlay(DrawingContext dc, Rect r)
    {
        var all = Boxes.ToList();
        if (_start != null && _live.Width > 0) all.Add(_live);
        for (int i = 0; i < all.Count; i++)
        {
            var v = ToView(all[i]);
            v.Intersect(r);
            if (v.IsEmpty) continue;
            var src = Mode switch { RedactStyle.Pixelate => Pixelated, RedactStyle.Blur => Blurred, _ => null };
            if (src != null)
            {
                dc.PushClip(new RectangleGeometry(v));
                dc.DrawImage(src, r);
                dc.Pop();
            }
            else dc.DrawRectangle(Brushes.Black, null, v);
            bool sel = i == Selected;
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(sel ? Color.FromRgb(0xcb, 0xa6, 0xf7) : Color.FromArgb(150, 255, 255, 255)), sel ? 2 : 1) { DashStyle = sel ? DashStyles.Solid : DashStyles.Dash }, v);
        }
    }

    int HitBox(Point img)
    {
        for (int i = Boxes.Count - 1; i >= 0; i--) if (Boxes[i].Contains(img)) return i;
        return -1;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var ip = ToImage(e.GetPosition(this));
        int hit = HitBox(ip);
        if (hit >= 0 && e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            Selected = hit;
            InvalidateVisual();
            Changed?.Invoke();
        }
        _start = Clamp(ip);
        _live = Rect.Empty;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_start == null) return;
        _live = new Rect(_start.Value, Clamp(ToImage(e.GetPosition(this))));
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (_start != null && _live.Width * Scale > 4 && _live.Height * Scale > 4)
        {
            Boxes.Add(_live);
            Selected = Boxes.Count - 1;
            Changed?.Invoke();
        }
        _start = null;
        _live = Rect.Empty;
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        int hit = HitBox(ToImage(e.GetPosition(this)));
        if (hit >= 0) { Boxes.RemoveAt(hit); Selected = -1; Changed?.Invoke(); InvalidateVisual(); }
    }

    Point Clamp(Point p) => new(Math.Clamp(p.X, 0, ImageSize.Width), Math.Clamp(p.Y, 0, ImageSize.Height));

    public void DeleteSelected()
    {
        if (Selected < 0 || Selected >= Boxes.Count) return;
        Boxes.RemoveAt(Selected);
        Selected = Boxes.Count - 1;
        Changed?.Invoke();
        InvalidateVisual();
    }
}

public sealed class RedactEditor : Editor
{
    readonly string _src;
    readonly MagickImage _img;
    readonly RedactSurface _surface = new();
    readonly FrameworkElement _controls;
    readonly TextBlock _count = UiKit.Label("");
    readonly Button _detect;

    public RedactEditor(string src, MagickImage img)
    {
        _src = src;
        _img = img;
        using var preview = (MagickImage)img.Clone();
        if (Math.Max(preview.Width, preview.Height) > 2200) preview.Resize(new MagickGeometry(2200, 2200));
        _surface.Image = Wpf.ToBitmapSource(preview);
        _surface.ImageSize = new Size(img.Width, img.Height);
        _surface.Pixelated = Wpf.ToBitmapSource(Pixelate(preview, BlockFor(preview)));
        using (var b = (MagickImage)preview.Clone()) { b.Blur(0, Math.Max(6, Math.Max(preview.Width, preview.Height) / 90.0)); _surface.Blurred = Wpf.ToBitmapSource(b); }
        _surface.Changed += () => _count.Text = _surface.Boxes.Count == 0 ? L.T("Drag over anything private") : L.N(_surface.Boxes.Count, "{0} area", "{0} areas") + " · " + L.T("right-click removes");
        _count.Text = L.T("Drag over anything private");

        _detect = UiKit.Btn(L.T("Find text"), async () => await DetectText());
        _detect.ToolTip = L.T("Box every line of text found by OCR");
        var dock = new DockPanel();
        DockPanel.SetDock(_count, Dock.Right);
        dock.Children.Add(_count);
        dock.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                UiKit.Label(L.T("Style")),
                UiKit.Chip(L.T("Pixelate"), "rs", true, () => SetStyle(RedactStyle.Pixelate)),
                UiKit.Chip(L.T("Blur"), "rs", false, () => SetStyle(RedactStyle.Blur)),
                UiKit.Chip(L.T("Black"), "rs", false, () => SetStyle(RedactStyle.Black)),
                new Border { Width = 14 },
                _detect,
                UiKit.Btn(L.T("Undo"), () => { if (_surface.Boxes.Count > 0) { _surface.Boxes.RemoveAt(_surface.Boxes.Count - 1); _surface.Selected = -1; _surface.InvalidateVisual(); } }),
            },
        });
        _controls = dock;
    }

    void SetStyle(RedactStyle s) { _surface.Mode = s; _surface.InvalidateVisual(); }

    static int BlockFor(MagickImage i) => (int)Math.Max(6, Math.Min(i.Width, i.Height) / 45.0);

    static MagickImage Pixelate(MagickImage src, int block)
    {
        var p = (MagickImage)src.Clone();
        uint w = Math.Max(1, p.Width / (uint)block), h = Math.Max(1, p.Height / (uint)block);
        p.Scale(w, h);
        p.FilterType = FilterType.Point;
        p.Resize(new MagickGeometry(src.Width, src.Height) { IgnoreAspectRatio = true });
        return p;
    }

    async Task DetectText()
    {
        _detect.IsEnabled = false;
        _detect.Content = L.T("Reading…");
        try
        {
            byte[] png;
            double k = 1;
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
            _surface.Selected = -1;
            _surface.InvalidateVisual();
            _count.Text = res.Lines.Count == 0 ? L.T("No text found") : L.N(_surface.Boxes.Count, "{0} area", "{0} areas") + " · " + L.T("right-click removes");
        }
        catch (Exception ex) { _count.Text = ex.Message; }
        _detect.Content = L.T("Find text");
        _detect.IsEnabled = true;
    }

    internal override void DevPrepare()
    {
        _surface.Boxes.Add(new Rect(_img.Width * .1, _img.Height * .1, _img.Width * .4, _img.Height * .2));
        _surface.Mode = RedactStyle.Pixelate;
    }

    public override string Title => L.T("Redact");
    public override FrameworkElement Preview => _surface;
    public override FrameworkElement Controls => _controls;

    public override void Reset()
    {
        _surface.Boxes.Clear();
        _surface.Selected = -1;
        _surface.InvalidateVisual();
    }

    public override void OnKey(KeyEventArgs e)
    {
        if (e.Key is Key.Delete or Key.Back) { _surface.DeleteSelected(); e.Handled = true; }
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && _surface.Boxes.Count > 0)
        {
            _surface.Boxes.RemoveAt(_surface.Boxes.Count - 1);
            _surface.InvalidateVisual();
            e.Handled = true;
        }
    }

    public override async Task<string?> Save()
    {
        if (_surface.Boxes.Count == 0) return null;
        var ext = CropEditor.Kinds(_src);
        var dst = Paths.Beside(_src, ext, "(redacted)");
        var boxes = _surface.Boxes.ToList();
        var style = _surface.Mode;
        await Task.Run(() =>
        {
            using var img = (MagickImage)_img.Clone();
            img.Strip(); // a redacted image shouldn't leak GPS/camera data either
            using var effect = style switch
            {
                RedactStyle.Pixelate => Pixelate(img, BlockFor(img)),
                RedactStyle.Blur => BlurAll(img),
                _ => null,
            };
            foreach (var b in boxes)
            {
                var r = Rect.Intersect(b, new Rect(0, 0, img.Width, img.Height));
                if (r.IsEmpty) continue;
                var g = new MagickGeometry((int)r.X, (int)r.Y, (uint)Math.Ceiling(r.Width), (uint)Math.Ceiling(r.Height));
                if (effect == null)
                {
                    using var black = new MagickImage(MagickColors.Black, g.Width, g.Height);
                    img.Composite(black, g.X, g.Y, CompositeOperator.Over);
                }
                else
                {
                    using var part = (MagickImage)effect.Clone();
                    part.Crop(g);
                    part.ResetPage();
                    img.Composite(part, g.X, g.Y, CompositeOperator.Over);
                }
            }
            Images.PrepareFor(img, ext, ext == "jpg" ? 94 : null);
            img.Write(dst, Images.FormatOf(ext));
        });
        return dst;
    }

    static MagickImage BlurAll(MagickImage img)
    {
        var b = (MagickImage)img.Clone();
        // heavy enough that text can't be recovered
        b.Blur(0, Math.Max(8, Math.Max(img.Width, img.Height) / 80.0));
        return b;
    }
}
