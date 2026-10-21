using System.Net;
using System.Net.Sockets;
using System.Text;
using ImageMagick;
using Convertino.Services;

namespace Convertino;

/// <summary>One drag the self-test plays: what a browser would put on offer, and what dropping it on a petal should give.</summary>
public sealed class DragCase
{
    public required string Name { get; init; }
    /// <summary>Format → payload, encoded the way the browser writes it (UTF-16 where it uses UTF-16).</summary>
    public List<(string Format, byte[] Data)> Formats { get; } = [];
    /// <summary>Windows virtual files (FileGroupDescriptorW + FileContents): served as an IStream (Firefox) or an HGLOBAL (Chrome).</summary>
    public List<(string Name, byte[] Data, bool Stream)> Virtual { get; } = [];
    /// <summary>Real paths the drag offers too (CF_HDROP, file:// URIs).</summary>
    public List<string> Local { get; } = [];
    public bool Bloom { get; init; } = true;
    public bool? Link { get; init; }
    /// <summary>The name the flower should show before the drop.</summary>
    public string? Shown { get; init; }
    /// <summary>The petal to drop on (none: only the bloom is checked).</summary>
    public string? Pick { get; init; }
    public Func<Outcome, string?>? Check { get; init; }
}

public sealed class Outcome
{
    public required WebDrop Web { get; init; }
    public Job? Job { get; init; }
    public string? Error { get; init; }
    public required string Downloads { get; init; }
    public required Func<string, int> Hits { get; init; }
}

/// <summary>
/// <c>--browser-drop-selftest &lt;dir&gt;</c>: drags shaped the way Chrome, Edge, Firefox and Safari shape them, played through
/// the real parse → petals → grab → download → petal path against a local web server that redirects, lies about types,
/// sends hostile file names, wants a sign-in, stalls, breaks off and sends too much. No outside network, nothing on screen;
/// results land in &lt;dir&gt;/Downloads instead of the user's. Each platform builds its own native drag data for the cases.
/// </summary>
public static class WebSelfTest
{
    /// <param name="family">"windows" (clipboard format names), "x11" (MIME types) or "mac" (UTIs).</param>
    /// <param name="native">The platform's own drag data for a case, read through its own <see cref="IDragData"/>, and the real paths it offers.</param>
    public static async Task<int> Run(string outDir, string family, Func<DragCase, (IDragData Data, IReadOnlyList<string>? Local)> native)
    {
        Directory.CreateDirectory(outDir);
        var downloads = Path.Combine(outDir, "Downloads");
        Fetch.DownloadsOverride = downloads;
        Fetch.Cap = 2 << 20;
        Fetch.IdleTimeout = TimeSpan.FromSeconds(1.5);
        var lines = new List<string>();
        int fails = 0, n = 0;
        void Report(string name, string? failure)
        {
            n++;
            if (failure != null) fails++;
            var line = (failure == null ? "ok    " : "FAIL  ") + name + (failure == null ? "" : "  — " + failure);
            lines.Add(line);
            Console.WriteLine(line);
        }

        foreach (var (name, failure) in Units()) Report("unit: " + name, failure);

        using var server = new TestServer();
        bool marks = MarksWork(outDir);
        if (!marks) lines.Add("note  this file system keeps no extended attributes: Mark-of-the-Web isn't checked");
        foreach (var c in Cases(family, server, outDir))
        {
            string? failure;
            try
            {
                if (Directory.Exists(downloads)) Directory.Delete(downloads, true);
                var (data, local) = native(c);
                failure = await Play(c, data, local, downloads, server, marks);
            }
            catch (Exception ex) { failure = "threw " + ex.GetType().Name + ": " + ex.Message; }
            Report($"{family}: {c.Name}", failure);
        }
        lines.Add($"{n - fails}/{n} passed");
        File.WriteAllLines(Path.Combine(outDir, "results.txt"), lines);
        Console.WriteLine($"{n - fails}/{n} passed");
        return fails;
    }

