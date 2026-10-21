namespace Convertino;

public readonly record struct ScreenPoint(int X, int Y);

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public enum Kind { Image, Pdf, Word, Excel, Slides, Text, Markdown, Video, Audio, Archive, Folder, Subtitles, Part, Checksum, Data, Other }

/// <summary>One petal of the flower: a conversion ("PNG") or a tool ("Crop").</summary>
public sealed class PetalAction
{
    public required string Id { get; init; }
    public required string Label { get; init { field = value; if (EnglishLabel.Length == 0) EnglishLabel = L.English(value); } }
    /// <summary>Small caption under a format label, e.g. "lossless".</summary>
    public string Hint { get; init; } = "";
    /// <summary>Icon key for tools ("crop", "compress" …); null for format petals. Each UI maps keys to glyphs.</summary>
    public string? Icon { get; init; }
    /// <summary>Full sentence, e.g. "Convert to PNG" — used for progress cards and drag captions.</summary>
    public required string Verb { get; init { field = value; if (EnglishVerb.Length == 0) EnglishVerb = L.English(value); } }
    /// <summary>Very short hub caption while hovering, e.g. "→ PNG".</summary>
    public string Short { get; init { field = value; if (EnglishShort.Length == 0) EnglishShort = L.English(value); } } = "";
    /// <summary>Opens its own window instead of running as a background job.</summary>
    public bool Interactive { get; init; }
    /// <summary>Finishes instantly (clipboard etc.) — no progress card.</summary>
    public bool Quick { get; init; }
    /// <summary>Where its results go instead of the "Save results" setting (a recipe's own choice; see <see cref="Output"/>).</summary>
    public string? Out { get; init; }
    public required Func<Job, Task> Run { get; init; }

    // the English of Label / Verb / Short, caught as they're set (straight after L.T made them): Recent and stats.json save
    // these, so they don't split per UI language, even where two English texts share one translation.
    // Code that copies a petal into a new one passes them along (EnglishVerb = other.EnglishVerb …); set ones are kept.
    public string EnglishLabel { get; init; } = "";
    public string EnglishVerb { get; init; } = "";
    public string EnglishShort { get; init; } = "";
}

public sealed class Job
{
    public required IReadOnlyList<string> Inputs { get; init; }
    public required PetalAction Action { get; init; }
    public ScreenPoint Where { get; init; }
    public CancellationToken Token { get; init; }
    public Action<double>? OnProgress { get; init; }
    /// <summary>Where results end up ("Save results", see <see cref="Output"/>), for drops and the CLI; null leaves them beside the originals.</summary>
    public string? Place { get; init; }
    public List<string> Outputs { get; } = [];
    /// <summary>One-line result for the card, e.g. "1.2 MB → 340 KB (−72%)".</summary>
    public string? Summary { get; set; }
    /// <summary>Set when the job produced something other than files (clipboard text …).</summary>
    public string? Title { get; set; }
    /// <summary>Nothing to report (e.g. an editor was closed without saving).</summary>
    public bool Silent { get; set; }
    /// <summary>The job ran but its answer is "no" (a checksum that doesn't match): an error card, and exit code 1 on the command line.</summary>
    public bool Failed { get; set; }
    /// <summary>Overrides the "space saved" tally when the outputs aren't smaller copies of the inputs (e.g. a report).</summary>
    public long? Saved { get; set; }

    public void Report(double p) => OnProgress?.Invoke(p);

    /// <summary>Progress for item i of n, with fraction f inside that item.</summary>
    public void Report(int i, int n, double f = 0) => OnProgress?.Invoke((i + Math.Clamp(f, 0, 1)) / Math.Max(1, n));
}

