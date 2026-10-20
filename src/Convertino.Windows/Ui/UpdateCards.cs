using Convertino.Native;
using Convertino.Services;

namespace Convertino.Ui;

/// <summary>The cards behind <see cref="Updates"/>: the one-time question, "x.y.z is out", and the manual check's answer.</summary>
public static class UpdateCards
{
    static string Glyph => Icons.Glyph("update")!;

    public static void Start()
    {
        if (Updates.ShouldAsk) Ask();
        Updates.StartDaily(r => Show(Updates.AnnounceTitle(r), Updates.AnnounceText, r.Url));
    }

    static void Ask()
    {
        var t = Toasts.Create(Win32.Cursor);
        t.SetChoice(Updates.AskTitle, Updates.AskText, Glyph, "Yes", "No", Updates.Answer, Updates.AskTip);
        Toasts.Show(t);
    }

    /// <summary>"Check for updates…" in the tray menu and the panel footer.</summary>
    public static async void CheckNow()
    {
        var (title, text, url) = await Updates.CheckNow();
        Show(title, text, url);
    }

    static void Show(string title, string text, string? url)
    {
        var t = Toasts.Create(Win32.Cursor);
        if (url != null) t.OnClick = () => Shell.Open(url);
        t.SetDone(title, text, null, Glyph);
        Toasts.Show(t);
    }
}
