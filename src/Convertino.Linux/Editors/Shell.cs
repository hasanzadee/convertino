using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Convertino.Services;
using Convertino.Ui;
using ImageMagick;
using Orientation = Avalonia.Layout.Orientation;
using FontWeight = Avalonia.Media.FontWeight;
using TextAlignment = Avalonia.Media.TextAlignment;
using FontStyle = Avalonia.Media.FontStyle;

namespace Convertino.Editors;

/// <summary>An editor plugged into the shell: a big preview area plus a control strip.</summary>
public abstract class Editor
{
    public abstract string Title { get; }
    public abstract Control Preview { get; }
    public abstract Control Controls { get; }
    public virtual void Reset() { }
    public abstract Task<string?> Save();
    public virtual void OnKey(KeyEventArgs e) { }
    /// <summary>Dev self-test: simulate a typical edit.</summary>
    internal virtual void DevPrepare() { }
    public EditorWindow? Shell { get; set; }

    /// <summary>The platform's shortcut modifier: ⌘ on macOS, Ctrl elsewhere.</summary>
    public static bool Shortcut(KeyEventArgs e) => e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
}

public sealed class EditorWindow : Window
{
    readonly Editor _editor;
    readonly TaskCompletionSource<string?> _done = new();
    readonly Button _save;
    bool _saving;

    public Task<string?> Result => _done.Task;

    static IBrush B(Color c) => new ImmutableSolidColorBrush(c);

    public EditorWindow(Editor editor, string subtitle, Size preferred)
    {
        _editor = editor;
        editor.Shell = this;
        var t = Skin.Current;
        Title = $"{editor.Title} — {subtitle}";
        Width = preferred.Width;
        Height = preferred.Height;
        MinWidth = 520;
        MinHeight = 420;
        Background = B(t.Window);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        using (var ms = new MemoryStream()) { AppIcon.Render(64).Save(ms, PngBitmapEncoderOptions.Default); ms.Position = 0; Icon = new WindowIcon(ms); }

        var reset = new Button { Content = L.T("Reset") };
        reset.Click += (_, _) => editor.Reset();
        var cancel = new Button { Content = L.T("Cancel"), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();
        _save = new Button
        {
            Content = L.T("Save"), MinWidth = 110, FontWeight = FontWeight.SemiBold,
            Background = t.AccentGradient(), Foreground = B(t.AccentText), HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        _save.Click += async (_, _) => await DoSave();
        var footer = new Grid { Margin = new Thickness(16, 10, 16, 16) };
        footer.Children.Add(reset);
        footer.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _save } });

        var preview = new Border
        {
            Margin = new Thickness(16, 16, 16, 0), CornerRadius = new CornerRadius(12), ClipToBounds = true,
            Background = B(t.IsDark ? Color.FromArgb(70, 0, 0, 0) : Color.FromArgb(18, 0, 0, 0)), Child = editor.Preview,
        };
        var controls = new ContentControl { Content = editor.Controls, Margin = new Thickness(16, 12, 16, 0) };
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto") };
        Grid.SetRow(controls, 1);
        Grid.SetRow(footer, 2);
        grid.Children.Add(preview);
        grid.Children.Add(controls);
        grid.Children.Add(footer);
        Content = grid;

        Closed += (_, _) => _done.TrySetResult(null);
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape && FocusManager?.GetFocusedElement() is not TextBox) { Close(); e.Handled = true; return; }
            if (e.Key == Key.Enter && Editor.Shortcut(e)) { e.Handled = true; await DoSave(); return; }
            editor.OnKey(e);
        };
    }

    async Task DoSave()
    {
        if (_saving) return;
        _saving = true;
        _save.IsEnabled = false;
        _save.Content = L.T("Saving…");
        try
        {
            var path = await _editor.Save();
            if (path != null) { _done.TrySetResult(path); Close(); return; }
        }
        catch (Exception ex)
        {
            Log.Error("editor save", ex);
            Title = L.T("Couldn't save: {0}", L.Saved(ex.Message));
        }
        _save.Content = L.T("Save");
        _save.IsEnabled = true;
        _saving = false;
    }
}

/// <summary>Draws an image fitted into the control and maps between view and image pixel coordinates.</summary>
public class ImageSurface : Control
{
    public Bitmap? Image { get; set; }
    public Size ImageSize { get; set; }
    public double Pad { get; set; } = 18;
    protected double Scale { get; private set; } = 1;
    protected Point Offset { get; private set; }

    public ImageSurface()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    protected Rect ImageRect
    {
        get
        {
            if (ImageSize.Width <= 0) return default;
            double aw = Math.Max(1, Bounds.Width - Pad * 2), ah = Math.Max(1, Bounds.Height - Pad * 2);
            Scale = Math.Min(aw / ImageSize.Width, ah / ImageSize.Height);
            double w = ImageSize.Width * Scale, h = ImageSize.Height * Scale;
            Offset = new Point((Bounds.Width - w) / 2, (Bounds.Height - h) / 2);
            return new Rect(Offset, new Size(w, h));
        }
    }

    public Point ToImage(Point v) { _ = ImageRect; return new((v.X - Offset.X) / Scale, (v.Y - Offset.Y) / Scale); }
    public Point ToView(Point i) { _ = ImageRect; return new(Offset.X + i.X * Scale, Offset.Y + i.Y * Scale); }
    public Rect ToView(Rect i) => new(ToView(i.TopLeft), ToView(i.BottomRight));
    public double ViewScale { get { _ = ImageRect; return Scale; } }

