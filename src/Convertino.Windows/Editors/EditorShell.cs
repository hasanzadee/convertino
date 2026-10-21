using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using Convertino.Native;
using Convertino.Ui;

namespace Convertino.Editors;

/// <summary>An editor plugged into the shell: a big preview area plus a control strip.</summary>
public abstract class Editor
{
    public abstract string Title { get; }
    public abstract FrameworkElement Preview { get; }
    public abstract FrameworkElement Controls { get; }
    public virtual string SaveLabel => L.T("Save");
    public virtual void Reset() { }
    /// <summary>Write the result; return the output path (null = nothing saved).</summary>
    public abstract Task<string?> Save();
    public virtual void OnKey(KeyEventArgs e) { }
    /// <summary>Dev self-test: simulate a typical edit.</summary>
    internal virtual void DevPrepare() { }
    public EditorShell? Shell { get; set; }
}

/// <summary>
/// Windows 11 acrylic window (DWM system backdrop) with a custom title bar.
/// Falls back to a solid themed background where the backdrop isn't available.
/// </summary>
public sealed class EditorShell : Window
{
    readonly Editor _editor;
    readonly TaskCompletionSource<string?> _done = new();
    readonly Button _save;
    readonly TextBlock _subtitle;
    readonly Border _root;
    bool _saving;

    public Task<string?> Result => _done.Task;

