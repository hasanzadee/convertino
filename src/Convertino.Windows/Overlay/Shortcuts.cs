using System.IO;
using Convertino.Input;
using Convertino.Native;
using Convertino.Services;
using Convertino.Ui;

namespace Convertino.Overlay;

/// <summary>
/// Convertino without dragging a file: Win+Shift+X opens the flower for what's on the clipboard, Win+Shift+A captures
/// an area of the screen and opens the flower for it (both from the settings, see <see cref="Hotkey"/>).
/// </summary>
public sealed class Shortcuts : IDisposable
{
    public static Shortcuts? Current { get; private set; }

    readonly OverlayController _overlay;
    readonly Hotkeys _keys = new();
    bool _capturing; // from the first await on, so a quick second press can't start another capture

    public Shortcuts(OverlayController overlay, GlobalInput input)
    {
        _overlay = overlay;
        Current = this;
        input.Escape += () => RegionCapture.Active?.Cancel();
        Hotkey.SettingsChanged += () => Apply(announce: true);
        Apply(announce: true);
    }

    /// <summary>(Re)register both shortcuts from the settings; a taken one gets a card saying so.</summary>
    void Apply(bool announce)
    {
        _keys.Clear();
        var s = Settings.Current;
        // paused: the keys go back to the other apps
        Hotkey.ClipboardProblem = s.Enabled && s.ClipboardHotkeyOn ? _keys.Register(s.ClipboardKey, Clipboard) : null;
        Hotkey.CaptureProblem = s.Enabled && s.CaptureHotkeyOn ? _keys.Register(s.CaptureKey, Capture) : null;
        if (!announce) return;
        foreach (var (key, problem) in new[] { (s.ClipboardKey, Hotkey.ClipboardProblem), (s.CaptureKey, Hotkey.CaptureProblem) })
            if (problem != null) Card(L.T("{0} isn't free", key.Label), L.T("{0} — pick another in the Convertino panel", Upper(L.Saved(problem))), "clipboard");
    }

    /// <summary>The flower for whatever is on the clipboard, at the cursor (click mode).</summary>
    public async void Clipboard()
    {
        if (!Settings.Current.Enabled) return;
        var at = Win32.Cursor;
        Offer? offer;
        try
        {
            var content = await Platform.Current.ReadClipboard();
            Log.Info($"clipboard flower: {Describe(content)}");
            offer = await Task.Run(() => ClipFlower.For(content));
        }
        catch (Exception ex)
        {
            Log.Error("clipboard flower", ex);
            Card(L.T("Couldn't read the clipboard"), Jobs.Friendly(ex), "clipboard");
            return;
        }
        if (offer == null) { Card(L.T("The clipboard is empty"), L.T("Copy a file, a picture or some text first"), "clipboard"); return; }
        _overlay.Open(offer, at);
    }

    void Capture() => Capture(delayMs: 0);

    /// <summary>Freeze the screens, let the user drag an area, save it to Pictures\Convertino, bloom its flower where the mouse came up.</summary>
    public async void Capture(int delayMs)
    {
        if (!Settings.Current.Enabled || _capturing || RegionCapture.Active != null) return;
        _capturing = true;
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs);
            if (_overlay.CloseIfOpen()) await Task.Delay(320); // gone before the screens are frozen
            var r = await RegionCapture.Run();
            if (r == null) return;
            var path = Captures.NewPath("Screenshot", "png");
            await File.WriteAllBytesAsync(path, await Task.Run(r.Png));
            var offer = await Task.Run(() => ClipFlower.ForScreenshot(path));
            await Task.Delay(60); // the overlay is surely off the screen before the flower grabs what's behind it for its glass
            _overlay.Open(offer, r.Release);
        }
        catch (Exception ex)
        {
            Log.Error("capture", ex);
            Card(L.T("Couldn't capture the screen"), Jobs.Friendly(ex), "capture");
        }
        finally { _capturing = false; }
    }

    static string Describe(ClipContent? c) => c switch
    {
        ClipContent.Files f => $"{f.Paths.Count} file(s)",
        ClipContent.Image i => $"picture {Paths.HumanSize(i.Png.Length)}",
        ClipContent.Text t => $"text {t.Value.Length} chars",
        _ => "nothing",
    };

    static string Upper(string s) => s.Length > 0 ? char.ToUpper(s[0], L.Culture) + s[1..] : s;

    static void Card(string title, string text, string icon)
    {
        var t = Toasts.Create(Win32.Cursor);
        t.SetDone(title, text, null, Icons.Glyph(icon) ?? "", error: false);
        Toasts.Show(t);
    }

    public void Dispose() => _keys.Dispose();
}
