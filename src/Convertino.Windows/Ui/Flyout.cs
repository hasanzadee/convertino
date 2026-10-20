using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Microsoft.Win32;
using Convertino.Native;

namespace Convertino.Ui;

/// <summary>Quick-settings style panel that pops up from the tray icon.</summary>
public sealed class Flyout : Window
{
    const double W = 340, Pad = 18;
    readonly TranslateTransform _slide = new();
    // a brush, not an Image element: it survives rebuilds and never feeds its bitmap size into the layout
    readonly ImageBrush _backdrop = new() { Stretch = Stretch.Fill };
    DateTime _hiddenAt;
    Stats? _stats;
    bool _animate; // only the build that opens the panel plays the activity intro

    public Flyout()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = W + Pad * 2;
        UseLayoutRounding = true;
        Title = "Convertino";
        Deactivated += (_, _) => HideAnimated();
        _hideTimer.Tick += (_, _) => FinishHide();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) HideAnimated(); };
        SourceInitialized += (_, _) => Win32.MakeToolWindow(new WindowInteropHelper(this).Handle, false);
        Theme.Changed += () => { if (IsVisible) Build(); };
    }

    public void Toggle()
    {
        if (IsVisible) { HideAnimated(); return; }
        if ((DateTime.Now - _hiddenAt).TotalMilliseconds < 250) return; // click on tray that just closed us
        _stats = Stats.Load();
        _animate = true;
        Build();
        _animate = false;
        var cursor = Win32.Cursor;
        var (work, _, scale) = Win32.MonitorAt(cursor);
        new WindowInteropHelper(this).EnsureHandle();
        var hwnd = new WindowInteropHelper(this).Handle;
        // show invisibly first so SizeToContent settles, then park bottom-right above the taskbar
        _hideTimer.Stop();
        _hiding = false;
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        // never taller than the work area: the card's top edge stays on screen, the options scroll instead
        MaxHeight = work.Height / scale + Pad - 14;
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, work.Left, work.Top, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        Show();
        UpdateLayout();
        Win32.GetWindowRect(hwnd, out var wr);
        int wPx = wr.Width, hPx = wr.Height;
        int x = work.Right - wPx + (int)(6 * scale), y = work.Bottom - hPx + (int)(6 * scale);
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        int m = (int)(Pad * scale);
        // fully transparent right now, so the capture sees only what's behind us
        _backdrop.ImageSource = Backdrop.Capture(new RECT { Left = x + m, Top = y + m, Right = x + wPx - m, Bottom = y + hPx - m }, 4, 8, 1.35);
        Win32.ForceForeground(hwnd);
        Activate();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
    }

    bool _hiding;
    readonly System.Windows.Threading.DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    public void HideAnimated()
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        _hiddenAt = DateTime.Now;
        _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 14, TimeSpan.FromMilliseconds(140)));
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)));
        _hideTimer.Start();
    }

    void FinishHide()
    {
        _hideTimer.Stop();
        Hide();
        BeginAnimation(OpacityProperty, null);
        _hiding = false;
    }

    /// <summary>Dev: render the panel to a PNG over a soft wallpaper (Convertino.exe --panel out.png [theme] [demo]).</summary>
    public static void Snapshot(string outPath, Stats stats)
    {
        const double scale = 2;
        var f = new Flyout { _stats = stats };
        var wall = new DrawingVisual();
        double w = W + Pad * 2, h = 1000;
        using (var dc = wall.RenderOpen())
            dc.DrawDrawing(Convertino.Editors.BackgroundEditor.Backdrops[0].Paint(new Rect(0, 0, w, h), null));
        var wallBmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        wallBmp.Render(wall);
        var blur = new Image { Source = wallBmp, Effect = new BlurEffect { Radius = 28 } };
        blur.Measure(new Size(w, h)); blur.Arrange(new Rect(0, 0, w, h));
        var blurred = new System.Windows.Media.Imaging.RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        blurred.Render(blur);

        f.Build();
        var root = (FrameworkElement)f.Content;
        f.Content = null;
        root.Measure(new Size(w, double.PositiveInfinity));
        h = root.DesiredSize.Height;
        root.Arrange(new Rect(0, 0, w, h));
        root.UpdateLayout();
        f._backdrop.ImageSource = new System.Windows.Media.Imaging.CroppedBitmap(blurred, new Int32Rect((int)Pad, (int)Pad, (int)(w - Pad * 2), (int)Math.Min(1000 - Pad, h - Pad * 2)));
        root.UpdateLayout();

        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawImage(wallBmp, new Rect(0, 0, w, 1000));
        rtb.Render(bg);
        rtb.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(outPath);
        enc.Save(fs);
    }

    /// <summary>Close on any click outside the panel or Esc — works even if Windows never gave us focus.</summary>
    public void Attach(Input.GlobalInput input)
    {
        input.MouseDown += p =>
        {
            if (!IsVisible || _hiding) return;
            var h = new WindowInteropHelper(this).Handle;
            if (h != IntPtr.Zero && Win32.GetWindowRect(h, out var r) && !r.Contains(p)) HideAnimated();
        };
        input.Escape += () => { if (IsVisible) HideAnimated(); };
    }

    // ---------- content ----------

    Theme T => Theme.Current;
    Brush B(Color c) => new SolidColorBrush(c);
    static Color A(Color c, double a) { c.A = (byte)(a * 255); return c; }
    static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    static readonly FontFamily TextF = new("Segoe UI Variable Text, Segoe UI");
    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    void Build()
    {
        var s = Settings.Current;
        var key = s.TriggerKey;
        var stack = new StackPanel();

        // header
        var logo = new Image { Source = new DrawingImage(AppIcon.Drawing(T)), Width = 34, Height = 34 };
        var title = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = "Convertino", FontFamily = Display, FontWeight = FontWeights.SemiBold, FontSize = 17, Foreground = B(T.Text) });
        title.Children.Add(new TextBlock { Text = s.Enabled ? $"Drag a file · {key.Tap()}" : "Paused", FontFamily = TextF, FontSize = 12, Foreground = B(T.SubText) });
        var header = new DockPanel { LastChildFill = false };
        var close = (FrameworkElement)FooterButton("\uE711", "", HideAnimated);
        close.ToolTip = "Close (Esc)";
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var hl = new StackPanel { Orientation = Orientation.Horizontal, Children = { logo, title } };
        header.Children.Add(hl);

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
                HowRow("", $"Drag any file, {key.Tap()}", "the flower opens under your cursor"),
                HowRow("", $"{key.KeyName()} again", "switch Convert ⇄ Tools"),
                HowRow("", $"Double-tap {key.KeyName()} in Explorer", "same menu for selected files"),
            },
        }, new Thickness(0, 14, 0, 0)));

        // theme picker
        stack.Children.Add(Label("Theme"));
        var themes = new UniformGrid4();
        foreach (var th in Theme.All) themes.Children.Add(ThemeChip(th, th.Name == T.Name));
        stack.Children.Add(themes);

        // toggles
        stack.Children.Add(Label("Options"));
        stack.Children.Add(Card(new StackPanel
        {
            Children =
            {
                ToggleRow("Enabled", s.Enabled, v => { s.Enabled = v; s.Save(); Build(); }),
                TriggerRow(key),
                ToggleRow($"Double-tap {key.KeyName()} in Explorer", s.DoubleShift, v => { s.DoubleShift = v; s.Save(); }),
                ToggleRow("Notifications", s.Notifications, v => { s.Notifications = v; s.Save(); }),
                ToggleRow("Start with Windows", s.LaunchAtStartup, v => { s.LaunchAtStartup = v; s.Save(); AutoStart.Apply(v); }),
            },
        }, new Thickness(0)));

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
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var quit = FooterButton("", "Quit", () => Application.Current.Shutdown());
        DockPanel.SetDock(quit, Dock.Right);
        footer.Children.Add(quit);
        footer.Children.Add(FooterButton("", "Log", () => Shell.Open(Paths.LogFile)));

        // header and footer stay put; only the middle scrolls when the screen is short
        var body = new DockPanel { Margin = new Thickness(18, 16, 18, 14) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(header);
        body.Children.Add(footer);
        body.Children.Add(new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
            Focusable = false,
        });

        var layers = new Grid
        {
            Children =
            {
                new Rectangle { Fill = _backdrop },
                new Rectangle { Fill = B(Theme.Solid(T.Tint)) },
                new Rectangle { Fill = new LinearGradientBrush(A(T.PetalTop, T.PetalTop.A / 255.0 * .7), T.PetalBottom, 90) },
                body,
            },
        };
        var card = new Border
        {
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            BorderBrush = B(T.Stroke),
            Child = layers,
            Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 6, Direction = 270, Opacity = T.IsDark ? .55 : .25 },
            RenderTransform = _slide,
        };
        layers.SizeChanged += (_, e) => layers.Clip = new RectangleGeometry(new Rect(e.NewSize), 17, 17);
        Content = new Grid { Margin = new Thickness(Pad), Children = { card } };
    }

    sealed class UniformGrid4 : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGrid4() { Columns = 4; Margin = new Thickness(-4, 0, -4, 0); }
    }

    TextBlock Label(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontFamily = TextF, FontWeight = FontWeights.SemiBold, FontSize = 10.5,
        Foreground = B(A(T.SubText, .8)), Margin = new Thickness(4, 16, 0, 7),
    };

    Border Card(UIElement child, Thickness margin) => new()
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

    const double BarsH = 58;

    UIElement Activity(Stats st)
    {
        var days = st.Last(14);
        int files = days.Sum(d => d.Day.Files);
        long saved = days.Sum(d => d.Day.Saved);
        int streak = st.Streak();
        var accent = new LinearGradientBrush(T.AccentA, T.AccentB, 0);

        // three big numbers that count up as the panel opens
        var nums = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
        nums.Children.Add(Stat(files, v => ((long)v).ToString("N0"), files == 1 ? "file" : "files", accent));
        nums.Children.Add(Stat(saved, v => Paths.HumanSize((long)v), "saved", accent));
        nums.Children.Add(Stat(streak, v => ((long)v).ToString(), "day streak", accent));

        // one bar per day; hovering one swaps the date row for that day's numbers
        int max = Math.Max(1, days.Max(d => d.Day.Files));
        var bars = new Grid { Height = BarsH, Margin = new Thickness(0, 14, 0, 0) };
        var first = new TextBlock { Text = days[0].Date.ToString("d MMM"), FontFamily = TextF, FontSize = 10.5, Foreground = B(T.SubText), HorizontalAlignment = HorizontalAlignment.Left };
        var today = new TextBlock { Text = "Today", FontFamily = TextF, FontSize = 10.5, Foreground = B(T.SubText), HorizontalAlignment = HorizontalAlignment.Right };
        var caption = new TextBlock { FontFamily = TextF, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = B(T.Text), HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
        var dates = new Grid { Margin = new Thickness(0, 5, 0, 0), Children = { first, today, caption } };

        var vertical = new LinearGradientBrush(T.AccentA, T.AccentB, 90);
        for (int i = 0; i < days.Count; i++)
        {
            var (date, day) = days[i];
            bool isToday = i == days.Count - 1;
            bars.ColumnDefinitions.Add(new ColumnDefinition());
            // headroom on top for the ease's overshoot
            double h = day.Files == 0 ? 4 : 8 + (BarsH - 12) * day.Files / max;
            var bar = new Border
            {
                Width = 11, Height = _animate ? 0 : h, CornerRadius = new CornerRadius(day.Files == 0 ? 2 : 4),
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = day.Files == 0 ? B(A(T.SubText, .25)) : vertical,
                Opacity = day.Files == 0 || isToday ? 1 : T.IsDark ? .68 : .55,
            };
            if (_animate)
                bar.BeginAnimation(HeightProperty, new DoubleAnimation(0, h, TimeSpan.FromMilliseconds(520))
                {
                    BeginTime = TimeSpan.FromMilliseconds(140 + 28 * i),
                    EasingFunction = new BackEase { Amplitude = .35, EasingMode = EasingMode.EaseOut },
                });
            var col = new Border { Background = Brushes.Transparent, Child = bar, Cursor = Cursors.Hand };
            Grid.SetColumn(col, i);
            double rest = bar.Opacity;
            col.MouseEnter += (_, _) =>
            {
                bar.Opacity = 1;
                caption.Text = $"{date:ddd d MMM} · {day.Files} {(day.Files == 1 ? "file" : "files")}" + (day.Saved > 0 ? $" · {Paths.HumanSize(day.Saved)} saved" : "");
                caption.Visibility = Visibility.Visible;
                first.Visibility = today.Visibility = Visibility.Hidden;
            };
            col.MouseLeave += (_, _) =>
            {
                bar.Opacity = rest;
                caption.Visibility = Visibility.Collapsed;
                first.Visibility = today.Visibility = Visibility.Visible;
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
                var tb = new TextBlock { FontFamily = TextF, FontSize = 11.5 };
                tb.Inlines.Add(new System.Windows.Documents.Run(name) { FontWeight = FontWeights.SemiBold, Foreground = B(T.Text) });
                tb.Inlines.Add(new System.Windows.Documents.Run($"  ×{count}") { Foreground = B(T.SubText) });
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

    UIElement Stat(double value, Func<double, string> fmt, string caption, Brush accent)
    {
        var big = new TextBlock { FontFamily = Display, FontWeight = FontWeights.SemiBold, FontSize = 21, Foreground = accent, Text = fmt(_animate ? 0 : value) };
        if (_animate && value > 0) CountUp(big, value, fmt);
        var sp = new StackPanel();
        sp.Children.Add(big);
        sp.Children.Add(new TextBlock { Text = caption, FontFamily = TextF, FontSize = 11, Foreground = B(T.SubText), Margin = new Thickness(1, -1, 0, 0) });
        return sp;
    }

    static void CountUp(TextBlock tb, double target, Func<double, string> fmt)
    {
        var start = DateTime.Now.AddMilliseconds(120);
        const double ms = 750;
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            double t = Math.Clamp((DateTime.Now - start).TotalMilliseconds / ms, 0, 1);
            double eased = 1 - Math.Pow(1 - t, 3);
            tb.Text = fmt(t >= 1 ? target : target * eased);
            if (t >= 1) CompositionTarget.Rendering -= tick;
        };
        CompositionTarget.Rendering += tick;
    }

    UIElement HowRow(string glyph, string title, string sub)
    {
        var icon = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 10, 0),
            Background = new LinearGradientBrush(A(T.AccentA, .25), A(T.AccentB, .2), 45),
            Child = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 13, Foreground = B(T.IsDark ? T.AccentA : T.AccentA), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontFamily = TextF, FontWeight = FontWeights.SemiBold, FontSize = 12.5, Foreground = B(T.Text) });
        text.Children.Add(new TextBlock { Text = sub, FontFamily = TextF, FontSize = 11.5, Foreground = B(T.SubText) });
        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5), Children = { icon, text } };
    }

    UIElement ToggleRow(string text, bool value, Action<bool> set)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
        var sw = Switch(value, set);
        DockPanel.SetDock(sw, Dock.Right);
        dp.Children.Add(sw);
        dp.Children.Add(new TextBlock { Text = text, FontFamily = TextF, FontSize = 13, Foreground = B(T.Text), VerticalAlignment = VerticalAlignment.Center });
        return dp;
    }

    /// <summary>"Open with" and a pill per key; "Shift ×2" leaves a single Shift to apps that use it on drop.</summary>
    UIElement TriggerRow(TriggerKey current)
    {
        var pills = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var k in TriggerKeys.All)
        {
            bool on = k == current;
            var pill = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 3, 8, 4), Margin = new Thickness(4, 0, 0, 0), Cursor = Cursors.Hand,
                Background = on ? new LinearGradientBrush(T.AccentA, T.AccentB, 0) : B(A(T.SubText, .12)),
                ToolTip = k == TriggerKey.ShiftTwice ? "Double-tap Shift while dragging: a single Shift stays free for the app you drop on" : null,
                Child = new TextBlock { Text = k == TriggerKey.ShiftTwice ? "Shift ×2" : k.Label(), FontFamily = TextF, FontSize = 11.5, Foreground = B(on ? T.AccentText : T.Text) },
            };
            pill.MouseLeftButtonUp += (_, e) =>
            {
                Settings.Current.TriggerKey = k;
                Settings.Current.Save();
                Build();
                e.Handled = true;
            };
            pills.Children.Add(pill);
        }
        var dp = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
        DockPanel.SetDock(pills, Dock.Right);
        dp.Children.Add(pills);
        dp.Children.Add(new TextBlock { Text = "Open with", FontFamily = TextF, FontSize = 13, Foreground = B(T.Text), VerticalAlignment = VerticalAlignment.Center });
        return dp;
    }

    FrameworkElement Switch(bool on, Action<bool> changed)
    {
        bool state = on;
        var knobT = new TranslateTransform(on ? 16 : 0, 0);
        var knob = new Ellipse { Width = 14, Height = 14, Margin = new Thickness(3, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = knobT };
        var track = new Border { Width = 36, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = knob, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
        void Paint()
        {
            track.Background = state ? new LinearGradientBrush(T.AccentA, T.AccentB, 0) : B(A(T.SubText, .12));
            track.BorderBrush = state ? Brushes.Transparent : B(A(T.SubText, .5));
            knob.Fill = state ? B(T.AccentText) : B(A(T.SubText, .9));
        }
        Paint();
        track.MouseLeftButtonUp += (_, e) =>
        {
            state = !state;
            Paint();
            knobT.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(state ? 16 : 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase() });
            changed(state);
            e.Handled = true;
        };
        return track;
    }

    UIElement ThemeChip(Theme th, bool selected)
    {
        var sw = new Grid { Width = 46, Height = 46 };
        sw.Children.Add(new Ellipse { Fill = new SolidColorBrush(th.Window), Stroke = B(A(T.SubText, .25)), StrokeThickness = 1 });
        sw.Children.Add(new Ellipse { Width = 22, Height = 22, Fill = new LinearGradientBrush(th.AccentA, th.AccentB, 45) });
        if (selected) sw.Children.Add(new Ellipse { Width = 54, Height = 54, Stroke = new LinearGradientBrush(T.AccentA, T.AccentB, 45), StrokeThickness = 2, Margin = new Thickness(-4) });
        var col = new StackPanel { Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Center };
        col.Children.Add(sw);
        col.Children.Add(new TextBlock { Text = th.Name, FontFamily = TextF, FontSize = 11, Foreground = B(selected ? T.Text : T.SubText), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
        col.MouseLeftButtonUp += (_, _) =>
        {
            Settings.Current.Theme = th.Name;
            Settings.Current.Save();
            Theme.Apply(th.Name);
        };
        return col;
    }

    UIElement RecentRow(RecentItem r)
    {
        var img = new Image { Width = 30, Height = 30, Stretch = Stretch.UniformToFill };
        Task.Run(() => Shell.Thumbnail(r.Path, 64)).ContinueWith(t => img.Source = t.Result, TaskScheduler.FromCurrentSynchronizationContext());
        var thumb = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(6), Child = img, Margin = new Thickness(0, 0, 10, 0), ClipToBounds = true };
        thumb.Clip = new RectangleGeometry(new Rect(0, 0, 30, 30), 6, 6);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(r.Path), FontFamily = TextF, FontSize = 12.5, Foreground = B(T.Text), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 230 });
        text.Children.Add(new TextBlock { Text = $"{r.Label} · {Ago(r.When)}", FontFamily = TextF, FontSize = 11, Foreground = B(T.SubText) });
        var row = new Border
        {
            Background = Brushes.Transparent, CornerRadius = new CornerRadius(8), Padding = new Thickness(4), Margin = new Thickness(-4, 1, -4, 1),
            Cursor = Cursors.Hand, Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { thumb, text } },
        };
        row.MouseEnter += (_, _) => row.Background = B(T.IsDark ? A(Colors.White, .06) : A(Colors.White, .6));
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => { Shell.Reveal(r.Path); HideAnimated(); };
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

    UIElement FooterButton(string glyph, string text, Action click)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 12, Foreground = B(T.SubText), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        sp.Children.Add(new TextBlock { Text = text, FontFamily = TextF, FontSize = 12.5, Foreground = B(T.SubText), VerticalAlignment = VerticalAlignment.Center });
        var b = new Border { Child = sp, Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        b.MouseEnter += (_, _) => b.Background = B(T.IsDark ? A(Colors.White, .07) : A(Colors.White, .6));
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }
}

