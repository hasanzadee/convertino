namespace Convertino.Native;

/// <summary>
/// Watches the pointer and keyboard on macOS by polling Quartz's combined session state (the same idea as the
/// X11 poller): no event tap, so nothing waits on Convertino, and it keeps working while Finder runs a drag session.
/// Fires on the UI thread via the post callback.
/// </summary>
public sealed class MacInput : IDisposable
{
    readonly Action<Action> _post;
    Thread? _thread;
    volatile bool _run = true;

    /// <summary>The trigger key (Shift, Control or Option, from the settings) went down. Args: dragging?, where.</summary>
    public event Action<bool, ScreenPoint>? TriggerDown;
    /// <summary>The trigger key came back up.</summary>
    public event Action? TriggerUp;
    /// <summary>Tab went down (arg: Shift held → previous). Like Esc, only seen with Input Monitoring.</summary>
    public event Action<bool>? Tab;
    public event Action<ScreenPoint>? ButtonDown;
    public event Action<ScreenPoint>? ButtonUp;
    public event Action? Escape;

    /// <summary>Poll faster while something interesting may happen.</summary>
    public volatile bool Hot;

    public MacInput(Action<Action> post) => _post = post;

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Convertino input" };
        _thread.Start();
    }

    public ScreenPoint Pointer
    {
        get
        {
            var p = Quartz.Cursor();
            return new ScreenPoint((int)Math.Round(p.X), (int)Math.Round(p.Y));
        }
    }

    void Loop()
    {
        bool lDown = false, rDown = false, dragging = false, trigger = false, esc = false, tab = false;
        double downX = 0, downY = 0;
        bool diag = Overlay.OverlayController.Diag;
        while (_run)
        {
            bool l, r, t, e, tb, shift;
            CGPoint c;
            try
            {
                l = Quartz.CGEventSourceButtonState(Quartz.CombinedSessionState, Quartz.LeftButton);
                r = Quartz.CGEventSourceButtonState(Quartz.CombinedSessionState, Quartz.RightButton);
                ulong flag = Settings.Current.TriggerKey.Modifier() switch
                {
                    ModifierKey.Ctrl => Quartz.ControlFlag,
                    ModifierKey.Alt => Quartz.OptionFlag,
                    _ => Quartz.ShiftFlag,
                };
                ulong flags = Quartz.CGEventSourceFlagsState(Quartz.CombinedSessionState);
                t = (flags & flag) != 0;
                shift = (flags & Quartz.ShiftFlag) != 0;
                // plain keys may read as "up" without Input Monitoring; Esc and Tab are niceties (dragging away also
                // cancels, the trigger key and the wheel also flip, and in click mode the flower's window gets Tab itself)
                e = Quartz.CGEventSourceKeyState(Quartz.CombinedSessionState, Quartz.EscapeKey);
                tb = Quartz.CGEventSourceKeyState(Quartz.CombinedSessionState, Quartz.TabKey);
                c = Quartz.Cursor();
            }
            catch (Exception ex) { Log.Error("input poll", ex); return; }
            var pt = new ScreenPoint((int)Math.Round(c.X), (int)Math.Round(c.Y));

            if (l && !lDown) { downX = c.X; downY = c.Y; dragging = false; _post(() => ButtonDown?.Invoke(pt)); if (diag) Log.Info($"mac button down {pt}"); }
            if (!l && lDown) { dragging = false; _post(() => ButtonUp?.Invoke(pt)); }
            if (r && !rDown) _post(() => ButtonDown?.Invoke(pt));
            rDown = r;
            if (l && !dragging && (Math.Abs(c.X - downX) > 6 || Math.Abs(c.Y - downY) > 6)) { dragging = true; if (diag) Log.Info("mac dragging"); }
            lDown = l;

            if (t && !trigger) { bool d = dragging && l; _post(() => TriggerDown?.Invoke(d, pt)); if (diag) Log.Info($"mac trigger d={d}"); }
            if (!t && trigger) _post(() => TriggerUp?.Invoke());
            trigger = t;
            if (e && !esc) _post(() => Escape?.Invoke());
            esc = e;
            if (tb && !tab) { bool back = shift; _post(() => Tab?.Invoke(back)); }
            tab = tb;

            Thread.Sleep(l || Hot ? 12 : 40);
        }
    }

    public void Dispose()
    {
        _run = false;
        _thread?.Join(300);
    }
}
