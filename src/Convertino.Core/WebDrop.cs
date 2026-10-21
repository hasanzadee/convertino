using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Convertino.Services;

namespace Convertino;

/// <summary>A drag's data as each platform hands it over: Windows clipboard formats, X11 MIME types or macOS pasteboard types.</summary>
public interface IDragData
{
    /// <summary>The formats on offer, by name ("UniformResourceLocatorW", "text/x-moz-url", "public.url" …).</summary>
    IReadOnlyList<string> Formats { get; }

    /// <summary>One format's bytes, or null when it isn't there or is bigger than <paramref name="max"/>.</summary>
    byte[]? Bytes(string format, int max);

    /// <summary>
    /// Bytes that aren't a plain format — Windows virtual files (FileContents), macOS file promises, X11 direct save
    /// (XDS) — written into <paramref name="dir"/>, as <paramref name="name"/> where the platform lets us choose.
    /// Called during the drop; null when this platform has no such thing.
    /// </summary>
    Task<string?>? Save(WebItem item, string dir, string name) => null;
}

public enum WebSource { Url, Virtual, Format, TempFile, Promise }

/// <summary>One thing dragged out of a page: an image, a file or a link.</summary>
public sealed class WebItem
{
    /// <summary>Where it lives on the web (http, https or data:). The download for <see cref="WebSource.Url"/>, a fallback for the rest.</summary>
    public Uri? Url { get; set; }
    /// <summary>The file name the page or the browser gave it (virtual file, DownloadURL, file promise).</summary>
    public string? Name { get; set; }
    /// <summary>The type it was declared as (DownloadURL, data:, a promised UTI, a raw image format).</summary>
    public string? Mime { get; set; }
    /// <summary>Link text, for the shortcut and QR names.</summary>
    public string? Title { get; set; }
    /// <summary>A picture: an &lt;img&gt;, raw image data or an image file.</summary>
    public bool Image { get; set; }
    /// <summary>Size the drag announced (virtual files), 0 when unknown.</summary>
    public long Size { get; set; }
    public WebSource Source { get; init; } = WebSource.Url;
    /// <summary>The format name (<see cref="WebSource.Format"/>) or the browser's temp file (<see cref="WebSource.TempFile"/>).</summary>
    public string? Key { get; init; }
    /// <summary>The virtual file / promise index.</summary>
    public int Index { get; init; }
    /// <summary>
    /// A picture format the drag also carries (X11 image/png, macOS public.tiff, Windows PNG / DIB): the browser's
    /// re-encoding of the decoded picture, so only the last resort when neither the original nor its download works.
    /// </summary>
    public string? Raw { get; set; }
    /// <summary>Set at the drop: the bytes the drag carried, in a file (promises arrive a moment later).</summary>
    internal Task<string?>? Grabbed { get; set; }
    internal string? RawGrabbed { get; set; }
}

/// <summary>
/// A drag out of a web browser — Chrome, Edge, Firefox, Safari — or any app that offers links, pictures or virtual files
/// instead of real paths. <see cref="Read"/> works out what it is from the formats alone (no network before the drop) so the
/// flower can bloom with the right petals; <see cref="Grab"/> takes the bytes the drag carries at the drop; <see cref="Wrap"/>
/// is the job: get the file (from the drag, or a download), keep it in Downloads, then run the petal on it there.
/// </summary>
public sealed class WebDrop
{
    public IReadOnlyList<WebItem> Items { get; }
    /// <summary>A link to a page rather than a file: Download · QR · Shortcut.</summary>
    public bool Link { get; }
    /// <summary>Working folder for the bytes before they move to Downloads (created at the drop, removed after the job).</summary>
    public string Dir { get; }
    /// <summary>What each item will be called ("photo.jpg"), for the hub and the progress card.</summary>
    public IReadOnlyList<string> Names { get; }
    /// <summary>"example.com", or "web page" when the drag says nothing about where it's from.</summary>
    public string Host { get; }

    // the petals are picked for these names (never created); an image of unknown type gets one no ring converts to
    readonly List<string> _catalog;
    const string AnyImage = ".tga";

    /// <summary>Paths to hand <see cref="Jobs.Start"/> (the card shows their names; the job itself ignores them).</summary>
    public IReadOnlyList<string> Files => Names.Select(n => Path.Combine(Dir, n)).ToList();

    public string HubTitle => Items.Count > 1 ? $"{Items.Count} items" : Items[0].Size > 0 ? Paths.HumanSize(Items[0].Size) : Host;
    public string HubSub => Items.Count > 1 ? (Items.Sum(i => i.Size) is > 0 and var s ? Paths.HumanSize(s) : Host) : Names[0];

