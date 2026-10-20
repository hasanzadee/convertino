using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Convertino.Ui;

/// <summary>Glass progress / result card in the corner of the screen the job came from.</summary>
public sealed class ToastWindow : Window
{
    public const double CardW = 360, CardH = 74;

    readonly Border _iconBox;
    readonly IconView _icon = new() { Width = 20, Height = 20 };
    readonly Image _thumb = new() { Stretch = Stretch.UniformToFill };
    readonly TextBlock _title, _sub;
    readonly Border _bar, _track;
    readonly Button _folder, _close;
    readonly StackPanel _buttons;
    readonly Border _card;
    readonly DispatcherTimer _life = new();
    bool _closing, _asking;

    public ScreenPoint Anchor { get; init; }
    public Action? OnCancel { get; set; }
    /// <summary>Replaces the default click (open / reveal the output).</summary>
    public Action? OnClick { get; set; }
    public Action<ToastWindow>? AfterClose { get; set; }
    public List<string> Outputs { get; } = [];
    public bool Running { get; private set; } = true;

    static IBrush B(Color c) => new ImmutableSolidColorBrush(c);

    public ToastWindow()
    {
        var t = Skin.Current;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Width = CardW + 20;
        Height = CardH + 20;
        Title = "Convertino";

        _icon.Brush = B(t.AccentText);
        _iconBox = new Border
        {
            Width = 42, Height = 42, CornerRadius = new CornerRadius(10), ClipToBounds = true,
            Background = t.AccentGradient(), VerticalAlignment = VerticalAlignment.Center,
            Child = new Panel { Children = { _icon, _thumb } },
        };
        _title = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 13.5, Foreground = B(t.Text), TextTrimming = TextTrimming.CharacterEllipsis };
        _sub = new TextBlock { FontSize = 12, Foreground = B(t.SubText), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        _bar = new Border { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Background = t.AccentGradient() };
        _track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 7, 0, 0), Background = B(Skin.WithA(t.SubText, .16)), Child = _bar };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0), Children = { _title, _sub, _track } };

        _folder = IconButton("folder", "Show in folder");
        _folder.Click += (_, _) => { if (Outputs.Count > 0) Platform.Current.Reveal(Outputs[0]); Dismiss(); };
        _folder.IsVisible = false;
        _close = IconButton("x", "Close");
        _close.Click += (_, _) => { if (Running) OnCancel?.Invoke(); Dismiss(); };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(14, 0, 10, 0) };
        Grid.SetColumn(text, 1);
        _buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { _folder, _close } };
        Grid.SetColumn(_buttons, 2);
        grid.Children.Add(_iconBox);
        grid.Children.Add(text);
        grid.Children.Add(_buttons);

        var card = _card = new Border
        {
            Width = CardW, Height = CardH, Margin = new Thickness(10),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            BorderBrush = B(t.Stroke),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(t.Solid(t.Window), 0), new GradientStop(t.Solid(Skin.WithA(t.Window, .9)), 1) },
            },
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 18, OffsetY = 4, Color = Skin.WithA(Colors.Black, t.IsDark ? .45 : .2) }),
            Child = grid,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        card.PointerReleased += (_, e) =>
        {
            if (_asking || e.Source is Visual v && IsInButton(v)) return;
            if (OnClick != null) { OnClick(); Dismiss(); return; }
            if (Outputs.Count == 1 && File.Exists(Outputs[0])) Platform.Current.Open(Outputs[0]);
            else if (Outputs.Count > 0) Platform.Current.Reveal(Outputs[0]);
            Dismiss();
        };
        card.PointerEntered += (_, _) => _life.Stop();
        card.PointerExited += (_, _) => { if (!Running && !_asking) _life.Start(); };
        Content = card;
        _life.Tick += (_, _) => { _life.Stop(); Dismiss(); };
        Opacity = 0;
        Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) }];
        _bar.Transitions = [new DoubleTransition { Property = WidthProperty, Duration = TimeSpan.FromMilliseconds(180) }];
    }

    static bool IsInButton(Visual v)
    {
        for (Visual? x = v; x != null; x = x.GetVisualParent()) if (x is Button) return true;
        return false;
    }

    static Button IconButton(string icon, string tip)
    {
        var b = new Button
        {
            Width = 30, Height = 30, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Content = new IconView { Key = icon, Width = 14, Height = 14, Brush = B(Skin.Current.SubText) },
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(b, tip);
        return b;
    }

    public void SetRunning(string title, string sub, string? icon)
    {
        Running = true;
        _title.Text = title;
        _sub.Text = sub;
        _sub.IsVisible = !string.IsNullOrEmpty(sub);
        _icon.Key = icon ?? "file";
        _track.IsVisible = true;
    }

    public void SetProgress(double p) => _bar.Width = (CardW - 14 - 10 - 42 - 12 - 6 - 64) * Math.Clamp(p, 0, 1);

    public void SetDone(string title, string sub, Bitmap? thumb, string? icon, bool error = false, double? seconds = null)
    {
        Running = false;
        _title.Text = title;
        _sub.Text = sub;
        _sub.IsVisible = !string.IsNullOrEmpty(sub);
        _track.IsVisible = false;
        _icon.Key = error ? "alert" : icon ?? "check";
        _icon.InvalidateVisual();
        if (error) _iconBox.Background = B(Skin.Current.Danger);
        if (thumb != null) _thumb.Source = thumb;
        _folder.IsVisible = Outputs.Count > 0;
        _life.Interval = TimeSpan.FromSeconds(seconds ?? (error ? 9 : 5));
        _life.Start();
    }

    /// <summary>A question with two answers that stays until answered; the text may take two lines.</summary>
    public void SetChoice(string title, string sub, string? icon, string yes, string no, Action<bool> answer, string? tip = null)
    {
        Running = false;
        _asking = true;
        _title.Text = title;
        _sub.Text = sub;
        _sub.IsVisible = !string.IsNullOrEmpty(sub);
        _sub.TextWrapping = TextWrapping.Wrap;
        _sub.MaxLines = 2;
        _track.IsVisible = false;
        _icon.Key = icon ?? "check";
        _folder.IsVisible = false;
        _close.IsVisible = false;
        _card.Cursor = Cursor.Default;
        if (tip != null) ToolTip.SetTip(_card, tip);
        _buttons.Children.Insert(0, TextButton(no, false, () => { answer(false); Dismiss(); }));
        _buttons.Children.Insert(0, TextButton(yes, true, () => { answer(true); Dismiss(); }));
    }

    static Button TextButton(string text, bool primary, Action click)
    {
        var t = Skin.Current;
        var b = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = B(primary ? t.AccentText : t.Text) },
            Background = primary ? t.AccentGradient() : B(Skin.WithA(t.SubText, .14)),
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4), Margin = new Thickness(0, 0, 4, 0), MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand),
        };
        b.Click += (_, _) => click();
        return b;
    }

    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _life.Stop();
        Opacity = 0;
        DispatcherTimer.RunOnce(() => { Close(); AfterClose?.Invoke(this); }, TimeSpan.FromMilliseconds(220));
    }
}

