using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ImageMagick;

namespace Convertino.Services;

/// <summary>A flower that doesn't come from a drag (the clipboard, a screenshot): its files, its rings, what the hub says.</summary>
public sealed class Offer
{
    /// <summary>What the flower works on. For clipboard text nothing is on disk (the petals carry the text); for a pasted picture, a private temp file until <see cref="Use"/>.</summary>
    public required IReadOnlyList<string> Files { get; init; }
    public required Pages Pages { get; init; }
    /// <summary>Big and small hub lines; null = the usual size and file name.</summary>
    public string? HubTitle { get; init; }
    public string? HubSub { get; init; }
    /// <summary>The pasted picture this flower shows, while it waits in <see cref="Captures.Pending"/>.</summary>
    internal ClipFlower.Pasted? Pasted { get; init; }

    /// <summary>A petal was picked: the files to hand it. A pasted picture moves into Pictures/Convertino now, so the result lands next to it there.</summary>
    public IReadOnlyList<string> Use() => Pasted is { } p ? [ClipFlower.Keep(p)] : Files;

    /// <summary>The flower closed (its job, if a petal ran, is over): a pasted picture nobody used is removed.</summary>
    public void Closed(bool used)
    {
        if (Pasted is { } p) ClipFlower.Release(p);
    }
}

/// <summary>
/// "Convertino without dragging a file": the flower for whatever is on the clipboard and for a screen capture.
/// Copied files get their normal flower, a picture gets the image flower (a "Quick" ring first) and is saved into
/// <see cref="Captures.Folder"/> once a petal uses it, text gets its own petals (TXT, MD, QR; JSON and CSV when it is
/// that; a link: QR plus whatever <see cref="LinkPetals"/> adds). Text stays in memory until a petal writes it, and text
/// a password manager marked as secret is never shown.
/// </summary>
public static class ClipFlower
{
    /// <summary>Extra petals for a link on the clipboard, after QR (the browser-download piece adds "Download" here).</summary>
    public static readonly List<Func<Uri, PetalAction?>> LinkPetals = [];

    /// <summary>The first ring of a pasted picture or a screenshot, in this order, when the image flower has them.</summary>
    static readonly string[] Quick = ["copyimg", "ocr", "readqr", "annotate", "crop", "redact", "to-jpg", "to-webp", "to-pdf", "cutout"];

    /// <summary>Longest text a QR code holds (bytes, at the lowest error correction).</summary>
    const int QrBytes = 2900;

    /// <summary>The flower for this clipboard content, or null when there is nothing to offer.</summary>
    public static Offer? For(ClipContent? content)
    {
        lock (Gate) if (!_swept) { _swept = true; Captures.SweepPending(); }
        return Make(content);
    }

    static Offer? Make(ClipContent? content) => content switch
    {
        ClipContent.Files f => ForFiles(f.Paths),
        ClipContent.Image i => ForPicture(i.Png),
        ClipContent.Text t when t.Value.Trim().Length > 0 => ForText(t.Value, t.Secret),
        _ => null,
    };

    /// <summary>A capture already saved as PNG (it was taken on purpose: it stays even if no petal is picked).</summary>
    public static Offer ForScreenshot(string png) => PictureOffer(png, "Screenshot", null);

    static Offer? ForFiles(IReadOnlyList<string> paths)
    {
        var files = paths.Where(p => File.Exists(p) || Directory.Exists(p)).Select(Path.GetFullPath).Distinct().ToList();
        return files.Count == 0 ? null : new Offer { Files = files, Pages = Catalog.For(files) };
    }

    // ---------- pasted pictures ----------

    /// <summary>
    /// A pasted picture waiting in the private temp folder: shared by the flowers showing it (the shortcut pressed again
    /// while one closes), moved into Pictures/Convertino when a petal uses it, removed when the last unused flower closes.
    /// </summary>
    internal sealed class Pasted
    {
        public required string Hash;
        public required string Path;
        public int Holders;
        public bool Kept;
    }

    static readonly object Gate = new();
    static Pasted? _last;
    static bool _swept;

    static Offer ForPicture(byte[] png)
    {
        var hash = Convert.ToHexString(SHA256.HashData(png));
        Pasted p;
        lock (Gate)
        {
            // the same picture twice in a row (the shortcut pressed again): one file
            if (_last is { } last && last.Hash == hash && File.Exists(last.Path))
            {
                if (last.Kept) return PictureOffer(last.Path, "Clipboard", null);
                p = last;
            }
            else
            {
                var path = Captures.NewPath("Clipboard", "png", Captures.Pending);
                File.WriteAllBytes(path, png);
                _last = p = new Pasted { Hash = hash, Path = path };
            }
            p.Holders++;
        }
        return PictureOffer(p.Path, "Clipboard", p);
    }

