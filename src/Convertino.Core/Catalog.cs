using ImageMagick;
using Convertino.Services;

namespace Convertino;

/// <summary>One page of the flower: its petals and the name the hub shows when you flip to it.</summary>
public sealed record Ring(string Name, IReadOnlyList<PetalAction> Petals);

public sealed record Pages(List<PetalAction> Convert, List<PetalAction> Tools, string Title, string Badge)
{
    /// <summary>Most petals one ring shows; a longer list is spread evenly over consecutive rings ("Tools", "Tools 2").</summary>
    public const int MaxPetals = 10;

    /// <summary>One-tap extras after Tools: no window, a new file beside the source or the clipboard (images: Half, Full HD, B&amp;W …).</summary>
    public List<PetalAction> More { get; init; } = [];

    /// <summary>Rarer output formats. Last in the cycle, so they sit one step back from Convert (images: HEIC, TIFF, ICO, BMP, JPEG XL, QOI).</summary>
    public List<PetalAction> MoreFormats { get; init; } = [];

    /// <summary>Your recipes and commands that fit these files (<see cref="Convertino.Recipes"/>): one flip from Convert.</summary>
    public List<PetalAction> Recipes { get; init; } = [];

    /// <summary>The rings in flip order, Convert → Recipes → Tools → More tools → More formats, empty ones left out.</summary>
    public IReadOnlyList<Ring> Rings => [.. Split("Convert", Convert), .. Split("Recipes", Recipes), .. Split("Tools", Tools), .. Split("More tools", More), .. Split("More formats", MoreFormats)];