public static class Kinds
{
    static readonly Dictionary<string, Kind> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = Kind.Image, [".jpg"] = Kind.Image, [".jpeg"] = Kind.Image, [".jfif"] = Kind.Image, [".webp"] = Kind.Image,
        [".heic"] = Kind.Image, [".heif"] = Kind.Image, [".avif"] = Kind.Image, [".bmp"] = Kind.Image, [".tif"] = Kind.Image,
        [".tiff"] = Kind.Image, [".gif"] = Kind.Image, [".ico"] = Kind.Image, [".tga"] = Kind.Image, [".psd"] = Kind.Image,
        [".svg"] = Kind.Image, [".jxl"] = Kind.Image, [".qoi"] = Kind.Image, [".dng"] = Kind.Image, [".cr2"] = Kind.Image, [".nef"] = Kind.Image, [".arw"] = Kind.Image,
        [".pdf"] = Kind.Pdf,
        [".docx"] = Kind.Word, [".doc"] = Kind.Word, [".rtf"] = Kind.Word, [".odt"] = Kind.Word, [".docm"] = Kind.Word,
        [".xlsx"] = Kind.Excel, [".xls"] = Kind.Excel, [".xlsm"] = Kind.Excel, [".ods"] = Kind.Excel, [".csv"] = Kind.Excel,
        [".pptx"] = Kind.Slides, [".ppt"] = Kind.Slides, [".odp"] = Kind.Slides, [".pps"] = Kind.Slides, [".ppsx"] = Kind.Slides,
        [".txt"] = Kind.Text, [".log"] = Kind.Text,
        [".md"] = Kind.Markdown, [".markdown"] = Kind.Markdown,
        [".json"] = Kind.Data, [".jsonl"] = Kind.Data, [".ndjson"] = Kind.Data, [".yaml"] = Kind.Data, [".yml"] = Kind.Data, [".toml"] = Kind.Data, [".xml"] = Kind.Data, [".css"] = Kind.Data,
        [".mp4"] = Kind.Video, [".mov"] = Kind.Video, [".mkv"] = Kind.Video, [".webm"] = Kind.Video, [".avi"] = Kind.Video,
        [".m4v"] = Kind.Video, [".wmv"] = Kind.Video, [".flv"] = Kind.Video, [".mpg"] = Kind.Video, [".mpeg"] = Kind.Video,
        [".ts"] = Kind.Video, [".3gp"] = Kind.Video, [".mts"] = Kind.Video,
        [".mp3"] = Kind.Audio, [".wav"] = Kind.Audio, [".flac"] = Kind.Audio, [".ogg"] = Kind.Audio, [".m4a"] = Kind.Audio,
        [".aac"] = Kind.Audio, [".opus"] = Kind.Audio, [".wma"] = Kind.Audio, [".aiff"] = Kind.Audio, [".amr"] = Kind.Audio,
        [".oga"] = Kind.Audio,
        [".zip"] = Kind.Archive, [".7z"] = Kind.Archive, [".rar"] = Kind.Archive, [".tar"] = Kind.Archive, [".tgz"] = Kind.Archive,
        [".gz"] = Kind.Archive, [".bz2"] = Kind.Archive, [".tbz2"] = Kind.Archive, [".tbz"] = Kind.Archive, [".xz"] = Kind.Archive,
        [".txz"] = Kind.Archive,
        [".001"] = Kind.Part,
        [".sha256"] = Kind.Checksum, [".sha1"] = Kind.Checksum, [".md5"] = Kind.Checksum, [".sha512"] = Kind.Checksum,
        [".sha256sum"] = Kind.Checksum, [".sha1sum"] = Kind.Checksum, [".md5sum"] = Kind.Checksum, [".sha512sum"] = Kind.Checksum,
        [".srt"] = Kind.Subtitles, [".vtt"] = Kind.Subtitles, [".ass"] = Kind.Subtitles, [".ssa"] = Kind.Subtitles, [".lrc"] = Kind.Subtitles,
    };

    public static Kind Of(string path)
    {
        if (Directory.Exists(path)) return Kind.Folder;
        if (Services.FileTools.IsChecksumList(path)) return Kind.Checksum;
        return Map.TryGetValue(Path.GetExtension(path), out var k) ? k : Kind.Other;
    }

    public static string Ext(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public static string Badge(string path)
    {
        if (Directory.Exists(path)) return "DIR";
        var e = Ext(path).ToUpperInvariant();
        return e.Length > 4 ? e[..4] : e;
    }
}