    public EditorShell(Editor editor, string subtitle, Size preferred)
    {
        _editor = editor;
        editor.Shell = this;
        Title = editor.Title;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        Background = Brushes.Transparent;
        Width = preferred.Width;
        Height = preferred.Height;
        MinWidth = 520;
        MinHeight = 420;
        UseLayoutRounding = true;
        Icon = AppIcon.Render(64);
        WindowStartupLocation = WindowStartupLocation.Manual;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        FontFamily = (FontFamily)Application.Current.Resources["Convertino.Font"];

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 46,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(6),
            UseAeroCaptionButtons = false,
            CornerRadius = new CornerRadius(0),
        });

        // title bar
        var close = new Button { Content = "", Style = (Style)Application.Current.Resources["Convertino.IconButton"], FontSize = 11, ToolTip = L.T("Close (Esc)") };
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        close.Click += (_, _) => Close();
        var titleText = new TextBlock { Text = editor.Title, FontFamily = (FontFamily)Application.Current.Resources["Convertino.Display"], FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "Convertino.Text");
        _subtitle = new TextBlock { Text = subtitle, FontSize = 12, Margin = new Thickness(10, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Convertino.SubText");
        var logo = new Image { Source = new DrawingImage(AppIcon.Drawing(Theme.Current)), Width = 18, Height = 18, Margin = new Thickness(0, 0, 10, 0) };
        var titleBar = new DockPanel { Height = 46, Margin = new Thickness(16, 0, 8, 0) };
        DockPanel.SetDock(close, Dock.Right);
        titleBar.Children.Add(close);
        titleBar.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { logo, titleText, _subtitle } });

        // footer
        var reset = new Button { Content = L.T("Reset"), Style = (Style)Application.Current.Resources["Convertino.Button"] };
        reset.Click += (_, _) => editor.Reset();
        var cancel = new Button { Content = L.T("Cancel"), Style = (Style)Application.Current.Resources["Convertino.Button"], Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();
        _save = new Button { Content = editor.SaveLabel, Style = (Style)Application.Current.Resources["Convertino.AccentButton"], MinWidth = 110 };
        _save.Click += async (_, _) => await DoSave();
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _save } };
        var footer = new Grid { Margin = new Thickness(16, 10, 16, 16) };
        footer.Children.Add(reset);
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        footer.Children.Add(right);

        var previewHost = new Border
        {
            Margin = new Thickness(16, 0, 16, 0),
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Child = editor.Preview,
        };
        previewHost.SetResourceReference(Border.BackgroundProperty, "Convertino.Canvas");
        var controls = new ContentControl { Content = editor.Controls, Margin = new Thickness(16, 12, 16, 0) };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(previewHost, 1);
        Grid.SetRow(controls, 2);
        Grid.SetRow(footer, 3);
        grid.Children.Add(titleBar);
        grid.Children.Add(previewHost);
        grid.Children.Add(controls);
        grid.Children.Add(footer);

        _root = new Border { Child = grid };
        Content = _root;

        SourceInitialized += (_, _) => ApplyBackdrop();
        StateChanged += (_, _) => _root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        Closed += (_, _) => _done.TrySetResult(null);
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox) { Close(); e.Handled = true; return; }
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; await DoSave(); return; }
            editor.OnKey(e);
        };
    }

    public void SetSubtitle(string s) => _subtitle.Text = s;

    void ApplyBackdrop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var t = Theme.Current;
        int dark = t.IsDark ? 1 : 0;
        Win32.DwmSetWindowAttribute(hwnd, 20, ref dark, 4); // immersive dark mode
        int round = 2;
        Win32.DwmSetWindowAttribute(hwnd, 33, ref round, 4); // rounded corners
        int none = unchecked((int)0xFFFFFFFE);
        Win32.DwmSetWindowAttribute(hwnd, 35, ref none, 4);  // no accent-coloured caption strip
        // our own close button replaces the system ones DWM would draw into the extended frame
        const int GWL_STYLE = -16, WS_SYSMENU = 0x00080000;
        var style = Win32.GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        Win32.SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style & ~WS_SYSMENU));
        bool ok = Environment.OSVersion.Version.Build >= 22621;
        if (ok)
        {
            HwndSource.FromHwnd(hwnd)!.CompositionTarget.BackgroundColor = Colors.Transparent;
            var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Win32.DwmExtendFrameIntoClientArea(hwnd, ref m);
            int acrylic = 3; // DWMSBT_TRANSIENTWINDOW
            ok = Win32.DwmSetWindowAttribute(hwnd, 38, ref acrylic, 4) == 0;
        }
        // tint on top of the acrylic so the theme colour wins
        var c = t.Window;
        c.A = ok ? (byte)(t.IsDark ? 150 : 120) : (byte)255;
        _root.Background = new SolidColorBrush(c);
    }

    async Task DoSave()
    {
        if (_saving) return;
        _saving = true;
        _save.IsEnabled = false;
        var old = _save.Content;
        _save.Content = L.T("Saving…");
        try
        {
            var path = await _editor.Save();
            if (path != null)
            {
                _done.TrySetResult(path);
                Close();
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error("editor save", ex);
            MessageBox.Show(this, L.Saved(ex.Message), "Convertino", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _save.Content = old;
        _save.IsEnabled = true;
        _saving = false;
    }

    /// <summary>Show near a screen point, sized to the preview, with a soft entrance.</summary>
    public void ShowNear(POINT p)
    {
        var (work, _, scale) = Win32.MonitorAt(p);
        double wDip = Math.Min(Width, work.Width / scale - 40), hDip = Math.Min(Height, work.Height / scale - 40);
        Width = wDip; Height = hDip;
        double left = p.X / scale - wDip / 2, top = p.Y / scale - hDip / 2;
        left = Math.Clamp(left, work.Left / scale + 12, work.Right / scale - wDip - 12);
        top = Math.Clamp(top, work.Top / scale + 12, work.Bottom / scale - hDip - 12);
        Left = left; Top = top;
        Opacity = 0;
        Show();
        // WPF positions in system-DPI units; re-place by pixels so mixed-DPI monitors land right
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32.SetWindowPos(hwnd, IntPtr.Zero, (int)(left * scale), (int)(top * scale), 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOZORDER);
        Win32.ForceForeground(hwnd);
        Activate();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }
}

/// <summary>Little factory helpers so editor code reads tidy.</summary>
public static class UiKit
{
    static Style S(string key) => (Style)Application.Current.Resources[key];

    public static TextBlock Label(string text, double minWidth = 0)
    {
        var t = new TextBlock { Text = text, Style = S("Convertino.Label"), MinWidth = minWidth, Margin = new Thickness(0, 0, 10, 0) };
        return t;
    }

    public static RadioButton Chip(string text, string group, bool isChecked, Action onChecked)
    {
        var r = new RadioButton { Content = text, GroupName = group, IsChecked = isChecked, Style = S("Convertino.RadioChip") };
        r.Checked += (_, _) => onChecked();
        return r;
    }

    public static ToggleButton Toggle(string text, bool isChecked, Action<bool> changed, string? tip = null)
    {
        var t = new ToggleButton { Content = text, IsChecked = isChecked, Style = S("Convertino.Chip"), ToolTip = tip };
        t.Checked += (_, _) => changed(true);
        t.Unchecked += (_, _) => changed(false);
        return t;
    }

    public static Button Icon(string glyph, string tip, Action click)
    {
        var b = new Button { Content = glyph, ToolTip = tip, Style = S("Convertino.IconButton") };
        b.Click += (_, _) => click();
        return b;
    }

    public static Button Btn(string text, Action click)
    {
        var b = new Button { Content = text, Style = S("Convertino.Button"), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    public static Slider Slider(double min, double max, double value, Action<double> changed, double width = 160)
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = value, Style = S("Convertino.Slider"), Width = width, VerticalAlignment = VerticalAlignment.Center };
        s.ValueChanged += (_, e) => changed(e.NewValue);
        return s;
    }

    public static FrameworkElement Row(params UIElement[] items)
    {
        var sp = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var i in items) sp.Children.Add(i);
        return sp;
    }

    public static FrameworkElement LabeledSlider(string label, Slider s, out TextBlock value, double labelWidth = 74)
    {
        value = new TextBlock { Style = S("Convertino.Label"), Width = 46, TextAlignment = TextAlignment.Right };
        var g = new DockPanel { Margin = new Thickness(0, 0, 18, 6) };
        g.Children.Add(Label(label, labelWidth));
        DockPanel.SetDock(value, Dock.Right);
        g.Children.Add(value);
        g.Children.Add(s);
        return g;
    }

    public static Border Swatch(Brush fill, bool selected, Action click, string? tip = null, double size = 26)
    {
        var b = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = fill, Margin = new Thickness(0, 0, 7, 6),
            Cursor = Cursors.Hand, ToolTip = tip, BorderThickness = new Thickness(selected ? 2.5 : 1),
        };
        if (selected) b.SetResourceReference(Border.BorderBrushProperty, "Convertino.Accent");
        else b.BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    public static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
