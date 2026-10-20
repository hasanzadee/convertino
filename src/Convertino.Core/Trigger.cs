namespace Convertino;

/// <summary>
/// What opens the flower during a drag. Modifiers only: they can be tapped with a mouse button held, and macOS
/// reports them without Input Monitoring. "Shift twice" leaves a single Shift to apps that use it on drop
/// (Claude's chat attaches a file on Shift + drop).
/// </summary>
public enum TriggerKey { Shift, ShiftTwice, Ctrl, Alt }

public enum ModifierKey { Shift, Ctrl, Alt }

public static class TriggerKeys
{
    public static readonly TriggerKey[] All = [TriggerKey.Shift, TriggerKey.ShiftTwice, TriggerKey.Ctrl, TriggerKey.Alt];

    /// <summary>The physical key each UI's input watcher listens to.</summary>
    public static ModifierKey Modifier(this TriggerKey k) => k switch
    {
        TriggerKey.Ctrl => ModifierKey.Ctrl,
        TriggerKey.Alt => ModifierKey.Alt,
        _ => ModifierKey.Shift,
    };

    /// <summary>"Ctrl" on Windows and Linux, "Control" on macOS, and so on.</summary>
    public static string KeyName(this TriggerKey k) => k.Modifier() switch
    {
        ModifierKey.Ctrl => OperatingSystem.IsMacOS() ? "Control" : "Ctrl",
        ModifierKey.Alt => OperatingSystem.IsMacOS() ? "Option" : "Alt",
        _ => "Shift",
    };

    /// <summary>Menu label: "Shift", "Shift twice", "Ctrl" …</summary>
    public static string Label(this TriggerKey k) => k == TriggerKey.ShiftTwice ? "Shift twice" : k.KeyName();

    /// <summary>Instruction for hints and cards: "tap Shift", "double-tap Shift" …</summary>
    public static string Tap(this TriggerKey k) => k == TriggerKey.ShiftTwice ? "double-tap Shift" : "tap " + k.KeyName();
}

/// <summary>Decides whether a trigger key press during a drag opens the flower.</summary>
public sealed class TriggerGate
{
    /// <summary>Two presses closer than this count as a double tap.</summary>
    public const int DoubleTapMs = 450;
    long _last;

    public bool Opens(TriggerKey k)
    {
        if (k != TriggerKey.ShiftTwice) return true;
        long now = Environment.TickCount64;
        bool second = _last != 0 && now - _last < DoubleTapMs;
        _last = second ? 0 : now;
        return second;
    }
}