    static async Task<string?> Play(DragCase c, IDragData data, IReadOnlyList<string>? local, string downloads, TestServer server, bool marks)
    {
        var web = WebDrop.Read(data, local is { Count: > 0 } ? local : null);
        if (!c.Bloom) return web == null ? null : $"bloomed ({web.Names[0]}) but shouldn't";
        if (web == null) return "didn't bloom";
        if (c.Link is { } l && web.Link != l) return $"link flower = {web.Link}, expected {l}";
        if (c.Shown != null && web.Names[0] != c.Shown) return $"shows '{web.Names[0]}', expected '{c.Shown}'";
        var petals = web.Pages().Rings.SelectMany(r => r.Petals).ToList();
        if (petals.FirstOrDefault()?.Id != "download") return "Save / Download isn't the first petal";
        if (c.Pick == null) return null;
        var petal = petals.FirstOrDefault(p => p.Id == c.Pick);
        if (petal == null) return $"no '{c.Pick}' petal; it has {string.Join(" ", petals.Select(p => p.Id))}";

        web.Grab(data);
        Job? job = null;
        string? error = null;
        try { job = await Jobs.RunHeadless(web.Wrap(petal), web.Files); }
        catch (Exception ex) { error = Jobs.Friendly(ex); }
        if (Directory.Exists(web.Dir)) return "its working folder was left behind";
        var o = new Outcome { Web = web, Job = job, Error = error, Downloads = downloads, Hits = server.Hits };
        // Linux marks only http(s) downloads (user.xdg.origin.url); Windows and macOS mark everything the flower saves
        if (error == null && marks && (!OperatingSystem.IsLinux() || web.Items.Any(i => i.Url is { } u && !Fetch.IsData(u))))
            foreach (var f in Directory.Exists(downloads) ? Directory.EnumerateFiles(downloads) : [])
                if (!Fetch.IsMarked(f)) return $"{Path.GetFileName(f)} isn't marked as coming from the internet";
        return c.Check?.Invoke(o);
    }

    static bool MarksWork(string dir)
    {
        var probe = Path.Combine(dir, "mark-probe.txt");
        File.WriteAllText(probe, "x");
        Fetch.Mark(probe, new Uri("https://example.com/x"));
        bool ok = Fetch.IsMarked(probe);
        File.Delete(probe);
        // Windows (NTFS streams) and macOS (xattrs) always must; Linux only where the file system keeps user xattrs
        return ok || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    }

    // ---------- checks ----------

