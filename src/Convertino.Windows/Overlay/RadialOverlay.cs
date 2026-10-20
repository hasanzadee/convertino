using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Convertino.Input;
using Convertino.Native;

namespace Convertino.Overlay;

/// <summary>Transparent, non-activating, topmost window that hosts the flower and is an OLE drop target.</summary>
public sealed class RadialWindow : Window
{
    public const double SizeDip = 460;
    public FlowerView View { get; }
    public IntPtr Hwnd { get; }

    public RadialWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Focusable = false;
        Width = Height = SizeDip;
        AllowDrop = true;
        Title = "Convertino";
        View = new FlowerView();
        Content = View;
        SourceInitialized += (_, _) => Win32.MakeToolWindow(new WindowInteropHelper(this).Handle, noActivate: true);
        Hwnd = new WindowInteropHelper(this).EnsureHandle();
        Left = -10000; Top = -10000;
    }

    /// <summary>Move to a pixel rect (top-left; size follows the monitor DPI).</summary>
    public void MoveTo(RECT r) =>
        Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, r.Left, r.Top, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);

    public void EnsurePlaced(RECT r)
    {
        // if the window landed on a monitor with a different DPI, WPF rescales it — re-centre
        if (Win32.GetWindowRect(Hwnd, out var now) && (now.Left != r.Left || now.Top != r.Top))
        {
            int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
            Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, cx - now.Width / 2, cy - now.Height / 2, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }
    }

    public bool ContainsScreenPoint(POINT p) => Win32.GetWindowRect(Hwnd, out var r) && IsVisible && r.Contains(p);
}

/// <summary>
/// The whole interaction: drag a file anywhere, tap the trigger key (Shift by default, see <see cref="TriggerKey"/>)
/// → the flower blooms under the cursor, drop on a petal → the action runs. The key again flips between Convert and Tools.
/// Double-tapping it in Explorer opens the same flower for the selected files (click mode).
/// </summary>
public sealed class OverlayController
{
    enum State { Hidden, Seed, Drag, Click, Leaving }

