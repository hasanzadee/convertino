using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Convertino.Native;

namespace Convertino.Ui;

/// <summary>Frosted progress / result card stacked in the corner of the screen the job came from.</summary>
public sealed class ToastWindow : Window
{
    public const double CardW = 360, CardH = 74, Margin_ = 18;

    readonly Border _card;
    readonly Image _backdrop;
    readonly Border _iconBox;
    readonly Image _thumb;
    readonly TextBlock _glyph;
    readonly TextBlock _title;
    readonly TextBlock _sub;
    readonly Border _barTrack;
    readonly Border _bar;
    readonly Button _close;
    readonly Button _folder;
    readonly StackPanel _buttons;
    readonly TranslateTransform _slide = new();
    readonly DispatcherTimer _life = new();
    bool _closing, _asking;

    public IntPtr Hwnd { get; private set; }
    public POINT Anchor { get; init; }
    public Action? OnCancel { get; set; }
    /// <summary>Replaces the default click (open / reveal the output).</summary>
    public Action? OnClick { get; set; }
    public Action<ToastWindow>? AfterClose { get; set; }
    public List<string> Outputs { get; } = [];
    public bool Running { get; private set; } = true;

    public ToastWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Width = CardW + Margin_ * 2;
        Height = CardH + Margin_ * 2;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var t = Theme.Current;
        _backdrop = new Image { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_backdrop, BitmapScalingMode.Linear);

        _thumb = new Image { Stretch = Stretch.UniformToFill };
        _glyph = new TextBlock
        {
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 18,
            Foreground = new SolidColorBrush(t.AccentText),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _iconBox = new Border
        {
            Width = 42, Height = 42, CornerRadius = new CornerRadius(10),
            Background = new LinearGradientBrush(t.AccentA, t.AccentB, 45),
            ClipToBounds = true,
            Child = new Grid { Children = { _glyph, _thumb } },
            VerticalAlignment = VerticalAlignment.Center,
        };
        _iconBox.Clip = new RectangleGeometry(new Rect(0, 0, 42, 42), 10, 10);

        _title = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13.5,
            Foreground = new SolidColorBrush(t.Text),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _sub = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 12,
            Foreground = new SolidColorBrush(t.SubText),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        };
        _bar = new Border
        {
            Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0,
            Background = new LinearGradientBrush(t.AccentA, t.AccentB, 0),
        };
        _barTrack = new Border
        {
            Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 7, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(40, t.SubText.R, t.SubText.G, t.SubText.B)),
            Child = _bar, ClipToBounds = true,
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0), Children = { _title, _sub, _barTrack } };

        _folder = IconButton("", "Show in folder");
        _folder.Click += (_, _) => { if (Outputs.Count > 0) Shell.Reveal(Outputs[0]); Dismiss(); };
        _folder.Visibility = Visibility.Collapsed;
        _close = IconButton("", "Close");
        _close.Click += (_, _) => { if (Running) OnCancel?.Invoke(); Dismiss(); };
        _buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { _folder, _close } };

        var grid = new Grid { Margin = new Thickness(14, 0, 10, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(_buttons, 2);
        grid.Children.Add(_iconBox);
        grid.Children.Add(text);
        grid.Children.Add(_buttons);

        var layers = new Grid
        {
            Children =
            {
                _backdrop,
                new Rectangle { Fill = new SolidColorBrush(Theme.Solid(t.Tint)) },
                new Rectangle { Fill = new LinearGradientBrush(t.PetalTop, t.PetalBottom, 90) },
                grid,
            },
        };
        _card = new Border
        {
            Width = CardW, Height = CardH,
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(t.Stroke),
            Child = layers,
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 5, Direction = 270, Opacity = t.IsDark ? .5 : .22, Color = Colors.Black },
            RenderTransform = _slide,
            Cursor = Cursors.Hand,
        };
        layers.Clip = new RectangleGeometry(new Rect(0, 0, CardW - 2, CardH - 2), 15, 15);
        _card.MouseLeftButtonUp += (_, e) =>
        {
            if (_asking || e.OriginalSource is DependencyObject d && FindButton(d) != null) return;
            if (OnClick != null) { OnClick(); Dismiss(); return; }
            if (Outputs.Count == 1 && File.Exists(Outputs[0])) Shell.Open(Outputs[0]);
            else if (Outputs.Count > 0) Shell.Reveal(Outputs[0]);
            Dismiss();
        };
        _card.MouseEnter += (_, _) => _life.Stop();
        _card.MouseLeave += (_, _) => { if (!Running && !_asking) _life.Start(); };
        Content = new Grid { Margin = new Thickness(Margin_), Children = { _card } };

        _life.Tick += (_, _) => { _life.Stop(); Dismiss(); };
        SourceInitialized += (_, _) =>
        {
            Hwnd = new WindowInteropHelper(this).Handle;
            Win32.MakeToolWindow(Hwnd, true);
        };
    }

    static Button? FindButton(DependencyObject d)
    {
        while (d != null) { if (d is Button b) return b; d = VisualTreeHelper.GetParent(d); }
        return null;
    }

    Button IconButton(string glyph, string tip)
    {
        var t = Theme.Current;
        var b = new Button
        {
            Width = 30, Height = 30, ToolTip = tip, Cursor = Cursors.Hand, Margin = new Thickness(2, 0, 0, 0),
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Foreground = new SolidColorBrush(t.SubText) },
            Template = HoverTemplate(),
        };
        return b;
    }

    static ControlTemplate HoverTemplate()
    {
        var t = Theme.Current;
        var tpl = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border), "bd");
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        tpl.VisualTree = bd;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(t.IsDark ? (byte)40 : (byte)22, t.Text.R, t.Text.G, t.Text.B)), "bd"));
        tpl.Triggers.Add(hover);
        return tpl;
    }

    public void SetBackdrop(BitmapSource? b) => _backdrop.Source = b;

    public void SetRunning(string title, string sub, string glyph)
    {
        Running = true;
        _title.Text = title;
        _sub.Text = sub;
        _glyph.Text = glyph;
        _barTrack.Visibility = Visibility.Visible;
        _sub.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetProgress(double p)
    {
        double w = (CardW - 14 - 10 - 42 - 12 - 6 - 64) * Math.Clamp(p, 0, 1);
        _bar.BeginAnimation(WidthProperty, new DoubleAnimation(w, TimeSpan.FromMilliseconds(180)) { EasingFunction = new QuadraticEase() });
    }

    public void SetDone(string title, string sub, BitmapSource? thumb, string glyph, bool error = false)
    {
        Running = false;
        _title.Text = title;
        _sub.Text = sub;
        _sub.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;
        _barTrack.Visibility = Visibility.Collapsed;
        _glyph.Text = error ? "" : glyph;
        if (error) _iconBox.Background = new SolidColorBrush(Theme.Current.Danger);
        if (thumb != null) { _thumb.Source = thumb; _thumb.Opacity = 0; _thumb.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(250))); }
        _folder.Visibility = Outputs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _life.Interval = TimeSpan.FromSeconds(error ? 9 : 5);
        if (!_card.IsMouseOver) _life.Start();
        // little "pop" so the eye catches completion
        var s = new ScaleTransform(1, 1, 21, 21);
        _iconBox.RenderTransform = s;
        var pop = new DoubleAnimation(1.18, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4 } };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    /// <summary>A question with two answers that stays until answered; the text may take two lines.</summary>
    public void SetChoice(string title, string sub, string glyph, string yes, string no, Action<bool> answer, string? tip = null)
    {
        Running = false;
        _asking = true;
        _title.Text = title;
        _sub.Text = sub;
        _sub.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;
        _sub.TextWrapping = TextWrapping.Wrap;
        _sub.MaxHeight = 34; // two lines
        _barTrack.Visibility = Visibility.Collapsed;
        _glyph.Text = glyph;
        _folder.Visibility = Visibility.Collapsed;
        _close.Visibility = Visibility.Collapsed;
        _card.Cursor = Cursors.Arrow;
        if (tip != null) _card.ToolTip = tip;
        _buttons.Children.Insert(0, TextButton(no, false, () => { answer(false); Dismiss(); }));
        _buttons.Children.Insert(0, TextButton(yes, true, () => { answer(true); Dismiss(); }));
    }

    static Button TextButton(string text, bool primary, Action click)
    {
        var t = Theme.Current;
        var tpl = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        bd.SetValue(Border.PaddingProperty, new Thickness(10, 4, 10, 5));
        bd.SetValue(Border.BackgroundProperty, primary ? new LinearGradientBrush(t.AccentA, t.AccentB, 0) : new SolidColorBrush(Color.FromArgb(36, t.SubText.R, t.SubText.G, t.SubText.B)));
        bd.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        tpl.VisualTree = bd;
        var b = new Button
        {
            Template = tpl, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock
            {
                Text = text, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(primary ? t.AccentText : t.Text),
            },
        };
        b.Click += (_, _) => click();
        return b;
    }

    public void AnimateIn()
    {
        _slide.X = 40;
        Opacity = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }

    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _life.Stop();
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 50, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => { Close(); AfterClose?.Invoke(this); };
        BeginAnimation(OpacityProperty, fade);
    }
}

