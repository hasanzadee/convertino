using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ImageMagick;

namespace Convertino.Services;

/// <summary>A flower that doesn't come from a drag (the clipboard, a screenshot): its files, its rings, what the hub says.</summary>
public sealed class Offer
{
    public required IReadOnlyList<string> Files { get; init; }
    public required Pages Pages { get; init; }
    /// <summary>Big and small hub lines; null = the usual size and file name.</summary>
    public string? HubTitle { get; init; }
    public string? HubSub { get; init; }
    /// <summary>Made only for this flower (a pasted picture): removed when it closes without a petal picked.</summary>
    public string? Scratch { get; init; }
    /// <summary>The clipboard text handed to the petals as a temp file: removed when the flower is done with it.</summary>
    public string? TempInput { get; init; }

    /// <summary>The flower closed; <paramref name="used"/> = a petal ran and its job is over.</summary>
    public void Closed(bool used)
    {
        if (TempInput != null) TryDelete(TempInput);
        if (!used && Scratch != null) TryDelete(Scratch);
    }

    static void TryDelete(string p)
    {
        try { File.Delete(p); } catch (Exception ex) { Log.Error("offer cleanup", ex); }
    }
}

/// <summary>
/// "Convertino without dragging a file": the flower for whatever is on the clipboard and for a screen capture.
/// Copied files get their normal flower, a picture is saved into <see cref="Captures.Folder"/> and gets the image
/// flower (a "Quick" ring first), text gets its own petals (TXT, MD, QR; JSON and CSV when it is that; a link: QR
/// plus whatever <see cref="LinkPetals"/> adds).
/// </summary>
public static class ClipFlower
{
    /// <summary>Extra petals for a link on the clipboard, after QR (the browser-download piece adds "Download" here).</summary>
    public static readonly List<Func<Uri, PetalAction?>> LinkPetals = [];

    /// <summary>The first ring of a pasted picture or a screenshot, in this order, when the image flower has them.</summary>
    static readonly string[] Quick = ["copyimg", "ocr", "readqr", "annotate", "crop", "redact", "to-jpg", "to-webp", "to-pdf", "cutout"];

    static (string Hash, string Path)? _lastPicture;

    /// <summary>The flower for this clipboard content, or null when there is nothing to offer.</summary>
    public static Offer? For(ClipContent? content) => content switch
    {
        ClipContent.Files f => ForFiles(f.Paths),
        ClipContent.Image i => ForPicture(i.Png),
        ClipContent.Text t when t.Value.Trim().Length > 0 => ForText(t.Value),
        _ => null,
    };

    /// <summary>A capture already saved as PNG (kept even if no petal is picked: it was taken on purpose).</summary>
    public static Offer ForScreenshot(string png) => PictureOffer(png, "Screenshot", scratch: false);

    static Offer? ForFiles(IReadOnlyList<string> paths)
    {
        var files = paths.Where(p => File.Exists(p) || Directory.Exists(p)).Select(Path.GetFullPath).Distinct().ToList();
        return files.Count == 0 ? null : new Offer { Files = files, Pages = Catalog.For(files) };
    }

    static Offer ForPicture(byte[] png)
    {
        // the same picture twice in a row (the shortcut pressed again): one file
        var hash = Convert.ToHexString(SHA256.HashData(png));
        if (_lastPicture is { } last && last.Hash == hash && File.Exists(last.Path)) return PictureOffer(last.Path, "Clipboard", scratch: false);
        var path = Captures.NewPath("Clipboard", "png");
        File.WriteAllBytes(path, png);
        _lastPicture = (hash, path);
        return PictureOffer(path, "Clipboard", scratch: true);
    }

