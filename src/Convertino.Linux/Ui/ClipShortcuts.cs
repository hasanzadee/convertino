using Convertino.Services;

namespace Convertino.Ui;

/// <summary>System-wide shortcuts of one platform (X11 XGrabKey, macOS Carbon hot keys).</summary>
public interface IHotkeys
{
    /// <summary>Null when it worked, otherwise why not ("another app uses it" …).</summary>
    string? Register(Hotkey key, Action action);
    void Clear();
}

/// <summary>
/// Convertino without dragging a file (Linux and macOS): one shortcut opens the flower for what's on the clipboard,
/// another captures an area of the screen and opens the flower for it. The platform brings its hot keys, the way it
/// shows a flower and the way it captures; the flow, the cards and the settings live here.
/// </summary>
public sealed class ClipShortcuts
{
    public static ClipShortcuts? Current { get; private set; }

    readonly IHotkeys? _keys;
    readonly Action<Offer, ScreenPoint?> _open;
    readonly Func<Task<(string Png, ScreenPoint? At)?>> _capture;
    bool _capturing;

    /// <param name="keys">Null where global shortcuts can't work (Wayland without X11 focus, no display): only the menu items and "convertino menu --clipboard / --capture" do.</param>
    /// <param name="open">Show the flower for an offer, at a point or at the pointer.</param>
    /// <param name="capture">Let the user pick an area; the saved PNG and where the pick ended, null when cancelled; throws with a message for the card.</param>
    public ClipShortcuts(IHotkeys? keys, Action<Offer, ScreenPoint?> open, Func<Task<(string Png, ScreenPoint? At)?>> capture)
    {
        _keys = keys;
        _open = open;
        _capture = capture;
        Current = this;
        Hotkey.SettingsChanged += () => Apply(announce: true);
        Apply(announce: true);
    }

    /// <summary>Whether this session has global shortcuts at all (the panel says how to get them otherwise).</summary>
    public bool HasHotkeys => _keys != null;

    void Apply(bool announce)
    {
        if (_keys == null) return;
        _keys.Clear();
        var s = Settings.Current;
        Hotkey.ClipboardProblem = s.ClipboardHotkeyOn ? _keys.Register(s.ClipboardKey, Clipboard) : null;
        Hotkey.CaptureProblem = s.CaptureHotkeyOn ? _keys.Register(s.CaptureKey, Capture) : null;
        if (!announce) return;
        foreach (var (key, problem) in new[] { (s.ClipboardKey, Hotkey.ClipboardProblem), (s.CaptureKey, Hotkey.CaptureProblem) })
            if (problem != null) Card($"{key.Label} isn't free", $"{char.ToUpperInvariant(problem[0])}{problem[1..]} — pick another in the Convertino panel", "clipboard");
    }

    /// <summary>The flower for whatever is on the clipboard, at the pointer (click mode).</summary>
    public async void Clipboard()
    {
        if (!Settings.Current.Enabled) return;
        Offer? offer;
        try
        {
            var content = await Platform.Current.ReadClipboard();
            Log.Info("clipboard flower: " + content switch
            {
                ClipContent.Files f => $"{f.Paths.Count} file(s)",
                ClipContent.Image i => $"picture {Paths.HumanSize(i.Png.Length)}",
                ClipContent.Text t => $"text {t.Value.Length} chars",
                _ => "nothing",
            });
            offer = await Task.Run(() => ClipFlower.For(content));
        }
        catch (Exception ex)
        {
            Log.Error("clipboard flower", ex);
            Card("Couldn't read the clipboard", Jobs.Friendly(ex), "clipboard");
            return;
        }
        if (offer == null) { Card("The clipboard is empty", "Copy a file, a picture or some text first", "clipboard"); return; }
        _open(offer, null);
    }

    public void Capture() => Capture(delayMs: 0);

    /// <summary>Pick an area of the screen, save it to Pictures/Convertino, bloom its flower where the pick ended.</summary>
    public async void Capture(int delayMs)
    {
        if (!Settings.Current.Enabled || _capturing) return;
        _capturing = true;
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs);
            var shot = await _capture();
            if (shot is not { } r) return;
            Log.Info($"capture saved: {r.Png}");
            var offer = await Task.Run(() => ClipFlower.ForScreenshot(r.Png));
            await Task.Delay(60); // the overlay is surely off the screen before the flower grabs what's behind it for its glass
            _open(offer, r.At);
        }
        catch (Exception ex)
        {
            Log.Error("capture", ex);
            Card("Couldn't capture the screen", Jobs.Friendly(ex), "capture");
        }
        finally { _capturing = false; }
    }

    static void Card(string title, string text, string icon)
    {
        var t = Toasts.Create(new ScreenPoint(0, 0));
        t.SetDone(title, text, null, icon, seconds: 8);
        Toasts.Show(t);
    }
}