public static class Toasts
{
    static readonly List<ToastWindow> Live = [];
    public static TopLevel? ScreensHost { get; set; }

    /// <summary>Per-platform window setup before a card is first shown (X11: override-redirect; macOS: float above all spaces).</summary>
    public static Action<ToastWindow>? Prepare { get; set; }

    public static ToastWindow Create(ScreenPoint anchor)
    {
        var w = new ToastWindow { Anchor = anchor };
        Prepare?.Invoke(w);
        w.AfterClose = t => { Live.Remove(t); Reflow(); };
        return w;
    }

    public static void Show(ToastWindow w)
    {
        Live.Add(w);
        w.Position = Slot(w, Live.Count - 1);
        w.Show();
        w.Position = Slot(w, Live.IndexOf(w));
        w.Opacity = 1;
    }

    static PixelPoint Slot(ToastWindow w, int slot)
    {
        var screens = (ScreensHost ?? w).Screens!;
        var s = screens.ScreenFromPoint(new PixelPoint(w.Anchor.X, w.Anchor.Y)) ?? screens.Primary;
        var work = s?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        double scale = s?.Scaling ?? 1;
        int wpx = (int)Math.Round(w.Width * scale), hpx = (int)Math.Round(w.Height * scale);
        int step = (int)Math.Round((ToastWindow.CardH + 10) * scale);
        return new PixelPoint(work.Right - wpx - (int)(6 * scale), work.Bottom - hpx - (int)(6 * scale) - slot * step);
    }

    static void Reflow()
    {
        for (int i = 0; i < Live.Count; i++) Live[i].Position = Slot(Live[i], i);
    }
}