    static Offer PictureOffer(string png, string what, bool scratch)
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
        return new Offer { Files = [png], Pages = pages, HubTitle = dims.Length > 0 ? dims : Paths.HumanSize(Paths.SizeOf(png)), HubSub = what, Scratch = scratch ? png : null };
    }

    // ---------- text ----------

    static Offer ForText(string text)
    {
        var tmp = Path.Combine(Paths.Temp, $"clipboard-{Guid.NewGuid():N}.txt");
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        var link = Link(text);
        var petals = new List<PetalAction>();
        if (link != null)
        {
            petals.Add(QrPetal);
            foreach (var extra in LinkPetals)
                try { if (extra(link) is { } p) petals.Add(p); }
                catch (Exception ex) { Log.Error("link petal", ex); }
        }
        petals.Add(SaveAs("txt", "TXT", "text"));
        petals.Add(SaveAs("md", "MD", "Markdown"));
        if (link == null && Encoding.UTF8.GetByteCount(text.Trim()) <= 2900) petals.Add(QrPetal);
        if (IsJson(text)) petals.Add(SaveAs("json", "JSON", "pretty"));
        if (Table(text) != null) petals.Add(SaveAs("csv", "CSV", "table"));
        string badge = link != null ? "URL" : "TEXT";
        var pages = new Pages(petals.Take(Pages.MaxPetals).ToList(), [], link != null ? "Link" : "Text", badge);
        return new Offer
        {
            Files = [tmp], Pages = pages, TempInput = tmp,
            HubTitle = link != null ? link.Host : Count(text),
            HubSub = link != null ? "Link" : Short(text),
        };
    }

    static PetalAction QrPetal => Catalog.Tool("clip-qr", "QR", "qr", "Make a QR code", async job =>
    {
        var text = (await File.ReadAllTextAsync(job.Inputs[0], job.Token)).Trim();
        job.Outputs.Add(await Task.Run(() => SaveText(text, "qr")));
        job.Summary = Ocr.Preview(text);
    });

    static PetalAction SaveAs(string ext, string label, string hint) => new()
    {
        Id = "clip-" + ext, Label = label, Hint = hint, Verb = $"Save as .{ext}", Short = $"→ {label}",
        Run = async job =>
        {
            var text = await File.ReadAllTextAsync(job.Inputs[0], job.Token);
            job.Outputs.Add(await Task.Run(() => SaveText(text, ext)));
            job.Summary = Ocr.Preview(text);
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
                if (!IsJson(text)) throw new InvalidOperationException("The clipboard text isn't JSON");
                var dst = Captures.NewPath("Clipboard", "json", dir);
                File.WriteAllText(dst, Data.Json(JsonNode.Parse(text), indent: true) + "\n", new UTF8Encoding(false));
                return dst;
            }
            case "csv":
            {
                var rows = Table(text) ?? throw new InvalidOperationException("The clipboard text isn't a table (tab-separated cells)");
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
                throw new InvalidOperationException($"Text can be saved as txt, md, json, csv or qr, not {ext}");
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
        return words == 1 ? $"{text.Trim().Length} chars" : $"{words} words";
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
        if (items.Count == 0) throw new FileNotFoundException("The result isn't there any more");
        if (items.Count == 1 && File.Exists(items[0]))
        {
            var f = items[0];
            if (Kinds.Of(f) == Kind.Image)
            {
                var job = new Job { Inputs = [f], Action = Catalog.Tool("copyimg", "Copy", "image", "Copy image", ImageTools.CopyImage, quick: true) };
                await ImageTools.CopyImage(job);
                return job.Title ?? "Image copied";
            }
            if (IsTextFile(f))
            {
                Platform.Current.SetClipboardText(TextFiles.Decode(await File.ReadAllBytesAsync(f)));
                return "Text copied";
            }
        }
        Platform.Current.SetClipboardFiles(items);
        return items.Count == 1 ? "Copied — paste it in a folder or a chat" : $"{items.Count} files copied";
    }

    static bool IsTextFile(string f) =>
        new FileInfo(f).Length < 4_000_000 &&
        (Kinds.Of(f) is Kind.Text or Kind.Markdown or Kind.Data or Kind.Subtitles or Kind.Checksum || Kinds.Ext(f) is "csv" or "tsv" or "html" or "htm" or "url");
}
