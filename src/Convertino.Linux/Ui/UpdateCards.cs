using Convertino.Services;

namespace Convertino.Ui;

/// <summary>The cards behind <see cref="Updates"/>: the one-time question, "x.y.z is out", and the manual check's answer.</summary>
public static class UpdateCards
{
    public static void Start()
    {
        if (Updates.ShouldAsk) Ask();
        Updates.StartDaily(r => Show(Updates.AnnounceTitle(r), Updates.AnnounceText, r.Url));
    }

    static void Ask()
    {
        var t = Toasts.Create(new ScreenPoint(0, 0));
        t.SetChoice(Updates.AskTitle, Updates.AskText, "update", "Yes", "No", Updates.Answer, Updates.AskTip);
        Toasts.Show(t);
    }

    /// <summary>"Check for updates…" in the tray / menu-bar menu.</summary>
    public static async void CheckNow()
    {
        var (title, text, url) = await Updates.CheckNow();
        Show(title, text, url);
    }

    static void Show(string title, string text, string? url)
    {
        var t = Toasts.Create(new ScreenPoint(0, 0));
        if (url != null) t.OnClick = () => Platform.Current.Open(url);
        t.SetDone(title, text, null, "update", seconds: url != null ? 12 : null);
        Toasts.Show(t);
    }
}