    WebDrop(List<WebItem> items)
    {
        Items = items;
        Dir = Path.Combine(Paths.Temp, "web", Guid.NewGuid().ToString("N")[..12]);
        Host = Fetch.HostOf(items.Select(i => i.Url).FirstOrDefault(u => u != null && !Fetch.IsData(u)) ?? items[0].Url);
        var names = new List<string>();
        _catalog = [];
        foreach (var it in items)
        {
            var (name, catalog) = NameFor(it);
            names.Add(name);
            _catalog.Add(Path.Combine(Dir, catalog));
        }
        Names = names;
        var only = items[0];
        Link = items.Count == 1 && only.Source == WebSource.Url && !only.Image && only.Mime == null && only.Url is { } u && !Fetch.IsData(u)
               && Fetch.PageExts.Contains(Kinds.Ext(names[0])) && Kinds.Of(names[0]) == Kind.Other;
        if (Link) Names = [LinkName];
        SweepOld();
    }

    /// <summary>The name an item will have, and the one its petals are picked for.</summary>
    static (string Name, string Catalog) NameFor(WebItem it)
    {
        string? given = it.Name is { Length: > 0 } n ? Fetch.SafeName(n, "") : null;
        if (given?.Length == 0) given = null;
        var ext = given != null ? Path.GetExtension(given) : "";
        if (ext.Length > 1 && !Fetch.PageExts.Contains(ext.TrimStart('.'))) return (given!, given!);

        string stem = given != null ? Path.GetFileNameWithoutExtension(given) : "";
        string? urlName = it.Url != null ? Fetch.UrlName(it.Url) : null;
        if (stem.Length == 0 && urlName != null) stem = Path.GetFileNameWithoutExtension(Fetch.SafeName(urlName, ""));
        if (stem.Length == 0) stem = it.Image ? "image" : "download";
        string mime = it.Mime ?? (it.Url is { } du && Fetch.IsData(du) ? DataMime(du.OriginalString) : "");
        var e = Fetch.MimeExt(mime);
        if (e.Length == 0 && urlName != null && Path.GetExtension(urlName) is { Length: > 1 } ue && !Fetch.PageExts.Contains(ue.TrimStart('.'))) e = ue.ToLowerInvariant();
        if (e.Length == 0 && given != null) e = ext;
        var name = Fetch.SafeName(stem + e, "download");
        // a picture whose type we can't tell yet: every format petal stays on (the real type is sniffed after the download)
        bool unknownImage = it.Image && Kinds.Of(name) != Kind.Image;
        return (unknownImage ? Path.GetFileNameWithoutExtension(name) : name, unknownImage ? Path.GetFileNameWithoutExtension(name) + AnyImage : name);
    }

    static string DataMime(string data)
    {
        int end = data.IndexOfAny([';', ','], 5);
        return end > 5 ? data[5..end].Trim().ToLowerInvariant() : "";
    }

    string LinkName
    {
        get
        {
            var t = Items[0].Title?.Trim();
            if (string.IsNullOrEmpty(t)) t = Host;
            var s = Fetch.SafeName(t.Length > 80 ? t[..80] : t, "link");
            return Path.GetExtension(s) is { Length: > 1 } x && Fetch.PageExts.Contains(x.TrimStart('.')) ? s[..^x.Length] : s;
        }
    }

    // ---------- reading the drag ----------

    /// <summary>
    /// What a drag carries when it isn't plain files, or null when it's nothing this understands. <paramref name="localFiles"/>:
    /// the real paths it also offers (CF_HDROP, file:// URIs). Those make it an ordinary file drag (null) unless they are a
    /// browser's temp copies (Firefox writes one for an image), which are then kept like a download.
    /// </summary>
    public static WebDrop? Read(IDragData d, IReadOnlyList<string>? localFiles = null)
    {
        try
        {
            var items = Parse(d, localFiles);
            return items is { Count: > 0 } ? new WebDrop(items) : null;
        }
        catch (Exception ex) { Log.Error("browser drag", ex); return null; }
    }

    /// <summary>The flower for a link from elsewhere (a copied URL): the link petals, or a file's petals when it names one.</summary>
    public static WebDrop? ForUrl(string url, string? title = null) =>
        Fetch.Parse(url) is { } u ? new WebDrop([new WebItem { Url = u, Title = title, Image = Fetch.IsData(u) && DataMime(u.OriginalString).StartsWith("image/") }]) : null;

    const int Small = 4 << 20, Inline = 512 << 20;

    static readonly string[] RawImages =
    [
        "PNG", "image/png", "public.png", "image/jpeg", "JFIF", "public.jpeg", "image/gif", "GIF", "com.compuserve.gif",
        "image/webp", "org.webmproject.webp", "public.tiff", "image/tiff", "NeXT TIFF v4.0 pasteboard type", "image/bmp", "CF_DIBV5", "DeviceIndependentBitmap",
    ];

    static readonly string[] PlainText = ["UnicodeText", "text/plain;charset=utf-8", "UTF8_STRING", "text/plain", "public.utf8-plain-text", "Text", "STRING"];