public static class Toasts
{
    static readonly List<ToastWindow> Live = [];

    public static ToastWindow Create(POINT anchor)
    {
        var w = new ToastWindow { Anchor = anchor };
        w.AfterClose = t => { Live.Remove(t); Reflow(); };
        return w;
    }

    public static void Show(ToastWindow w)
    {
        Live.Add(w);
        var (rect, _) = SlotRect(w.Anchor, Live.Count - 1);
        var cardPx = Inset(rect, w);
        w.SetBackdrop(Backdrop.Capture(cardPx, 4, 6, 1.3));
        new WindowInteropHelper(w).EnsureHandle();
        var hwnd = new WindowInteropHelper(w).Handle;
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, rect.Left, rect.Top, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        w.Show();
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, rect.Left, rect.Top, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        w.AnimateIn();
    }

    static RECT Inset(RECT r, ToastWindow w)
    {
        double s = r.Width / w.Width;
        int m = (int)Math.Round(ToastWindow.Margin_ * s);
        return new RECT { Left = r.Left + m, Top = r.Top + m, Right = r.Right - m, Bottom = r.Bottom - m };
    }

    static (RECT, double) SlotRect(POINT anchor, int slot)
    {
        var (work, _, scale) = Win32.MonitorAt(anchor);
        int w = (int)Math.Round((ToastWindow.CardW + ToastWindow.Margin_ * 2) * scale);
        int h = (int)Math.Round((ToastWindow.CardH + ToastWindow.Margin_ * 2) * scale);
        int step = (int)Math.Round((ToastWindow.CardH + 10) * scale);
        int x = work.Right - w + (int)(4 * scale);
        int y = work.Bottom - h + (int)(4 * scale) - slot * step;
        return (new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h }, scale);
    }

    static void Reflow()
    {
        for (int i = 0; i < Live.Count; i++)
        {
            var (r, _) = SlotRect(Live[i].Anchor, i);
            if (Live[i].Hwnd != IntPtr.Zero)
                Win32.SetWindowPos(Live[i].Hwnd, Win32.HWND_TOPMOST, r.Left, r.Top, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }
    }
}