    /// <summary>A petal uses the picture: into Pictures/Convertino with it (once), its path there.</summary>
    internal static string Keep(Pasted p)
    {
        lock (Gate)
        {
            if (p.Kept || !File.Exists(p.Path)) return p.Path;
            try
            {
                var dst = Captures.NewPath("Clipboard", "png");
                File.Move(p.Path, dst);
                p.Path = dst;
                p.Kept = true;
            }
            catch (Exception ex) { Log.Error("keep pasted picture", ex); } // the petal still runs, on the temp copy
            return p.Path;
        }
    }

    internal static void Release(Pasted p)
    {
        lock (Gate)
        {
            if (--p.Holders > 0 || p.Kept) return;
            try { File.Delete(p.Path); } catch (Exception ex) { Log.Error("pasted picture cleanup", ex); }
        }
    }

    static Offer PictureOffer(string png, string what, Pasted? pasted)
    {
        var all = Catalog.For([png]);
        var pool = all.Convert.Concat(all.Tools).Concat(all.More).ToList();
        var first = Quick.Select(id => pool.FirstOrDefault(a => a.Id == id)).OfType<PetalAction>().ToList();
        bool Rest(PetalAction a) => !first.Contains(a);
        var pages = new Pages(first, [.. all.Tools.Where(Rest)], all.Title, all.Badge)
        {
            FirstRing = "Quick",
            More = [.. all.More.Where(Rest)],
            MoreFormats = [.. all.Convert.Where(Rest), .. all.MoreFormats],
        };
        string dims = "";
        try { var info = new MagickImageInfo(png); dims = $"{info.Width}×{info.Height}"; } catch (Exception ex) { Log.Error("picture size", ex); }
        return new Offer { Files = [png], Pages = pages, HubTitle = dims.Length > 0 ? dims : Paths.HumanSize(Paths.SizeOf(png)), HubSub = L.T(what), Pasted = pasted };
    }

    // ---------- text ----------

    /// <summary>
    /// Text petals carry the text themselves: nothing is written until one is picked, and then only its own result.
    /// <paramref name="secret"/> (a password manager's copy): the hub says "Hidden text" and the cards never quote it.
    /// </summary>
    static Offer ForText(string text, bool secret)
    {
        var link = secret ? null : Link(text);
        bool fitsQr = Encoding.UTF8.GetByteCount(text.Trim()) <= QrBytes;
        var petals = new List<PetalAction>();
        if (link != null)
        {
            if (fitsQr) petals.Add(QrPetal(text, secret));
            foreach (var extra in LinkPetals)
                try { if (extra(link) is { } p) petals.Add(p); }
                catch (Exception ex) { Log.Error("link petal", ex); }
        }
        petals.Add(SaveAs(text, secret, "txt", "TXT", "text"));
        petals.Add(SaveAs(text, secret, "md", "MD", "Markdown"));
        if (link == null && fitsQr) petals.Add(QrPetal(text, secret));
        if (!secret && IsJson(text)) petals.Add(SaveAs(text, secret, "json", "JSON", "pretty"));
        if (!secret && Table(text) != null) petals.Add(SaveAs(text, secret, "csv", "CSV", "table"));
        string badge = link != null ? "URL" : "TEXT";
        var pages = new Pages(petals.Take(Pages.MaxPetals).ToList(), [], link != null ? L.T("Link") : L.T("Text"), badge);
        return new Offer
        {
            // nothing at this path: it only names the input on cards ("Clipboard text")
            Files = [Path.Combine(Path.GetTempPath(), "Clipboard text")],
            Pages = pages,
            HubTitle = secret ? L.T("Hidden text") : link != null ? link.Host : Count(text),
            HubSub = secret ? L.N(text.Length, "{0} char", "{0} chars") : link != null ? L.T("Link") : Short(text),
        };
    }

    static string Said(string text, bool secret) => secret ? L.N(text.Length, "Hidden text, {0} char", "Hidden text, {0} chars") : Ocr.Preview(text);

    static PetalAction QrPetal(string text, bool secret) => Catalog.Tool("clip-qr", "QR", "qr", "Make a QR code", async job =>
    {
        job.Outputs.Add(await Task.Run(() => SaveText(text, "qr"), job.Token));
        job.Summary = Said(text, secret);
    });

    static PetalAction SaveAs(string text, bool secret, string ext, string label, string hint) => new()
    {
        Id = "clip-" + ext, Label = label, Hint = L.T(hint), Verb = L.T("Save as .{0}", ext), Short = $"→ {label}",
        Run = async job =>
        {
            job.Outputs.Add(await Task.Run(() => SaveText(text, ext), job.Token));
            job.Summary = Said(text, secret);
        },
    };