    static List<WebItem>? Parse(IDragData d, IReadOnlyList<string>? localFiles)
    {
        var formats = new HashSet<string>(d.Formats, StringComparer.OrdinalIgnoreCase);
        bool Has(string f) => formats.Contains(f);
        string? Text(string f, int max = Small) => Has(f) && d.Bytes(f, max) is { Length: > 0 } b ? DecodeText(b) : null;

        // the link (and its text) from the richest format there is
        Uri? url = null;
        string? title = null;
        void Link(string? s)
        {
            if (url != null || string.IsNullOrWhiteSpace(s)) return;
            var lines = s.Split('\n', StringSplitOptions.TrimEntries);
            url = Fetch.Parse(lines[0]);
            if (url != null && lines.Length > 1 && lines[1].Length > 0) title ??= lines[1];
        }
        Link(Text("text/x-moz-url"));
        Link(Text("_NETSCAPE_URL"));
        Link(Text("UniformResourceLocatorW"));
        Link(Text("UniformResourceLocator"));
        Link(Text("public.url"));
        if (url != null) title ??= Text("public.url-name")?.Trim();
        Link(UriList(Text("text/uri-list")));

        var custom = ChromeCustom(d, formats);
        Link(UriList(custom.GetValueOrDefault("text/uri-list")));

        // <img src> in the HTML; Windows' CF_HTML also says which page it came from
        string? html = null;
        Uri? page = null;
        if (Text("HTML Format", 8 << 20) is { } cf) (html, page) = CfHtml(cf);
        html ??= Text("text/html", 8 << 20) ?? Text("public.html", 8 << 20);
        var img = html != null ? ImgSrc(html, page ?? url) : null;
        if (url == null && html != null && Href(html, page) is { } href) url = href;
        // Chrome names a dragged link only in its HTML: <a href="…">Title</a>
        if (title == null && html != null && img == null) title = LinkText(html);
        foreach (var t in PlainText) if (url == null && Text(t, 64 << 10) is { } s && !s.Trim().Contains('\n')) Link(s);

        var items = new List<WebItem>();
        // the browser's own temp copy (Firefox images): kept like a download, never treated as the user's file
        if (localFiles is { Count: > 0 })
        {
            bool browser = url != null || img != null || Has("application/x-moz-file-promise-url") || Has("application/x-moz-file-promise");
            var temp = Path.GetFullPath(Path.GetTempPath());
            if (!browser || !localFiles.All(f => File.Exists(f) && Path.GetFullPath(f).StartsWith(temp, StringComparison.OrdinalIgnoreCase))) return null;
            foreach (var f in localFiles)
                items.Add(new WebItem { Source = WebSource.TempFile, Key = f, Name = Path.GetFileName(f), Title = title, Size = Paths.SizeOf(f) });
            Fallback(items, img, url);
            return items;
        }

        // virtual files (Chrome / Edge / Firefox images, Outlook attachments); a lone "Title.url" is just the link again
        var desc = Has("FileGroupDescriptorW") && d.Bytes("FileGroupDescriptorW", Small) is { } w ? Descriptors(w, wide: true)
                 : Has("FileGroupDescriptor") && d.Bytes("FileGroupDescriptor", Small) is { } a ? Descriptors(a, wide: false) : [];
        if (desc.Count == 1 && Kinds.Ext(desc[0].Name) == "url" && url != null)
        {
            title ??= Path.GetFileNameWithoutExtension(desc[0].Name);
            desc.Clear();
        }
        if (desc.Count > 0 && Has("FileContents"))
        {
            foreach (var (i, name, size) in desc)
                items.Add(new WebItem { Source = WebSource.Virtual, Index = i, Name = name, Size = size, Title = title });
            Fallback(items, img, url);
            return items;
        }

        var raw = RawImages.FirstOrDefault(Has);

        // file promises: macOS (Safari, Chrome — the type announced as a UTI) and X11's direct save (XDS: Firefox,
        // Chromium). The browser writes the original file itself at the drop, with its own cookies
        if (Has("com.apple.pasteboard.promised-file-content-type") || Has("com.apple.NSFilePromiseItemMetaData") || Has("Apple files promise pasteboard type")
            || Has("XdndDirectSave0"))
        {
            var uti = Text("com.apple.pasteboard.promised-file-content-type")?.Trim();
            var mime = UtiMime(uti) ?? PromisedExtMime(Text("Apple files promise pasteboard type"));
            var name = Text("com.apple.pasteboard.promised-suggested-file-name")?.Trim();
            items.Add(new WebItem { Source = WebSource.Promise, Name = name, Mime = mime, Title = title, Image = mime?.StartsWith("image/") == true, Raw = raw });
            Fallback(items, img, url);
            return items;
        }

        // Chrome's DownloadURL ("mime:name:url"), set by pages such as Gmail for their attachments
        if ((Text("DownloadURL") ?? custom.GetValueOrDefault("DownloadURL")) is { } dl && DownloadUrl(dl) is { } du)
            return [du];

        // the picture itself (X11 image/png, macOS public.png / public.tiff, Windows PNG / DIB) is the browser's
        // re-encoding: the original from the picture's own address first, these bytes when that fails
        if (raw != null)
        {
            var pic = img ?? (url != null && Fetch.UrlName(url) is { } un && Kinds.Of(un) == Kind.Image ? url : null);
            if (pic != null) return [new WebItem { Url = pic, Image = true, Title = title, Raw = raw }];
            var mime = RawMime(raw);
            string? stem = url != null && Fetch.UrlName(url) is { } un2 ? Path.GetFileNameWithoutExtension(un2) : null;
            items.Add(new WebItem { Source = WebSource.Format, Key = raw, Name = (stem ?? "image") + Fetch.MimeExt(mime), Mime = mime, Image = true, Title = title });
            return items;
        }

        if (img != null) return [new WebItem { Url = img, Image = true, Title = title }];
        if (url != null) return [new WebItem { Url = url, Title = title, Image = Fetch.IsData(url) && DataMime(url.OriginalString).StartsWith("image/") }];
        return null;
    }

