namespace Convertino;

/// <summary>Modifier keys of a global shortcut. Super is the Windows key on Windows, Super on Linux, Command on macOS.</summary>
[Flags]
public enum HotMods { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Super = 8 }

/// <summary>
/// A global shortcut: modifiers plus one key (A–Z, 0–9, F1–F24), stored as text like "Super+Shift+X".
/// The defaults stay clear of what Windows, PowerToys, GNOME/KDE, Office, browsers and IDEs already use, and of
/// Ctrl+Alt+letter: that is AltGr on many European layouts and types a character there (ł, €, @ …).
/// Windows / Linux: Win+Shift+X (clipboard) and Win+Shift+A (area). macOS: Control+Option+X / A, since
/// Command+Shift+letter belongs to apps and Option alone types characters.
/// </summary>
public readonly record struct Hotkey(HotMods Mods, string Key)
{
    /// <summary>A panel changed a shortcut setting (already saved): each app registers its shortcuts again.</summary>
    public static event Action? SettingsChanged;
    public static void Changed() => SettingsChanged?.Invoke();

    /// <summary>Why a shortcut couldn't be registered ("another app uses it" …) for the panels; null when it works or is off.</summary>
    public static string? ClipboardProblem { get; set; }
    public static string? CaptureProblem { get; set; }

    public static Hotkey Clipboard => OperatingSystem.IsMacOS() ? new(HotMods.Ctrl | HotMods.Alt, "X") : new(HotMods.Super | HotMods.Shift, "X");
    public static Hotkey Capture => OperatingSystem.IsMacOS() ? new(HotMods.Ctrl | HotMods.Alt, "A") : new(HotMods.Super | HotMods.Shift, "A");

    /// <summary>What a click on a shortcut in the panel steps through; settings.json takes any other.</summary>
    public static Hotkey[] Choices(bool capture)
    {
        var m = OperatingSystem.IsMacOS() ? HotMods.Ctrl | HotMods.Alt : HotMods.Super | HotMods.Shift;
        string[] keys = capture ? (OperatingSystem.IsMacOS() ? ["A", "S"] : ["A", "Q"]) : (OperatingSystem.IsMacOS() ? ["X", "V"] : ["X", "Z"]);
        return [.. keys.Select(k => new Hotkey(m, k))];
    }

    /// <summary>The next of <see cref="Choices"/> after this one (the first if this one isn't among them).</summary>
    public Hotkey Next(bool capture)
    {
        var all = Choices(capture);
        int i = Array.IndexOf(all, this);
        return all[(i + 1) % all.Length];
    }

    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var mods = HotMods.None;
        string? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control" or "ctl": mods |= HotMods.Ctrl; break;
                case "alt" or "option" or "opt": mods |= HotMods.Alt; break;
                case "shift": mods |= HotMods.Shift; break;
                case "super" or "win" or "windows" or "meta" or "cmd" or "command": mods |= HotMods.Super; break;
                default:
                    if (key != null) return null;
                    key = raw.ToUpperInvariant();
                    break;
            }
        }
        if (key == null || mods == HotMods.None || !ValidKey(key)) return null;
        return new Hotkey(mods, key);
    }

    static bool ValidKey(string k) =>
        (k.Length == 1 && char.IsAsciiLetterOrDigit(k[0])) ||
        (k.Length is 2 or 3 && k[0] == 'F' && int.TryParse(k[1..], out int f) && f is >= 1 and <= 24);

    /// <summary>The settings form: "Super+Shift+X".</summary>
    public override string ToString() => string.Join("+", Parts(storage: true));

    /// <summary>For people: "Win+Shift+X" on Windows, "Super+Shift+X" on Linux, "Control+Option+X" on macOS.</summary>
    public string Label => string.Join("+", Parts(storage: false));

    /// <summary>Win / Super first as Windows and Linux write it (Win+Shift+S); Command last as macOS does (⌃⌥⇧⌘).</summary>
    IEnumerable<string> Parts(bool storage)
    {
        bool mac = OperatingSystem.IsMacOS() && !storage;
        bool super = Mods.HasFlag(HotMods.Super);
        if (super && !mac) yield return storage ? "Super" : OperatingSystem.IsWindows() ? "Win" : "Super";
        if (Mods.HasFlag(HotMods.Ctrl)) yield return mac ? "Control" : "Ctrl";
        if (Mods.HasFlag(HotMods.Alt)) yield return mac ? "Option" : "Alt";
        if (Mods.HasFlag(HotMods.Shift)) yield return "Shift";
        if (super && mac) yield return "Command";
        yield return Key;
    }
}
