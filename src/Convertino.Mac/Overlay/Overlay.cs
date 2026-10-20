using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Convertino.Native;
using Convertino.Services;

namespace Convertino.Overlay;

/// <summary>Transparent, borderless, non-activating window that hosts the flower and accepts Finder drops.</summary>
public sealed class RadialWindow : Window
{
    public FlowerView View { get; } = new();

    public RadialWindow()
    {
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        TransparencyBackgroundFallback = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Width = Height = FlowerView.Size;
        Title = "Convertino";
        DragDrop.SetAllowDrop(this, true);
        Content = View;
    }
}

/// <summary>
/// Drag a file, tap the trigger key (Shift by default, see <see cref="TriggerKey"/>) → the flower blooms under the cursor,
/// drop on a petal → the action runs. The key again flips Convert ⇄ Tools. "convertino menu files…" (Finder Quick Action) opens the same flower in click mode.
/// Same state machine as the Linux build; the window and the glass are native AppKit.
/// </summary>
public sealed class OverlayController
{
    enum State { Hidden, Seed, Drag, Click, Leaving }

    readonly RadialWindow _win = new();
    readonly MacInput? _input;
    readonly DispatcherTimer _seedTimeout = new() { Interval = TimeSpan.FromMilliseconds(1600) };
    readonly DispatcherTimer _releaseTimer = new() { Interval = TimeSpan.FromMilliseconds(380) };
    readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(520) };
    readonly Dictionary<int, byte[]> _masks = [];
    Vibrancy? _glass;
    State _state = State.Hidden;
    IReadOnlyList<string> _files = [];
    Pages? _pages;
    int _page;
    int _session;
    PixelRect _rect;
    bool _pinned = Environment.GetEnvironmentVariable("CONVERTINO_PIN") != null;

    public TopLevel Host => _win;

    public OverlayController(MacInput? input)
    {
        _input = input;
        _win.AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        _win.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        _win.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        _win.AddHandler(DragDrop.DropEvent, OnDrop);
        _win.PointerMoved += (_, e) => { if (_state == State.Click) _win.View.SetHover(_win.View.HitTestPetal(e.GetPosition(_win))); };
        _win.PointerReleased += OnClick;
        _win.KeyDown += (_, e) => { if (e.Key == Key.Escape && Visible) Hide(); };

        _seedTimeout.Tick += (_, _) => { _seedTimeout.Stop(); if (_state == State.Seed && !_pinned) Hide(); };
        _releaseTimer.Tick += (_, _) => { _releaseTimer.Stop(); if (_state is State.Seed or State.Drag) Hide(); };
        _leaveTimer.Tick += (_, _) => { _leaveTimer.Stop(); if (_state == State.Drag) Hide(); };

        if (input != null)
        {
            input.TriggerDown += OnTriggerDown;
            input.ButtonUp += _ => { if (_state is State.Seed or State.Drag && !_pinned) _releaseTimer.Start(); };
            input.ButtonDown += p => { if (_state == State.Click && !Contains(p)) Hide(); };
            input.Escape += () => { if (_state is State.Seed or State.Drag or State.Click) Hide(); };
        }

        // realise the NSWindow once so the first bloom is instant. It must be on a screen: before its first show
        // Avalonia clamps the size to [window screen].visibleFrame, which is empty off-screen (→ a 0×0 window).
        // Nothing is drawn until the flower appears, so this first show is invisible.
        var primary = _win.Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1440, 900);
        _win.Position = new PixelPoint(primary.X, primary.Y);
        _win.Show();
        MacWindow.Float(_win, MacWindow.PopUpMenuLevel);
        _glass = Vibrancy.Attach(_win, FlowerView.Size, FlowerView.Size);
        _win.Hide();
    }

    bool Visible => _state is State.Seed or State.Drag or State.Click;

    /// <summary>Debug: park the flower (seed state, no timeout) to test drops and screenshots.</summary>
    public void Pin(ScreenPoint at, IReadOnlyList<string>? files = null)
    {
        ShowAt(at, State.Seed);
        _seedTimeout.Stop();
        if (files is { Count: > 0 }) Bloom(files);
    }

    bool Contains(ScreenPoint p) => _rect.Contains(new PixelPoint(p.X, p.Y));

    readonly TriggerGate _gate = new();

    void OnTriggerDown(bool dragging, ScreenPoint pt)
    {
        if (Diag) Log.Info($"trigger dragging={dragging} state={_state} at {pt}");
        if (!Settings.Current.Enabled) return;
        if (Visible) { if (_state != State.Seed) FlipPage(); return; }
        if (_state == State.Hidden && dragging && _gate.Opens(Settings.Current.TriggerKey)) ShowAt(pt, State.Seed);
    }

    /// <summary>Open the flower for files (Finder Quick Action / CLI).</summary>
    public void Menu(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return;
        Log.Info($"menu: {files.Count} item(s)");
        if (_state != State.Hidden) { _state = State.Leaving; FinishHide(); }
        var pt = _input?.Pointer ?? CenterOfPrimary();
        ShowAt(pt, State.Click);
        Bloom(files);
        _win.Activate();
    }

    ScreenPoint CenterOfPrimary()
    {
        var s = _win.Screens.Primary;
        return s == null ? new ScreenPoint(800, 500) : new ScreenPoint(s.WorkingArea.X + s.WorkingArea.Width / 2, s.WorkingArea.Y + s.WorkingArea.Height / 2);
    }

    void ShowAt(ScreenPoint pt, State state)
    {
        _session++;
        _releaseTimer.Stop();
        _leaveTimer.Stop();
        var v = _win.View;
        v.Reset();
        // Avalonia reports macOS screens in points (Scaling = 1), the same space as the Quartz cursor
        var screen = _win.Screens.ScreenFromPoint(new PixelPoint(pt.X, pt.Y)) ?? _win.Screens.Primary;
        double scale = screen?.Scaling ?? 1;
        int size = (int)Math.Round(FlowerView.Size * scale);
        var work = screen?.WorkingArea ?? new PixelRect(0, 0, 1440, 900);
        int pad = (int)(30 * scale);
        int x = Math.Clamp(pt.X - size / 2, work.X - pad, Math.Max(work.X - pad, work.Right - size + pad));
        int y = Math.Clamp(pt.Y - size / 2, work.Y - pad, Math.Max(work.Y - pad, work.Bottom - size + pad));
        _rect = new PixelRect(x, y, size, size);
        _win.Position = new PixelPoint(x, y);
        _state = state;
        if (_input != null) _input.Hot = true;
        _win.Show();
        MacWindow.Float(_win, MacWindow.PopUpMenuLevel);
        Glass(0);
        if (Diag) Log.Info($"show at {x},{y} size={size} transparency={_win.ActualTransparencyLevel} glass={_glass != null}");
        v.Appear();
        if (state == State.Seed) _seedTimeout.Start();
        if (Diag) DispatcherTimer.RunOnce(() => Log.Info($"overlay {MacWindow.Describe(_win)} avalonia visible={_win.IsVisible} pos={_win.Position} " +
            $"client={_win.ClientSize} view={v.Bounds} appear={Spring(v, "_appear")} petals={v.CurrentActions.Count}"), TimeSpan.FromSeconds(1));
    }

    /// <summary>Debug log only: a spring's position inside the shared FlowerView.</summary>
    static string Spring(FlowerView v, string field) =>
        typeof(FlowerView).GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(v) is Spring sp
            ? sp.X.ToString("0.###") : "?";

    // ---------- native glass ----------

    /// <summary>Blur only under the petals (0 petals = the pulsing seed).</summary>
    void Glass(int petals)
    {
        if (_glass == null) return;
        if (!_masks.TryGetValue(petals, out var png)) _masks[petals] = png = Silhouette(petals);
        _glass.SetMask(png, FlowerView.Size, FlowerView.Size);
        _glass.FadeTo(1, petals == 0 ? 120 : 200);
    }

    static byte[] Silhouette(int petals)
    {
        const double s = FlowerView.Size;
        var rtb = new RenderTargetBitmap(new PixelSize((int)s * 2, (int)s * 2), new Vector(192, 192));
        using (var dc = rtb.CreateDrawingContext(true))
        {
            var c = new Point(s / 2, s / 2);
            var ink = Brushes.Black;
            if (petals == 0) dc.DrawEllipse(ink, null, c, FlowerView.HubR * 0.62, FlowerView.HubR * 0.62);
            else
            {
                double span = 2 * Math.PI / petals;
                for (int i = 0; i < petals; i++)
                {
                    double mid = -Math.PI / 2 + i * span;
                    dc.DrawGeometry(ink, null, FlowerView.PetalGeometry(c, FlowerView.R1, FlowerView.R2, mid - span / 2, mid + span / 2, FlowerView.Gap, FlowerView.Corner));
                }
                dc.DrawEllipse(ink, null, c, FlowerView.HubR, FlowerView.HubR);
            }
        }
        using var ms = new MemoryStream();
        rtb.Save(ms, PngBitmapEncoderOptions.Default);
        return ms.ToArray();
    }

    void Bloom(IReadOnlyList<string> files)
    {
        _seedTimeout.Stop();
        _files = files;
        _pages = Catalog.For(files);
        _page = 0;
        var v = _win.View;
        v.HubTitle = files.Count == 1 ? Paths.HumanSize(Paths.SizeOf(files[0])) : $"{files.Count} items";
        v.HubSub = files.Count == 1 ? _pages.Title : Paths.HumanSize(files.Sum(Paths.SizeOf));
        v.HubBadge = _pages.Badge;
        v.SetMode(HubMode.Info);
        v.SetPages(_pages.Tools.Count > 0 ? 2 : 1, 0);
        v.SetPetals(_pages.Convert, +1);
        Glass(_pages.Convert.Count);
        if (_state == State.Seed) _state = State.Drag;

        int session = _session;
        var first = files[0];
        Task.Run(() => MacPlatform.Thumbnail(first, 48)).ContinueWith(t =>
        {
            if (session == _session && t.Result != null) Dispatcher.UIThread.Post(() => { v.Thumb = t.Result; v.InvalidateVisual(); });
        });
    }

    void FlipPage()
    {
        if (_pages == null) return;
        if (_pages.Tools.Count == 0) { _win.View.Nudge(); return; }
        _page ^= 1;
        var actions = _page == 0 ? _pages.Convert : _pages.Tools;
        _win.View.SetPages(2, _page);
        _win.View.SetPetals(actions, _page == 0 ? -1 : +1);
        Glass(actions.Count);
    }

    async void Hide()
    {
        if (_state is State.Hidden or State.Leaving) return;
        _state = State.Leaving;
        _seedTimeout.Stop(); _releaseTimer.Stop(); _leaveTimer.Stop();
        int session = _session;
        _win.View.Collapse();
        _glass?.FadeTo(0, 150);
        await Task.Delay(230);
        if (session != _session) return;
        FinishHide();
    }

    async void FinishHide()
    {
        bool wasActive = _win.IsActive;
        _win.View.Reset();
        _glass?.FadeTo(0, 0);
        await Task.Delay(34);
        if (_state != State.Leaving) return;
        _win.Hide();
        _state = State.Hidden;
        if (_input != null) _input.Hot = false;
        _files = [];
        _pages = null;
        if (wasActive) MacWindow.Deactivate();
    }

    // ---------- drag & drop ----------

    static List<string>? FilesOf(DragEventArgs e)
    {
        try
        {
            var items = e.DataTransfer.TryGetFiles();
            if (items == null) return null;
            return items.Select(i => i.TryGetLocalPath()).Where(p => p != null && (File.Exists(p) || Directory.Exists(p))).Select(p => p!).ToList();
        }
        catch { return null; }
    }

    public static bool Diag = Environment.GetEnvironmentVariable("CONVERTINO_DEBUG") == "1";

    void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (Diag) Log.Info($"dragenter state={_state} formats={string.Join(",", e.DataTransfer.Formats)}");
        if (!Visible) { e.DragEffects = DragDropEffects.None; return; }
        _releaseTimer.Stop();
        _leaveTimer.Stop();
        if (_state == State.Seed)
        {
            var files = FilesOf(e);
            if (files == null || files.Count == 0) { e.DragEffects = DragDropEffects.None; Hide(); return; }
            Bloom(files);
        }
        Feedback(e);
    }

    void OnDragOver(object? sender, DragEventArgs e)
    {
        if (_state != State.Drag) { e.DragEffects = DragDropEffects.None; return; }
        Feedback(e);
    }

    void Feedback(DragEventArgs e)
    {
        int idx = _win.View.HitTestPetal(e.GetPosition(_win));
        _win.View.SetHover(idx);
        // never MOVE: the source file must stay where it is
        e.DragEffects = idx >= 0 ? DragDropEffects.Copy : DragDropEffects.None;
    }

    void OnDragLeave(object? sender, DragEventArgs e)
    {
        _win.View.SetHover(-1);
        if (_state == State.Drag) _leaveTimer.Start();
    }

    void OnDrop(object? sender, DragEventArgs e)
    {
        if (Diag) Log.Info($"drop state={_state} petal={_win.View.HitTestPetal(e.GetPosition(_win))}");
        _releaseTimer.Stop();
        if (_state != State.Drag) { Hide(); return; }
        e.DragEffects = DragDropEffects.Copy;
        Launch(_win.View.HitTestPetal(e.GetPosition(_win)));
    }

    // ---------- click mode ----------

    void OnClick(object? sender, PointerReleasedEventArgs e)
    {
        if (_state != State.Click) return;
        var pos = e.GetPosition(_win);
        if (_win.View.IsOverHub(pos)) { FlipPage(); return; }
        Launch(_win.View.HitTestPetal(pos));
    }

    async void Launch(int idx)
    {
        var actions = _win.View.CurrentActions;
        if (idx < 0 || idx >= actions.Count) { Hide(); return; }
        var action = actions[idx];
        var files = _files;
        var where = new ScreenPoint(_rect.X + _rect.Width / 2, _rect.Y + _rect.Height / 2);
        _state = State.Leaving;
        _seedTimeout.Stop();
        int session = _session;
        _win.View.Launch(idx);
        _glass?.FadeTo(0, 200);
        await Task.Delay(action.Interactive ? 120 : 170);
        if (session == _session)
        {
            _win.View.Collapse();
            _ = Task.Delay(220).ContinueWith(_ => Dispatcher.UIThread.Post(() => { if (session == _session) FinishHide(); }));
        }
        await Jobs.Start(action, files, where);
    }
}
