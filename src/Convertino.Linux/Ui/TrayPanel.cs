using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Convertino.Overlay;
using Convertino.Services;

namespace Convertino.Ui;

/// <summary>
/// Quick-settings panel next to the tray icon / menu-bar item, like the Windows one: two weeks of activity (or how
/// to start until there is any), theme, options, recent files.
/// </summary>
public sealed class TrayPanel : Window
{
    const double W = 340, Pad = 18, BarsH = 58;

    /// <summary>Where the pointer is now; the panel opens next to it (the tray icon, the menu-bar item).</summary>
    public Func<ScreenPoint>? Pointer { get; init; }

    /// <summary>Thumbnail for a recent file (path, size in DIPs), called off the UI thread.</summary>
    public Func<string, int, Bitmap?>? Thumbnail { get; init; }

    /// <summary>A setting changed here (the tray menu redraws its check marks).</summary>
    public event Action? SettingsChanged;

    readonly TranslateTransform _slide = new();
    readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(15) };
    readonly List<Tween> _tweens = [];
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Dictionary<string, Control> _chips = [];
    readonly Dictionary<string, Bitmap> _thumbs = []; // for as long as the panel is open, so a restyle doesn't flash
    Stats? _stats;
    TextBlock? _subtitle;
    ScrollViewer? _scroll;
    bool _animate; // only the build that opens the panel plays the intro
    bool _hiding, _top;
    bool _settled; // the slide-in has played: the swatches are where a click finds them
    string? _logged;
    double _shownAt = -1e9, _hiddenAt = -1e9;
    ScreenPoint? _anchor;

    public TrayPanel()
    {
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        SizeToContent = SizeToContent.Height;
        Width = W + Pad * 2;
        Title = "Convertino";
        _ticker.Tick += (_, _) => Tick();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) HideAnimated(); };
        // focus moving elsewhere closes it too (a click outside is caught by ClickAt, focus or not)
        Deactivated += (_, _) => { if (!_picking && Now - _shownAt > 300) HideAnimated(); };
        // restyle in place (a swatch here or the tray menu), scrolled where it was
        Skin.Changed += () =>
        {
            if (!IsVisible || _hiding) return;
            Rebuild();
            if (OverlayController.Diag) Log.Info($"panel theme {Skin.Current.Name}");
            LogSwatches();
        };
        PositionChanged += (_, _) => LogSwatches();
        SizeChanged += (_, _) => LogSwatches();
    }

    void Rebuild()
    {
        var offset = _scroll?.Offset ?? default;
        Build();
        UpdateLayout();
        if (_scroll != null) _scroll.Offset = offset;
        Place();
    }

    double Now => _clock.Elapsed.TotalMilliseconds;

    public void Toggle()
    {
        if (IsVisible && !_hiding) { HideAnimated(); return; }
        if (Now - _hiddenAt < 250) return; // the click on the tray icon that just closed us
        Open();
    }

    /// <summary>Show (or refresh) the panel; <paramref name="stats"/> replaces stats.json (CONVERTINO_PANEL=demo).</summary>
    public void Open(Stats? stats = null)
    {
        _tweens.Clear();
        _hiding = false;
        _thumbs.Clear();
        _stats = stats ?? Stats.Load();
        _anchor = Pointer?.Invoke();
        _animate = true;
        Build();
        _animate = false;
        Opacity = 0;
        Place();
        Show();
        UpdateLayout();
        Place(); // SizeToContent has settled now
        Activate();
        _shownAt = Now;
        _settled = false;
        _logged = null;
        _slide.Y = _top ? -24 : 24;
        Animate(0, 300, CubicOut, v => _slide.Y = (_top ? -24 : 24) * (1 - v), () => { _settled = true; LogSwatches(); });
        Animate(0, 200, t => t, v => Opacity = v);
    }

    /// <summary>
    /// Diag: where the swatches are on screen, so tests can click them. Logged once the slide-in has played, then
    /// again whenever the window moves, resizes or restyles: the window settles after Show on its own time (X11
    /// confirms a move later, macOS lets SizeToContent grow it once shown), so the last line is where they are.
    /// </summary>
    void LogSwatches()
    {
        if (!OverlayController.Diag || !_settled) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible || _hiding || !_settled) return;
            string Centre(Control c) { var p = c.PointToScreen(new Point(c.Bounds.Width / 2, 23)); return $"{p.X},{p.Y}"; }
            var line = $"panel at {Position} {Bounds.Width}x{Bounds.Height} top={_top} empty={_stats?.IsEmpty} swatches "
                + string.Join(" ", _chips.Select(c => $"{c.Key}={Centre(c.Value)}"));
            if (line == _logged) return;
            _logged = line;
            Log.Info(line);
        }, DispatcherPriority.Background);
    }

    public void HideAnimated()
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        _hiddenAt = Now;
        _tweens.Clear();
        double y0 = _slide.Y, o0 = Opacity;
        Animate(0, 140, t => t, v => { _slide.Y = y0 + (_top ? -14 : 14) * v; Opacity = o0 * (1 - v); }, () =>
        {
            Hide();
            _hiding = false;
        });
    }

    /// <summary>A mouse button went down somewhere on screen (global input): outside the panel, that closes it.</summary>
    public void ClickAt(ScreenPoint p)
    {
        if (!IsVisible || _hiding || _picking || Now - _shownAt < 150) return;
        double s = DesktopScaling;
        var r = new PixelRect(Position, new PixelSize((int)Math.Round(Bounds.Width * s), (int)Math.Round(Bounds.Height * s)));
        if (!r.Contains(new PixelPoint(p.X, p.Y))) HideAnimated();
    }

    /// <summary>
    /// Next to the pointer, in the half of the screen it is in: under a top bar / the menu bar, or above a bottom
    /// panel. Without a pointer, the bottom-right corner like the toasts.
    /// </summary>
    void Place()
    {
        var screens = (Toasts.ScreensHost ?? this).Screens;
        var p = _anchor;
        var s = (p is { } a ? screens?.ScreenFromPoint(new PixelPoint(a.X, a.Y)) : null) ?? screens?.Primary;
        var work = s?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        double scale = s?.Scaling ?? 1;
        int wpx = (int)Math.Round(Width * scale);
        double hDip = Bounds.Height > 0 ? Bounds.Height : 300;
        MaxHeight = work.Height / scale + Pad * 2 - 12;
        int hpx = (int)Math.Round(Math.Min(hDip, MaxHeight) * scale);
        int edge = (int)Math.Round((Pad - 6) * scale); // the shadow margin may hang off the work area
        _top = p is { } q && q.Y < work.Y + work.Height / 2;
        int x = p is { } c ? c.X - wpx / 2 : int.MaxValue;
        x = Math.Clamp(x, work.X - edge, work.Right - wpx + edge);
        int y = _top ? work.Y - edge : work.Bottom - hpx + edge;
        Position = new PixelPoint(x, y);
    }

    // ---------- tweens (one timer drives the open/close and the activity intro) ----------

    sealed class Tween(double start, double ms, Func<double, double> ease, Action<double> apply, Action? done)
    {
        public readonly double Start = start, Ms = ms;
        public readonly Func<double, double> Ease = ease;
        public readonly Action<double> Apply = apply;
        public readonly Action? Done = done;
    }

    void Animate(double delay, double ms, Func<double, double> ease, Action<double> apply, Action? done = null)
    {
        _tweens.Add(new Tween(Now + delay, ms, ease, apply, done));
        if (!_ticker.IsEnabled) _ticker.Start();
    }

    void Tick()
    {
        double now = Now;
        foreach (var tw in _tweens.ToList())
        {
            if (now < tw.Start) continue;
            double t = Math.Clamp((now - tw.Start) / tw.Ms, 0, 1);
            tw.Apply(t >= 1 ? 1 : tw.Ease(t));
            if (t < 1) continue;
            _tweens.Remove(tw);
            tw.Done?.Invoke();
        }
        if (_tweens.Count == 0) _ticker.Stop();
    }

    static double CubicOut(double t) => 1 - Math.Pow(1 - t, 3);

    /// <summary>WPF's BackEase (EaseOut): a small overshoot, then settle.</summary>
    static Func<double, double> BackOut(double amplitude) => t =>
    {
        double u = 1 - t;
        return 1 - (u * u * u - u * amplitude * Math.Sin(Math.PI * u));
    };

    // ---------- content ----------

    Skin T => Skin.Current;
    static IBrush B(Color c) => new ImmutableSolidColorBrush(c);
    static Color A(Color c, double a) => Skin.WithA(c, a);

    static LinearGradientBrush Gradient(Color a, Color b, bool vertical) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = vertical ? new RelativePoint(0, 1, RelativeUnit.Relative) : new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) },
    };

    void Build()
    {
        var s = Settings.Current;
        var key = s.TriggerKey;
        var stack = new StackPanel();

        // header
        var logo = new Image { Source = AppIcon.Render(68, T), Width = 34, Height = 34 };
        var title = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = "Convertino", FontWeight = FontWeight.SemiBold, FontSize = 17, Foreground = B(T.Text) });
        title.Children.Add(_subtitle = new TextBlock { FontSize = 12, Foreground = B(T.SubText) });
        Subtitle();
        var header = new DockPanel { LastChildFill = false };
        var close = IconButton("x", HideAnimated);
        ToolTip.SetTip(close, "Close (Esc)");
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { logo, title } });

        // what you've been doing; until there's anything, how to start
        var stats = _stats ??= Stats.Load();
        if (!stats.IsEmpty)
        {
            stack.Children.Add(Label("Activity · 2 weeks"));
            stack.Children.Add(Activity(stats));
        }
        else stack.Children.Add(Card(new StackPanel
        {
            Children =
            {
                HowRow("file", $"Drag any file, {key.Tap()}", "the flower opens under your cursor"),
                HowRow("flip", $"{key.KeyName()} again, Tab or wheel", "next ring; Shift+Tab goes back"),
                OperatingSystem.IsMacOS()
                    ? HowRow("folder", "Right-click → Quick Actions", "Convertino: same menu in Finder")
                    : HowRow("folder", "Right-click → Convertino", "same menu in your file manager"),
            },
        }, new Thickness(0, 14, 0, 0)));

        // theme picker
        stack.Children.Add(Label("Theme"));
        var themes = new UniformGrid { Columns = 4, Margin = new Thickness(-4, 0, -4, 0) };
        _chips.Clear();
        foreach (var th in Skin.All) themes.Children.Add(_chips[th.Name] = ThemeChip(th, th.Name == T.Name));
        stack.Children.Add(themes);

        // the tray menu's toggles
        stack.Children.Add(Label("Options"));
        stack.Children.Add(Card(new StackPanel
        {
            Children =
            {
                ToggleRow("Enabled", s.Enabled, v => { s.Enabled = v; Subtitle(); }),
                TriggerRow(key),
                ToggleRow("Notifications", s.Notifications, v => s.Notifications = v),
                ToggleRow("Tell me about new versions", s.CheckForUpdates == true, Updates.Answer),
                ToggleRow(OperatingSystem.IsMacOS() ? "Open at login" : "Start at login", s.LaunchAtStartup,
                    v => { s.LaunchAtStartup = v; Platform.Current.SetAutostart(v); }),
            },
        }, new Thickness(0)));

        // folders whose new files convert by themselves
        stack.Children.Add(Label("Watch folders"));
        stack.Children.Add(WatchCard());

        // recent
        var recent = s.Recent.Where(r => File.Exists(r.Path) || Directory.Exists(r.Path)).Take(4).ToList();
        if (recent.Count > 0)
        {
            stack.Children.Add(Label("Recent"));
            var list = new StackPanel();
            foreach (var r in recent) list.Children.Add(RecentRow(r));
            stack.Children.Add(Card(list, new Thickness(0)));
        }

        // footer
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        var quit = FooterButton("power", "Quit", () => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());
        DockPanel.SetDock(quit, Dock.Right);
        footer.Children.Add(quit);
        footer.Children.Add(FooterButton("log", "Log", () => Platform.Current.Open(Paths.LogFile)));
        footer.Children.Add(FooterButton("update", "Updates", UpdateCards.CheckNow));

        // header and footer stay put; only the middle scrolls when the screen is short
        var body = new DockPanel { Margin = new Thickness(18, 16, 18, 14) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(header);
        body.Children.Add(footer);
        body.Children.Add(_scroll = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });

        Content = new Border
        {
            Margin = new Thickness(Pad),
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            BorderBrush = B(T.Stroke),
            Background = B(T.Solid(T.Window)),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 26, OffsetY = 6, Color = A(Colors.Black, T.IsDark ? .55 : .25) }),
            RenderTransform = _slide,
            Child = new Border
            {
                CornerRadius = new CornerRadius(17),
                Background = Gradient(A(T.PetalTop, T.PetalTop.A / 255.0 * .7), T.PetalBottom, true),
                Child = body,
            },
        };
    }

    TextBlock Label(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontWeight = FontWeight.SemiBold, FontSize = 10.5,
        Foreground = B(A(T.SubText, .8)), Margin = new Thickness(4, 16, 0, 7),
    };

    Border Card(Control child, Thickness margin) => new()
    {
        Background = B(T.IsDark ? A(Colors.White, .05) : A(Colors.White, .45)),
        BorderBrush = B(T.IsDark ? A(Colors.White, .06) : A(Colors.White, .7)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(12, 6, 12, 6),
        Margin = margin,
        Child = child,
    };

    // ---------- activity ----------

    Control Activity(Stats st)
    {
        var days = st.Last(14);
        int files = days.Sum(d => d.Day.Files);
        long saved = days.Sum(d => d.Day.Saved);
        int streak = st.Streak();
        var accent = Gradient(T.AccentA, T.AccentB, false);

        // three big numbers that count up as the panel opens
        var nums = new UniformGrid { Columns = 3 };
        nums.Children.Add(Stat(files, v => ((long)v).ToString("N0"), files == 1 ? "file" : "files", accent));
        nums.Children.Add(Stat(saved, v => Paths.HumanSize((long)v), "saved", accent));
        nums.Children.Add(Stat(streak, v => ((long)v).ToString(), "day streak", accent));

        // one bar per day; hovering one swaps the date row for that day's numbers
        int max = Math.Max(1, days.Max(d => d.Day.Files));
        var bars = new Grid { Height = BarsH, Margin = new Thickness(0, 14, 0, 0), ColumnDefinitions = new ColumnDefinitions(string.Join(",", days.Select(_ => "*"))) };
        var first = new TextBlock { Text = days[0].Date.ToString("d MMM"), FontSize = 10.5, Foreground = B(T.SubText), HorizontalAlignment = HorizontalAlignment.Left };
        var today = new TextBlock { Text = "Today", FontSize = 10.5, Foreground = B(T.SubText), HorizontalAlignment = HorizontalAlignment.Right };
        var caption = new TextBlock { FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = B(T.Text), HorizontalAlignment = HorizontalAlignment.Center, IsVisible = false };
        var dates = new Grid { Margin = new Thickness(0, 5, 0, 0), Children = { first, today, caption } };

        var vertical = Gradient(T.AccentA, T.AccentB, true);
        var spring = BackOut(.35);
        for (int i = 0; i < days.Count; i++)
        {
            var (date, day) = days[i];
            bool isToday = i == days.Count - 1;
            // headroom on top for the ease's overshoot
            double h = day.Files == 0 ? 4 : 8 + (BarsH - 12) * day.Files / max;
            var bar = new Border
            {
                Width = 11, Height = _animate ? 0 : h, CornerRadius = new CornerRadius(day.Files == 0 ? 2 : 4),
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = day.Files == 0 ? B(A(T.SubText, .25)) : vertical,
                Opacity = day.Files == 0 || isToday ? 1 : T.IsDark ? .68 : .55,
            };
            if (_animate) Animate(140 + 28 * i, 520, spring, v => bar.Height = Math.Max(0, h * v));
            var col = new Border { Background = Brushes.Transparent, Child = bar, Cursor = new Cursor(StandardCursorType.Hand) };
            Grid.SetColumn(col, i);
            double rest = bar.Opacity;
            col.PointerEntered += (_, _) =>
            {
                bar.Opacity = 1;
                caption.Text = $"{date:ddd d MMM} · {day.Files} {(day.Files == 1 ? "file" : "files")}" + (day.Saved > 0 ? $" · {Paths.HumanSize(day.Saved)} saved" : "");
                caption.IsVisible = true;
                first.Opacity = today.Opacity = 0;
            };
            col.PointerExited += (_, _) =>
            {
                bar.Opacity = rest;
                caption.IsVisible = false;
                first.Opacity = today.Opacity = 1;
            };
            bars.Children.Add(col);
        }

        var body = new StackPanel { Children = { nums, bars, dates } };

        // what you reach for most
        var top = st.Top(14, 3);
        if (top.Count > 0)
        {
            var chips = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            foreach (var (name, count) in top)
            {
                var tb = new TextBlock
                {
                    FontSize = 11.5,
                    Inlines = new InlineCollection
                    {
                        new Run(name) { FontWeight = FontWeight.SemiBold, Foreground = B(T.Text) },
                        new Run($"  ×{count}") { Foreground = B(T.SubText) },
                    },
                };
                chips.Children.Add(new Border
                {
                    Child = tb, CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3, 9, 4), Margin = new Thickness(0, 0, 6, 4),
                    Background = B(A(T.AccentA, T.IsDark ? .16 : .13)),
                    BorderBrush = B(A(T.AccentA, T.IsDark ? .28 : .22)), BorderThickness = new Thickness(1),
                });
            }
            body.Children.Add(chips);
        }

        var card = Card(body, new Thickness(0));
        card.Padding = new Thickness(14, 12, 14, 10);
        return card;
    }

    Control Stat(double value, Func<double, string> fmt, string caption, IBrush accent)
    {
        var big = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 21, Foreground = accent, HorizontalAlignment = HorizontalAlignment.Left, Text = fmt(_animate ? 0 : value) };
        if (_animate && value > 0) Animate(120, 750, CubicOut, v => big.Text = fmt(v >= 1 ? value : value * v));
        var sp = new StackPanel();
        sp.Children.Add(big);
        sp.Children.Add(new TextBlock { Text = caption, FontSize = 11, Foreground = B(T.SubText), Margin = new Thickness(1, -1, 0, 0) });
        return sp;
    }

    Control HowRow(string icon, string title, string sub)
    {
        var box = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 10, 0),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(A(T.AccentA, .25), 0), new GradientStop(A(T.AccentB, .2), 1) },
            },
            Child = new IconView { Key = icon, Width = 14, Height = 14, Brush = B(T.AccentA), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 12.5, Foreground = B(T.Text) });
        text.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = B(T.SubText) });
        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5), Children = { box, text } };
    }

    Border IconButton(string icon, Action click)
    {
        var b = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Top,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new IconView { Key = icon, Width = 13, Height = 13, Brush = B(T.SubText), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        b.PointerEntered += (_, _) => b.Background = B(T.IsDark ? A(Colors.White, .07) : A(Colors.White, .6));
        b.PointerExited += (_, _) => b.Background = Brushes.Transparent;
        b.PointerReleased += (_, _) => click();
        return b;
    }

    // ---------- theme, options, recent ----------

    void Subtitle()
    {
        var s = Settings.Current;
        if (_subtitle != null) _subtitle.Text = s.Enabled ? $"Drag a file · {s.TriggerKey.Tap()}" : "Paused";
    }

    static LinearGradientBrush Diagonal(Color a, Color b) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) },
    };

    Control ThemeChip(Skin th, bool selected)
    {
        var ring = new Ellipse { Width = 54, Height = 54, Margin = new Thickness(-4), StrokeThickness = 2 };
        ring.Stroke = selected ? Diagonal(T.AccentA, T.AccentB) : null;
        var sw = new Grid { Width = 46, Height = 46 };
        sw.Children.Add(new Ellipse { Fill = B(th.Window), Stroke = B(A(T.SubText, .25)), StrokeThickness = 1 });
        sw.Children.Add(new Ellipse { Width = 22, Height = 22, Fill = Diagonal(th.AccentA, th.AccentB) });
        sw.Children.Add(ring);
        var col = new StackPanel { Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), HorizontalAlignment = HorizontalAlignment.Center };
        col.Children.Add(sw);
        col.Children.Add(new TextBlock { Text = th.Name, FontSize = 11, Foreground = B(selected ? T.Text : T.SubText), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
        if (!selected)
        {
            col.PointerEntered += (_, _) => ring.Stroke = B(A(T.SubText, .45));
            col.PointerExited += (_, _) => ring.Stroke = null;
        }
        col.PointerReleased += (_, _) =>
        {
            if (th.Name == Skin.Current.Name) return;
            Skin.Choose(th.Name); // rebuilds this panel through Skin.Changed
            SettingsChanged?.Invoke();
        };
        return col;
    }

    /// <summary>"Open with" and a segment per key, like the Windows panel; "Shift ×2" leaves a single Shift to apps that use it on drop.</summary>
    Control TriggerRow(TriggerKey current)
    {
        var keys = new UniformGrid { Columns = TriggerKeys.All.Length, Margin = new Thickness(-2, 7, -2, 0) };
        foreach (var k in TriggerKeys.All)
        {
            bool on = k == current;
            IBrush rest = on ? Gradient(T.AccentA, T.AccentB, false) : B(A(T.SubText, .12));
            var pill = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(4, 4, 4, 5), Margin = new Thickness(2, 0, 2, 0),
                Background = rest, Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = k == TriggerKey.ShiftTwice ? "Shift ×2" : k.Label(), FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = B(on ? T.AccentText : T.Text), FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal,
                },
            };
            if (k == TriggerKey.ShiftTwice) ToolTip.SetTip(pill, "Double-tap Shift while dragging: a single Shift stays free for the app you drop on");
            if (!on)
            {
                pill.PointerEntered += (_, _) => pill.Background = B(A(T.SubText, .22));
                pill.PointerExited += (_, _) => pill.Background = rest;
            }
            pill.PointerReleased += (_, _) =>
            {
                if (Settings.Current.TriggerKey == k) return;
                Settings.Current.TriggerKey = k;
                Settings.Current.Save();
                Rebuild(); // the header and the how-to card name the key
                SettingsChanged?.Invoke();
            };
            keys.Children.Add(pill);
        }
        var row = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        row.Children.Add(new TextBlock { Text = "Open with", FontSize = 13, Foreground = B(T.Text) });
        row.Children.Add(keys);
        return row;
    }

    /// <summary>A whole-row switch; <paramref name="set"/> changes the setting, saving is done here.</summary>
    Control ToggleRow(string text, bool value, Action<bool> set)
    {
        bool state = value;
        var knobT = new TranslateTransform(state ? 16 : 0, 0);
        var knob = new Ellipse { Width = 14, Height = 14, Margin = new Thickness(2, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, RenderTransform = knobT };
        var track = new Border { Width = 36, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = knob, VerticalAlignment = VerticalAlignment.Center };
        void Paint()
        {
            track.Background = state ? Gradient(T.AccentA, T.AccentB, false) : B(A(T.SubText, .12));
            track.BorderBrush = state ? Brushes.Transparent : B(A(T.SubText, .5));
            knob.Fill = B(state ? T.AccentText : A(T.SubText, .9));
        }
        Paint();
        var row = new DockPanel { Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Margin = new Thickness(0, 6, 0, 6) };
        DockPanel.SetDock(track, Dock.Right);
        row.Children.Add(track);
        row.Children.Add(new TextBlock { Text = text, FontSize = 13, Foreground = B(T.Text), VerticalAlignment = VerticalAlignment.Center });
        row.PointerReleased += (_, _) =>
        {
            state = !state;
            Paint();
            double from = knobT.X, to = state ? 16 : 0;
            Animate(0, 180, CubicOut, v => knobT.X = from + (to - from) * v);
            set(state);
            Settings.Current.Save();
            SettingsChanged?.Invoke();
        };
        return row;
    }

    Control RecentRow(RecentItem r)
    {
        var thumb = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(6), ClipToBounds = true, Margin = new Thickness(0, 0, 10, 0),
            Background = B(A(T.SubText, .14)),
            Child = new IconView { Key = Directory.Exists(r.Path) ? "folder" : "file", Width = 14, Height = 14, Brush = B(T.SubText), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        void Picture(Bitmap bmp) => thumb.Child = new Image { Source = bmp, Width = 30, Height = 30, Stretch = Stretch.UniformToFill };
        if (_thumbs.TryGetValue(r.Path, out var known)) Picture(known);
        else if (Thumbnail is { } render && File.Exists(r.Path))
            Task.Run(() => render(r.Path, 30)).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result is { } bmp)
                    Dispatcher.UIThread.Post(() => { _thumbs[r.Path] = bmp; Picture(bmp); });
            });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(r.Path), FontSize = 12.5, Foreground = B(T.Text), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 230 });
        text.Children.Add(new TextBlock { Text = $"{r.Label} · {Ago(r.When)}", FontSize = 11, Foreground = B(T.SubText) });
        var row = new Border
        {
            Background = Brushes.Transparent, CornerRadius = new CornerRadius(8), Padding = new Thickness(4), Margin = new Thickness(-4, 1, -4, 1),
            Cursor = new Cursor(StandardCursorType.Hand), Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { thumb, text } },
        };
        row.PointerEntered += (_, _) => row.Background = B(T.IsDark ? A(Colors.White, .06) : A(Colors.White, .6));
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        row.PointerReleased += (_, _) => { Platform.Current.Reveal(r.Path); HideAnimated(); };
        return row;
    }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        return t.ToString("d MMM");
    }

    // ---------- watch folders ----------

    string? _pendingWatch; // picked, waiting for "new files become …"
    bool _picking;         // the folder dialog is up: losing focus to it mustn't close the panel

    Control WatchCard()
    {
        var list = new StackPanel();
        foreach (var wf in Watches.Load())
        {
            var path = wf.Path;
            var icon = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 10, 0), Background = B(A(T.SubText, .14)),
                Child = new IconView { Key = "folder", Width = 14, Height = 14, Brush = B(T.SubText), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(path.TrimEnd('/', '\\')), FontSize = 12.5, Foreground = B(T.Text), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 210 });
            text.Children.Add(new TextBlock { Text = $"new files → {Watches.LabelOf(wf.Action)} · in {wf.Out}" + (Directory.Exists(path) ? "" : " · missing"), FontSize = 11, Foreground = B(T.SubText) });
            var main = new Border
            {
                Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, text } },
            };
            ToolTip.SetTip(main, path);
            main.PointerReleased += (_, _) => { if (Directory.Exists(path)) { Platform.Current.Open(path); HideAnimated(); } };
            var remove = IconButton("x", () => { Watches.Remove(path); Rebuild(); });
            ToolTip.SetTip(remove, "Stop watching");
            remove.VerticalAlignment = VerticalAlignment.Center;
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(main);
            list.Children.Add(row);
        }

        if (_pendingWatch is { } dir)
        {
            list.Children.Add(new TextBlock
            {
                Text = $"New files in {System.IO.Path.GetFileName(dir.TrimEnd('/', '\\'))} become:", FontSize = 12.5,
                Foreground = B(T.Text), Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var pills = new UniformGrid { Columns = 3, Margin = new Thickness(-2, 6, -2, 2) };
            foreach (var (id, label) in Watches.Presets)
            {
                IBrush rest = B(A(T.SubText, .12));
                var caption = new TextBlock { Text = label, FontSize = 11.5, Foreground = B(T.Text), HorizontalAlignment = HorizontalAlignment.Center };
                var pill = new Border
                {
                    CornerRadius = new CornerRadius(8), Padding = new Thickness(4, 4, 4, 5), Margin = new Thickness(2),
                    Background = rest, Cursor = new Cursor(StandardCursorType.Hand), Child = caption,
                };
                pill.PointerEntered += (_, _) => { pill.Background = Gradient(T.AccentA, T.AccentB, false); caption.Foreground = B(T.AccentText); };
                pill.PointerExited += (_, _) => { pill.Background = rest; caption.Foreground = B(T.Text); };
                pill.PointerReleased += (_, _) =>
                {
                    Watches.Add(new WatchFolder { Path = dir, Action = id });
                    _pendingWatch = null;
                    Rebuild();
                };
                pills.Children.Add(pill);
            }
            list.Children.Add(pills);
            list.Children.Add(new TextBlock { Text = "Results go to a Converted folder inside it.", FontSize = 11, Foreground = B(T.SubText), Margin = new Thickness(2, 2, 0, 4) });
            list.Children.Add(FooterButton("x", "Cancel", () => { _pendingWatch = null; Rebuild(); }));
        }
        else list.Children.Add(FooterButton("folder", "Watch a folder…", PickWatchFolder));
        return Card(list, new Thickness(0));
    }

    async void PickWatchFolder()
    {
        _picking = true;
        try
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "New files in this folder will be converted automatically", AllowMultiple = false,
            });
            if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path) _pendingWatch = path;
        }
        catch (Exception ex) { Log.Error("folder picker", ex); }
        finally { _picking = false; }
        if (IsVisible && !_hiding) { Rebuild(); Activate(); }
        else { _hiddenAt = -1e9; Open(); }
    }

    Control FooterButton(string icon, string text, Action click)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new IconView { Key = icon, Width = 13, Height = 13, Brush = B(T.SubText), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        sp.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = B(T.SubText), VerticalAlignment = VerticalAlignment.Center });
        var b = new Border { Child = sp, Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
        b.PointerEntered += (_, _) => b.Background = B(T.IsDark ? A(Colors.White, .07) : A(Colors.White, .6));
        b.PointerExited += (_, _) => b.Background = Brushes.Transparent;
        b.PointerReleased += (_, _) => click();
        return b;
    }
}