    static Func<Outcome, string?> Made(params string[] names) => o =>
    {
        if (o.Error != null) return "failed: " + o.Error;
        var got = o.Job!.Outputs.Select(Path.GetFileName).ToArray();
        if (!got.SequenceEqual(names)) return $"made [{string.Join(", ", got)}], expected [{string.Join(", ", names)}]";
        foreach (var f in o.Job.Outputs)
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(f)), Path.GetFullPath(o.Downloads), StringComparison.OrdinalIgnoreCase)) return $"{f} isn't in Downloads";
        return null;
    };

    static Func<Outcome, string?> Fails(string text) => o =>
        o.Error?.Contains(text, StringComparison.OrdinalIgnoreCase) == true ? null
        : $"expected an error saying '{text}', got " + (o.Error ?? "success: " + string.Join(", ", o.Job!.Outputs.Select(Path.GetFileName)));

    static Func<Outcome, string?> And(this Func<Outcome, string?> a, Func<Outcome, string?> b) => o => a(o) ?? b(o);

    static Func<Outcome, string?> Kept(string original) => o => File.Exists(Path.Combine(o.Downloads, original)) ? null
        : $"the original {original} isn't in Downloads (has: {string.Join(", ", Directory.EnumerateFiles(o.Downloads).Select(Path.GetFileName))})";

    static Func<Outcome, string?> Offline(TestServer s, string path)
    {
        int before = s.Hits(path);
        return o => s.Hits(path) == before ? null : $"downloaded {path} although the drag carried it";
    }

    static Func<Outcome, string?> Holds(string name, Func<string, bool> ok, string what) => o =>
        File.Exists(Path.Combine(o.Downloads, name)) && ok(File.ReadAllText(Path.Combine(o.Downloads, name))) ? null : $"{name} doesn't {what}";

    // ---------- the drags ----------

    static byte[] U16(string s) => Encoding.Unicode.GetBytes(s + "\0");
    static byte[] U8(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>CF_HTML as Chrome writes it, with the page it came from.</summary>
    public static byte[] CfHtml(string fragment, string source)
    {
        const string head = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\nSourceURL:{4}\r\n";
        string pre = "<html><body>\r\n<!--StartFragment-->", post = "<!--EndFragment-->\r\n</body></html>";
        int h = Encoding.UTF8.GetByteCount(string.Format(head, 0, 0, 0, 0, source));
        int sf = h + Encoding.UTF8.GetByteCount(pre), ef = sf + Encoding.UTF8.GetByteCount(fragment), eh = ef + Encoding.UTF8.GetByteCount(post);
        return U8(string.Format(head, h, eh, sf, ef, source) + pre + fragment + post);
    }

    static (string, byte[])[] UrlFormats(string family, string url, string? title) => family switch
    {
        // Chrome / Edge: the link, its text only in the HTML
        "windows" => [("UniformResourceLocatorW", U16(url)), ("UnicodeText", U16(url)),
            .. title == null ? [] : new[] { ("HTML Format", CfHtml($"<a href=\"{url}\">{WebUtility.HtmlEncode(title)}</a>", url)) }],
        "x11" => [("text/x-moz-url", Encoding.Unicode.GetBytes(url + "\n" + (title ?? url))), ("_NETSCAPE_URL", U8(url + "\n" + (title ?? url))), ("text/plain;charset=utf-8", U8(url))],
        _ => [("public.url", U8(url)), ("public.url-name", U8(title ?? url)), ("public.utf8-plain-text", U8(url))],
    };

    static (string, byte[])[] HtmlFormats(string family, string fragment, string page) => family switch
    {
        "windows" => [("HTML Format", CfHtml(fragment, page))],
        "x11" => [("text/html", Encoding.Unicode.GetBytes("\ufeff" + fragment))],
        _ => [("public.html", U8(fragment))],
    };

    static DragCase LinkCase(string family, string name, string url, string? title = null, bool bloom = true, bool? link = null, string? shown = null, string? pick = null, Func<Outcome, string?>? check = null)
    {
        var c = new DragCase { Name = name, Bloom = bloom, Link = link, Shown = shown, Pick = pick, Check = check };
        c.Formats.AddRange(UrlFormats(family, url, title));
        return c;
    }

    static DragCase ImgCase(string family, string name, string src, string page, string? shown = null, string? pick = null, Func<Outcome, string?>? check = null)
    {
        var c = new DragCase { Name = name, Shown = shown, Pick = pick, Check = check, Link = false };
        // only Windows' CF_HTML says which page it came from; Firefox and Chrome write absolute links elsewhere
        if (family != "windows") src = new Uri(new Uri(page), src).AbsoluteUri;
        c.Formats.AddRange(HtmlFormats(family, $"<img alt=\"x\" src=\"{src}\">", page));
        return c;
    }

    static IEnumerable<DragCase> Cases(string family, TestServer s, string outDir)
    {
        string b = s.Base, sc = family == "windows" ? "url" : family == "mac" ? "webloc" : "desktop";

        // links to pages: Download · QR · Shortcut
        yield return LinkCase(family, "page link → shortcut", $"{b}/page.html", "Example page", link: true, shown: "Example page", pick: "shortcut",
            check: Made($"Example page.{sc}").And(Holds($"Example page.{sc}", t => t.Contains($"{b}/page.html"), "hold the link")));
        yield return LinkCase(family, "page link → QR", $"{b}/page.html", "Example page", pick: "qrlink",
            check: Made("Example page (QR).png").And(o => Qr.Decode(o.Job!.Outputs[0]).FirstOrDefault() == $"{b}/page.html" ? null : "the QR code doesn't read back as the link"));
        yield return LinkCase(family, "page link → Download", $"{b}/page.html", "Example page", pick: "download", check: Made("Example page.html"));
        yield return LinkCase(family, "nameless picture link → saved by its type", $"{b}/img/noext", "Picture", link: true, pick: "download", check: Made("Picture.png"));

        // files and pictures by URL
        yield return LinkCase(family, "image link → JPG (original kept)", $"{b}/img/photo.png", "Photo", link: false, shown: "photo.png", pick: "to-jpg",
            check: Made("photo.jpg").And(Kept("photo.png")));
        yield return ImgCase(family, "<img> whose server lies about the type → WEBP", "../img/lying.png", $"{b}/gallery/index.html", shown: "lying.png", pick: "to-webp",
            check: Made("lying.webp").And(Kept("lying.jpg")));
        yield return ImgCase(family, "<img> with a script URL → PNG", $"{b}/img/page.php?id=3", $"{b}/", shown: "page", pick: "to-png",
            check: Made("page.png").And(Kept("page.jpg")));
        yield return LinkCase(family, "data: image → Save", "data:image/png;base64," + Convert.ToBase64String(TestServer.Png), link: false, shown: "image.png", pick: "download", check: Made("image.png"));
        yield return LinkCase(family, "5 redirects → Save", $"{b}/r/1", pick: "download", check: Made("photo.png"));

        // refusals
        yield return LinkCase(family, "redirect loop", $"{b}/loop/1", pick: "download", check: Fails("Too many redirects"));
        yield return LinkCase(family, "redirect to file:", $"{b}/tofile", pick: "download", check: Fails("doesn't open"));
        yield return LinkCase(family, "redirect to ftp:", $"{b}/toftp", pick: "download", check: Fails("doesn't open"));
        yield return LinkCase(family, "redirect to data:", $"{b}/todata", pick: "download", check: Fails("doesn't open"));
        yield return LinkCase(family, "401 → sign-in message", $"{b}/401", pick: "download", check: Fails(Fetch.SignIn));
        yield return LinkCase(family, "403 → sign-in message", $"{b}/403", pick: "download", check: Fails(Fetch.SignIn));
        yield return ImgCase(family, "<img> behind a login page → sign-in message", $"{b}/private/photo.jpg", $"{b}/", pick: "to-png", check: Fails(Fetch.SignIn));
        yield return LinkCase(family, "too big (Content-Length)", $"{b}/big.bin", link: false, shown: "big.bin", pick: "download", check: Fails("Too big"));
        yield return LinkCase(family, "too big (no length)", $"{b}/chunked.bin", pick: "download", check: Fails("Too big"));
        yield return LinkCase(family, "server stalls", $"{b}/stall.bin", pick: "download", check: Fails("stopped sending"));
        yield return LinkCase(family, "server breaks off", $"{b}/broken.bin", pick: "download", check: Fails("broke off"));

        // names from the server
        yield return LinkCase(family, "../../ in Content-Disposition", $"{b}/evil", pick: "download",
            check: Made("evil.png").And(_ => File.Exists(Path.Combine(outDir, "evil.png")) || File.Exists(Path.Combine(Path.GetDirectoryName(outDir)!, "evil.png")) ? "escaped Downloads" : null));
        yield return LinkCase(family, "encoded ../ in filename*", $"{b}/evil2", pick: "download", check: Made("evil2.png"));
        yield return LinkCase(family, "device name CON", $"{b}/con", pick: "download", check: Made("_CON.png"));
        yield return LinkCase(family, "right-to-left override + .exe", $"{b}/bidi", pick: "download", check: Made("photognp.png"));
        yield return LinkCase(family, "no cookies or credentials sent", s.Base.Replace("://", "://user:secret@") + "/echo.txt", shown: "echo.txt", pick: "download",
            check: Made("echo.txt").And(Holds("echo.txt", t => t == "cookie=;auth=", "show that no cookie or credentials were sent")));

        // not for us
        yield return LinkCase(family, "ftp: link ignored", "ftp://127.0.0.1/a.png", bloom: false);
        yield return LinkCase(family, "file: link ignored", OperatingSystem.IsWindows() ? "file:///C:/Windows/win.ini" : "file:///etc/hosts", bloom: false);
        yield return LinkCase(family, "javascript: ignored", "javascript:alert(1)", bloom: false);
        {
            var t = new DragCase { Name = "plain text isn't a link", Bloom = false };
            t.Formats.Add(family switch { "windows" => ("UnicodeText", U16("hello world")), "x11" => ("text/plain;charset=utf-8", U8("hello world")), _ => ("public.utf8-plain-text", U8("hello world")) });
            yield return t;
        }

        foreach (var c in family switch { "windows" => Windows(s, outDir), "x11" => X11(s), _ => Mac(s) }) yield return c;
    }

    static IEnumerable<DragCase> Windows(TestServer s, string outDir)
    {
        string b = s.Base, img = $"{b}/img/photo.png";
        var html = CfHtml($"<img src=\"{img}\">", $"{b}/gallery/index.html");

        var chrome = new DragCase { Name = "Chrome image: virtual file (HGLOBAL) → JPG, no download", Shown = "photo.png", Pick = "to-jpg", Check = Made("photo.jpg").And(Kept("photo.png")).And(Offline(s, "/img/photo.png")) };
        chrome.Virtual.Add(("photo.png", TestServer.Png, false));
        chrome.Formats.AddRange([("UniformResourceLocatorW", U16(img)), ("HTML Format", html)]);
        yield return chrome;

        var ff = new DragCase { Name = "Firefox image: virtual file (IStream) → WEBP", Shown = "photo.png", Pick = "to-webp", Check = Made("photo.webp").And(Offline(s, "/img/photo.png")) };
        ff.Virtual.Add(("photo.png", TestServer.Png, true));
        ff.Formats.AddRange([("text/x-moz-url", U16(img + "\nPhoto")), ("application/x-moz-file-promise-url", U16(img)), ("HTML Format", html)]);
        yield return ff;

        var tmpDir = Path.Combine(Path.GetTempPath(), "convertino-selftest-ff");
        Directory.CreateDirectory(tmpDir);
        var tmp = Path.Combine(tmpDir, "photo.png");
        File.WriteAllBytes(tmp, TestServer.Png);
        var ffTemp = new DragCase { Name = "Firefox image: its temp copy in CF_HDROP → JPG", Pick = "to-jpg",
            Check = Made("photo.jpg").And(Kept("photo.png")).And(_ => File.Exists(tmp) ? null : "the browser's temp file was moved") };
        ffTemp.Local.Add(tmp);
        ffTemp.Virtual.Add(("photo.png", TestServer.Png, true));
        ffTemp.Formats.AddRange([("text/x-moz-url", U16(img + "\nPhoto")), ("application/x-moz-file-promise-url", U16(img))]);
        yield return ffTemp;

        var lying = new DragCase { Name = "virtual file named .jpg holding a PNG → kept as .png", Shown = "photo.jpg", Pick = "to-webp", Check = Made("photo.webp").And(Kept("photo.png")) };
        lying.Virtual.Add(("photo.jpg", TestServer.Png, false));
        yield return lying;

        var dl = new DragCase { Name = "DownloadURL → Save", Shown = "report.pdf", Link = false, Pick = "download",
            Check = Made("report.pdf").And(Holds("report.pdf", t => t.StartsWith("%PDF-"), "hold the PDF")) };
        dl.Formats.Add(("DownloadURL", U16($"application/pdf:report.pdf:{b}/doc")));
        yield return dl;

        var custom = new DragCase { Name = "DownloadURL in Chrome's custom data", Shown = "report.pdf", Pick = "download", Check = Made("report.pdf") };
        custom.Formats.Add(("Chromium Web Custom MIME Data Format", Pickle(("DownloadURL", $"application/pdf:report.pdf:{b}/doc"))));
        yield return custom;

        var link = new DragCase { Name = "link drag with its virtual .url → link flower", Link = true, Shown = "Example page", Pick = "shortcut", Check = Made("Example page.url") };
        link.Virtual.Add(("Example page.url", U8($"[InternetShortcut]\r\nURL={b}/page.html\r\n"), true));
        link.Formats.Add(("text/x-moz-url", U16($"{b}/page.html\nExample page")));
        yield return link;

        var png = new DragCase { Name = "raw PNG format → JPG, no download", Pick = "to-jpg", Check = Made("photo.jpg").And(Offline(s, "/img/photo.png")) };
        png.Formats.AddRange([("PNG", TestServer.Png), ("UniformResourceLocatorW", U16(img))]);
        yield return png;

        var dib = new DragCase { Name = "CF_DIBV5 only → PNG", Shown = "image.bmp", Pick = "to-png", Check = Made("image.png") };
        dib.Formats.Add(("CF_DIBV5", TestServer.Dib));
        yield return dib;

        var two = new DragCase { Name = "two virtual files (Outlook-style) → Save both", Pick = "download", Check = Made("notes.txt", "data.csv") };
        two.Virtual.Add(("notes.txt", U8("hello"), true));
        two.Virtual.Add(("data.csv", U8("a,b\n1,2\n"), false));
        yield return two;

        var real = Path.Combine(outDir, "mine.png");
        File.WriteAllBytes(real, TestServer.Png);
        var explorer = new DragCase { Name = "Explorer file drag stays a file drag", Bloom = false };
        explorer.Local.Add(real);
        yield return explorer;

        var tempOnly = new DragCase { Name = "a file from %TEMP% without browser formats stays a file drag", Bloom = false };
        tempOnly.Local.Add(tmp);
        yield return tempOnly;
    }

    static IEnumerable<DragCase> X11(TestServer s)
    {
        string b = s.Base, img = $"{b}/img/photo.png";
        var ff = new DragCase { Name = "Firefox image: image/png in the drag → JPG, no download", Shown = "photo.png", Pick = "to-jpg", Check = Made("photo.jpg").And(Offline(s, "/img/photo.png")) };
        ff.Formats.AddRange([("text/x-moz-url", Encoding.Unicode.GetBytes(img + "\nPhoto")), ("_NETSCAPE_URL", U8(img + "\nPhoto")),
            ("text/html", Encoding.Unicode.GetBytes($"\ufeff<img src=\"{img}\">")), ("image/png", TestServer.Png), ("text/plain;charset=utf-8", U8(img))]);
        yield return ff;

        var chrome = new DragCase { Name = "Chrome image: uri-list + html → download → WEBP", Shown = "photo.png", Pick = "to-webp", Check = Made("photo.webp").And(Kept("photo.png")) };
        chrome.Formats.AddRange([("text/uri-list", U8(img + "\r\n")), ("text/html", U8($"<meta charset=\"utf-8\"><img src=\"{img}\">"))]);
        yield return chrome;

        var list = new DragCase { Name = "uri-list with a comment and a data: image", Shown = "image.png", Pick = "download", Check = Made("image.png") };
        list.Formats.Add(("text/uri-list", U8("# dragged\r\ndata:image/png;base64," + Convert.ToBase64String(TestServer.Png) + "\r\n")));
        yield return list;
    }

    static IEnumerable<DragCase> Mac(TestServer s)
    {
        string b = s.Base;
        var safari = new DragCase { Name = "Safari image: file promise (no promise here) → falls back to the URL → PNG", Shown = "photo.jpg", Pick = "to-png", Check = Made("photo.png").And(Kept("photo.jpg")) };
        safari.Formats.AddRange([("com.apple.pasteboard.promised-file-content-type", U8("public.jpeg")), ("com.apple.NSFilePromiseItemMetaData", U8("x")),
            ("public.url", U8($"{b}/img/photo.jpg")), ("public.url-name", U8("Photo"))]);
        yield return safari;

        var chrome = new DragCase { Name = "Chrome image: public.png in the drag → JPG, no download", Shown = "photo.png", Pick = "to-jpg", Check = Made("photo.jpg").And(Offline(s, "/img/photo.png")) };
        chrome.Formats.AddRange([("public.png", TestServer.Png), ("public.url", U8($"{b}/img/photo.png")), ("public.html", U8($"<img src=\"{b}/img/photo.png\">"))]);
        yield return chrome;

        var tiff = new DragCase { Name = "public.tiff only → PNG", Shown = "image.tiff", Pick = "to-png", Check = Made("image.png") };
        tiff.Formats.Add(("public.tiff", TestServer.Tiff));
        yield return tiff;
    }

    /// <summary>Chrome's pickled custom data: payload size, count, then UTF-16 key/value pairs padded to 4 bytes.</summary>
    static byte[] Pickle(params (string Key, string Value)[] pairs)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0);
        w.Write(pairs.Length);
        void Str(string s)
        {
            w.Write(s.Length);
            w.Write(Encoding.Unicode.GetBytes(s));
            while (ms.Length % 4 != 0) w.Write((byte)0);
        }
        foreach (var (k, v) in pairs) { Str(k); Str(v); }
        var bytes = ms.ToArray();
        BitConverter.GetBytes(bytes.Length - 4).CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>FILEGROUPDESCRIPTORW for virtual files (names + sizes), as the browser writes it.</summary>
    public static byte[] Descriptor(IReadOnlyList<(string Name, long Size)> files)
    {
        var b = new byte[4 + files.Count * 592];
        BitConverter.GetBytes(files.Count).CopyTo(b, 0);
        for (int i = 0; i < files.Count; i++)
        {
            int o = 4 + i * 592;
            BitConverter.GetBytes(0x40u | 0x4000u | 0x80000000u).CopyTo(b, o); // FD_FILESIZE | FD_PROGRESSUI | FD_UNICODE
            BitConverter.GetBytes((uint)(files[i].Size >> 32)).CopyTo(b, o + 64);
            BitConverter.GetBytes((uint)files[i].Size).CopyTo(b, o + 68);
            var name = Encoding.Unicode.GetBytes(files[i].Name);
            Array.Copy(name, 0, b, o + 72, Math.Min(name.Length, 518));
        }
        return b;
    }

    // ---------- the pure parts ----------

    static IEnumerable<(string, string?)> Units()
    {
        static string? Eq(string got, string want) => got == want ? null : $"'{got}', expected '{want}'";
        foreach (var (raw, want) in new[]
        {
            ("../../a.png", "a.png"), ("..\\..\\b.png", "b.png"), ("CON", "_CON"), ("con.txt", "_con.txt"), ("LPT1.jpg", "_LPT1.jpg"), ("Com3 .tar.gz", "_Com3 .tar.gz"),
            ("photo\u202Egnp.exe", "photognp.exe"), (".bashrc", "bashrc"), ("a<b>c:d|e?\"*.png", "abcde.png"), ("  x.png. ", "x.png"), ("", "download"),
            ("....", "download"), ("\u0000x\u0007.png", "x.png"), ("C:\\Windows\\win.ini", "win.ini"), ("zero\u200bwidth.png", "zerowidth.png"),
        })
            yield return ($"SafeName {raw.Replace("\u202E", "<RLO>").Replace("\u0000", "<NUL>")}", Eq(Fetch.SafeName(raw), want));
        var longName = Fetch.SafeName(new string('a', 300) + ".png");
        yield return ("SafeName 300 chars", longName.Length == 120 && longName.EndsWith(".png") ? null : $"{longName.Length} chars");

        foreach (var (bytes, want) in new (byte[], string?)[]
        {
            (TestServer.Png, "png"), (TestServer.Jpg, "jpg"), (U8("GIF89a...."), "gif"), (U8("%PDF-1.7"), "pdf"), ([0x50, 0x4B, 3, 4, 0], "zip"),
            (U8("RIFF\0\0\0\0WEBPVP8 "), "webp"), (U8("\ufeff <!DOCTYPE html><html>"), "html"), (U8("<?xml version=\"1.0\"?><svg xmlns=\"x\"/>"), "svg"),
            (U8("MZ\u0090\0"), null), (U8("hello"), null), ([0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'a', (byte)'v', (byte)'i', (byte)'f'], "avif"),
        })
            yield return ($"Sniff {want ?? "nothing"}", Fetch.Sniff(bytes) == want ? null : $"got {Fetch.Sniff(bytes) ?? "null"}");

        foreach (var (name, sniffed, mime, want) in new (string, string?, string?, string)[]
        {
            ("photo.jpg", "png", null, "photo.png"), ("x.jpeg", "jpg", null, "x.jpeg"), ("doc.docx", "zip", null, "doc.docx"), ("view.php", null, "text/html", "view.html"),
            ("evil.exe", "png", null, "evil.png"), ("setup.exe", null, "application/octet-stream", "setup.exe"), ("image", "webp", null, "image.webp"),
            ("notes", null, "text/plain; charset=utf-8", "notes.txt"), ("raw.dng", "tiff", null, "raw.dng"), ("clip.mp4", "mp4", "video/mp4", "clip.mp4"),
        })
            yield return ($"FixExtension {name} ({sniffed ?? mime})", Eq(Fetch.FixExtension(name, sniffed, mime), want));

        yield return ("Parse ftp:", Fetch.Parse("ftp://x/y") == null ? null : "accepted");
        yield return ("Parse file:", Fetch.Parse("file:///etc/passwd") == null ? null : "accepted");
        yield return ("Parse javascript:", Fetch.Parse("javascript:alert(1)") == null ? null : "accepted");
        yield return ("Parse drops user:pass", Fetch.Parse("https://u:p@example.com/a")?.UserInfo == "" ? null : "kept the credentials");
        yield return ("Parse data:", Fetch.Parse("data:,hi") != null ? null : "refused");
        yield return ("DataUri base64", Fetch.DataUri("data:image/png;base64," + Convert.ToBase64String(TestServer.Png)).Bytes.SequenceEqual(TestServer.Png) ? null : "wrong bytes");
        yield return ("DataUri percent", Eq(Encoding.UTF8.GetString(Fetch.DataUri("data:text/plain,a%20b").Bytes), "a b"));
        yield return ("DataUri broken", Throws(() => Fetch.DataUri("data:image/png;base64,@@@")));

        var du = WebDrop.DownloadUrl("image/png:My pic.png:https://example.com/a:b/c.png");
        yield return ("DownloadURL", du?.Name == "My pic.png" && du.Mime == "image/png" && du.Url?.AbsoluteUri == "https://example.com/a:b/c.png" ? null : "parsed wrong");
        var (frag, page) = WebDrop.CfHtml(Encoding.UTF8.GetString(CfHtml("<img src='x.png'>", "https://example.com/g/")));
        yield return ("CF_HTML", frag == "<img src='x.png'>" && WebDrop.ImgSrc(frag, page)?.AbsoluteUri == "https://example.com/g/x.png" ? null : $"'{frag}' / {page}");
        yield return ("img src entities", WebDrop.ImgSrc("<IMG data-x=1 SRC=\"https://e.com/a.png?x=1&amp;y=2\">", null)?.Query == "?x=1&y=2" ? null : "not decoded");
        yield return ("UTF-16 text", Eq(WebDrop.DecodeText(Encoding.Unicode.GetBytes("https://e.com/\nT\0junk")), "https://e.com/\nT"));
        yield return ("UTF-8 text", Eq(WebDrop.DecodeText(U8("https://e.com/ü")), "https://e.com/ü"));
        var fgd = Descriptor([("a.png", 10), ("b.txt", 20)]);
        var d = WebDrop.Descriptors(fgd, true);
        yield return ("FileGroupDescriptorW", d.Count == 2 && d[1].Name == "b.txt" && d[1].Size == 20 ? null : $"{d.Count} entries");
        var bmp = WebDrop.DibToBmp(TestServer.Dib);
        yield return ("DIB → BMP", Probe(() => { using var i = new MagickImage(bmp); return i.Width == 64 ? null : $"{i.Width} wide"; }));

        var tmp = Path.Combine(Path.GetTempPath(), $"convertino-xdg-{Environment.ProcessId}");
        File.WriteAllText(tmp, "# comment\nXDG_DESKTOP_DIR=\"$HOME/Desktop\"\nXDG_DOWNLOAD_DIR=\"$HOME/Загрузки\"\n");
        yield return ("user-dirs.dirs", Eq(Fetch.XdgDownload(tmp, "/home/u") ?? "null", "/home/u/Загрузки"));
        File.WriteAllText(tmp, "XDG_DOWNLOAD_DIR=\"$HOME/\"\n");
        yield return ("user-dirs.dirs disabled", Eq(Fetch.XdgDownload(tmp, "/home/u") ?? "null", "null"));
        File.Delete(tmp);
    }

    static string? Throws(Action a)
    {
        try { a(); return "didn't throw"; }
        catch (Fetch.Refused) { return null; }
        catch (Exception ex) { return "threw " + ex.GetType().Name; }
    }

    static string? Probe(Func<string?> f)
    {
        try { return f(); }
        catch (Exception ex) { return ex.Message; }
    }
}

