using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;

namespace Convertino.Services;

/// <summary>
/// Downloads for drops out of a browser, copied links and <c>convertino fetch</c>. Everything from the web is untrusted:
/// http, https and data: only (every redirect hop re-checked, at most 5), no cookies or credentials sent, 30 s to
/// connect and 30 s of silence at most, 2 GB at most, the real type sniffed from the bytes, the file name cleaned of
/// paths, device names and spoofing characters. Results land in the Downloads folder marked as coming from the internet.
/// </summary>
public static class Fetch
{
    public const long MaxBytes = 2L << 30;
    const int MaxRedirects = 5;

    /// <summary>Connect, headers and the longest pause between two reads. The self-test shortens it.</summary>
    public static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>The size cap; the self-test lowers it.</summary>
    public static long Cap { get; set; } = MaxBytes;
    /// <summary>Where saved files go instead of the user's Downloads folder (self-test, <c>convertino fetch --out</c>).</summary>
    public static string? DownloadsOverride { get; set; }

    public const string SignIn = "This site needs a sign-in — save the file first";

    /// <summary>A download that was refused, with a message fit for the result card.</summary>
    public sealed class Refused(string message) : Exception(message);

    public sealed record Result(string Path, string? Mime, long Bytes, Uri Source);

    static readonly HttpClient Http = Client();

