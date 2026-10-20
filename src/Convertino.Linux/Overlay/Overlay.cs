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

/// <summary>Transparent, topmost, undecorated window that hosts the flower and accepts XDND drops.</summary>
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

    public IntPtr XWindow => TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
}

/// <summary>
/// Drag a file, tap the trigger key (Shift by default, see <see cref="TriggerKey"/>) → the flower blooms under the cursor,
/// drop on a petal → the action runs. The key again flips Convert ⇄ Tools. "convertino menu files…" opens the same flower in click mode.
/// </summary>
public sealed class OverlayController
{
    enum State { Hidden, Seed, Drag, Click, Leaving }

    readonly RadialWindow _win = new();
    readonly X11Input? _input;
    readonly DispatcherTimer _seedTimeout = new() { Interval = TimeSpan.FromMilliseconds(1600) };
    readonly DispatcherTimer _releaseTimer = new() { Interval = TimeSpan.FromMilliseconds(380) };
    readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(520) };
    State _state = State.Hidden;
    IReadOnlyList<string> _files = [];
    Pages? _pages;
    int _page;
    int _session;
    PixelRect _rect;
    bool _shaped;
    bool _pinned = Environment.GetEnvironmentVariable("CONVERTINO_PIN") != null;

    public TopLevel Host => _win;

    public OverlayController(X11Input? input)
    {
        _input = input;
        _win.AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        _win.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        _win.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        _win.AddHandler(DragDrop.DropEvent, OnDrop);
        _win.PointerMoved += (_, e) => { if (_state == State.Click) _win.View.SetHover(_win.View.HitTestPetal(e.GetPosition(_win))); };
        _win.PointerReleased += OnClick;

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

        // unmanaged (override-redirect) so the WM can't move it away from the cursor;
        // then realise it once, off-screen, so the first bloom is instant
        if (input != null && _win.XWindow != IntPtr.Zero) X11.OverrideRedirect(input.Display, _win.XWindow);
        _win.Position = new PixelPoint(-10000, -10000);
        _win.Show();
        _win.Hide();
    }

    bool Visible => _state is State.Seed or State.Drag or State.Click;

    /// <summary>Debug: park the flower (seed state, no timeout) to test drops onto a pre-mapped window.</summary>
    public void Pin(ScreenPoint at)
    {
        ShowAt(at, State.Seed);
        _seedTimeout.Stop();
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

    /// <summary>Open the flower for files (file-manager integration / CLI).</summary>
    public void Menu(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return;
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
        var screen = _win.Screens.ScreenFromPoint(new PixelPoint(pt.X, pt.Y)) ?? _win.Screens.Primary;
        double scale = screen?.Scaling ?? 1;
        int size = (int)Math.Round(FlowerView.Size * scale);
        var work = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        int pad = (int)(30 * scale);
        int x = Math.Clamp(pt.X - size / 2, work.X - pad, Math.Max(work.X - pad, work.Right - size + pad));
        int y = Math.Clamp(pt.Y - size / 2, work.Y - pad, Math.Max(work.Y - pad, work.Bottom - size + pad));
        _rect = new PixelRect(x, y, size, size);
        v.Backdrop = CaptureBackdrop(_rect);
        _win.Position = new PixelPoint(x, y);
        _state = state;
        if (_input != null) _input.Hot = true;
        _win.Show();
        if (Diag) Log.Info($"show at {x},{y} size={size} transparency={_win.ActualTransparencyLevel} backdrop={v.Backdrop != null}");
        ShapeInput(size, scale);
        v.Appear();
        if (state == State.Seed) _seedTimeout.Start();
    }

    /// <summary>Corners of the square window shouldn't swallow drops meant for whatever is underneath.</summary>
    void ShapeInput(int size, double scale)
    {
        if (_input == null || _win.XWindow == IntPtr.Zero) return;
        if (Environment.GetEnvironmentVariable("CONVERTINO_NOSHAPE") == "1") return;
        bool noAlpha = _win.ActualTransparencyLevel == WindowTransparencyLevel.None;
        X11.ShapeCircle(_input.Display, _win.XWindow, size, (int)((FlowerView.R2 + 14) * scale), bounding: noAlpha);
        _shaped = true;
    }

    Bitmap? CaptureBackdrop(PixelRect r)
    {
        if (_input == null) return null;
        try
        {
            var px = X11.Capture(_input.Display, r.X, r.Y, r.Width, r.Height, out int stride);
            if (px == null) return null;
            var small = Blur.Downscale(px, r.Width, r.Height, stride, 4, out int w, out int h);
            if (Blur.LooksEmpty(small)) return null; // e.g. XWayland can't see native Wayland windows
            Blur.Frost(small, w, h, 7, 1.45);
            var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using (var fb = bmp.Lock())
                for (int row = 0; row < h; row++)
                    System.Runtime.InteropServices.Marshal.Copy(small, row * w * 4, fb.Address + row * fb.RowBytes, w * 4);
            return bmp;
        }
        catch (Exception ex) { Log.Error("backdrop", ex); return null; }
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
        if (_state == State.Seed) _state = State.Drag;

        int session = _session;
        var first = files[0];
        Task.Run(() => LinuxPlatform.Thumbnail(first, 48)).ContinueWith(t =>
        {
            if (session == _session && t.Result != null) Dispatcher.UIThread.Post(() => { v.Thumb = t.Result; v.InvalidateVisual(); });
        });
    }

    void FlipPage()
    {
        if (_pages == null) return;
        if (_pages.Tools.Count == 0) { _win.View.Nudge(); return; }
        _page ^= 1;
        _win.View.SetPages(2, _page);
        _win.View.SetPetals(_page == 0 ? _pages.Convert : _pages.Tools, _page == 0 ? -1 : +1);
    }

    async void Hide()
    {
        if (_state is State.Hidden or State.Leaving) return;
        _state = State.Leaving;
        _seedTimeout.Stop(); _releaseTimer.Stop(); _leaveTimer.Stop();
        int session = _session;
        _win.View.Collapse();
        await Task.Delay(230);
        if (session != _session) return;
        FinishHide();
    }

    async void FinishHide()
    {
        _win.View.Reset();
        await Task.Delay(34);
        if (_state != State.Leaving) return;
        _win.Hide();
        if (_shaped && _input != null && _win.XWindow != IntPtr.Zero) { X11.Unshape(_input.Display, _win.XWindow); _shaped = false; }
        _state = State.Hidden;
        if (_input != null) _input.Hot = false;
        _files = [];
        _pages = null;
    }

    // ---------- XDND ----------

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
        // never MOVE: with Shift held a file manager would delete the source after a "move" drop
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
        await Task.Delay(action.Interactive ? 120 : 170);
        if (session == _session)
        {
            _win.View.Collapse();
            _ = Task.Delay(220).ContinueWith(_ => Dispatcher.UIThread.Post(() => { if (session == _session) FinishHide(); }));
        }
        await Jobs.Start(action, files, where);
    }
}