    /// <summary>
    /// Clipboard text as a new file in <paramref name="dir"/> (the captures folder by default): txt, md, json (pretty),
    /// csv (from tab-separated cells, as spreadsheets copy them) or qr (a PNG). Returns its path.
    /// </summary>
    public static string SaveText(string text, string ext, string? dir = null)
    {
        switch (ext)
        {
            case "qr":
            {
                var dst = Captures.NewPath("QR", "png", dir);
                File.WriteAllBytes(dst, Qr.Png(text.Trim()));
                return dst;
            }
            case "json":
            {
                if (!IsJson(text)) throw new InvalidOperationException(L.En("The clipboard text isn't JSON"));
                var dst = Captures.NewPath("Clipboard", "json", dir);
                File.WriteAllText(dst, Data.Json(JsonNode.Parse(text), indent: true) + "\n", new UTF8Encoding(false));
                return dst;
            }
            case "csv":
            {
                var rows = Table(text) ?? throw new InvalidOperationException(L.En("The clipboard text isn't a table (tab-separated cells)"));
                var dst = Captures.NewPath("Clipboard", "csv", dir);
                // with a BOM: Excel opens UTF-8 CSV right only then
                File.WriteAllText(dst, string.Join("\r\n", rows.Select(r => string.Join(",", r.Select(CsvCell)))) + "\r\n", new UTF8Encoding(true));
                return dst;
            }
            case "txt" or "md":
            {
                var dst = Captures.NewPath("Clipboard", ext, dir);
                File.WriteAllText(dst, text, new UTF8Encoding(false));
                return dst;
            }
            default:
                throw new InvalidOperationException(L.En("Text can be saved as txt, md, json, csv or qr, not {0}", ext));
        }
    }

    /// <summary>Local paths in a text/uri-list (or x-special/gnome-copied-files) payload; comments and non-file links skipped.</summary>
    public static List<string> UriList(string text)
    {
        var list = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\0');
            if (line.Length == 0 || line.StartsWith('#') || line is "copy" or "cut") continue;
            if (Uri.TryCreate(line, UriKind.Absolute, out var u) && u.IsFile && (File.Exists(u.LocalPath) || Directory.Exists(u.LocalPath)))
                list.Add(u.LocalPath);
        }
        return list;
    }

    /// <summary>A single http(s)/ftp link and nothing else.</summary>
    public static Uri? Link(string text)
    {
        var t = text.Trim();
        if (t.Length > 4000 || t.Any(char.IsWhiteSpace)) return null;
        return Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "ftp" && u.Host.Length > 0 ? u : null;
    }

    static bool IsJson(string text)
    {
        var t = text.TrimStart();
        if (t.Length == 0 || t[0] is not ('{' or '[')) return false;
        try { return JsonNode.Parse(text) != null; }
        catch (System.Text.Json.JsonException) { return false; }
    }

    /// <summary>Cells copied from a spreadsheet: two or more lines, each with the same number of tabs (at least one).</summary>
    static List<string[]>? Table(string text)
    {
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        if (lines.Length < 2) return null;
        int tabs = lines[0].Count(c => c == '\t');
        if (tabs == 0 || lines.Any(l => l.Count(c => c == '\t') != tabs)) return null;
        return [.. lines.Select(l => l.Split('\t'))];
    }

    static string CsvCell(string s) => s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    static string Count(string text)
    {
        int words = text.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;
        return words == 1 ? L.N(text.Trim().Length, "{0} char", "{0} chars") : L.N(words, "{0} word", "{0} words");
    }

    static string Short(string text)
    {
        var t = string.Join(" ", text.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return t.Length > 22 ? t[..21] + "…" : t;
    }

    // ---------- results back to the clipboard ----------

    /// <summary>
    /// A finished job's result onto the clipboard (the result card's Copy button): a picture as a picture, a text
    /// file as its text, anything else as files. Returns what to tell the user. Call it off the UI thread.
    /// </summary>
    public static async Task<string> CopyBack(IReadOnlyList<string> outputs)
    {
        var items = outputs.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (items.Count == 0) throw new FileNotFoundException(L.En("The result isn't there any more"));
        if (items.Count == 1 && File.Exists(items[0]))
        {
            var f = items[0];
            // a GIF stays a file: as a picture only its first frame would paste
            if (Kinds.Of(f) == Kind.Image && Kinds.Ext(f) != "gif")
            {
                var job = new Job { Inputs = [f], Action = Catalog.Tool("copyimg", "Copy", "image", "Copy image", ImageTools.CopyImage, quick: true) };
                await ImageTools.CopyImage(job);
                return job.Title ?? L.T("Image copied");
            }
            if (IsTextFile(f))
            {
                Platform.Current.SetClipboardText(TextFiles.Decode(await File.ReadAllBytesAsync(f)));
                return L.T("Text copied");
            }
        }
        Platform.Current.SetClipboardFiles(items);
        return items.Count == 1 ? L.T("Copied — paste it in a folder or a chat") : L.N(items.Count, "{0} file copied", "{0} files copied");
    }

    static bool IsTextFile(string f) =>
        new FileInfo(f).Length < 4_000_000 &&
        (Kinds.Of(f) is Kind.Text or Kind.Markdown or Kind.Data or Kind.Subtitles or Kind.Checksum || Kinds.Ext(f) is "csv" or "tsv" or "html" or "htm" or "url");
}