    static HttpClient Client()
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            // redirects by hand, so every hop's scheme is checked; no cookie jar, no stored or default credentials
            AllowAutoRedirect = false,
            UseCookies = false,
            Credentials = null,
            PreAuthenticate = false,
            DefaultProxyCredentials = null,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            $"Mozilla/5.0 (compatible; Convertino/{typeof(Fetch).Assembly.GetName().Version?.ToString(3)}; +https://github.com/hasanzadee/convertino)");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        return http;
    }

    /// <summary>An absolute http, https or data: URL (user name and password dropped), or null.</summary>
    public static Uri? Parse(string? s)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s) || s.Length > 64 << 20 || !Uri.TryCreate(s, UriKind.Absolute, out var u)) return null;
        if (u.Scheme == "data") return u;
        if (u.Scheme is not ("http" or "https") || string.IsNullOrEmpty(u.Host)) return null;
        if (u.UserInfo.Length > 0) u = new UriBuilder(u) { UserName = "", Password = "" }.Uri;
        return u;
    }

    public static bool IsData(Uri u) => u.Scheme == "data";

    /// <summary>"example.com" for the card; "web page" for a data: URL.</summary>
    public static string HostOf(Uri? u) => u == null || IsData(u) ? "web page" : u.IdnHost.StartsWith("www.") ? u.IdnHost[4..] : u.IdnHost;

    /// <summary>
    /// Get <paramref name="url"/> into <paramref name="dir"/> (a fresh working folder). The name comes from
    /// <paramref name="suggested"/> (what the page or browser called it), Content-Disposition or the URL, with the
    /// extension corrected to what the bytes are; <paramref name="fallback"/> (a link's title) only when neither names a
    /// real file. <paramref name="expectFile"/>: the drag promised a file, so an HTML sign-in page instead of it means the
    /// site wants a login.
    /// </summary>
    public static async Task<Result> Download(Uri url, string dir, string? suggested, Action<long, long?>? progress, CancellationToken ct,
        bool expectFile = false, string? fallback = null)
    {
        Directory.CreateDirectory(dir);
        if (IsData(url))
        {
            var (mime, bytes) = DataUri(url.OriginalString);
            var name = SafeName(suggested is { Length: > 0 } ? suggested : "image" + MimeExt(mime), "download");
            var p = Path.Combine(dir, FixExtension(name, Sniff(bytes), mime));
            await File.WriteAllBytesAsync(p, bytes, ct);
            progress?.Invoke(bytes.Length, bytes.Length);
            return new(p, mime, bytes.Length, url);
        }

        var current = url;
        for (int hop = 0; ; hop++)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(IdleTimeout);
            using var req = new HttpRequestMessage(HttpMethod.Get, current);
            HttpResponseMessage resp;
            try { resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, idle.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new Refused($"{HostOf(current)} didn't answer for {IdleTimeout.TotalSeconds:0} s"); }
            catch (HttpRequestException ex) { throw new Refused($"Couldn't reach {HostOf(current)}" + (ex.HttpRequestError == HttpRequestError.NameResolutionError ? " (no such site)" : "")); }
            using (resp)
            {
                int code = (int)resp.StatusCode;
                if (code is 301 or 302 or 303 or 307 or 308)
                {
                    if (hop >= MaxRedirects) throw new Refused("Too many redirects");
                    var loc = resp.Headers.Location;
                    if (loc == null) throw new Refused($"{HostOf(current)} redirected nowhere");
                    var next = Parse(new Uri(current, loc).AbsoluteUri);
                    // https → http is allowed (browsers do too); file:, ftp:, data: and the rest never are
                    if (next == null || IsData(next)) throw new Refused($"{HostOf(current)} redirected to a link Convertino doesn't open");
                    current = next;
                    continue;
                }
                if (code is 401 or 403 or 407) throw new Refused(SignIn);
                if (code is < 200 or > 299) throw new Refused($"{HostOf(current)} answered {code} {resp.ReasonPhrase}".TrimEnd());

                var mime = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                if (expectFile && mime is "text/html" or "application/xhtml+xml" && LooksLikeSignIn(url, current)) throw new Refused(SignIn);
                long? total = resp.Content.Headers.ContentLength;
                if (total > Cap) throw new Refused($"Too big to download ({Paths.HumanSize(total.Value)}; {Paths.HumanSize(Cap)} at most)");

                var landed = UrlName(current);
                bool real = landed != null && Path.GetExtension(landed) is { Length: > 1 } le && !PageExts.Contains(le[1..]);
                var name = suggested is { Length: > 0 } s ? s
                    : DispositionName(resp.Content.Headers) ?? (real ? landed : fallback ?? landed ?? UrlName(url)) ?? "download";
                name = SafeName(name, "download");
                var part = Path.Combine(dir, ".download.part");
                var head = new byte[4096];
                int headLen = 0;
                long done = 0;
                try
                {
                    await using (var body = await resp.Content.ReadAsStreamAsync(idle.Token))
                    await using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true))
                    {
                        var buf = new byte[1 << 16];
                        long lastTick = 0;
                        while (true)
                        {
                            idle.CancelAfter(IdleTimeout);
                            int n;
                            try { n = await body.ReadAsync(buf, idle.Token); }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new Refused($"{HostOf(current)} stopped sending for {IdleTimeout.TotalSeconds:0} s"); }
                            catch (Exception ex) when (ex is IOException or HttpRequestException) { throw new Refused($"The download from {HostOf(current)} broke off"); }
                            if (n == 0) break;
                            done += n;
                            if (done > Cap) throw new Refused($"Too big to download ({Paths.HumanSize(Cap)} at most)");
                            if (headLen < head.Length) { int k = Math.Min(n, head.Length - headLen); Buffer.BlockCopy(buf, 0, head, headLen, k); headLen += k; }
                            await file.WriteAsync(buf.AsMemory(0, n), ct);
                            if (done - lastTick > 256 * 1024) { lastTick = done; progress?.Invoke(done, total); }
                        }
                    }
                    if (total is { } t && done < t) throw new Refused($"The download from {HostOf(current)} broke off");
                    progress?.Invoke(done, done);
                    var final = Path.Combine(dir, FixExtension(name, Sniff(head.AsSpan(0, headLen)), mime));
                    File.Move(part, final);
                    return new(final, mime, done, current);
                }
                catch { FileTools.TryDeleteFile(part); throw; }
            }
        }
    }

    /// <summary>A login wall: the server sent HTML from a sign-in looking address (or another site) where a file was promised.</summary>
    static bool LooksLikeSignIn(Uri asked, Uri landed)
    {
        var path = (landed.AbsolutePath + landed.Query).ToLowerInvariant();
        return !string.Equals(asked.Host, landed.Host, StringComparison.OrdinalIgnoreCase)
            || new[] { "login", "signin", "sign-in", "sign_in", "logon", "sso", "oauth", "auth", "account", "session" }.Any(path.Contains);
    }

    static string? DispositionName(HttpContentHeaders h)
    {
        try
        {
            var cd = h.ContentDisposition;
            var n = cd?.FileNameStar ?? cd?.FileName;
            return string.IsNullOrWhiteSpace(n) ? null : n.Trim().Trim('"');
        }
        catch { return null; }
    }

    /// <summary>The last path segment of an http(s) URL, unescaped ("Sunset%20Beach.jpg" → "Sunset Beach.jpg").</summary>
    public static string? UrlName(Uri u)
    {
        if (IsData(u)) return null;
        var seg = u.AbsolutePath.TrimEnd('/');
        seg = seg[(seg.LastIndexOf('/') + 1)..];
        if (seg.Length == 0) return null;
        try { seg = Uri.UnescapeDataString(seg); } catch { }
        return seg.Length == 0 ? null : seg;
    }

    // ---------- data: ----------

    /// <summary>data:[mime][;base64],payload → (mime, bytes). Throws <see cref="Refused"/> when malformed.</summary>
    public static (string Mime, byte[] Bytes) DataUri(string s)
    {
        int comma = s.IndexOf(',');
        if (!s.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || comma < 0) throw new Refused("A broken data: link");
        var meta = s[5..comma].Split(';');
        var mime = meta[0].Trim().ToLowerInvariant();
        if (mime.Length == 0) mime = "text/plain";
        bool b64 = meta.Skip(1).Any(m => m.Trim().Equals("base64", StringComparison.OrdinalIgnoreCase));
        var payload = s[(comma + 1)..];
        try
        {
            var bytes = b64 ? Convert.FromBase64String(Uri.UnescapeDataString(payload).Replace(" ", "").Replace("\n", "").Replace("\r", ""))
                            : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
            if (bytes.Length > Cap) throw new Refused($"Too big ({Paths.HumanSize(Cap)} at most)");
            return (mime, bytes);
        }
        catch (FormatException) { throw new Refused("A broken data: link"); }
    }

    // ---------- names ----------

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>
    /// A file name that is safe on every OS: no folders ("../../x" → "x"), no characters Windows refuses, no control or
    /// direction-override characters (the "photo‮gpj.exe" trick), no leading dots (hidden files, ".."), no trailing dots
    /// or spaces, no device names ("CON.png" → "_CON.png"), at most 120 characters with the extension kept.
    /// </summary>
    public static string SafeName(string? raw, string fallback = "download")
    {
        var s = raw ?? "";
        int slash = Math.Max(s.LastIndexOf('/'), s.LastIndexOf('\\'));
        if (slash >= 0) s = s[(slash + 1)..];
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch < 0x20 || ch == 0x7f || ch is '<' or '>' or ':' or '"' or '|' or '?' or '*') continue;
            if (ch is >= '​' and <= '‏' or >= '‪' and <= '‮' or >= '⁦' and <= '⁩' or '﻿' or '؜') continue;
            sb.Append(ch);
        }
        s = sb.ToString().Trim().TrimStart('.').TrimEnd('.', ' ').Trim();
        if (s.Length == 0) return fallback;
        int dot = s.IndexOf('.');
        var stem = (dot < 0 ? s : s[..dot]).TrimEnd(' ');
        if (Reserved.Contains(stem)) s = "_" + s;
        if (s.Length > 120)
        {
            var ext = Path.GetExtension(s);
            if (ext.Length > 12) ext = "";
            s = s[..(120 - ext.Length)].TrimEnd('.', ' ') + ext;
        }
        return s;
    }

    // ---------- types ----------

    /// <summary>The file type the first bytes say (extension, lower case), or null when they don't say. Executables are never named.</summary>
    public static string? Sniff(ReadOnlySpan<byte> b)
    {

        if (Has(b, 0, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return "png";
        if (Has(b, 0, 0xFF, 0xD8, 0xFF)) return "jpg";
        if (At(b, 0, "GIF87a") || At(b, 0, "GIF89a")) return "gif";
        if (At(b, 0, "RIFF") && At(b, 8, "WEBP")) return "webp";
        if (At(b, 0, "RIFF") && At(b, 8, "WAVE")) return "wav";
        if (At(b, 0, "RIFF") && At(b, 8, "AVI ")) return "avi";
        if (At(b, 0, "8BPS")) return "psd";
        if (At(b, 0, "qoif")) return "qoi";
        if (Has(b, 0, 0xFF, 0x0A) || Has(b, 0, 0, 0, 0, 0x0C, 0x4A, 0x58, 0x4C, 0x20)) return "jxl";
        if (Has(b, 0, 0x49, 0x49, 0x2A, 0) || Has(b, 0, 0x4D, 0x4D, 0, 0x2A)) return "tiff";
        if (At(b, 0, "BM") && b.Length >= 14 && b[6] == 0 && b[7] == 0 && b[8] == 0 && b[9] == 0) return "bmp";
        if (Has(b, 0, 0, 0, 1, 0) && b.Length >= 6 && b[4] + b[5] > 0) return "ico";
        if (At(b, 4, "ftyp") && b.Length >= 12)
        {
            var brand = Encoding.ASCII.GetString(b.Slice(8, 4));
            return brand switch
            {
                "avif" or "avis" => "avif",
                "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" or "mif1" or "msf1" => "heic",
                "qt  " => "mov",
                "M4A " or "M4B " => "m4a",
                "M4V " => "m4v",
                "crx " => null,
                _ when brand.StartsWith("3g") => "3gp",
                _ => "mp4",
            };
        }
        if (Has(b, 0, 0x1A, 0x45, 0xDF, 0xA3)) return b.IndexOf("webm"u8) >= 0 ? "webm" : "mkv";
        if (At(b, 0, "OggS")) return b.IndexOf("OpusHead"u8) >= 0 ? "opus" : "ogg";
        if (At(b, 0, "fLaC")) return "flac";
        if (At(b, 0, "ID3") || (b.Length > 1 && b[0] == 0xFF && b[1] is 0xFB or 0xF3 or 0xF2 or 0xFA)) return "mp3";
        if (b.Length > 1 && b[0] == 0xFF && b[1] is 0xF1 or 0xF9) return "aac";
        if (At(b, 0, "%PDF-")) return "pdf";
        if (Has(b, 0, 0x50, 0x4B, 3, 4) || Has(b, 0, 0x50, 0x4B, 5, 6)) return "zip";
        if (Has(b, 0, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C)) return "7z";
        if (At(b, 0, "Rar!\u001a\u0007")) return "rar";
        if (Has(b, 0, 0x1F, 0x8B)) return "gz";
        if (At(b, 0, "BZh")) return "bz2";
        if (Has(b, 0, 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0)) return "xz";
        if (At(b, 257, "ustar")) return "tar";

        // text: skip a BOM and white space, then look at the first tag
        int i = Has(b, 0, 0xEF, 0xBB, 0xBF) ? 3 : 0;
        while (i < b.Length && b[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        if (i >= b.Length || b[i] != '<') return null;
        var text = Encoding.UTF8.GetString(b[i..Math.Min(b.Length, i + 1024)]).ToLowerInvariant();
        if (text.StartsWith("<svg") || (text.StartsWith("<?xml") && text.Contains("<svg"))) return "svg";
        if (text.StartsWith("<!doctype html") || text.StartsWith("<html") || text.StartsWith("<head") || text.StartsWith("<body")) return "html";
        return null;
    }

    static bool At(ReadOnlySpan<byte> b, int i, string ascii)
    {
        if (b.Length < i + ascii.Length) return false;
        for (int k = 0; k < ascii.Length; k++) if (b[i + k] != ascii[k]) return false;
        return true;
    }

    static bool Has(ReadOnlySpan<byte> b, int i, params byte[] x) => b.Length >= i + x.Length && b.Slice(i, x.Length).SequenceEqual(x);

    /// <summary>Extensions one sniffed type may legitimately carry (a ZIP may be a .docx, a TIFF a camera RAW…).</summary>
    static readonly string[][] Families =
    [
        ["jpg", "jpeg", "jfif", "jpe"],
        ["tiff", "tif", "dng", "cr2", "nef", "arw"],
        ["heic", "heif"],
        ["mp4", "m4v", "m4a", "mov", "3gp"],
        ["mkv", "webm"],
        ["ogg", "oga", "ogv", "opus"],
        ["zip", "docx", "docm", "xlsx", "xlsm", "pptx", "ppsx", "odt", "ods", "odp", "epub", "jar", "apk", "cbz", "xpi", "vsix", "nupkg", "whl", "kmz", "3mf", "ipa"],
        ["gz", "tgz"],
        ["bz2", "tbz", "tbz2"],
        ["xz", "txz"],
        ["html", "htm", "xhtml", "shtml"],
        ["ico", "cur"],
    ];

    /// <summary>Server-side page extensions: the name says nothing about what was sent.</summary>
    public static readonly HashSet<string> PageExts = new(StringComparer.OrdinalIgnoreCase) { "", "php", "asp", "aspx", "jsp", "cgi", "pl", "do", "action", "html", "htm", "xhtml", "shtml", "cfm" };

    /// <summary>
    /// The name with the extension of what the bytes really are: "photo.jpg" holding a PNG → "photo.png", "image" → "image.webp",
    /// "view.php" sending HTML → "view.html". Without a sniffed type the Content-Type decides, and only for nameless or page names.
    /// </summary>
    public static string FixExtension(string name, string? sniffed, string? mime)
    {
        var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        var stem = ext.Length > 0 ? name[..^(ext.Length + 1)] : name;
        if (stem.Length == 0) stem = "download";
        if (sniffed != null)
        {
            if (ext == sniffed || Families.Any(f => f.Contains(sniffed) && f.Contains(ext))) return name;
            return $"{stem}.{sniffed}";
        }
        if (PageExts.Contains(ext) && MimeExt(mime) is { Length: > 0 } m && m != "." + ext) return stem + m;
        return name;
    }

    static readonly Dictionary<string, string> Mimes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png", ["image/apng"] = ".png", ["image/jpeg"] = ".jpg", ["image/jpg"] = ".jpg", ["image/pjpeg"] = ".jpg", ["image/gif"] = ".gif",
        ["image/webp"] = ".webp", ["image/avif"] = ".avif", ["image/heic"] = ".heic", ["image/heif"] = ".heif", ["image/bmp"] = ".bmp",
        ["image/x-ms-bmp"] = ".bmp", ["image/tiff"] = ".tiff", ["image/svg+xml"] = ".svg", ["image/x-icon"] = ".ico",
        ["image/vnd.microsoft.icon"] = ".ico", ["image/jxl"] = ".jxl", ["image/qoi"] = ".qoi",
        ["application/pdf"] = ".pdf", ["application/zip"] = ".zip", ["application/x-zip-compressed"] = ".zip",
        ["application/x-7z-compressed"] = ".7z", ["application/vnd.rar"] = ".rar", ["application/x-rar-compressed"] = ".rar",
        ["application/gzip"] = ".gz", ["application/x-gzip"] = ".gz", ["application/x-tar"] = ".tar",
        ["video/mp4"] = ".mp4", ["video/webm"] = ".webm", ["video/quicktime"] = ".mov", ["video/x-matroska"] = ".mkv",
        ["audio/mpeg"] = ".mp3", ["audio/mp4"] = ".m4a", ["audio/ogg"] = ".ogg", ["audio/wav"] = ".wav", ["audio/x-wav"] = ".wav",
        ["audio/flac"] = ".flac", ["audio/webm"] = ".webm", ["audio/opus"] = ".opus",
        ["text/plain"] = ".txt", ["text/html"] = ".html", ["application/xhtml+xml"] = ".html", ["text/csv"] = ".csv",
        ["text/markdown"] = ".md", ["application/json"] = ".json", ["text/xml"] = ".xml", ["application/xml"] = ".xml",
        ["text/css"] = ".css", ["text/vtt"] = ".vtt", ["application/x-subrip"] = ".srt", ["application/yaml"] = ".yaml",
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ".docx",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = ".xlsx",
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = ".pptx",
        ["application/msword"] = ".doc", ["application/vnd.ms-excel"] = ".xls", ["application/vnd.ms-powerpoint"] = ".ppt",
        ["application/rtf"] = ".rtf", ["application/epub+zip"] = ".epub",
    };

    /// <summary>".png" for "image/png" (parameters ignored), "" when unknown.</summary>
    public static string MimeExt(string? mime)
    {
        if (string.IsNullOrEmpty(mime)) return "";
        int semi = mime.IndexOf(';');
        return Mimes.TryGetValue((semi < 0 ? mime : mime[..semi]).Trim(), out var e) ? e : "";
    }

    // ---------- where results go ----------

    /// <summary>The user's Downloads folder (Windows: the Downloads known folder, wherever it was moved; Linux: XDG_DOWNLOAD_DIR; macOS: ~/Downloads).</summary>
    public static string DownloadsDir()
    {
        string dir = DownloadsOverride ?? Resolve();
        Directory.CreateDirectory(dir);
        return dir;

        static string Resolve()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsWindows())
            {
                var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
                if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) == 0 && p != IntPtr.Zero)
                {
                    try { if (Marshal.PtrToStringUni(p) is { Length: > 0 } s) return s; }
                    finally { Marshal.FreeCoTaskMem(p); }
                }
            }
            else if (!OperatingSystem.IsMacOS())
            {
                if (Environment.GetEnvironmentVariable("XDG_DOWNLOAD_DIR") is { Length: > 0 } env && Path.IsPathRooted(env)) return env;
                var cfg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (string.IsNullOrEmpty(cfg)) cfg = Path.Combine(home, ".config");
                if (XdgDownload(Path.Combine(cfg, "user-dirs.dirs"), home) is { } x) return x;
            }
            return Path.Combine(home, "Downloads");
        }
    }

    /// <summary>XDG_DOWNLOAD_DIR="$HOME/Downloads" from user-dirs.dirs, or null.</summary>
    public static string? XdgDownload(string file, string home)
    {
        try
        {
            if (!File.Exists(file)) return null;
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (!line.StartsWith("XDG_DOWNLOAD_DIR=")) continue;
                var v = line["XDG_DOWNLOAD_DIR=".Length..].Trim().Trim('"');
                if (v.StartsWith("$HOME")) v = home + v[5..];
                // "$HOME/" alone means "no Downloads folder" (xdg-user-dirs): fall back to ~/Downloads
                v = v.TrimEnd('/');
                return Path.IsPathRooted(v) && v != home.TrimEnd('/') ? v : null;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Move a finished download into <paramref name="dir"/> under a free name ("photo (2).png"), then mark it as from the internet.</summary>
    public static string Keep(string file, string dir, Uri? source)
    {
        FileTools.CreateUnique(dir, Path.GetFileName(file), out var dst).Dispose();
        try { File.Move(file, dst, true); }
        catch { FileTools.TryDeleteFile(dst); throw; }
        Mark(dst, source);
        return dst;
    }

    /// <summary>
    /// Mark-of-the-Web: a Zone.Identifier stream (Internet zone) on Windows, com.apple.quarantine on macOS,
    /// user.xdg.origin.url on Linux. Folders are marked file by file (up to 5000). Best effort: FAT drives have no streams.
    /// </summary>
    public static void Mark(string path, Uri? source)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Take(5000)) Mark(f, source);
                return;
            }
            if (!File.Exists(path)) return;
            string host = source == null || IsData(source) || source.AbsoluteUri.Length > 2000 ? "about:internet" : source.AbsoluteUri;
            if (OperatingSystem.IsWindows())
                File.WriteAllText(path + ":Zone.Identifier", $"[ZoneTransfer]\r\nZoneId=3\r\nHostUrl={host}\r\n");
            else if (OperatingSystem.IsMacOS())
            {
                var v = Encoding.UTF8.GetBytes($"0081;{DateTimeOffset.UtcNow.ToUnixTimeSeconds():x8};Convertino;");
                MacSetXattr(path, "com.apple.quarantine", v, (nuint)v.Length, 0, 0);
            }
            else if (host != "about:internet")
            {
                var v = Encoding.UTF8.GetBytes(host);
                LinuxSetXattr(path, "user.xdg.origin.url", v, (nuint)v.Length, 0);
            }
        }
        catch (Exception ex) { Log.Info($"mark of the web skipped for {Path.GetFileName(path)}: {ex.Message}"); }
    }

    /// <summary>Whether <paramref name="path"/> carries the mark <see cref="Mark"/> writes (self-test).</summary>
    public static bool IsMarked(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return File.Exists(path + ":Zone.Identifier") && File.ReadAllText(path + ":Zone.Identifier").Contains("ZoneId=3");
            var buf = new byte[256];
            long n = OperatingSystem.IsMacOS() ? (long)MacGetXattr(path, "com.apple.quarantine", buf, (nuint)buf.Length, 0, 0)
                                               : (long)LinuxGetXattr(path, "user.xdg.origin.url", buf, (nuint)buf.Length);
            return n > 0;
        }
        catch { return false; }
    }

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "setxattr", SetLastError = true)]
    static extern int MacSetXattr(string path, string name, byte[] value, nuint size, uint position, int options);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "getxattr", SetLastError = true)]
    static extern nint MacGetXattr(string path, string name, byte[] value, nuint size, uint position, int options);
    [DllImport("libc.so.6", EntryPoint = "setxattr", SetLastError = true)]
    static extern int LinuxSetXattr(string path, string name, byte[] value, nuint size, int flags);
    [DllImport("libc.so.6", EntryPoint = "getxattr", SetLastError = true)]
    static extern nint LinuxGetXattr(string path, string name, byte[] value, nuint size);
}
