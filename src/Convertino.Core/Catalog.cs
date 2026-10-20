using ImageMagick;
using Convertino.Services;

namespace Convertino;

public sealed record Pages(List<PetalAction> Convert, List<PetalAction> Tools, string Title, string Badge);

/// <summary>Which petals a given set of dropped files gets.</summary>
public static class Catalog
{
    /// <summary>Extra tools contributed at startup (e.g. the AI cut-out when its model is present).</summary>
    public static readonly List<Func<Kind, IReadOnlyList<string>, PetalAction?>> Extensions = [];

    static PetalAction Fmt(string ext, string hint, Func<Job, Task> run, string? label = null) => new()
    {
        Id = "to-" + ext,
        Label = label ?? ext.ToUpperInvariant(),
        Hint = hint,
        Verb = $"Convert to {label ?? ext.ToUpperInvariant()}",
        Short = $"→ {label ?? ext.ToUpperInvariant()}",
        Run = run,
    };

    public static PetalAction Tool(string id, string label, string icon, string verb, Func<Job, Task> run, bool interactive = false, bool quick = false) => new()
    {
        Id = id, Label = label, Icon = icon, Verb = verb, Short = label, Run = run, Interactive = interactive, Quick = quick,
    };

    static PetalAction ZipA => Tool("zip", "Zip", "zip", "Compress to .zip", Misc.Zip);
    static PetalAction PathA => Tool("path", "Path", "link", "Copy full path", Misc.CopyPath, quick: true);
    static PetalAction HashA => Tool("sha", "SHA-256", "hash", "Copy SHA-256 hash", Misc.Sha256, quick: true);
    static PetalAction B64A => Tool("b64", "Base64", "code", "Copy as data URI", Misc.Base64, quick: true);
    static PetalAction CopyA => Tool("copy", "Copy", "copy", "Copy to clipboard", Misc.CopyFiles, quick: true);

    /// <summary>Every action reachable for these files (both rings), for the CLI.</summary>
    public static IEnumerable<PetalAction> All(IReadOnlyList<string> files)
    {
        var p = For(files);
        return p.Convert.Concat(p.Tools);
    }

    public static Pages For(IReadOnlyList<string> files)
    {
        var kinds = files.Select(Kinds.Of).Distinct().ToList();
        int n = files.Count;
        string title = n == 1 ? Path.GetFileName(files[0].TrimEnd('\\', '/')) : $"{n} items";
        string badge = n == 1 ? Kinds.Badge(files[0]) : n.ToString();

        if (kinds.Count == 1) return ForKind(kinds[0], files, title, badge);

        // mixed: images + PDFs can still become one PDF
        if (kinds.All(k => k is Kind.Image or Kind.Pdf))
            return new([Tool("merge", "Merge PDF", "merge", "Merge into one PDF", Pdfs.Merge), ZipA, PathA], [], title, badge);
        return new([ZipA, PathA, CopyA], [], title, badge);
    }

    static PetalAction? Editor(string id, string label, string icon, string verb)
    {
        var run = Platform.Current.Editor(id);
        return run == null ? null : Tool(id, label, icon, verb, run, interactive: true);
    }