public static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true)!;
            var exe = Environment.ProcessPath!;
            if (on) k.SetValue("Convertino", $"\"{exe}\"");
            else k.DeleteValue("Convertino", false);
        }
        catch (Exception ex) { Log.Error("startup", ex); }
    }
}

public sealed class TrayIcon : IDisposable
{
    readonly System.Windows.Forms.NotifyIcon _icon;
    readonly Flyout _flyout = new();

    public TrayIcon()
    {
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = AppIcon.TrayIcon(32),
            Text = $"Convertino — drag a file, {Settings.Current.TriggerKey.Tap()}",
            Visible = true,
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
                Application.Current.Dispatcher.BeginInvoke(() => _flyout.Toggle());
        };
        var menu = new System.Windows.Forms.ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        FillMenu(menu);
        // refilled on every open so the check marks match what the panel changed meanwhile
        menu.Opening += (_, _) => { _flyout.HideAnimated(); FillMenu(menu); };
        _icon.ContextMenuStrip = menu;
    }

    public void Attach(Input.GlobalInput input) => _flyout.Attach(input);

    void FillMenu(System.Windows.Forms.ContextMenuStrip m)
    {
        var s = Settings.Current;
        m.Items.Clear();
        _icon.Text = $"Convertino — drag a file, {s.TriggerKey.Tap()}";
        System.Windows.Forms.ToolStripMenuItem Item(string text, Action click, bool? check = null)
        {
            var it = new System.Windows.Forms.ToolStripMenuItem(text);
            if (check.HasValue) it.Checked = check.Value;
            it.Click += (_, _) => Application.Current.Dispatcher.BeginInvoke(click);
            return it;
        }
        m.Items.Add(Item("Open panel", () => _flyout.Toggle()));
        m.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        m.Items.Add(Item("Enabled", () => { s.Enabled = !s.Enabled; s.Save(); }, s.Enabled));
        var themes = new System.Windows.Forms.ToolStripMenuItem("Theme");
        foreach (var th in Theme.All)
            themes.DropDownItems.Add(Item(th.Name, () => { s.Theme = th.Name; s.Save(); Theme.Apply(th.Name); }, th.Name == Theme.Current.Name));
        m.Items.Add(themes);
        var keys = new System.Windows.Forms.ToolStripMenuItem("Open with");
        foreach (var k in TriggerKeys.All)
            keys.DropDownItems.Add(Item(k.Label(), () => { s.TriggerKey = k; s.Save(); }, k == s.TriggerKey));
        m.Items.Add(keys);
        m.Items.Add(Item("Notifications", () => { s.Notifications = !s.Notifications; s.Save(); }, s.Notifications));
        m.Items.Add(Item("Start with Windows", () => { s.LaunchAtStartup = !s.LaunchAtStartup; s.Save(); AutoStart.Apply(s.LaunchAtStartup); }, s.LaunchAtStartup));
        m.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        m.Items.Add(Item("Open log", () => Shell.Open(Paths.LogFile)));
        m.Items.Add(Item("Quit Convertino", () => Application.Current.Shutdown()));
    }

    public void ShowFlyout() { if (!_flyout.IsVisible) _flyout.Toggle(); }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