    /// <summary>A single in-hand item can still be downloaded if the drag fails to hand it over: from the &lt;img&gt;, or a link to the same kind of file.</summary>
    static void Fallback(List<WebItem> items, Uri? img, Uri? url)
    {
        if (items.Count != 1) return;
        var it = items[0];
        var kind = it.Name is { Length: > 0 } n ? Kinds.Of(n) : it.Mime?.StartsWith("image/") == true ? Kind.Image : Kind.Other;
        var linked = url != null && Fetch.UrlName(url) is { } un ? Kinds.Of(un) : Kind.Other;
        it.Image |= kind == Kind.Image || it.Raw != null || kind == Kind.Other && (img != null || linked == Kind.Image);
        if (img != null && (kind is Kind.Image or Kind.Other)) it.Url = img;
        else if (linked != Kind.Other && (linked == kind || kind == Kind.Other)) it.Url = url;
    }

    /// <summary>UTF-16 (with or without a BOM, as Windows and Firefox write it) or UTF-8, cut at the first NUL.</summary>
    public static string DecodeText(byte[] b)
    {
        string s;
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) s = Encoding.Unicode.GetString(b, 2, b.Length - 2);
        else if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) s = Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        else if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) s = Encoding.UTF8.GetString(b, 3, b.Length - 3);
        else
        {
            int zeros = 0, n = Math.Min(b.Length, 512) & ~1;
            for (int i = 1; i < n; i += 2) if (b[i] == 0) zeros++;
            s = n >= 2 && zeros * 2 >= n / 2 ? Encoding.Unicode.GetString(b, 0, b.Length & ~1) : Encoding.UTF8.GetString(b);
        }
        int nul = s.IndexOf('\0');
        return (nul >= 0 ? s[..nul] : s).Replace("\r\n", "\n");
    }

    /// <summary>The first link of a text/uri-list (# lines are comments).</summary>
    static string? UriList(string? s) => s?.Split('\n', StringSplitOptions.TrimEntries).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));

    /// <summary>"image/png:photo.png:https://…" → a download item.</summary>
    public static WebItem? DownloadUrl(string s)
    {
        s = s.Trim();
        int a = s.IndexOf(':'), b = a < 0 ? -1 : s.IndexOf(':', a + 1);
        if (a <= 0 || b < 0) return null;
        var url = Fetch.Parse(s[(b + 1)..]);
        if (url == null) return null;
        var mime = s[..a].Trim().ToLowerInvariant();
        return new WebItem { Url = url, Name = s[(a + 1)..b], Mime = mime.Length > 0 ? mime : null, Image = mime.StartsWith("image/") };
    }

    /// <summary>CF_HTML: "Version:0.9 … SourceURL:https://… &lt;!--StartFragment--&gt;…" → (fragment, page).</summary>
    public static (string Html, Uri? Page) CfHtml(string cf)
    {
        Uri? page = null;
        var m = Regex.Match(cf, @"^SourceURL:(\S+)", RegexOptions.Multiline);
        if (m.Success) page = Fetch.Parse(m.Groups[1].Value);
        int s = cf.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase), e = cf.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
        var html = s >= 0 && e > s ? cf[(s + 20)..e] : cf;
        return (html, page);
    }

    static readonly Regex ImgTag = new(@"<img\b[^>]*?\bsrc\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    static readonly Regex ATag = new(@"<a\b[^>]*?\bhref\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    static readonly Regex ALink = new(@"<a\b[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    static readonly Regex Tags = new(@"<[^>]*>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static Uri? ImgSrc(string html, Uri? baseUrl) => Attr(ImgTag, html, baseUrl);

    /// <summary>The text of the first link, tags and entities removed, white space collapsed.</summary>
    public static string? LinkText(string html)
    {
        try
        {
            var m = ALink.Match(html.Length > 1_000_000 ? html[..1_000_000] : html);
            if (!m.Success) return null;
            var t = Spaces.Replace(WebUtility.HtmlDecode(Tags.Replace(m.Groups[1].Value, " ")), " ").Trim();
            return t.Length > 0 ? t : null;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }
    static Uri? Href(string html, Uri? baseUrl) => Attr(ATag, html, baseUrl);

    static Uri? Attr(Regex re, string html, Uri? baseUrl)
    {
        try
        {
            var m = re.Match(html.Length > 1_000_000 ? html[..1_000_000] : html);
            if (!m.Success) return null;
            var raw = WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).Trim();
            if (Fetch.Parse(raw) is { } abs) return abs;
            return baseUrl != null && !Fetch.IsData(baseUrl) && Uri.TryCreate(baseUrl, raw, out var rel) ? Fetch.Parse(rel.AbsoluteUri) : null;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    /// <summary>FILEGROUPDESCRIPTOR(W): the virtual files' names and sizes (folders skipped).</summary>
    public static List<(int Index, string Name, long Size)> Descriptors(byte[] b, bool wide)
    {
        var list = new List<(int, string, long)>();
        if (b.Length < 4) return list;
        int count = BitConverter.ToInt32(b, 0), size = wide ? 592 : 332;
        for (int i = 0; i < count && i < 256 && 4 + (i + 1) * size <= b.Length; i++)
        {
            int o = 4 + i * size;
            uint flags = BitConverter.ToUInt32(b, o), attrs = BitConverter.ToUInt32(b, o + 36);
            if ((flags & 0x04) != 0 && (attrs & 0x10) != 0) continue; // FD_ATTRIBUTES + FILE_ATTRIBUTE_DIRECTORY
            long len = (flags & 0x40) != 0 ? ((long)BitConverter.ToUInt32(b, o + 64) << 32) | BitConverter.ToUInt32(b, o + 68) : 0;
            var name = wide ? Encoding.Unicode.GetString(b, o + 72, 520) : Encoding.Latin1.GetString(b, o + 72, 260);
            int nul = name.IndexOf('\0');
            if (nul >= 0) name = name[..nul];
            if (name.Length > 0) list.Add((i, name, len));
        }
        return list;
    }

    /// <summary>Chrome's web custom data (a pickled list of type → value pairs): where it carries DownloadURL and text/uri-list set by the page.</summary>
    static Dictionary<string, string> ChromeCustom(IDragData d, HashSet<string> formats)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var f = new[] { "Chromium Web Custom MIME Data Format", "chromium/x-web-custom-data", "org.chromium.web-custom-data" }.FirstOrDefault(formats.Contains);
        if (f == null || d.Bytes(f, Small) is not { Length: >= 8 } b) return map;
        try
        {
            int pos = 4, count = BitConverter.ToInt32(b, pos);
            pos += 4;
            string? Str()
            {
                if (pos + 4 > b.Length) return null;
                int len = BitConverter.ToInt32(b, pos);
                pos += 4;
                if (len < 0 || pos + len * 2 > b.Length) return null;
                var s = Encoding.Unicode.GetString(b, pos, len * 2);
                pos += (len * 2 + 3) & ~3;
                return s;
            }
            for (int i = 0; i < count && i < 64; i++)
                if (Str() is { } k && Str() is { } v) map[k] = v; else break;
        }
        catch { }
        return map;
    }

    static readonly Dictionary<string, string> Utis = new(StringComparer.OrdinalIgnoreCase)
    {
        ["public.png"] = "image/png", ["public.jpeg"] = "image/jpeg", ["com.compuserve.gif"] = "image/gif", ["org.webmproject.webp"] = "image/webp",
        ["public.heic"] = "image/heic", ["public.heif"] = "image/heif", ["public.avif"] = "image/avif", ["com.microsoft.bmp"] = "image/bmp",
        ["public.tiff"] = "image/tiff", ["public.svg-image"] = "image/svg+xml", ["com.microsoft.ico"] = "image/x-icon", ["public.jxl"] = "image/jxl",
        ["com.adobe.pdf"] = "application/pdf", ["public.zip-archive"] = "application/zip", ["public.mpeg-4"] = "video/mp4",
        ["com.apple.quicktime-movie"] = "video/quicktime", ["org.webmproject.webm"] = "video/webm", ["public.mp3"] = "audio/mpeg",
        ["public.mpeg-4-audio"] = "audio/mp4", ["public.html"] = "text/html", ["public.plain-text"] = "text/plain", ["public.utf8-plain-text"] = "text/plain",
        ["public.json"] = "application/json", ["public.comma-separated-values-text"] = "text/csv",
    };

    static string? UtiMime(string? uti) => uti != null && Utis.TryGetValue(uti, out var m) ? m : null;

    /// <summary>The old NSFilesPromisePboardType: a property list of extensions ("jpg").</summary>
    static string? PromisedExtMime(string? plist)
    {
        var m = plist == null ? null : Regex.Match(plist, @"<string>\s*\.?([A-Za-z0-9]{1,8})\s*</string>");
        if (m is not { Success: true }) return null;
        var ext = "." + m.Groups[1].Value.ToLowerInvariant();
        return Utis.Values.Concat(["image/png", "image/jpeg", "image/gif", "image/webp", "application/pdf"]).FirstOrDefault(x => Fetch.MimeExt(x) == ext);
    }

    static string RawMime(string format) => format switch
    {
        "PNG" or "image/png" or "public.png" => "image/png",
        "image/jpeg" or "JFIF" or "public.jpeg" => "image/jpeg",
        "image/gif" or "GIF" or "com.compuserve.gif" => "image/gif",
        "image/webp" or "org.webmproject.webp" => "image/webp",
        "public.tiff" or "image/tiff" or "NeXT TIFF v4.0 pasteboard type" => "image/tiff",
        _ => "image/bmp",
    };

    /// <summary>A packed DIB (CF_DIB / CF_DIBV5) with the 14-byte BMP file header in front, so any decoder reads it.</summary>
    public static byte[] DibToBmp(byte[] dib)
    {
        if (dib.Length < 40) throw new InvalidDataException("Not a bitmap");
        int hdr = BitConverter.ToInt32(dib, 0), bits = BitConverter.ToUInt16(dib, 14), compression = BitConverter.ToInt32(dib, 16), used = BitConverter.ToInt32(dib, 32);
        int palette = (used > 0 ? used : bits <= 8 ? 1 << bits : 0) * 4;
        int masks = hdr == 40 && compression == 3 ? 12 : 0; // BI_BITFIELDS after a plain BITMAPINFOHEADER
        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(14 + hdr + masks + palette).CopyTo(bmp, 10);
        dib.CopyTo(bmp, 14);
        return bmp;
    }

    // ---------- the flower ----------

    PetalAction? _save;
    PetalAction SaveA => _save ??= Catalog.Tool("download", Link ? "Download" : "Save", "download",
        Link ? "Download the page" : Items.Count == 1 ? "Save to Downloads" : $"Save {Items.Count} to Downloads", _ => Task.CompletedTask);

    static string ShortcutExt => OperatingSystem.IsWindows() ? "url" : OperatingSystem.IsMacOS() ? "webloc" : "desktop";

    /// <summary>The rings: the petals for the file it will be (with Save first), or Download · QR · Shortcut for a link.</summary>
    public Pages Pages()
    {
        if (Link)
            return new([SaveA, Catalog.Tool("qrlink", "QR", "qr", "QR code of the link", QrLink), Catalog.Tool("shortcut", "Shortcut", "link", $"Save as .{ShortcutExt}", Shortcut)],
                [], Items[0].Title?.Trim() is { Length: > 0 } t ? t : Host, "URL");
        var p = Catalog.For(_catalog);
        List<PetalAction> conv = [SaveA, .. p.Convert];
        string badge = Items.Count > 1 ? Items.Count.ToString() : Kinds.Ext(Names[0]) is { Length: > 0 } e ? (e.Length > 4 ? e[..4] : e).ToUpperInvariant() : Items[0].Image ? "IMG" : "WEB";
        return p with { Convert = conv, Title = Items.Count == 1 ? Names[0] : $"{Items.Count} items", Badge = badge };
    }

    /// <summary>At the drop, while the drag data is still alive: copy out what it carries (virtual files, raw pictures, temp copies, promises).</summary>
    public void Grab(IDragData d)
    {
        for (int i = 0; i < Items.Count; i++)
        {
            var it = Items[i];
            if (it.Raw != null && it.Source != WebSource.Format)
                try
                {
                    var rawDir = Path.Combine(Dir, i + "-raw");
                    Directory.CreateDirectory(rawDir);
                    if (d.Bytes(it.Raw, Inline) is { Length: > 0 } rb)
                    {
                        if (it.Raw is "CF_DIBV5" or "DeviceIndependentBitmap") rb = DibToBmp(rb);
                        var rp = Path.Combine(rawDir, Path.GetFileNameWithoutExtension(Names[i]) + Fetch.MimeExt(RawMime(it.Raw)));
                        File.WriteAllBytes(rp, rb);
                        it.RawGrabbed = rp;
                    }
                }
                catch (Exception ex) { Log.Error($"browser drop: picture data for {Names[i]}", ex); }
            if (it.Source == WebSource.Url) continue;
            try
            {
                var dir = Path.Combine(Dir, i.ToString());
                Directory.CreateDirectory(dir);
                switch (it.Source)
                {
                    case WebSource.Format when d.Bytes(it.Key!, Inline) is { Length: > 0 } bytes:
                    {
                        if (it.Key is "CF_DIBV5" or "DeviceIndependentBitmap") bytes = DibToBmp(bytes);
                        var p = Path.Combine(dir, Names[i].Contains('.') ? Names[i] : Names[i] + Fetch.MimeExt(it.Mime));
                        File.WriteAllBytes(p, bytes);
                        it.Grabbed = Task.FromResult<string?>(p);
                        break;
                    }
                    case WebSource.TempFile when it.Key != null && new FileInfo(it.Key) is { Exists: true } fi && fi.Length <= Fetch.Cap:
                    {
                        var p = Path.Combine(dir, Fetch.SafeName(fi.Name));
                        File.Copy(fi.FullName, p);
                        it.Grabbed = Task.FromResult<string?>(p);
                        break;
                    }
                    case WebSource.Virtual or WebSource.Promise:
                        it.Grabbed = d.Save(it, dir, Names[i].Contains('.') ? Fetch.SafeName(Names[i]) : Fetch.SafeName(Names[i]) + Fetch.MimeExt(it.Mime));
                        break;
                }
            }
            catch (Exception ex) { Log.Error($"browser drop: grab {Names[i]}", ex); }
        }
    }

    /// <summary>The job for a petal of this flower: the file first (from the drag, or downloaded), kept in Downloads, then the petal on it there.</summary>
    public PetalAction Wrap(PetalAction picked) => new()
    {
        Id = picked.Id, Label = picked.Label, Hint = picked.Hint, Icon = picked.Icon, Verb = picked.Verb, Short = picked.Short,
        Interactive = picked.Interactive,
        Run = job => Run(job, picked),
    };

    async Task Run(Job job, PetalAction picked)
    {
        if (Link && picked.Id is "qrlink" or "shortcut") { await picked.Run(job); return; }
        bool own = picked.Id == "download";
        var kept = new List<string>();
        try
        {
            var got = await Task.Run(() => Collect(job, own ? 1 : 0.4), job.Token);
            var downloads = Fetch.DownloadsDir();
            foreach (var (path, src) in got) kept.Add(Fetch.Keep(path, downloads, src));
            long size = kept.Sum(Paths.SizeOf);
            job.Saved = 0;
            if (own)
            {
                job.Outputs.AddRange(kept);
                job.Title = kept.Count == 1 ? Path.GetFileName(kept[0]) : $"{kept.Count} files saved";
                job.Summary = $"{Paths.HumanSize(size)} · from {Host}";
                return;
            }

            // the petal again, for what really arrived: the link may have said JPEG and sent a PNG (or a page)
            var action = Catalog.All(kept).FirstOrDefault(a => a.Id == picked.Id);
            if (action == null)
            {
                job.Outputs.AddRange(kept);
                job.Title = kept.Count == 1 ? $"Saved {Path.GetFileName(kept[0])}" : $"{kept.Count} files saved";
                job.Summary = $"{picked.Label} doesn't apply to {(kept.Count == 1 ? "it" : "them")} · kept in Downloads";
                return;
            }
            var sub = new Job { Inputs = kept, Action = action, Where = job.Where, Token = job.Token, OnProgress = p => job.Report(0.4 + 0.6 * p) };
            if (action.Interactive) await action.Run(sub);
            else await Task.Run(() => action.Run(sub), job.Token);
            if (sub.Summary == null && sub.Outputs.Count > 0) sub.Summary = Images.SizeSummary(sub);
            var origin = Items.Select(i => i.Url).FirstOrDefault(u => u != null);
            foreach (var o in sub.Outputs) Fetch.Mark(o, origin);
            job.Outputs.AddRange(sub.Outputs);
            job.Title = sub.Title;
            job.Summary = sub.Summary;
            job.Silent = sub.Silent;
            job.Failed = sub.Failed;
            long outSize = sub.Outputs.Sum(Paths.SizeOf);
            job.Saved = sub.Saved ?? (sub.Outputs.Count > 0 && outSize > 0 && size > outSize ? size - outSize : 0);
        }
        catch (OperationCanceledException)
        {
            foreach (var k in kept) FileTools.TryDeleteFile(k);
            throw;
        }
        finally { FileTools.TryDeleteDir(Dir); }
    }

    /// <summary>Each item into its own folder under <see cref="Dir"/>: what the drag handed over, else a download. <paramref name="share"/> of the progress bar.</summary>
    async Task<List<(string Path, Uri? Source)>> Collect(Job job, double share)
    {
        var list = new List<(string, Uri?)>();
        int n = Items.Count;
        for (int i = 0; i < n; i++)
        {
            job.Token.ThrowIfCancellationRequested();
            var it = Items[i];
            var dir = Path.Combine(Dir, i.ToString());
            string? path = null;
            if (it.Grabbed != null)
            {
                try { path = await it.Grabbed.WaitAsync(Fetch.IdleTimeout * 4, job.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException || !job.Token.IsCancellationRequested) { Log.Error($"browser drop: {Names[i]} from the drag", ex); }
            }
            if (path != null && !File.Exists(path)) path = null;
            if (path == null && it.Url != null)
            {
                // a name the page or browser chose wins; otherwise Content-Disposition or the URL; a link is saved under its title
                string? suggested = it.Source != WebSource.Url || it.Mime != null ? Fetch.SafeName(Names[i], "download") : null;
                int k = i;
                try
                {
                    var r = await Fetch.Download(it.Url, dir, suggested, (done, total) => job.Report(share * (k + (total is long t && t > 0 ? Math.Min(1, (double)done / t) : 0)) / n),
                        job.Token, expectFile: !Link, fallback: Link ? LinkName : null);
                    list.Add((r.Path, r.Source));
                    continue;
                }
                // a sign-in, a dead link…: the browser's own copy of the picture still beats nothing
                catch (Exception ex) when (it.RawGrabbed != null && ex is not OperationCanceledException)
                {
                    Log.Info($"browser drop: {ex.Message}; using the picture data from the drag");
                }
            }
            path ??= it.RawGrabbed;
            if (path != null && File.Exists(path))
            {
                if (new FileInfo(path).Length > Fetch.Cap) throw new Fetch.Refused($"Too big ({Paths.HumanSize(Fetch.Cap)} at most)");
                // the name the drag gave may not match the bytes either
                var head = new byte[4096];
                int got;
                await using (var fs = File.OpenRead(path)) got = await fs.ReadAsync(head, job.Token);
                var fixedName = Fetch.FixExtension(Fetch.SafeName(Path.GetFileName(path)), Fetch.Sniff(head.AsSpan(0, got)), it.Mime);
                if (fixedName != Path.GetFileName(path))
                {
                    var p2 = Path.Combine(Path.GetDirectoryName(path)!, fixedName);
                    if (!File.Exists(p2)) { File.Move(path, p2); path = p2; }
                }
                list.Add((path, it.Url));
                job.Report(share * (i + 1) / n);
                continue;
            }
            throw new Fetch.Refused(it.Source == WebSource.Url ? "Nothing to download" : "The browser didn't hand the file over — save it first");
        }
        return list;
    }

    async Task QrLink(Job job)
    {
        var url = Items[0].Url!.AbsoluteUri;
        var png = Qr.Png(url);
        string dst;
        await using (var fs = FileTools.CreateUnique(Fetch.DownloadsDir(), LinkName + " (QR)", ".png", out dst)) await fs.WriteAsync(png, job.Token);
        Fetch.Mark(dst, Items[0].Url);
        job.Outputs.Add(dst);
        job.Summary = url.Length > 80 ? url[..80] + "…" : url;
        job.Saved = 0;
    }

    /// <summary>A link file the desktop opens in the browser: .url on Windows, .webloc on macOS, a Type=Link .desktop entry on Linux.</summary>
    async Task Shortcut(Job job)
    {
        var url = Items[0].Url!.AbsoluteUri; // escaped: no line breaks can sneak in
        var name = LinkName;
        string body = ShortcutExt switch
        {
            "url" => $"[InternetShortcut]\r\nURL={url}\r\n",
            "webloc" => "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                        $"<plist version=\"1.0\">\n<dict>\n\t<key>URL</key>\n\t<string>{WebUtility.HtmlEncode(url)}</string>\n</dict>\n</plist>\n",
            _ => $"[Desktop Entry]\nVersion=1.0\nType=Link\nName={name.Replace('\n', ' ').Replace('\r', ' ')}\nURL={url}\nIcon=text-html\n",
        };
        string dst;
        await using (var fs = FileTools.CreateUnique(Fetch.DownloadsDir(), name, "." + ShortcutExt, out dst))
            await fs.WriteAsync(new UTF8Encoding(false).GetBytes(body), job.Token);
        Fetch.Mark(dst, Items[0].Url);
        job.Outputs.Add(dst);
        job.Summary = Host;
        job.Saved = 0;
    }

    /// <summary>Working folders a crash or a killed job left behind.</summary>
    static void SweepOld()
    {
        try
        {
            var root = Path.Combine(Paths.Temp, "web");
            if (!Directory.Exists(root)) return;
            foreach (var d in new DirectoryInfo(root).EnumerateDirectories())
                if (d.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) FileTools.TryDeleteDir(d.FullName);
        }
        catch { }
    }
}