    static Pages ForKind(Kind kind, IReadOnlyList<string> files, string title, string badge)
    {
        bool single = files.Count == 1;
        string ext = Kinds.Ext(files[0]);
        List<PetalAction> conv = [], tools = [];
        var office = Platform.Current.Office;
        bool hasOffice(Kind k) => office?.Supports(k) == true;

        switch (kind)
        {
            case Kind.Image:
            {
                void F(string e, string hint, MagickFormat mf)
                {
                    if (Images.CanWrite(mf)) conv.Add(Fmt(e, hint, j => Images.Convert(j, e)));
                }
                F("png", "lossless", MagickFormat.Png);
                F("jpg", "universal", MagickFormat.Jpeg);
                F("webp", "web", MagickFormat.WebP);
                F("avif", "smallest", MagickFormat.Avif);
                conv.Add(Fmt("pdf", single ? "document" : "one file", j => Pdfs.ImagesToPdf(j, merge: true)));
                F("heic", "Apple", MagickFormat.Heic);
                F("ico", "icon", MagickFormat.Ico);
                F("tiff", "print", MagickFormat.Tiff);
                conv.RemoveAll(a => a.Id == "to-" + Norm(ext));
                if (conv.Count > 8) conv.RemoveAt(conv.Count - 1);

                // AI background removal only when its model is installed (convertino model install)
                var cutout = Cutout.Available ? Tool("cutout", "Cut out", "cutout", "Remove background", Cutout.Run) : null;
                if (single)
                {
                    if (cutout != null) tools.Add(cutout);
                    tools.Add(Tool("compress", "Compress", "compress", "Make it smaller", Images.Compress));
                    tools.Add(Tool("meta", "Clean", "tag", "Strip metadata & GPS", Images.StripMetadata));
                    foreach (var e in new[]
                    {
                        Editor("adjust", "Adjust", "adjust", "Light, color, size"),
                        Editor("annotate", "Annotate", "annotate", "Draw, arrows, text"),
                        Editor("addbg", "Add BG", "addbg", "Beautify on a backdrop"),
                        Editor("crop", "Crop", "crop", "Crop"),
                        Editor("redact", "Redact", "redact", "Hide sensitive parts"),
                    })
                        if (e != null) tools.Add(e);
                    tools.Add(Tool("ocr", "Text", "ocr", "Copy text (OCR)", OcrToClipboard));
                }
                else
                {
                    tools.Add(Tool("compress", "Compress", "compress", $"Compress {files.Count} images", Images.Compress));
                    tools.Add(Tool("meta", "Clean", "tag", "Strip metadata & GPS", Images.StripMetadata));
                    if (cutout != null) tools.Add(cutout);
                    tools.Add(Tool("ocr", "Text", "ocr", "Copy text (OCR)", OcrToClipboard));
                    tools.Add(ZipA);
                }
                break;
            }
            case Kind.Pdf:
                if (hasOffice(Kind.Pdf)) conv.Add(Fmt("docx", "Word", j => office!.Convert(j, "docx")));
                conv.Add(Fmt("jpg", "pages", j => Pdfs.ToImages(j, "jpg")));
                conv.Add(Fmt("txt", "text", Pdfs.ToText));
                conv.Add(Fmt("png", "pages", j => Pdfs.ToImages(j, "png")));
                if (!single) tools.Add(Tool("merge", "Merge", "merge", $"Merge {files.Count} PDFs", Pdfs.Merge));
                tools.Add(Tool("ocr", "Text", "ocr", "Copy text (OCR)", Pdfs.Ocr_));
                tools.Add(Tool("split", "Split", "cut", "One PDF per page", Pdfs.Split));
                tools.Add(Tool("rotate", "Rotate", "rotate", "Rotate 90°", Pdfs.Rotate));
                tools.Add(Tool("clean", "Clean", "tag", "Strip metadata", Pdfs.Clean));
                if (single) tools.Add(ZipA);
                break;
            case Kind.Word when hasOffice(Kind.Word):
                conv.Add(Fmt("pdf", "print", j => office!.Convert(j, "pdf")));
                if (ext != "docx") conv.Add(Fmt("docx", "Word", j => office!.Convert(j, "docx")));
                conv.Add(Fmt("txt", "plain", j => office!.Convert(j, "txt")));
                conv.Add(Fmt("png", "pages", j => office!.ToImages(j, "png")));
                if (ext != "rtf") conv.Add(Fmt("rtf", "rich text", j => office!.Convert(j, "rtf")));
                if (ext != "odt") conv.Add(Fmt("odt", "OpenDoc", j => office!.Convert(j, "odt")));
                conv.Add(Fmt("html", "web", j => office!.Convert(j, "html")));
                tools.AddRange([ZipA, PathA, HashA]);
                break;
            case Kind.Excel when hasOffice(Kind.Excel):
                conv.Add(Fmt("pdf", "print", j => office!.Convert(j, "pdf")));
                if (ext != "xlsx") conv.Add(Fmt("xlsx", "Excel", j => office!.Convert(j, "xlsx")));
                if (ext != "csv") conv.Add(Fmt("csv", "UTF-8", j => office!.Convert(j, "csv")));
                if (ext != "ods") conv.Add(Fmt("ods", "OpenDoc", j => office!.Convert(j, "ods")));
                conv.Add(Fmt("html", "web", j => office!.Convert(j, "html")));
                tools.AddRange([ZipA, PathA, HashA]);
                break;
            case Kind.Slides when hasOffice(Kind.Slides):
                conv.Add(Fmt("pdf", "handout", j => office!.Convert(j, "pdf")));
                conv.Add(Fmt("png", "slides", j => office!.Convert(j, "png")));
                if (ext != "pptx") conv.Add(Fmt("pptx", "PowerPoint", j => office!.Convert(j, "pptx")));
                if (ext != "odp") conv.Add(Fmt("odp", "OpenDoc", j => office!.Convert(j, "odp")));
                tools.AddRange([ZipA, PathA, HashA]);
                break;
            case Kind.Text:
                if (hasOffice(Kind.Text))
                {
                    conv.Add(Fmt("pdf", "print", j => office!.Convert(j, "pdf")));
                    conv.Add(Fmt("docx", "Word", j => office!.Convert(j, "docx")));
                }
                conv.Add(Tool("copytext", "Copy", "copy", "Copy contents", CopyText, quick: true));
                tools.AddRange([ZipA, PathA, HashA, B64A]);
                break;
            case Kind.Markdown:
                conv.Add(Fmt("html", "web page", Misc.MarkdownHtml));
                if (hasOffice(Kind.Markdown))
                {
                    conv.Add(Fmt("pdf", "print", j => Misc.MarkdownVia(j, "pdf")));
                    conv.Add(Fmt("docx", "Word", j => Misc.MarkdownVia(j, "docx")));
                }
                conv.Add(Tool("copytext", "Copy", "copy", "Copy contents", CopyText, quick: true));
                tools.AddRange([ZipA, PathA, HashA]);
                break;
            case Kind.Video when Media.Available:
                conv.Add(Fmt("mp4", "H.264", j => Media.Convert(j, "mp4")));
                conv.Add(Fmt("webm", "VP9", j => Media.Convert(j, "webm")));
                conv.Add(Fmt("gif", "loop", j => Media.Convert(j, "gif")));
                conv.Add(Fmt("mp3", "audio", j => Media.Convert(j, "mp3")));
                conv.Add(Fmt("mov", "Apple", j => Media.Convert(j, "mov")));
                conv.Add(Fmt("mkv", "remux", j => Media.Convert(j, "mkv")));
                conv.Add(Fmt("wav", "audio", j => Media.Convert(j, "wav")));
                conv.RemoveAll(a => a.Id == "to-" + ext);
                tools.Add(Tool("vcompress", "Compress", "compress", "Smaller video", Media.Compress));
                tools.Add(Tool("720p", "720p", "resize", "Downscale to 720p", Media.To720));
                tools.Add(Tool("mute", "Mute", "mute", "Remove audio", Media.Mute));
                tools.Add(Tool("frame", "Frame", "camera", "Grab a still", Media.Snapshot));
                tools.Add(Tool("subtitles", "Subtitles", "subtitles", "Save subtitles as SRT", Subtitles.FromVideo));
                break;
            case Kind.Audio when Media.Available:
                foreach (var (e, h) in new[] { ("mp3", "universal"), ("m4a", "AAC"), ("wav", "raw"), ("flac", "lossless"), ("ogg", "Vorbis"), ("opus", "tiny") })
                    if (e != ext) conv.Add(Fmt(e, h, j => Media.Convert(j, e)));
                tools.Add(Tool("normalize", "Loudness", "volume", "Normalize volume", Media.Normalize));
                tools.Add(Tool("trim", "Trim", "cut", "Trim silence", Media.TrimSilence));
                tools.AddRange([ZipA, PathA]);
                break;
            case Kind.Archive:
                conv.Add(Tool("extract", "Extract", "folder", "Extract here", Misc.Extract));
                conv.AddRange([PathA, HashA]);
                break;
            case Kind.Folder:
                conv.AddRange([ZipA, PathA, CopyA]);
                break;
            case Kind.Subtitles:
            {
                // .ssa → ASS would only drop its styles; players read SSA as it is
                bool same = files.All(f => Kinds.Ext(f) == ext);
                foreach (var (e, h) in new[] { ("srt", "universal"), ("vtt", "web"), ("ass", "advanced"), ("txt", "text only") })
                    if (!same || (e != ext && !(e == "ass" && ext == "ssa"))) conv.Add(Fmt(e, h, j => Subtitles.Convert(j, e)));
                tools.Add(Tool("utf8", "UTF-8", "encoding", "Re-encode as UTF-8", Subtitles.ToUtf8));
                tools.AddRange([ZipA, PathA, HashA]);
                break;
            }
            default:
                conv.AddRange([ZipA, PathA, HashA, B64A]);
                break;
        }

        foreach (var ext2 in Extensions)
            if (ext2(kind, files) is { } extra)
            {
                var ring = tools.Count > 0 ? tools : conv;
                if (ring.Count >= 8) ring.Insert(Math.Min(ring.Count - 1, 7), extra); else ring.Add(extra);
                if (ring.Count > 8) ring.RemoveAt(ring.Count - 1);
            }

        if (conv.Count == 0) conv.AddRange([ZipA, PathA, HashA]);
        return new(conv, tools, title, badge);
    }

    static string Norm(string ext) => ext switch { "jpeg" or "jfif" => "jpg", "tif" => "tiff", "heif" => "heic", _ => ext };

    static async Task OcrToClipboard(Job job)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < job.Inputs.Count; i++)
        {
            job.Report(i, job.Inputs.Count, 0.2);
            sb.AppendLine(await Ocr.RecognizeFile(job.Inputs[i])).AppendLine();
        }
        var text = sb.ToString().Trim();
        Platform.Current.SetClipboardText(text);
        job.Title = text.Length == 0 ? "No text found" : "Text copied";
        job.Summary = Ocr.Preview(text);
    }

    static async Task CopyText(Job job)
    {
        var parts = new List<string>();
        foreach (var f in job.Inputs) parts.Add(await File.ReadAllTextAsync(f, job.Token));
        var text = string.Join(Environment.NewLine + Environment.NewLine, parts);
        Platform.Current.SetClipboardText(text);
        job.Title = "Text copied";
        job.Summary = Ocr.Preview(text);
    }
}