/// <summary>The self-test's web server on 127.0.0.1: every way a download can go wrong.</summary>
sealed class TestServer : IDisposable
{
    readonly HttpListener _l = new();
    readonly Dictionary<string, int> _hits = [];
    public int Port { get; }
    public string Base { get; }

    public static readonly byte[] Png = Picture(MagickFormat.Png), Jpg = Picture(MagickFormat.Jpeg), Tiff = Picture(MagickFormat.Tiff);
    /// <summary>A packed DIB (BITMAPV5HEADER + pixels), as CF_DIBV5 carries it.</summary>
    public static readonly byte[] Dib = Picture(MagickFormat.Bmp)[14..];

    static byte[] Picture(MagickFormat f)
    {
        using var img = new MagickImage("gradient:#ff8a3d-#ff3d68", new MagickReadSettings { Width = 64, Height = 48 });
        return img.ToByteArray(f);
    }

    public TestServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        // http.sys lets anyone listen on localhost; a literal 127.0.0.1 would need an admin URL reservation
        Base = $"http://localhost:{Port}";
        _l.Prefixes.Add(Base + "/");
        _l.Start();
        _ = Loop();
    }

    public int Hits(string path) { lock (_hits) return _hits.GetValueOrDefault(path); }

    async Task Loop()
    {
        while (_l.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _l.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(async () =>
            {
                try { await Serve(ctx); }
                catch { try { ctx.Response.Abort(); } catch { } return; }
                try { ctx.Response.Close(); } catch { }
            });
        }
    }

    async Task Serve(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        lock (_hits) _hits[path] = _hits.GetValueOrDefault(path) + 1;
        var r = ctx.Response;
        async Task Send(byte[] body, string type, string? disposition = null)
        {
            r.ContentType = type;
            if (disposition != null) r.Headers.Add("Content-Disposition", disposition);
            r.ContentLength64 = body.Length;
            await r.OutputStream.WriteAsync(body);
        }
        void Redirect(string to) { r.StatusCode = 302; r.RedirectLocation = to; }

        switch (path)
        {
            case "/img/photo.png" or "/img/noext": await Send(Png, "image/png"); break;
            case "/img/photo.jpg": await Send(Jpg, "image/jpeg"); break;
            case "/img/lying.png": await Send(Jpg, "image/png"); break;
            case "/img/page.php": await Send(Jpg, "text/html"); break;
            case "/doc": await Send(Encoding.ASCII.GetBytes("%PDF-1.4\n%\u00e2\u00e3\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n"), "application/octet-stream"); break;
            case "/page.html": await Send(Encoding.UTF8.GetBytes("<!doctype html><html><head><title>Example page</title></head><body>Hi</body></html>"), "text/html; charset=utf-8"); break;
            case "/echo.txt":
                await Send(Encoding.UTF8.GetBytes($"cookie={ctx.Request.Headers["Cookie"]};auth={ctx.Request.Headers["Authorization"]}"), "text/plain");
                break;
            case "/evil": await Send(Png, "image/png", "attachment; filename=\"../../../evil.png\""); break;
            case "/evil2": await Send(Png, "image/png", "attachment; filename*=UTF-8''..%2F..%2Fevil2.png"); break;
            case "/con": await Send(Png, "image/png", "attachment; filename=\"CON.png\""); break;
            case "/bidi": await Send(Png, "image/png", "attachment; filename*=UTF-8''photo%E2%80%AEgnp.exe"); break;
            case "/tofile": Redirect(OperatingSystem.IsWindows() ? "file:///C:/Windows/win.ini" : "file:///etc/passwd"); break;
            case "/toftp": Redirect("ftp://127.0.0.1/x.png"); break;
            case "/todata": Redirect("data:text/plain,hi"); break;
            case "/401": r.StatusCode = 401; r.Headers.Add("WWW-Authenticate", "Basic realm=\"private\""); break;
            case "/403": r.StatusCode = 403; break;
            case "/private/photo.jpg": Redirect("/accounts/login?next=/private/photo.jpg"); break;
            case "/accounts/login": await Send(Encoding.UTF8.GetBytes("<!doctype html><html><body><form>Sign in</form></body></html>"), "text/html"); break;
            case "/big.bin":
                r.ContentType = "application/octet-stream";
                r.ContentLength64 = Fetch.Cap + 1;
                await r.OutputStream.WriteAsync(new byte[16]);
                await r.OutputStream.FlushAsync();
                await Task.Delay(500);
                r.Abort();
                break;
            case "/chunked.bin":
                r.ContentType = "application/octet-stream";
                r.SendChunked = true;
                var block = new byte[64 * 1024];
                for (long sent = 0; sent < Fetch.Cap * 2; sent += block.Length) await r.OutputStream.WriteAsync(block);
                break;
            case "/stall.bin":
                r.ContentType = "application/octet-stream";
                r.SendChunked = true;
                await r.OutputStream.WriteAsync(new byte[1024]);
                await r.OutputStream.FlushAsync();
                await Task.Delay(4000);
                await r.OutputStream.WriteAsync(new byte[1024]);
                break;
            case "/broken.bin":
                r.ContentType = "application/octet-stream";
                r.ContentLength64 = 10000;
                await r.OutputStream.WriteAsync(new byte[100]);
                await r.OutputStream.FlushAsync();
                await Task.Delay(200);
                r.Abort();
                break;
            default:
                if (path.StartsWith("/r/") && int.TryParse(path[3..], out int k)) Redirect(k < 5 ? $"/r/{k + 1}" : "/img/photo.png");
                else if (path.StartsWith("/loop/") && int.TryParse(path[6..], out int j)) Redirect($"/loop/{j + 1}");
                else r.StatusCode = 404;
                break;
        }
    }

    public void Dispose()
    {
        try { _l.Stop(); _l.Close(); } catch { }
    }
}
