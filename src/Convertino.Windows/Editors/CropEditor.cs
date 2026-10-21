using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ImageMagick;
using Orientation = System.Windows.Controls.Orientation;
using Convertino.Services;

namespace Convertino.Editors;

sealed class CropSurface : ImageSurface
{
    public Rect Crop;               // image pixels
    public double? Ratio;           // width / height, null = free
    public event Action? Changed;

    enum Drag { None, Move, New, L, R, T, B, TL, TR, BL, BR }
    Drag _drag;
    Point _anchorImg;
    Rect _startCrop;

    public CropSurface() { Cursor = Cursors.Cross; }

    protected override void DrawOverlay(DrawingContext dc, Rect r)
    {
        var v = ToView(Crop);
        var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(r), new RectangleGeometry(v));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null, outside);

        var thin = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1);
        for (int i = 1; i < 3; i++)
        {
            double x = v.Left + v.Width * i / 3, y = v.Top + v.Height * i / 3;
            dc.DrawLine(thin, new Point(x, v.Top), new Point(x, v.Bottom));
            dc.DrawLine(thin, new Point(v.Left, y), new Point(v.Right, y));
        }
        dc.DrawRectangle(null, new Pen(Brushes.White, 1.5), v);

        // iOS-style corner brackets + edge ticks
        var bracket = new Pen(Brushes.White, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        double L = Math.Min(20, Math.Min(v.Width, v.Height) / 3);
        void Corner(Point p, double dx, double dy)
        {
            dc.DrawLine(bracket, p, new Point(p.X + dx * L, p.Y));
            dc.DrawLine(bracket, p, new Point(p.X, p.Y + dy * L));
        }
        Corner(v.TopLeft, 1, 1); Corner(v.TopRight, -1, 1); Corner(v.BottomLeft, 1, -1); Corner(v.BottomRight, -1, -1);
        double cx = v.Left + v.Width / 2, cy = v.Top + v.Height / 2, t = Math.Min(10, L / 2);
        dc.DrawLine(bracket, new Point(cx - t, v.Top), new Point(cx + t, v.Top));
        dc.DrawLine(bracket, new Point(cx - t, v.Bottom), new Point(cx + t, v.Bottom));
        dc.DrawLine(bracket, new Point(v.Left, cy - t), new Point(v.Left, cy + t));
        dc.DrawLine(bracket, new Point(v.Right, cy - t), new Point(v.Right, cy + t));
    }

    Drag HitTest(Point p)
    {
        var v = ToView(Crop);
        const double k = 14;
        bool nl = Math.Abs(p.X - v.Left) < k, nr = Math.Abs(p.X - v.Right) < k;
        bool nt = Math.Abs(p.Y - v.Top) < k, nb = Math.Abs(p.Y - v.Bottom) < k;
        bool inX = p.X > v.Left - k && p.X < v.Right + k, inY = p.Y > v.Top - k && p.Y < v.Bottom + k;
        if (nl && nt) return Drag.TL;
        if (nr && nt) return Drag.TR;
        if (nl && nb) return Drag.BL;
        if (nr && nb) return Drag.BR;
        if (nl && inY) return Drag.L;
        if (nr && inY) return Drag.R;
        if (nt && inX) return Drag.T;
        if (nb && inX) return Drag.B;
        if (v.Contains(p)) return Drag.Move;
        return Drag.New;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_drag == Drag.None)
        {
            Cursor = HitTest(p) switch
            {
                Drag.TL or Drag.BR => Cursors.SizeNWSE,
                Drag.TR or Drag.BL => Cursors.SizeNESW,
                Drag.L or Drag.R => Cursors.SizeWE,
                Drag.T or Drag.B => Cursors.SizeNS,
                Drag.Move => Cursors.SizeAll,
                _ => Cursors.Cross,
            };
            return;
        }
        var ip = ToImage(p);
        double W = ImageSize.Width, H = ImageSize.Height;
        ip = new Point(Math.Clamp(ip.X, 0, W), Math.Clamp(ip.Y, 0, H));
        var c = _startCrop;
        double l = c.Left, t = c.Top, r = c.Right, b = c.Bottom;
        switch (_drag)
        {
            case Drag.Move:
            {
                double dx = ip.X - _anchorImg.X, dy = ip.Y - _anchorImg.Y;
                double nx = Math.Clamp(c.X + dx, 0, W - c.Width), ny = Math.Clamp(c.Y + dy, 0, H - c.Height);
                Crop = new Rect(nx, ny, c.Width, c.Height);
                Changed?.Invoke();
                InvalidateVisual();
                return;
            }
            case Drag.New: l = _anchorImg.X; t = _anchorImg.Y; r = ip.X; b = ip.Y; break;
            case Drag.L: l = ip.X; break;
            case Drag.R: r = ip.X; break;
            case Drag.T: t = ip.Y; break;
            case Drag.B: b = ip.Y; break;
            case Drag.TL: l = ip.X; t = ip.Y; break;
            case Drag.TR: r = ip.X; t = ip.Y; break;
            case Drag.BL: l = ip.X; b = ip.Y; break;
            case Drag.BR: r = ip.X; b = ip.Y; break;
        }
        var rect = new Rect(new Point(l, t), new Point(r, b));
        if (Ratio is double ratio && rect.Width > 0 && rect.Height > 0)
        {
            // keep the edge being dragged, fit the other dimension to the ratio
            bool horizontalEdge = _drag is Drag.T or Drag.B;
            double w = rect.Width, h = rect.Height;
            if (horizontalEdge || (_drag != Drag.L && _drag != Drag.R && h * ratio > w)) w = h * ratio; else h = w / ratio;
            double ax = (_drag is Drag.L or Drag.TL or Drag.BL) || (_drag == Drag.New && ip.X < _anchorImg.X) ? rect.Right - w : rect.Left;
            double ay = (_drag is Drag.T or Drag.TL or Drag.TR) || (_drag == Drag.New && ip.Y < _anchorImg.Y) ? rect.Bottom - h : rect.Top;
            if (_drag is Drag.T or Drag.B) ax = c.Left + c.Width / 2 - w / 2;
            if (_drag is Drag.L or Drag.R) ay = c.Top + c.Height / 2 - h / 2;
            rect = new Rect(ax, ay, w, h);
            // shrink to stay inside the image
            if (rect.Left < 0 || rect.Top < 0 || rect.Right > W || rect.Bottom > H)
            {
                rect.Intersect(new Rect(0, 0, W, H));
                if (rect.IsEmpty) return;
                double w2 = Math.Min(rect.Width, rect.Height * ratio);
                rect = new Rect(rect.X, rect.Y, w2, w2 / ratio);
            }
        }
        rect.Intersect(new Rect(0, 0, W, H));
        if (!rect.IsEmpty && rect.Width >= 4 && rect.Height >= 4) Crop = rect;
        Changed?.Invoke();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        _drag = HitTest(p);
        _anchorImg = ToImage(p);
        _startCrop = Crop;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _drag = Drag.None;
        ReleaseMouseCapture();
    }

    public void ApplyRatio(double? ratio)
    {
        Ratio = ratio;
        double W = ImageSize.Width, H = ImageSize.Height;
        if (ratio is double r)
        {
            // largest centred rect of that ratio inside the current crop's centre
            double w = W, h = W / r;
            if (h > H) { h = H; w = H * r; }
            w *= 0.9; h *= 0.9;
            var cc = new Point(Crop.X + Crop.Width / 2, Crop.Y + Crop.Height / 2);
            double x = Math.Clamp(cc.X - w / 2, 0, W - w), y = Math.Clamp(cc.Y - h / 2, 0, H - h);
            Crop = new Rect(x, y, w, h);
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
    readonly TextBlock _size;
    readonly FrameworkElement _controls;

    public CropEditor(string src, MagickImage img)
    {
        _src = src;
        _img = img;
        _surface.Image = Wpf.ToBitmapSource(Downscaled(img));
        _surface.ImageSize = new Size(img.Width, img.Height);
        _surface.Crop = new Rect(0, 0, img.Width, img.Height);
        _size = UiKit.Label("");
        _surface.Changed += UpdateSize;
        UpdateSize();

        var chips = new WrapPanel();
        void Ratio(string label, double? r, bool on = false) => chips.Children.Add(UiKit.Chip(label, "ratio", on, () => _surface.ApplyRatio(r)));
        Ratio(L.T("Free"), null, true);
        Ratio(L.T("Original"), img.Width / (double)img.Height);
        Ratio("1:1", 1);
        Ratio("4:3", 4 / 3.0);
        Ratio("3:2", 3 / 2.0);
        Ratio("16:9", 16 / 9.0);
        Ratio("9:16", 9 / 16.0);
        Ratio("4:5", 4 / 5.0);
        var dock = new DockPanel();
        DockPanel.SetDock(_size, Dock.Right);
        dock.Children.Add(_size);
        dock.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { UiKit.Label(L.T("Aspect")), chips } });
        _controls = dock;
    }

    static MagickImage Downscaled(MagickImage img)
    {
        var p = (MagickImage)img.Clone();
        if (Math.Max(p.Width, p.Height) > 2200) p.Resize(new MagickGeometry(2200, 2200));
        return p;
    }

    void UpdateSize()
    {
        var c = _surface.Crop;
        _size.Text = $"{(int)Math.Round(c.Width)} × {(int)Math.Round(c.Height)} px";
    }

    internal override void DevPrepare() => _surface.Crop = new Rect(_img.Width * .25, _img.Height * .25, _img.Width * .5, _img.Height * .4);

    public override string Title => L.T("Crop");
    public override FrameworkElement Preview => _surface;
    public override FrameworkElement Controls => _controls;

    public override void Reset()
    {
        _surface.Crop = new Rect(0, 0, _img.Width, _img.Height);
        _surface.Ratio = null;
        UpdateSize();
        _surface.InvalidateVisual();
    }

    public override async Task<string?> Save()
    {
        var c = _surface.Crop;
        var ext = Kinds(_src);
        var dst = Paths.Beside(_src, ext, "(cropped)");
        await Task.Run(() =>
        {
            using var copy = (MagickImage)_img.Clone();
            copy.Crop(new MagickGeometry((int)Math.Round(c.X), (int)Math.Round(c.Y), (uint)Math.Round(c.Width), (uint)Math.Round(c.Height)));
            copy.ResetPage();
            Images.PrepareFor(copy, ext, ext is "jpg" or "jpeg" ? 95 : null);
            copy.Write(dst, Images.FormatOf(ext));
        });
        return dst;
    }

    /// <summary>Keep the source format when we can write it, else PNG.</summary>
    internal static string Kinds(string src)
    {
        var e = Convertino.Kinds.Ext(src);
        return e switch
        {
            "jpg" or "jpeg" or "jfif" => "jpg",
            "png" or "webp" or "bmp" or "gif" or "tif" or "tiff" => e == "tif" ? "tiff" : e,
            "avif" when Images.CanWrite(MagickFormat.Avif) => "avif",
            "heic" or "heif" when Images.CanWrite(MagickFormat.Heic) => "heic",
            _ => "png",
        };
    }
}
