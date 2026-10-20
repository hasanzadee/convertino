using Convertino.Services;

namespace Convertino;

/// <summary>Everything the shared core needs from the host OS / UI toolkit.</summary>
public interface IPlatform
{
    /// <summary>Render PDF pages to PNG bytes. dpi = output dots per inch.</summary>
    Task<List<byte[]>> RenderPdf(string path, double dpi, CancellationToken ct, Action<double>? progress = null, int maxPages = int.MaxValue);

    /// <summary>Text recognition on an encoded image (PNG/JPEG…).</summary>
    Task<OcrResult> Ocr(byte[] image);

    void SetClipboardText(string text);
    void SetClipboardFiles(IEnumerable<string> files);
    /// <summary>Put a picture (PNG bytes) on the clipboard itself, not as a file, so it pastes into chats and documents.</summary>
    void SetClipboardImage(byte[] png);

    /// <summary>Document converter (Microsoft Office on Windows, LibreOffice anywhere) or null.</summary>
    IOffice? Office { get; }

    /// <summary>Interactive image editor by id (crop, adjust, annotate, addbg, redact) or null if this UI has none.</summary>
    Func<Job, Task>? Editor(string id);

    void Reveal(string path);
    void Open(string path);
    void SetAutostart(bool on);

    IJobUi Ui { get; }
}

/// <summary>Progress/result card for one job.</summary>
public interface IJobView
{
    Action? OnCancel { set; }
    void Progress(double p);
    void Done(string title, string summary, IReadOnlyList<string> outputs, string? icon);
    void Failed(string title, string message, string? icon);
    void Dismiss();
}

public interface IJobUi
{
    /// <summary>Run on the UI thread.</summary>
    void Post(Action a);
    IJobView Show(PetalAction action, IReadOnlyList<string> files, ScreenPoint where);
}

public static class Platform
{
    static IPlatform? _current;

    public static IPlatform Current
    {
        get => _current ?? throw new InvalidOperationException("Platform not initialised");
        set => _current = value;
    }

    public static bool IsWindows => OperatingSystem.IsWindows();
}

/// <summary>Converts office-type documents.</summary>
public interface IOffice
{
    string Name { get; }
    bool Supports(Kind kind);
    /// <summary>
    /// Convert every job input to <paramref name="ext"/>; outputs land beside the originals.
    /// <paramref name="prepare"/> can swap an input for a pre-processed file (e.g. Markdown → HTML).
    /// </summary>
    Task Convert(Job job, string ext, Func<string, string>? prepare = null);

    /// <summary>Document → page images (one file, or a folder for several pages).</summary>
    Task ToImages(Job job, string ext);
}
