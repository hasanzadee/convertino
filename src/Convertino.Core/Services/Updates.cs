using System.Net.Http;
using System.Text.Json;

namespace Convertino.Services;

/// <summary>
/// Is there a newer release on GitHub? Notify only: nothing is downloaded or installed, and the one request is an
/// anonymous GET of the latest release (drafts and pre-releases are excluded there). The automatic check runs only
/// after the user said yes on the one-time card (<see cref="Settings.CheckForUpdates"/>).
/// </summary>
public static class Updates
{
    public const string ReleasesPage = "https://github.com/hasanzadee/convertino/releases/latest";
    const string Api = "https://api.github.com/repos/hasanzadee/convertino/releases/latest";
    static readonly TimeSpan Every = TimeSpan.FromDays(1);

    public sealed record Release(Version Version, string Url);

    // the one-time question, the same on every platform
    public const string AskTitle = "Notify about updates?";
    public const string AskText = "Checks GitHub once a day; nothing else is sent.";
    public const string AskTip = "Let Convertino tell you when a new version is out? It checks GitHub once a day; nothing else is sent.";

    /// <summary>Ask on start until answered (quitting without an answer asks again next time). Not in CI (screenshots).</summary>
    public static bool ShouldAsk => Settings.Current.CheckForUpdates == null && Environment.GetEnvironmentVariable("CI") != "true";

    static readonly SemaphoreSlim Wake = new(0);

    public static void Answer(bool yes)
    {
        Settings.Current.CheckForUpdates = yes;
        Settings.Current.Save();
        Log.Info($"update check {(yes ? "on" : "off")}");
        if (yes) Wake.Release(); // check now rather than within the hour
    }

    public static string AnnounceTitle(Release r) => $"Convertino {r.Version} is out";
    public static string AnnounceText => $"You have {Current} · click to open the release";

    public static Version Current => Parse(Cli.Version) ?? new Version(0, 0, 0);

    /// <summary>"v1.2.0", "1.2" → 1.2.0; anything else → null.</summary>
    public static Version? Parse(string? tag)
    {
        if (!Version.TryParse(tag?.Trim().TrimStart('v', 'V'), out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>The latest release; null when its tag isn't a version. Throws when GitHub can't be reached.</summary>
    public static async Task<Release?> Latest(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Convertino/{Cli.Version}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var doc = JsonDocument.Parse(await http.GetStringAsync(Api, ct));
        var root = doc.RootElement;
        var v = Parse(root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null);
        if (v == null) return null;
        var url = root.TryGetProperty("html_url", out var u) && u.GetString() is { Length: > 0 } s ? s : ReleasesPage;
        return new Release(v, url);
    }

    /// <summary>"Check for updates…": always answers, including when offline.</summary>
    public static async Task<(string Title, string Text, string? Url)> CheckNow()
    {
        try
        {
            var r = await Task.Run(() => Latest());
            if (r != null && r.Version > Current) return (AnnounceTitle(r), AnnounceText, r.Url);
            return ("You're up to date", $"Convertino {Current} is the latest version", null);
        }
        catch (Exception ex)
        {
            Log.Info($"update check failed: {ex.Message}");
            return ("Couldn't check for updates", "GitHub didn't answer · try again later", null);
        }
    }

    /// <summary>
    /// Background check while the app runs: at most once a day, only with the user's yes, silent on any failure,
    /// and each new version is announced once. <paramref name="announce"/> runs on the UI thread.
    /// </summary>
    public static void StartDaily(Action<Release> announce)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    if (await Due() is { } r) Platform.Current.Ui.Post(() => announce(r));
                }
                catch (Exception ex) { Log.Info($"update check skipped: {ex.Message}"); }
                await Wake.WaitAsync(TimeSpan.FromHours(1));
            }
        });
    }

    static async Task<Release?> Due()
    {
        var s = Settings.Current;
        if (s.CheckForUpdates != true || DateTime.Now - s.LastUpdateCheck < Every) return null;
        s.LastUpdateCheck = DateTime.Now;
        s.Save();
        var r = await Latest();
        if (r == null || r.Version <= Current || r.Version.ToString() == s.LastNotifiedVersion) return null;
        s.LastNotifiedVersion = r.Version.ToString();
        s.Save();
        return r;
    }
}