    readonly RadialWindow _win;
    readonly GlobalInput _input;
    readonly DispatcherTimer _seedTimeout = new() { Interval = TimeSpan.FromMilliseconds(1600) };
    readonly DispatcherTimer _releaseTimer = new() { Interval = TimeSpan.FromMilliseconds(380) };
    // dragged off the flower: the user is heading elsewhere (Shift-drag also means "move" in Explorer)
    readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(520) };
    State _state = State.Hidden;
    IReadOnlyList<string> _files = [];
    Pages? _pages;
    int _page;
    bool _helperEntered;
    RECT _placed;
    int _session;

    public OverlayController(GlobalInput input)
    {
        _input = input;
        _win = new RadialWindow();
        _win.DragEnter += OnDragEnter;
        _win.DragOver += OnDragOver;
        _win.DragLeave += OnDragLeave;
        _win.Drop += OnDrop;
        _win.MouseMove += OnMouseMove;
        _win.MouseLeftButtonUp += OnClick;
        _win.MouseLeave += (_, _) => { if (_state == State.Click) _win.View.SetHover(-1); };

        _seedTimeout.Tick += (_, _) => { _seedTimeout.Stop(); if (_state == State.Seed) Hide(); };
        _releaseTimer.Tick += (_, _) => { _releaseTimer.Stop(); if (_state is State.Seed or State.Drag) Hide(); };
        _leaveTimer.Tick += (_, _) => { _leaveTimer.Stop(); if (_state == State.Drag) Hide(); };

        input.TriggerDown += OnTriggerDown;
        input.TriggerDoubleTap += OnDoubleTap;
        input.MouseUp += _ => { if (_state is State.Seed or State.Drag) _releaseTimer.Start(); };
        input.MouseDown += p => { if (_state == State.Click && !_win.ContainsScreenPoint(p)) Hide(); };
        input.Escape += () => { if (_state is State.Seed or State.Drag or State.Click) Hide(); };
    }

    bool Visible => _state is State.Seed or State.Drag or State.Click;

    // ---------- triggers ----------

    readonly TriggerGate _gate = new();

    void OnTriggerDown(bool dragging, POINT pt)
    {
        if (!Settings.Current.Enabled) return;
        if (Visible)
        {
            if (_state != State.Seed) FlipPage();
            return;
        }
        if (_state == State.Hidden && dragging && _gate.Opens(Settings.Current.TriggerKey)) ShowAt(pt, State.Seed);
    }

    void OnDoubleTap(POINT pt)
    {
        if (!Settings.Current.Enabled || !Settings.Current.DoubleShift || _state != State.Hidden) return;
        var fg = Win32.GetForegroundWindow();
        var cls = Win32.ClassOf(Win32.GetAncestor(fg, Win32.GA_ROOT));
        if (cls is not ("CabinetWClass" or "Progman" or "WorkerW")) return;
        // don't hijack the key while the user is renaming a file
        var gti = new GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>() };
        if (Win32.GetGUIThreadInfo(Win32.GetWindowThreadProcessId(fg, out _), ref gti) && gti.hwndFocus != IntPtr.Zero
            && Win32.ClassOf(gti.hwndFocus) == "Edit") return;

        var files = Shell.ExplorerSelection(fg);
        if (files.Count == 0) return;
        ShowAt(pt, State.Click);
        Bloom(files);
    }

    /// <summary>Open the flower for files (Send to → Convertino, "Convertino.exe menu files…").</summary>
    public void Menu(IReadOnlyList<string> files)
    {
        if (files.Count == 0 || _state != State.Hidden) return;
        ShowAt(Win32.Cursor, State.Click);
        Bloom(files);
    }

    // ---------- show / hide ----------

    void ShowAt(POINT pt, State state)
    {
        _session++;
        _releaseTimer.Stop();
        _leaveTimer.Stop();
        _win.View.Reset();
        var r = ComputeRect(pt);
        _win.View.Backdrop = Native.Backdrop.Capture(r, 4, 7, 1.45);
        _win.MoveTo(r);
        _placed = r;
        _state = state;
        _helperEntered = false;
        _win.Show();
        _win.EnsurePlaced(_placed);
        _win.View.Appear();
        if (state == State.Seed) _seedTimeout.Start();
    }

    static RECT ComputeRect(POINT p)
    {
        var (work, _, scale) = Win32.MonitorAt(p);
        int size = (int)Math.Round(RadialWindow.SizeDip * scale);
        int x = p.X - size / 2, y = p.Y - size / 2;
        int pad = (int)Math.Round(30 * scale);
        x = Math.Clamp(x, work.Left - pad, Math.Max(work.Left - pad, work.Right - size + pad));
        y = Math.Clamp(y, work.Top - pad, Math.Max(work.Top - pad, work.Bottom - size + pad));
        return new RECT { Left = x, Top = y, Right = x + size, Bottom = y + size };
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
        Task.Run(() => Shell.Thumbnail(first, 96)).ContinueWith(t =>
        {
            if (session == _session && t.Result != null) { v.Thumb = t.Result; v.InvalidateVisual(); }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void FlipPage()
    {
        if (_pages == null) return;
        if (_pages.Tools.Count == 0) { _win.View.Nudge(); return; }
        _page ^= 1;
        _win.View.SetPages(2, _page);
        _win.View.SetPetals(_page == 0 ? _pages.Convert : _pages.Tools, _page == 0 ? -1 : +1);
        UpdateHover(Mouse.GetPosition(_win));
    }

    async void Hide()
    {
        if (_state is State.Hidden or State.Leaving) return;
        _state = State.Leaving;
        _seedTimeout.Stop();
        _releaseTimer.Stop();
        _leaveTimer.Stop();
        if (_helperEntered) { Shell.HelperLeave(); _helperEntered = false; }
        int session = _session;
        _win.View.Collapse();
        await Task.Delay(230);
        if (session != _session) return;
        FinishHide();
    }

    async void FinishHide()
    {
        _win.View.Reset();
        // let WPF push an empty frame into the layered window before hiding,
        // otherwise the old flower flashes for a frame next time it's shown
        await Task.Delay(34);
        if (_state != State.Leaving) return;
        _win.Hide();
        _state = State.Hidden;
        _files = [];
        _pages = null;
    }

    // ---------- OLE drag & drop ----------

    static string[]? FilesOf(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0)
                return f.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
        }
        catch { }
        return null;
    }

    static DragDropEffects SafeEffect(DragEventArgs e)
    {
        // never MOVE: with Shift held Explorer would delete the source after a "move" drop
        if (e.AllowedEffects.HasFlag(DragDropEffects.Copy)) return DragDropEffects.Copy;
        if (e.AllowedEffects.HasFlag(DragDropEffects.Link)) return DragDropEffects.Link;
        return DragDropEffects.None;
    }

    void OnDragEnter(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!Visible) { e.Effects = DragDropEffects.None; return; }
        _releaseTimer.Stop();
        _leaveTimer.Stop();
        if (_state == State.Seed)
        {
            var files = FilesOf(e.Data);
            if (files == null || files.Length == 0) { e.Effects = DragDropEffects.None; Hide(); return; }
            Bloom(files);
        }
        UpdateHover(e.GetPosition(_win));
        _described = int.MinValue;
        e.Effects = Feedback(e);
        if (!_helperEntered)
        {
            Shell.HelperEnter(_win.Hwnd, e.Data, Win32.Cursor, (int)e.Effects);
            _helperEntered = true;
        }
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_state != State.Drag) { e.Effects = DragDropEffects.None; return; }
        UpdateHover(e.GetPosition(_win));
        e.Effects = Feedback(e);
        if (_helperEntered) Shell.HelperOver(Win32.Cursor, (int)e.Effects);
    }

    void OnDragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        // hand the caption back to Explorer's own targets
        Shell.SetDropDescription(e.Data, -1);
        _described = int.MinValue;
        if (_helperEntered) { Shell.HelperLeave(); _helperEntered = false; }
        _win.View.SetHover(-1);
        if (_state == State.Drag) _leaveTimer.Start();
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        Log.Info($"drop state={_state} petal={_win.View.HitTestPetal(e.GetPosition(_win))}");
        _releaseTimer.Stop();
        e.Effects = SafeEffect(e);
        Shell.SetDropDescription(e.Data, -1);
        _described = int.MinValue;
        if (_helperEntered) { Shell.HelperDrop(e.Data, Win32.Cursor, (int)e.Effects); _helperEntered = false; }
        if (_state != State.Drag) { Hide(); return; }
        int idx = _win.View.HitTestPetal(e.GetPosition(_win));
        // the OLE Drop call is synchronous for Explorer — return immediately, work async
        Launch(idx);
    }

    // ---------- click mode ----------

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_state == State.Click) UpdateHover(e.GetPosition(_win));
    }

    void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (_state != State.Click) return;
        var pos = e.GetPosition(_win);
        if (_win.View.IsOverHub(pos)) { FlipPage(); return; }
        Launch(_win.View.HitTestPetal(pos));
    }

    void UpdateHover(Point pos)
    {
        int idx = _win.View.HitTestPetal(pos);
        _win.View.SetHover(idx);
    }

    int _described = int.MinValue;

    /// <summary>Drag feedback under Explorer's drag image: "Convert to PNG", "Crop", or "Drop on a petal".</summary>
    DragDropEffects Feedback(DragEventArgs e)
    {
        var pos = e.GetPosition(_win);
        int idx = _win.View.HitTestPetal(pos);
        var acts = _win.View.CurrentActions;
        bool onPetal = idx >= 0 && idx < acts.Count;
        var effect = onPetal ? SafeEffect(e) : DragDropEffects.None;
        int key = onPetal ? idx * 2 + _page : -1;
        if (key != _described)
        {
            _described = key;
            if (onPetal)
            {
                var a = acts[idx];
                if (a.Icon == null) Shell.SetDropDescription(e.Data, 1, "Convert to %1", a.Label);
                else Shell.SetDropDescription(e.Data, 1, "%1", a.Verb);
            }
            else Shell.SetDropDescription(e.Data, 0, _pages?.Tools.Count > 0 ? "Drop on a petal · ⇧ for tools" : "Drop on a petal");
        }
        return effect;
    }

    async void Launch(int idx)
    {
        var actions = _win.View.CurrentActions;
        if (idx < 0 || idx >= actions.Count) { Hide(); return; }
        var action = actions[idx];
        var files = _files;
        var where = Win32.Cursor;
        _state = State.Leaving;
        _seedTimeout.Stop();
        int session = _session;
        _win.View.Launch(idx);
        await Task.Delay(action.Interactive ? 120 : 170);
        if (session == _session)
        {
            _win.View.Collapse();
            _ = Task.Delay(220).ContinueWith(_ => { if (session == _session) FinishHide(); }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        await Jobs.Start(action, files, new ScreenPoint(where.X, where.Y));
    }
}
