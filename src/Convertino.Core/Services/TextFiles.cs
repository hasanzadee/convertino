using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;

namespace Convertino.Services;

/// <summary>
/// Plain-text files of any kind (TXT, Markdown, CSV, JSON, subtitles, scripts…): which encoding they're in, re-encoding
/// them as UTF-8 and switching their line endings. A file with NUL bytes that isn't UTF-16/32 is binary and never touched.
/// </summary>
public static class TextFiles
{
    static TextFiles() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    // where re-encoding and line endings are safe; .bat/.cmd/.ps1/.reg stay out on purpose, because cmd.exe,
    // Windows PowerShell and regedit read them in the OEM / ANSI code page or UTF-16
    static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "log", "md", "markdown", "csv", "tsv", "srt", "vtt", "ass", "ssa", "lrc", "json", "jsonl", "ndjson", "xml",
        "yaml", "yml", "toml", "ini", "cfg", "conf", "html", "htm", "css", "js", "sql", "sh", "py", "tex", "nfo",
    };

    // Excel reads a CSV as UTF-8 only when it starts with a BOM, and players want one on subtitles;
    // JSON and shell scripts must not have one
    static readonly HashSet<string> BomExt = new(StringComparer.OrdinalIgnoreCase) { "csv", "tsv", "srt", "ass", "ssa", "lrc" };

    public static bool IsTextFile(string path) => TextExt.Contains(Kinds.Ext(path));

    /// <summary>
    /// "UTF-8" and the line-ending petal for plain-text files: LF when they use CRLF, CRLF when they use LF,
    /// both when they mix. Sniffs the first 16 KB of up to 8 files; none for folders, other files or binaries.
    /// </summary>
    public static List<PetalAction> Petals(IReadOnlyList<string> files)
    {
        var list = new List<PetalAction>();
        if (files.Count == 0 || !files.All(f => IsTextFile(f) && File.Exists(f))) return list;
        int crlf = 0, lf = 0, cr = 0;
        foreach (var f in files.Take(8))
        {
            var head = Head(f, 16 * 1024);
            if (head == null || IsBinary(head)) return list;
            var u = Unicode(head, out int bom);
            var (a, b, c) = Count(u != null ? u.GetString(head, bom, head.Length - bom) : Encoding.Latin1.GetString(head));
            crlf += a; lf += b; cr += c;
        }
        list.Add(Catalog.Tool("utf8", "UTF-8", "encoding", "Re-encode as UTF-8", ToUtf8));
        if (crlf + cr > 0) list.Add(Catalog.Tool("lf", "LF", "eol", "Convert line endings to LF", ToLf));
        if (lf + cr > 0) list.Add(Catalog.Tool("crlf", "CRLF", "eol", "Convert line endings to CRLF", ToCrlf));
        return list;
    }

    static byte[]? Head(string path, int max)
    {
        try
        {
            using var s = File.OpenRead(path);
            var buf = new byte[Math.Min(max, s.Length)];
            s.ReadExactly(buf);
            return buf;
        }
        catch { return null; }
    }

    // ── petals ──────────────────────────────────────────────────────────────

    /// <summary>
    /// "UTF-8": legacy code pages and UTF-16/32 re-encoded as UTF-8 beside the file ("notes (UTF-8).txt"); mixed
    /// line endings become the file's main style. A UTF-8 CSV without a BOM gets one, so Excel reads it as UTF-8.
    /// </summary>
    public static async Task ToUtf8(Job job)
    {
        int n = job.Inputs.Count, binary = 0;
        var from = new SortedSet<string>();
        bool bomAdded = false, eolFixed = false;
        for (int i = 0; i < n; i++)
        {
            job.Token.ThrowIfCancellationRequested();
            job.Report(i, n);
            var src = job.Inputs[i];
            var bytes = await File.ReadAllBytesAsync(src, job.Token);
            if (IsBinary(bytes)) { binary++; continue; }
            var enc = Detect(bytes, out int bom);
            var text = enc.GetString(bytes, bom, bytes.Length - bom);
            var ext = Kinds.Ext(src);
            var (crlf, lf, cr) = Count(text);
            bool utf8 = enc.CodePage == 65001, mixed = cr > 0 || (crlf > 0 && lf > 0);
            bool addBom = utf8 && bom == 0 && ext is "csv" or "tsv";
            if (utf8 && !mixed && !addBom) continue;
            if (mixed) text = Normalize(text, crlf >= lf && crlf > 0 ? "\r\n" : "\n", out _);
            var dst = Paths.Beside(src, ext, "(UTF-8)");
            await File.WriteAllTextAsync(dst, text, new UTF8Encoding(BomExt.Contains(ext)), job.Token);
            job.Outputs.Add(dst);
            if (!utf8) from.Add(Name(enc));
            bomAdded |= addBom;
            eolFixed |= mixed;
        }
        if (job.Outputs.Count == 0)
        {
            if (binary == n) throw new InvalidDataException(Binary(n));
            job.Title = n == 1 ? "Already UTF-8" : "All already UTF-8";
            job.Summary = binary > 0 ? $"Nothing to change · {binary} binary skipped" : "Nothing to change";
            return;
        }
        var parts = new List<string>();
        if (from.Count > 0) parts.Add($"{string.Join(", ", from)} → UTF-8");
        if (bomAdded) parts.Add("BOM added for Excel");
        if (eolFixed) parts.Add(parts.Count == 0 ? "Line endings fixed" : "line endings fixed");
        if (binary > 0) parts.Add($"{binary} binary skipped");
        job.Summary = string.Join(" · ", parts);
    }

    public static Task ToLf(Job job) => LineEndings(job, "\n", "LF");
    public static Task ToCrlf(Job job) => LineEndings(job, "\r\n", "CRLF");

    /// <summary>"LF" / "CRLF": every line break (CRLF, LF, a lone CR) rewritten, beside the file ("run (LF).sh"); the encoding stays.</summary>
    static async Task LineEndings(Job job, string nl, string label)
    {
        int n = job.Inputs.Count, binary = 0, changed = 0;
        for (int i = 0; i < n; i++)
        {
            job.Token.ThrowIfCancellationRequested();
            job.Report(i, n);
            var src = job.Inputs[i];
            var bytes = await File.ReadAllBytesAsync(src, job.Token);
            if (IsBinary(bytes)) { binary++; continue; }
            byte[] result;
            int count;
            if (Unicode(bytes, out int bom) is { } u)
            {
                // UTF-16/32: rewrite the characters, keep the encoding and its BOM
                var text = Normalize(u.GetString(bytes, bom, bytes.Length - bom), nl, out count);
                result = [.. bytes[..bom], .. u.GetBytes(text)];
            }
            else
            {
                // CR and LF are the same bytes in UTF-8 and every legacy code page, so no decoding (and no guessing) needed
                var text = Normalize(Encoding.Latin1.GetString(bytes), nl, out count);
                result = Encoding.Latin1.GetBytes(text);
            }
            if (count == 0) continue;
            var dst = Paths.Beside(src, Kinds.Ext(src), $"({label})");
            await File.WriteAllBytesAsync(dst, result, job.Token);
            job.Outputs.Add(dst);
            changed += count;
        }
        if (job.Outputs.Count == 0)
        {
            if (binary == n) throw new InvalidDataException(Binary(n));
            job.Title = $"Already {label}";
            job.Summary = binary > 0 ? $"Nothing to change · {binary} binary skipped" : "Nothing to change";
            return;
        }
        job.Summary = $"{changed:N0} line {(changed == 1 ? "ending" : "endings")} → {label}" + (binary > 0 ? $" · {binary} binary skipped" : "");
    }

    static string Binary(int n) => n == 1 ? "This looks like a binary file, not text; it was left untouched" : "These look like binary files, not text; they were left untouched";

    // ── text & line endings ─────────────────────────────────────────────────

    internal static string[] Lines(string text) => text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

    static (int Crlf, int Lf, int Cr) Count(string s)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (int k = 0; k < s.Length; k++)
        {
            if (s[k] == '\r') { if (k + 1 < s.Length && s[k + 1] == '\n') { crlf++; k++; } else cr++; }
            else if (s[k] == '\n') lf++;
        }
        return (crlf, lf, cr);
    }

    /// <summary>Every CRLF, LF and lone CR becomes <paramref name="nl"/>; <paramref name="changed"/> counts the ones that differed.</summary>
    static string Normalize(string s, string nl, out int changed)
    {
        changed = 0;
        var sb = new StringBuilder(s.Length + s.Length / 16);
        for (int k = 0; k < s.Length; k++)
        {
            char c = s[k];
            if (c is not ('\r' or '\n')) { sb.Append(c); continue; }
            bool crlf = c == '\r' && k + 1 < s.Length && s[k + 1] == '\n';
            if (crlf) k++;
            if ((crlf ? "\r\n" : c.ToString()) != nl) changed++;
            sb.Append(nl);
        }
        return sb.ToString();
    }

    /// <summary>NUL bytes in anything but UTF-16/32 mean it isn't text.</summary>
    public static bool IsBinary(ReadOnlySpan<byte> b) => b.Contains((byte)0) && Unicode(b, out _) == null;

    /// <summary>The bytes decoded with <see cref="Detect"/>, without the BOM.</summary>
    public static string Decode(byte[] bytes)
    {
        var enc = Detect(bytes, out int bom);
        return enc.GetString(bytes, bom, bytes.Length - bom);
    }

    // ── encodings ───────────────────────────────────────────────────────────

    /// <summary>UTF-16 or UTF-32, by BOM or (UTF-16 without one) by the zero byte that ASCII leaves in every other place.</summary>
    static Encoding? Unicode(ReadOnlySpan<byte> b, out int bom)
    {
        bom = 0;
        if (b is [0xFF, 0xFE, 0, 0, ..]) { bom = 4; return new UTF32Encoding(false, false); }
        if (b is [0, 0, 0xFE, 0xFF, ..]) { bom = 4; return new UTF32Encoding(true, false); }
        if (b is [0xFF, 0xFE, ..]) { bom = 2; return new UnicodeEncoding(false, false); }
        if (b is [0xFE, 0xFF, ..]) { bom = 2; return new UnicodeEncoding(true, false); }
        int len = Math.Min(b.Length, 8192) & ~1, evenZero = 0, oddZero = 0;
        for (int k = 0; k < len; k += 2) { if (b[k] == 0) evenZero++; if (b[k + 1] == 0) oddZero++; }
        int pairs = len / 2;
        if (pairs >= 8 && oddZero > pairs * 0.3 && evenZero < pairs * 0.05) return new UnicodeEncoding(false, false);
        if (pairs >= 8 && evenZero > pairs * 0.3 && oddZero < pairs * 0.05) return new UnicodeEncoding(true, false);
        return null;
    }

    /// <summary>
    /// The text's encoding and the length of its BOM. A BOM decides; then UTF-16 by its zero bytes; then strict UTF-8
    /// (plain ASCII included); otherwise the most plausible reading among the system code page, Windows-1252 (Western),
    /// Windows-1251 (Cyrillic), Windows-1254 (Turkish), KOI8-R and DOS 866.
    /// </summary>
    public static Encoding Detect(ReadOnlySpan<byte> b, out int bom)
    {
        if (b is [0xEF, 0xBB, 0xBF, ..]) { bom = 3; return new UTF8Encoding(false); }
        if (Unicode(b, out bom) is { } u) return u;
        if (Utf8.IsValid(b)) return new UTF8Encoding(false);

        Encoding? best = null;
        double bestScore = double.MinValue;
        foreach (var cp in CodePages())
        {
            Encoding enc;
            try { enc = Encoding.GetEncoding(cp, EncoderFallback.ReplacementFallback, new DecoderReplacementFallback("�")); }
            catch { continue; }
            // near-ties (a word or two) go to the commoner code pages; exact ties to the earlier one, the system's own first
            var score = Plausibility(enc.GetString(b)) - cp switch { 866 => 1, 20866 => 0.5, _ => 0 };
            if (score > bestScore) { best = enc; bestScore = score; }
        }
        return best ?? Encoding.Latin1;
    }

    static List<int> CodePages()
    {
        var list = new List<int>();
        void Add(int cp) { if (cp is > 0 and not 65001 and not 1200 and not 1201 && !list.Contains(cp)) list.Add(cp); }
        if (OperatingSystem.IsWindows()) { try { Add(GetACP()); } catch { } }
        Add(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        foreach (var cp in new[] { 1252, 1251, 1254, 20866, 866 }) Add(cp);
        return list;
    }

    [DllImport("kernel32.dll")]
    static extern int GetACP();

    const string Punctuation = " «»‘’‚“”„–—…•·°№€£¥©®™§¶±¹²³½¼¾¿¡‎‏";
    const string RareLetters = "ðþýÐÞÝÿ";
    const string CommonCyrillic = "оеаинтсрвлкм";

    /// <summary>
    /// How natural a decoding reads. Real words stick to one script, don't capitalise mid-word (KOI8-R read as 1251 does),
    /// aren't made only of accented letters (Cyrillic read as 1252 is), and avoid control codes, stray symbols (box
    /// drawing from DOS 866 / KOI8-R) and the Icelandic letters that Turkish ğ ı ş become in 1252.
    /// </summary>
    static double Plausibility(string s)
    {
        double score = 0;
        int k = 0;
        while (k < s.Length)
        {
            char c = s[k];
            if (c == '�' || c is >= '\u0080' and <= '\u009F') { score -= 20; k++; continue; }
            if (!char.IsLetter(c))
            {
                if (c >= 0x80 && !char.IsWhiteSpace(c) && !Punctuation.Contains(c))
                    score -= k > 0 && k + 1 < s.Length && char.IsLetter(s[k - 1]) && char.IsLetter(s[k + 1]) ? 3 : 1;
                k++;
                continue;
            }
            int ascii = 0, latin = 0, cyrillic = 0, flips = 0;
            bool lower = false;
            for (; k < s.Length && char.IsLetter(s[k]); k++)
            {
                char w = s[k];
                if (w < 0x80) ascii++;
                else if (w is >= 'Ѐ' and <= 'ӿ')
                {
                    cyrillic++;
                    // the Cyrillic code pages disagree on which bytes are letters, not on whether: frequent letters tell them apart
                    if (CommonCyrillic.Contains(char.ToLowerInvariant(w))) score += 0.5;
                }
                else if (w < 0x0250) latin++;
                if (RareLetters.Contains(w)) score -= 2;
                if (lower && char.IsUpper(w)) flips++;
                lower = char.IsLower(w);
            }
            if (cyrillic > 0 && ascii + latin > 0) score -= 5;
            if (latin >= 2 && ascii == 0 && cyrillic == 0) score -= 2 * latin;
            score -= 3 * flips;
        }
        return score;
    }

    /// <summary>"Windows-1251", "KOI8-R", "DOS-866", "UTF-16 LE"…</summary>
    public static string Name(Encoding enc) => enc.CodePage switch
    {
        65001 => "UTF-8",
        1200 => "UTF-16 LE",
        1201 => "UTF-16 BE",
        12000 => "UTF-32 LE",
        12001 => "UTF-32 BE",
        20866 => "KOI8-R",
        866 => "DOS-866",
        >= 1250 and <= 1258 => $"Windows-{enc.CodePage}",
        _ => enc.WebName.ToUpperInvariant(),
    };
}