    static IEnumerable<Ring> Split(string name, List<PetalAction> petals)
    {
        if (petals.Count == 0) yield break;
        int rings = (petals.Count + MaxPetals - 1) / MaxPetals, size = (petals.Count + rings - 1) / rings;
        for (int i = 0; i < rings; i++)
            yield return new(i == 0 ? name : $"{name} {i + 1}", petals.Skip(i * size).Take(size).ToList());
    }
}

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
    static PetalAction VerifyA => Tool("verify", "Verify", "check", "Verify checksum", FileTools.Verify);
    static PetalAction DupesA => Tool("dupes", "Duplicates", "search", "Find duplicate files", FileTools.Duplicates);
    static PetalAction SevenZA => Pack("7z", "7Z", "smallest", Archives.SevenZip);
    static PetalAction TarGzA => Pack("tar.gz", "TAR.GZ", "Linux, Mac", Archives.TarGz);

    static PetalAction Pack(string ext, string label, string hint, Func<Job, Task> run) => new()
    {
        Id = "to-" + ext, Label = label, Hint = hint, Verb = $"Compress to .{ext}", Short = $"→ {label}", Run = run,
    };

    /// <summary>For the Tools ring: split presets that fit one file (none under 10 MB), then MD5 / SHA-1 / SHA-512.</summary>
    static IEnumerable<PetalAction> FileToolsFor(IReadOnlyList<string> files)
    {
        if (files.Count == 1)
            foreach (var p in FileTools.SplitPresets(Paths.SizeOf(files[0])))
                yield return new()
                {
                    Id = "split-" + p.Id, Label = p.Label, Hint = p.Hint, Verb = $"Split into {p.Label} parts", Short = $"Split {p.Label}",
                    Run = j => FileTools.Split(j, p.Bytes),
                };
        foreach (var (id, label) in new[] { ("md5", "MD5"), ("sha1", "SHA-1"), ("sha512", "SHA-512") })
            yield return Tool(id, label, "hash", $"Copy {label} hash", j => FileTools.CopyHash(j, label));
    }

    /// <summary>Every action reachable for these files (all rings), for the CLI.</summary>
    public static IEnumerable<PetalAction> All(IReadOnlyList<string> files)
    {
        var p = For(files);
        return p.Convert.Concat(p.Recipes).Concat(p.Tools).Concat(p.More).Concat(p.MoreFormats);
    }

    /// <summary>The built-in petals alone, without your recipes and commands: what a recipe's steps are made of.</summary>
    public static IEnumerable<PetalAction> Builtin(IReadOnlyList<string> files)
    {
        var p = Base(files);
        return p.Convert.Concat(p.Tools).Concat(p.More).Concat(p.MoreFormats);
    }

    public static Pages For(IReadOnlyList<string> files)
    {
        var p = Base(files);
        return Convertino.Recipes.Petals(files, p) is { Count: > 0 } mine ? p with { Recipes = mine } : p;
    }

    static Pages Base(IReadOnlyList<string> files)
    {
        var kinds = files.Select(Kinds.Of).Distinct().ToList();
        int n = files.Count;
        string title = n == 1 ? Path.GetFileName(files[0].TrimEnd('\\', '/')) : $"{n} items";
        string badge = n == 1 ? Kinds.Badge(files[0]) : n.ToString();

        if (kinds.Count == 1) return ForKind(kinds[0], files, title, badge);

        // one .sha256 / SHA256SUMS dropped together with the files it lists
        var lists = files.Where(f => Kinds.Of(f) == Kind.Checksum).ToList();
        if (lists.Count == 1 && FileTools.Covers(lists[0], files.Where(f => f != lists[0]).ToList()))
            return new([VerifyA, ZipA, PathA], [], title, badge);

        // mixed: images + PDFs can still become one PDF
        if (kinds.All(k => k is Kind.Image or Kind.Pdf))
            return new([Tool("merge", "Merge PDF", "merge", "Merge into one PDF", Pdfs.Merge), ZipA, PathA], [], title, badge);
        return new([ZipA, SevenZA, TarGzA, DupesA, PathA, CopyA], [], title, badge);
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
        List<PetalAction> conv = [], tools = [], more = [], formats = [];
        var office = Platform.Current.Office;
        bool hasOffice(Kind k) => office?.Supports(k) == true;

        switch (kind)
        {
            case Kind.Image:
            {
                void F(List<PetalAction> ring, string e, string hint, MagickFormat mf)
                {
                    if (Images.CanWrite(mf)) ring.Add(Fmt(e, hint, j => Images.Convert(j, e)));
                }
                F(conv, "png", "lossless", MagickFormat.Png);
                F(conv, "jpg", "universal", MagickFormat.Jpeg);
                F(conv, "webp", "web", MagickFormat.WebP);
                F(conv, "avif", "smallest", MagickFormat.Avif);
                conv.Add(Fmt("pdf", single ? "document" : "one file", j => Pdfs.ImagesToPdf(j, merge: true)));
                F(conv, "gif", "classic", MagickFormat.Gif);
                // rarer formats get the last ring, one step back from Convert
                F(formats, "heic", "Apple", MagickFormat.Heic);
                F(formats, "tiff", "print", MagickFormat.Tiff);
                F(formats, "ico", "icon", MagickFormat.Ico);
                F(formats, "bmp", "plain", MagickFormat.Bmp);
                F(formats, "jxl", "JPEG XL", MagickFormat.Jxl);
                F(formats, "qoi", "fast", MagickFormat.Qoi);
                conv.RemoveAll(a => a.Id == "to-" + Norm(ext));
                formats.RemoveAll(a => a.Id == "to-" + Norm(ext));
                // More tools: one tap, no window, a new file beside each source (the same for one image or many).
                // New one-tap image tools go here; editors and AI stay on Tools. Rings over 10 petals split by themselves.
                more.Add(Tool("half", "Half", "half", "Half the size", Images.Half));
                more.Add(Tool("fithd", "Full HD", "fithd", "Fit in 1920×1080", Images.FitFullHd));
                more.Add(Tool("gray", "B&W", "gray", "Black & white", Images.Grayscale));
                more.AddRange(ImageTools.Petals(files)); // Messenger, Square, Rounded, Palette, Favicon, Copy, QR; Compare/Contact/Slideshow for several

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
                if (Pdfs.CanCompress) tools.Add(Tool("compress", "Compress", "compress", "Make the PDF smaller", Pdfs.Compress));
                tools.Add(Tool("pdfimages", "Images", "picture", "Extract the images", Pdfs.ExtractImages));
                tools.Add(Tool("pagenum", "Numbers", "hash", "Add page numbers", Pdfs.PageNumbers));
                tools.Add(Tool("cover", "Cover", "camera", "First page as PNG", Pdfs.Cover));
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
            case Kind.Excel when hasOffice(Kind.Excel) || files.All(Sheets.IsSheet):
            {
                // CSV ⇄ XLSX, → JSON and the HTML / Markdown tables run in-process: no office suite needed, UTF-8 kept, every sheet converted
                bool sheets = files.All(Sheets.IsSheet), viaOffice = hasOffice(Kind.Excel);
                if (viaOffice) conv.Add(Fmt("pdf", "print", j => office!.Convert(j, "pdf")));
                if (ext != "xlsx") conv.Add(Fmt("xlsx", "Excel", j => sheets ? Data.Convert(j, "xlsx") : office!.Convert(j, "xlsx")));
                if (ext != "csv") conv.Add(Fmt("csv", "UTF-8", j => sheets ? Data.Convert(j, "csv") : office!.Convert(j, "csv")));
                if (viaOffice && ext != "ods") conv.Add(Fmt("ods", "OpenDoc", j => office!.Convert(j, "ods")));
                conv.Add(Fmt("html", sheets ? "table" : "web", j => sheets ? Data.Convert(j, "html") : office!.Convert(j, "html")));
                if (sheets) conv.Add(Fmt("json", "data", j => Data.Convert(j, "json")));
                if (sheets) conv.Add(Fmt("md", "table", j => Data.Convert(j, "md")));
                tools.AddRange([ZipA, PathA, HashA]);
                break;
            }
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
                tools.AddRange(ImageTools.Petals(files)); // QR code of the text
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
            case Kind.Data:
            {
                // JSON, JSON Lines, YAML, TOML and XML into one another, and to CSV / XLSX (Services/Data.cs);
                // CSS only gets Pretty / Minify, so they lead its first ring
                string self = Data.Canon(ext);
                bool same = files.All(f => Data.Canon(Kinds.Ext(f)) == self), css = files.Any(f => Kinds.Ext(f) == "css");
                void D(string to, string hint)
                {
                    if (!css && !(same && to == self)) conv.Add(Fmt(to, hint, j => Data.Convert(j, to)));
                }
                D("json", "web");
                D("yaml", "readable");
                D("toml", "config");
                D("xml", "markup");
                D("csv", "table");
                D("xlsx", "Excel");
                if (self is "json" or "jsonl") D("jsonl", "lines");
                var ring = css ? conv : tools;
                if (files.All(Data.CanReformat))
                {
                    ring.Add(Tool("pretty", "Pretty", "code", "Pretty-print", Data.Pretty));
                    ring.Add(Tool("minify", "Minify", "compress", "Minify", Data.Minify));
                }
                ring.Add(Tool("copytext", "Copy", "copy", "Copy contents", CopyText, quick: true));
                tools.AddRange([ZipA, PathA, HashA, B64A]);
                break;
            }
            case Kind.Archive:
                conv.Add(Tool("extract", "Extract", "folder", "Extract here", Archives.Extract));
                conv.AddRange([PathA, HashA]);
                tools.AddRange(FileToolsFor(files));
                break;
            case Kind.Part:
                conv.Add(Tool("join", "Join", "merge", "Join the parts", FileTools.Join));
                conv.AddRange([PathA, HashA]);
                break;
            case Kind.Checksum:
                conv.Add(VerifyA);
                conv.Add(Tool("copytext", "Copy", "copy", "Copy contents", CopyText, quick: true));
                conv.Add(PathA);
                break;
            case Kind.Folder:
                conv.AddRange([ZipA, SevenZA, TarGzA, DupesA, PathA, CopyA]);
                break;
            case Kind.Subtitles:
            {
                // .ssa → ASS would only drop its styles; players read SSA as it is
                bool same = files.All(f => Kinds.Ext(f) == ext);
                foreach (var (e, h) in new[] { ("srt", "universal"), ("vtt", "web"), ("ass", "advanced"), ("txt", "text only") })
                    if (!same || (e != ext && !(e == "ass" && ext == "ssa"))) conv.Add(Fmt(e, h, j => Subtitles.Convert(j, e)));
                tools.AddRange([ZipA, PathA, HashA]);
                break;
            }
            default:
                conv.AddRange([ZipA, PathA, HashA, B64A]);
                tools.AddRange(FileToolsFor(files));
                tools.AddRange(ImageTools.Petals(files)); // QR code of a .url / .webloc link
                if (!single) conv.InsertRange(1, [SevenZA, TarGzA]);
                break;
        }

        // several files of one kind: which of them are the same?
        if (!single && kind is not (Kind.Folder or Kind.Part or Kind.Checksum))
        {
            var ring = tools.Count > 0 ? tools : conv;
            if (ring.Count < 8) ring.Add(DupesA);
        }

        foreach (var ext2 in Extensions)
            if (ext2(kind, files) is { } extra)
            {
                var ring = tools.Count > 0 ? tools : conv;
                if (ring.Count >= 8) ring.Insert(Math.Min(ring.Count - 1, 7), extra); else ring.Add(extra);
                if (ring.Count > 8) ring.RemoveAt(ring.Count - 1);
            }

        // UTF-8 and LF / CRLF for anything that is plain text, whatever its kind: decided by extension and a sniff
        if (TextFiles.Petals(files) is { Count: > 0 } text)
        {
            var ring = tools.Count > 0 ? tools : conv;
            ring.InsertRange(0, text);
            while (ring.Count > 8) ring.RemoveAt(ring.Count - 1);
        }

        if (conv.Count == 0) conv.AddRange([ZipA, PathA, HashA]);
        return new(conv, tools, title, badge) { More = more, MoreFormats = formats };
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
        foreach (var f in job.Inputs) parts.Add(TextFiles.Decode(await File.ReadAllBytesAsync(f, job.Token)));
        var text = string.Join(Environment.NewLine + Environment.NewLine, parts);
        Platform.Current.SetClipboardText(text);
        job.Title = "Text copied";
        job.Summary = Ocr.Preview(text);
    }
}
