namespace Convertino;

/// <summary>
/// Which ring of the flower is showing and how the user moves between them, shared by the three overlays:
/// <list type="bullet">
/// <item>the trigger key again: flips forward when the key is released, so a trigger + Tab chord (Shift + Tab) isn't also a flip;</item>
/// <item>Tab / Shift + Tab: next / previous;</item>
/// <item>the mouse wheel: down = next, up = previous, one flip per burst of wheel events (a touchpad swipe or a
/// spun wheel sends dozens of them; the next flip needs a short pause in between).</item>
/// </list>
/// The rings wrap around: Convert → Tools → More tools → More formats → Convert.
/// </summary>
public sealed class RingPager
{
    /// <summary>Wheel events closer together than this belong to the same burst.</summary>
    public const int WheelGapMs = 220;
    /// <summary>The same Tab press can arrive twice (a global watcher and the window itself); also swallows key repeat.</summary>
    public const int TabGapMs = 140;

    IReadOnlyList<Ring> _rings = [];
    bool _triggerPending;
    long _wheelAt = -WheelGapMs, _tabAt = -TabGapMs;

    public int Page { get; private set; }
    public int Count => _rings.Count;
    public IReadOnlyList<PetalAction> Current => _rings.Count == 0 ? [] : _rings[Page].Petals;
    public string Name => _rings.Count == 0 ? "" : _rings[Page].Name;

    public void Reset(Pages pages) { _rings = pages.Rings; Page = 0; _triggerPending = false; }

    public void Clear() { _rings = []; Page = 0; _triggerPending = false; }

    /// <summary>Move by <paramref name="dir"/> (+1 next, −1 previous), wrapping. False when there's nothing to flip to.</summary>
    public bool Step(int dir)
    {
        if (dir == 0 || _rings.Count < 2) return false;
        Page = ((Page + dir) % _rings.Count + _rings.Count) % _rings.Count;
        return true;
    }

    // ---- input → direction to step (0 = nothing) ----

    /// <summary>The trigger key went down while the flower was open: flip once it comes back up.</summary>
    public void TriggerDown() => _triggerPending = true;

    public int TriggerUp()
    {
        bool flip = _triggerPending;
        _triggerPending = false;
        return flip ? +1 : 0;
    }

    public int Tab(bool back)
    {
        _triggerPending = false; // Shift + Tab: the Shift was part of the chord
        long now = Environment.TickCount64;
        bool dup = now - _tabAt < TabGapMs;
        _tabAt = now;
        return dup ? 0 : back ? -1 : +1;
    }

    /// <summary><paramref name="delta"/> &gt; 0 means the wheel turned down (towards the user).</summary>
    public int Wheel(double delta)
    {
        if (delta == 0) return 0;
        long now = Environment.TickCount64;
        bool burst = now - _wheelAt < WheelGapMs;
        _wheelAt = now;
        return burst ? 0 : delta > 0 ? +1 : -1;
    }
}