    public override void Render(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        var r = ImageRect;
        if (Image == null || r.Width <= 0) return;
        DrawChecker(dc, r);
        dc.DrawImage(Image, r);
        DrawOverlay(dc, r);
    }

    protected virtual void DrawOverlay(DrawingContext dc, Rect imageRect) { }

    static void DrawChecker(DrawingContext dc, Rect r)
    {
        using (dc.PushClip(r))
        {
            dc.DrawRectangle(new ImmutableSolidColorBrush(Color.FromRgb(0xee, 0xee, 0xee)), null, r);
            var dark = new ImmutableSolidColorBrush(Color.FromRgb(0xd8, 0xd8, 0xd8));
            for (double y = r.Top; y < r.Bottom; y += 8)
                for (double x = r.Left + ((int)((y - r.Top) / 8) % 2) * 8; x < r.Right; x += 16)
                    dc.DrawRectangle(dark, null, new Rect(x, y, 8, 8));
        }
    }
}

/// <summary>Magick ⇄ Avalonia pixel bridges.</summary>
public static class Px
{
    public static WriteableBitmap ToBitmap(MagickImage img)
    {
        var (bgra, w, h) = Images.ToBgra(img);
        return ToBitmap(bgra, w, h);
    }

    public static WriteableBitmap ToBitmap(byte[] bgra, int w, int h)
    {
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var fb = bmp.Lock();
        for (int row = 0; row < h; row++) Marshal.Copy(bgra, row * w * 4, fb.Address + row * fb.RowBytes, w * 4);
        return bmp;
    }

    /// <summary>Straight-alpha BGRA from a rendered (premultiplied) bitmap.</summary>
    public static (byte[] bgra, int w, int h) Pixels(Bitmap bmp)
    {
        int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height;
        var buf = new byte[w * h * 4];
        var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try { bmp.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), buf.Length, w * 4); }
        finally { handle.Free(); }
        if (bmp.AlphaFormat == AlphaFormat.Premul)
            for (int i = 0; i < buf.Length; i += 4)
            {
                byte a = buf[i + 3];
                if (a is > 0 and < 255)
                {
                    buf[i] = (byte)Math.Min(255, buf[i] * 255 / a);
                    buf[i + 1] = (byte)Math.Min(255, buf[i + 1] * 255 / a);
                    buf[i + 2] = (byte)Math.Min(255, buf[i + 2] * 255 / a);
                }
            }
        return (buf, w, h);
    }
}

/// <summary>Small factory helpers so editor code reads tidy.</summary>
public static class Kit
{
    static IBrush B(Color c) => new ImmutableSolidColorBrush(c);

    public static TextBlock Label(string text, double minWidth = 0) => new()
    {
        Text = text, FontSize = 12, Foreground = B(Skin.Current.SubText), VerticalAlignment = VerticalAlignment.Center,
        MinWidth = minWidth, Margin = new Thickness(0, 0, 10, 0),
    };

    public static RadioButton Chip(string text, string group, bool on, Action checkedAction)
    {
        var r = new RadioButton { Content = text, GroupName = group, IsChecked = on, Margin = new Thickness(0, 0, 10, 0) };
        r.IsCheckedChanged += (_, _) => { if (r.IsChecked == true) checkedAction(); };
        return r;
    }

    public static ToggleButton Toggle(string text, bool on, Action<bool> changed)
    {
        var t = new ToggleButton { Content = text, IsChecked = on, Margin = new Thickness(0, 0, 6, 0) };
        t.IsCheckedChanged += (_, _) => changed(t.IsChecked == true);
        return t;
    }

    public static Button Btn(string text, Action click)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    public static Button Icon(string icon, string tip, Action click)
    {
        var b = new Button
        {
            Width = 34, Height = 32, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 0),
            Content = new IconView { Key = icon, Width = 16, Height = 16, Brush = B(Skin.Current.Text), Weight = 1.8 },
        };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    public static Slider Slider(double min, double max, double value, Action<double> changed, double width = 150)
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = value, Width = width, VerticalAlignment = VerticalAlignment.Center };
        s.ValueChanged += (_, e) => changed(e.NewValue);
        return s;
    }

    public static Control LabeledSlider(string label, Slider s, out TextBlock value, double labelWidth = 74)
    {
        value = new TextBlock { FontSize = 12, Foreground = B(Skin.Current.SubText), Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var d = new DockPanel { Margin = new Thickness(0, 0, 18, 4) };
        d.Children.Add(Label(label, labelWidth));
        DockPanel.SetDock(value, Dock.Right);
        d.Children.Add(value);
        d.Children.Add(s);
        return d;
    }

    public static Border Swatch(IBrush fill, bool selected, Action click, string? tip = null, double size = 26)
    {
        var b = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = fill, Margin = new Thickness(0, 0, 7, 6),
            Cursor = new Cursor(StandardCursorType.Hand), BorderThickness = new Thickness(selected ? 2.5 : 1),
            BorderBrush = selected ? B(Skin.Current.AccentA) : B(Color.FromArgb(60, 128, 128, 128)),
        };
        if (tip != null) ToolTip.SetTip(b, tip);
        b.PointerReleased += (_, _) => click();
        return b;
    }

    public static Color Hex(string s) => Color.Parse(s);
}
