using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Convertino.Services;

/// <summary>
/// Subtitles: SubRip, WebVTT, ASS/SSA and LRC lyrics turned into each other (read in whatever encoding
/// <see cref="TextFiles"/> detects), and a video's subtitle track saved as SRT (the only part that needs ffmpeg).
/// </summary>
public static class Subtitles
{
    /// <summary>One cue. Times in ms; lines joined by '\n', the only markup is balanced &lt;i&gt; &lt;b&gt; &lt;u&gt;.
    /// <paramref name="Top"/>: shown at the top of the picture ({\an8} in SRT and ASS, line:0 in WebVTT).</summary>
    public sealed record Cue(long Start, long End, string Text, bool Top = false);

    // ── petals ──────────────────────────────────────────────────────────────

    /// <summary>"SRT", "VTT", "ASS", "TXT": each file converted beside itself, always written as UTF-8.</summary>
    public static async Task Convert(Job job, string ext)
    {
        int n = job.Inputs.Count, total = 0;
        bool rounded = false;
        var legacy = new SortedSet<string>();
        for (int i = 0; i < n; i++)
        {
            job.Token.ThrowIfCancellationRequested();
            job.Report(i, n);
            var src = job.Inputs[i];
            var bytes = await File.ReadAllBytesAsync(src, job.Token);
            var enc = TextFiles.Detect(bytes, out int bom);
            var cues = Parse(enc.GetString(bytes, bom, bytes.Length - bom), Kinds.Ext(src));
            if (cues.Count == 0) throw new InvalidDataException(L.T("No subtitles found in {0}", Path.GetFileName(src)));
            var dst = Paths.Beside(src, ext);
            await File.WriteAllTextAsync(dst, Write(cues, ext), new UTF8Encoding(ext is "srt" or "ass"), job.Token);
            job.Outputs.Add(dst);
            total += cues.Count;
            rounded |= ext == "ass" && cues.Any(c => c.Start % 10 != 0 || c.End % 10 != 0);
            if (enc.CodePage != 65001) legacy.Add(TextFiles.Name(enc));
        }
        var parts = new List<string> { L.N(total, "{0} cue", "{0} cues") };
        if (legacy.Count > 0) parts.Add(L.T("read as {0}", string.Join(", ", legacy)));
        if (rounded) parts.Add(L.T("ASS keeps 1/100 s"));
        job.Summary = string.Join(" · ", parts);
    }

    /// <summary>"Subtitles" on a video: its first text subtitle track, saved beside it as SRT (movie.eng.srt).</summary>
    public static async Task FromVideo(Job job)
    {
        int n = job.Inputs.Count;
        string why = L.T("This video has no subtitle track");
        for (int i = 0; i < n; i++)
        {
            int idx = i;
            var src = job.Inputs[i];
            var (tracks, startTime) = await Tracks(src, job.Token);
            var track = tracks?.FirstOrDefault(t => !Pictures.ContainsKey(t.Codec));
            if (tracks != null && track == null)
            {
                why = tracks.Count == 0 ? L.T("This video has no subtitle track")
                    : L.T("This video's subtitles are pictures ({0}), not text, so they can't be saved as SRT", Pictures[tracks[0].Codec]);
                continue;
            }
            var lang = track?.Language is { Length: > 0 } l && l != "und" ? l : null;
            var dst = Paths.Beside(src, lang != null ? lang + ".srt" : "srt");
            // ffmpeg shifts everything by the file's start time; when that is just an encoder delay (AAC's −23 ms)
            // keep the times as stored, which is where players show the embedded track
            var copyts = Math.Abs(startTime) < 1 ? "-copyts " : "";
            var map = track != null ? track.Index.ToString(CultureInfo.InvariantCulture) : "s:0";
            // ffmpeg turns ASS styles into <font size="64"> and style-wide bold; our own converter drops them
            var ass = track?.Codec is "ass" or "ssa" ? Path.Combine(Paths.Temp, Guid.NewGuid().ToString("N") + ".ass") : null;
            try
            {
                await Media.Run($"{copyts}-i \"{src}\" -map 0:{map} {(ass != null ? $"-c:s copy \"{ass}\"" : $"-c:s srt \"{dst}\"")}",
                    Media.Duration(src), p => job.Report(idx, n, p), job.Token);
                if (ass != null)
                {
                    var cues = Parse(await File.ReadAllTextAsync(ass, job.Token), "ass");
                    await File.WriteAllTextAsync(dst, cues.Count > 0 ? WriteSrt(cues) : "", new UTF8Encoding(true), job.Token);
                }
            }
            catch (IOException ex) when (track == null && ex.Message.Contains("matches no streams"))
            {
                // no ffprobe to ask first: ffmpeg's own answer
                TryDelete(dst);
                continue;
            }
            catch { TryDelete(dst); throw; }
            finally { if (ass != null) TryDelete(ass); }
            if (Paths.SizeOf(dst) <= 3) { TryDelete(dst); why = L.T("The video's subtitle track is empty"); continue; }
            job.Outputs.Add(dst);
            if (n == 1)
            {
                var parts = new List<string> { track?.Title is { Length: > 0 } t ? t : lang ?? L.T("Subtitle track") };
                if (ass != null) parts.Add(L.T("ASS styles dropped"));
                if (tracks is { Count: > 1 }) parts.Add(L.T("first of {0} tracks", tracks.Count));
                job.Summary = string.Join(" · ", parts);
            }
        }
        if (job.Outputs.Count == 0) throw new InvalidOperationException(n == 1 ? why : L.T("None of these videos has a text subtitle track"));
        if (n > 1) job.Summary = L.T("{0} of {1} videos had subtitles", job.Outputs.Count, n);
    }

    // bitmap subtitles: turning them into text would take OCR
    static readonly Dictionary<string, string> Pictures = new()
    {
        ["hdmv_pgs_subtitle"] = "PGS", ["dvd_subtitle"] = "VobSub", ["dvb_subtitle"] = "DVB", ["xsub"] = "XSUB",
    };

    sealed record Track(int Index, string Codec, string? Language, string? Title);

    /// <summary>The video's subtitle streams (null when ffprobe isn't there to ask, or takes over 15 s) and the file's start time in seconds.</summary>
    static async Task<(List<Track>? Tracks, double Start)> Tracks(string path, CancellationToken ct)
    {
        if (Media.FFprobe is not { } probe) return (null, 0);
        try
        {
            var psi = new ProcessStartInfo(probe)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var a in new[] { "-v", "error", "-select_streams", "s", "-show_entries", "stream=index,codec_name:stream_tags=language,title:format=start_time", "-of", "json", path })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
            _ = p.StandardError.ReadToEndAsync(CancellationToken.None);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(15000);
                try { await p.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    try { p.Kill(true); } catch { }
                    ct.ThrowIfCancellationRequested();
                    Log.Info($"ffprobe gave no answer in 15 s: {path}");
                    return (null, 0);
                }
            }
            using var doc = JsonDocument.Parse(await output);
            var list = new List<Track>();
            if (doc.RootElement.TryGetProperty("streams", out var streams))
                foreach (var s in streams.EnumerateArray())
                {
                    string? Tag(string key) => s.TryGetProperty("tags", out var tags) && tags.TryGetProperty(key, out var v) ? v.GetString() : null;
                    list.Add(new Track(s.GetProperty("index").GetInt32(), s.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "", Tag("language"), Tag("title")));
                }
            double start = 0;
            if (doc.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("start_time", out var st))
                double.TryParse(st.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out start);
            return (list, start);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Error("ffprobe subtitle tracks", ex);
            return (null, 0);
        }
    }

    static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    // ── reading ─────────────────────────────────────────────────────────────

    /// <summary>Cues of a subtitle file's text; the format comes from its first line, else from the extension.</summary>
    public static List<Cue> Parse(string text, string ext)
    {
        var head = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (head.StartsWith("WEBVTT", StringComparison.Ordinal)) ext = "vtt";
        else if (head.StartsWith("[Script Info]", StringComparison.OrdinalIgnoreCase)) ext = "ass";
        var cues = ext switch
        {
            "vtt" => ParseSrtVtt(text, vtt: true),
            "ass" or "ssa" => Merge(ParseAss(text)),
            "lrc" => ParseLrc(text),
            _ => ParseSrtVtt(text, vtt: false),
        };
        return cues.OrderBy(c => c.Start).ToList();
    }

    static readonly Regex Timing = new(@"^\s*((?:\d+:)?\d{1,2}:\d{1,2}(?:[.,]\d+)?)\s*-->\s*((?:\d+:)?\d{1,2}:\d{1,2}(?:[.,]\d+)?)(.*)$", RegexOptions.Compiled);

    static List<Cue> ParseSrtVtt(string text, bool vtt)
    {
        var cues = new List<Cue>();
        List<string>? body = null;
        long start = 0, end = 0;
        bool top = false;

        void Flush()
        {
            if (body == null) return;
            var (t, anTop) = vtt ? (FromVtt(string.Join('\n', body)), false) : FromSrt(string.Join('\n', body));
            if (t.Length > 0) cues.Add(new Cue(start, Math.Max(start, end), t, top || anTop));
            body = null;
        }

        foreach (var line in TextFiles.Lines(text))
        {
            var m = Timing.Match(line);
            if (m.Success)
            {
                // in SubRip the line above a timing line is the next cue's number, after a blank line; without the blank
                // line it is text ("1984") of a file that numbers no cues
                if (!vtt && body is [.., var before, var last] && before.Trim().Length == 0 && last.Trim() is { Length: > 0 } num && num.All(char.IsAsciiDigit))
                    body.RemoveAt(body.Count - 1);
                Flush();
                start = ParseTime(m.Groups[1].Value);
                end = ParseTime(m.Groups[2].Value);
                top = vtt && VttTop(m.Groups[3].Value);
                body = [];
            }
            else if (body != null)
            {
                // a WebVTT cue ends at its first blank line (NOTE / STYLE blocks follow one); SubRip players read on to the
                // next number, and the blank lines (dropped later) tell a cue number from text
                if (line.Trim().Length == 0 && vtt) Flush();
                else body.Add(line);
            }
        }
        Flush();
        return cues;
    }

    /// <summary>"line:0" or "line:10%" in the cue settings: the cue sits at the top.</summary>
    static bool VttTop(string settings)
    {
        foreach (var s in settings.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!s.StartsWith("line:", StringComparison.Ordinal)) continue;
            var v = s[5..].Split(',')[0];
            if (v.EndsWith('%')) return double.TryParse(v[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) && pct < 40;
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ln) && ln >= 0;
        }
        return false;
    }

    static readonly Regex OverrideBlock = new(@"\{\\[^}]*\}", RegexOptions.Compiled);

    /// <summary>SubRip text: HTML-ish tags, plus the ASS override blocks ({\an8}, {\i1}) many SRT files carry.</summary>
    static (string Text, bool Top) FromSrt(string s)
    {
        bool top = false;
        s = OverrideBlock.Replace(s, m =>
        {
            var (t, anTop) = FromAss(m.Value, Plain, null, wrap2: false);
            top |= anTop;
            return t;
        });
        return (Clean(s), top);
    }

    static readonly Regex Ruby = new(@"<rt\b[^>]*>.*?</rt>", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>WebVTT payload: voice, class, language and timestamp tags and ruby text go, entities are decoded.</summary>
    static string FromVtt(string s) => WebUtility.HtmlDecode(Clean(Ruby.Replace(s, "")));

    sealed record AssStyle(bool Italic, bool Top);
    static readonly AssStyle Plain = new(false, false);

    static List<Cue> ParseAss(string text)
    {
        var cues = new List<Cue>();
        var styles = new Dictionary<string, AssStyle>(StringComparer.OrdinalIgnoreCase);
        string section = "";
        string[] styleFormat = [];
        string[] eventFormat = ["layer", "start", "end", "style", "name", "marginl", "marginr", "marginv", "effect", "text"];
        bool wrap2 = false;

        static string[] Names(string v) => v.Split(',').Select(x => x.Trim().ToLowerInvariant()).ToArray();

        foreach (var raw in TextFiles.Lines(text))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line.ToLowerInvariant(); continue; }
            int colon = line.IndexOf(':');
            if (colon <= 0 || line.StartsWith(';')) continue;
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].TrimStart();

            if (section == "[script info]" && key == "wrapstyle") wrap2 = value.Trim() == "2";
            else if (section.Contains("styles"))
            {
                // SSA's [V4 Styles] numbers alignments 1-3 bottom, 5-7 top, 9-11 middle; ASS uses the numpad
                bool ssa = section == "[v4 styles]";
                if (key == "format") styleFormat = Names(value);
                else if (key == "style")
                {
                    var fmt = styleFormat.Length > 0 ? styleFormat : ssa
                        ? Names("Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, TertiaryColour, BackColour, Bold, Italic, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, AlphaLevel, Encoding")
                        : Names("Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
                    var f = value.Split(',');
                    string Get(string name) { int k = Array.IndexOf(fmt, name); return k >= 0 && k < f.Length ? f[k].Trim() : ""; }
                    int.TryParse(Get("alignment"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var align);
                    styles[Get("name").TrimStart('*')] = new AssStyle(Get("italic") is not ("" or "0"), ssa ? align is 5 or 6 or 7 : align is 7 or 8 or 9);
                }
            }
            else if (section == "[events]")
            {
                if (key == "format") eventFormat = Names(value);
                else if (key == "dialogue")
                {
                    var f = value.Split(',', eventFormat.Length);
                    if (f.Length < eventFormat.Length) continue;
                    int si = Array.IndexOf(eventFormat, "start"), ei = Array.IndexOf(eventFormat, "end"), st = Array.IndexOf(eventFormat, "style");
                    if (si < 0 || ei < 0) continue;
                    var style = st >= 0 && styles.TryGetValue(f[st].Trim().TrimStart('*'), out var s) ? s : Plain;
                    var (t, top) = FromAss(f[^1], style, styles, wrap2);
                    t = Clean(t);
                    if (t.Length == 0) continue;
                    long start = ParseTime(f[si], 10), end = ParseTime(f[ei], 10);
                    cues.Add(new Cue(start, Math.Max(start, end), t, top));
                }
            }
        }
        return cues;
    }

    static readonly Regex Transform = new(@"\\t\([^)]*\)?", RegexOptions.Compiled);

    /// <summary>
    /// ASS dialogue text → our markup. Keeps line breaks (\N; \n too under WrapStyle 2), italics (the style's and
    /// {\i1}…{\i0}), {\b1} bold, {\u1} underline and whether it sits at the top; every other override and drawings go.
    /// </summary>
    static (string Text, bool Top) FromAss(string s, AssStyle style, Dictionary<string, AssStyle>? styles, bool wrap2)
    {
        var sb = new StringBuilder();
        bool top = style.Top, drawing = false;
        if (style.Italic) sb.Append("<i>");
        for (int k = 0; k < s.Length; k++)
        {
            char c = s[k];
            // \N, \n, \h; libass's \{ \} for literal braces; and "\⁠" (a word joiner after it), how WriteAss keeps a
            // literal backslash from forming one of those
            if (c == '\\' && k + 1 < s.Length && s[k + 1] is 'N' or 'n' or 'h' or '{' or '}' or '⁠')
            {
                char e = s[++k];
                if (!drawing) sb.Append(e switch { 'N' => "\n", 'n' => wrap2 ? "\n" : " ", 'h' => " ", '⁠' => "\\", _ => e.ToString() });
                continue;
            }
            if (c == '{' && s.IndexOf('}', k + 1) is var close and > 0)
            {
                // \t(…) animates other tags over time; what it switches on isn't the line's own state.
                // Text before the first backslash is a comment ({TL note}), not a tag.
                foreach (var raw in Transform.Replace(s[(k + 1)..close], "").Split('\\').Skip(1))
                {
                    var tag = raw.Trim();
                    if (tag.StartsWith("an", StringComparison.Ordinal) && tag.Length > 2 && char.IsAsciiDigit(tag[2])) top = tag[2] is '7' or '8' or '9';
                    else if (tag.Length > 1 && tag[0] == 'a' && char.IsAsciiDigit(tag[1])) top = tag[1..] is "5" or "6" or "7";
                    else if (tag.Length > 1 && tag[0] == 'p' && char.IsAsciiDigit(tag[1])) drawing = tag[1..] != "0";
                    else if (tag.StartsWith('r'))
                    {
                        var to = tag.Length > 1 && styles != null && styles.TryGetValue(tag[1..].Trim().TrimStart('*'), out var named) ? named : style;
                        sb.Append("</u></b></i>");
                        if (to.Italic) sb.Append("<i>");
                    }
                    else if (Toggle(tag, 'i') is var it and >= 0) sb.Append(it == 1 || (it == 2 && style.Italic) ? "<i>" : "</i>");
                    else if (Toggle(tag, 'b') is var bt and >= 0) sb.Append(bt == 1 ? "<b>" : "</b>");
                    else if (Toggle(tag, 'u') is var ut and >= 0) sb.Append(ut == 1 ? "<u>" : "</u>");
                }
                k = close;
                continue;
            }
            if (drawing) continue;
            sb.Append(c);
        }
        return (sb.ToString(), top);
    }

    /// <summary>\i1 → 1, \i0 → 0, a bare \i (back to the style) → 2; -1 for any other tag (\iclip, \bord, \blur, \be…).
    /// \b also takes a font weight: \b700 is bold.</summary>
    static int Toggle(string tag, char name)
    {
        if (tag.Length == 0 || tag[0] != name) return -1;
        var arg = tag[1..].Trim();
        if (arg.Length == 0) return 2;
        if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return -1;
        return v == 1 || v >= 500 ? 1 : 0;
    }

    /// <summary>Typesetting often splits one line into several events (layers, outlines): same time and place → one cue.</summary>
    static List<Cue> Merge(List<Cue> cues)
    {
        var res = new List<Cue>();
        var at = new Dictionary<(long, long, bool), int>();
        foreach (var c in cues)
        {
            if (!at.TryGetValue((c.Start, c.End, c.Top), out var k)) { at[(c.Start, c.End, c.Top)] = res.Count; res.Add(c); continue; }
            if (res[k].Text != c.Text && !res[k].Text.Split('\n').Contains(c.Text)) res[k] = res[k] with { Text = res[k].Text + "\n" + c.Text };
        }
        return res;
    }

    static readonly Regex LrcStamp = new(@"^\[(\d+):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    static readonly Regex LrcOffset = new(@"^\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex LrcWord = new(@"<\d+:\d{1,2}(?:[.:]\d{1,3})?>", RegexOptions.Compiled);

    /// <summary>LRC lyrics: a line lasts until the next one starts (an empty line just ends the one before); the last gets 5 s.</summary>
    static List<Cue> ParseLrc(string text)
    {
        long offset = 0;
        var marks = new List<(long At, string Text)>();
        foreach (var raw in TextFiles.Lines(text))
        {
            var line = raw.Trim();
            var times = new List<long>();
            for (var m = LrcStamp.Match(line); m.Success; m = LrcStamp.Match(line))
            {
                times.Add((long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60 + long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)) * 1000 + Fraction(m.Groups[3].Value, 10));
                line = line[m.Length..].TrimStart();
            }
            if (times.Count == 0)
            {
                if (LrcOffset.Match(line) is { Success: true } o) offset = long.Parse(o.Groups[1].Value, CultureInfo.InvariantCulture);
                continue;
            }
            var t = Clean(LrcWord.Replace(line, ""));
            foreach (var at in times) marks.Add((at, t));
        }
        marks = marks.OrderBy(m => m.At).ToList();
        var cues = new List<Cue>();
        for (int i = 0; i < marks.Count; i++)
        {
            if (marks[i].Text.Length == 0) continue;
            // a positive offset shows the lyrics earlier; lines sharing a timestamp all last until the next later one
            long start = Math.Max(0, marks[i].At - offset);
            int next = i + 1;
            while (next < marks.Count && marks[next].At <= marks[i].At) next++;
            long end = next < marks.Count ? Math.Max(0, marks[next].At - offset) : start + 5000;
            cues.Add(new Cue(start, Math.Max(start, end), marks[i].Text));
        }
        return cues;
    }

    /// <summary>"1:02:03,456", "02:03.456", "0:02:03.45" → ms; <paramref name="unit"/> is what the fraction counts (1 ms, 10 for centiseconds).</summary>
    static long ParseTime(string s, int unit = 1)
    {
        s = s.Trim();
        long ms = 0;
        int dot = s.IndexOfAny(['.', ',']);
        if (dot >= 0) { ms = Fraction(s[(dot + 1)..], unit); s = s[..dot]; }
        long t = 0;
        foreach (var p in s.Split(':')) t = t * 60 + (long.TryParse(p.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0);
        return t * 1000 + ms;
    }

    /// <summary>
    /// The fraction counts the format's unit, the way ffmpeg, VLC and libass read it: an unpadded SRT ",5" is 5 ms and
    /// ASS ".5" is 5 cs, so converted files show cues exactly when the originals did. Three digits are always ms;
    /// longer fractions are cut to ms.
    /// </summary>
    static long Fraction(string digits, int unit)
    {
        digits = digits.Trim();
        if (digits.Length == 0 || !long.TryParse(digits.Length > 3 ? digits[..3] : digits, NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return 0;
        return digits.Length >= 3 ? v : v * unit;
    }

    // ── markup ──────────────────────────────────────────────────────────────

    // formatting is exactly <i> <b> <u> (WebVTT may add classes: <i.loud>); known tags that carry no text of their own
    // are dropped (SRT's <font>, WebVTT's <c.yellow> <v Bob> <lang en>, ruby, karaoke times); anything else in angle
    // brackets ("a<b and c>d", "<Narrator>") is text
    static readonly Regex Tags = new(
        @"<(?<close>/?)(?<fmt>[ibu])(?:\.[\w.-]+)?>" +
        @"|</?(?<known>font|c|v|lang|ruby|rt|rb|s|span|br)(?=[\s./>])[^>]*>" +
        @"|<(?:\d+:)+\d+\.\d+>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Basic = new(@"</?[ibu]>", RegexOptions.Compiled);
    static readonly Regex Empty = new(@"<([ibu])>(\s*)</\1>|</([ibu])><\3>", RegexOptions.Compiled);
    static readonly Regex OpenAtEol = new(@"((?:<[ibu]>)+)([ \t\u00A0]*\n)", RegexOptions.Compiled);
    static readonly Regex CloseAtBol = new(@"(\n[ \t\u00A0]*)((?:</[ibu]>)+)", RegexOptions.Compiled);

    /// <summary>
    /// Our markup: &lt;i&gt; &lt;b&gt; &lt;u&gt; made lower-case, balanced and nested; known non-text tags
    /// (&lt;font&gt;, &lt;c.yellow&gt;, &lt;v Bob&gt;, &lt;00:01.000&gt;) dropped, other angle brackets kept as text;
    /// lines trimmed and blank ones dropped.
    /// </summary>
    static string Clean(string s)
    {
        var sb = new StringBuilder();
        var open = new List<char>();
        int last = 0;
        foreach (Match m in Tags.Matches(s))
        {
            sb.Append(s, last, m.Index - last);
            last = m.Index + m.Length;
            if (m.Groups["known"].Value.Equals("br", StringComparison.OrdinalIgnoreCase)) sb.Append('\n');
            if (!m.Groups["fmt"].Success) continue;
            char c = char.ToLowerInvariant(m.Groups["fmt"].Value[0]);
            bool closing = m.Groups["close"].Length > 0;
            if (!closing && !open.Contains(c)) { open.Add(c); sb.Append('<').Append(c).Append('>'); }
            else if (closing && open.LastIndexOf(c) is var k and >= 0)
            {
                // close what was opened inside it, then reopen that, so the result always nests
                for (int j = open.Count - 1; j >= k; j--) sb.Append("</").Append(open[j]).Append('>');
                open.RemoveAt(k);
                for (int j = k; j < open.Count; j++) sb.Append('<').Append(open[j]).Append('>');
            }
        }
        sb.Append(s, last, s.Length - last);
        for (int j = open.Count - 1; j >= 0; j--) sb.Append("</").Append(open[j]).Append('>');

        // "<i></i>" and "</i><i>" say nothing
        var t = sb.ToString();
        for (string prev = ""; prev != t;) { prev = t; t = Empty.Replace(t, "$2"); }
        // "<i>\nText</i>" → "\n<i>Text</i>", so a line of bare tags can be dropped
        t = CloseAtBol.Replace(OpenAtEol.Replace(t, "$2$1"), "$2$1");
        return string.Join('\n', t.Split('\n').Select(l => l.Trim()).Where(l => Basic.Replace(l, "").Trim().Length > 0));
    }

    // ── writing ─────────────────────────────────────────────────────────────

    public static string Write(IReadOnlyList<Cue> cues, string ext) => ext switch
    {
        "vtt" => WriteVtt(cues),
        "ass" => WriteAss(cues),
        "txt" => WriteText(cues),
        _ => WriteSrt(cues),
    };

    static string Ts(long ms, char sep) =>
        string.Create(CultureInfo.InvariantCulture, $"{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00}{sep}{ms % 1000:000}");

    static string WriteSrt(IReadOnlyList<Cue> cues)
    {
        var sb = new StringBuilder();
        for (int k = 0; k < cues.Count; k++)
        {
            var c = cues[k];
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"{k + 1}")).Append("\r\n")
              .Append(Ts(c.Start, ',')).Append(" --> ").Append(Ts(c.End, ',')).Append("\r\n")
              .Append(c.Top ? @"{\an8}" : "").Append(c.Text.Replace("\n", "\r\n")).Append("\r\n\r\n");
        }
        return sb.ToString();
    }

    static string WriteVtt(IReadOnlyList<Cue> cues)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var c in cues)
        {
            sb.Append(Ts(c.Start, '.')).Append(" --> ").Append(Ts(c.End, '.')).Append(c.Top ? " line:0" : "").Append('\n');
            // only our own tags stay markup; a literal < or & in the text has to be escaped
            int last = 0;
            foreach (Match m in Basic.Matches(c.Text))
            {
                sb.Append(Escape(c.Text[last..m.Index])).Append(m.Value);
                last = m.Index + m.Length;
            }
            sb.Append(Escape(c.Text[last..])).Append("\n\n");
        }
        return sb.ToString();
    }

    static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // the style ffmpeg and players give SRT when they render it through libass
    static readonly string AssHeader = string.Join("\r\n",
        "[Script Info]",
        "; Converted by Convertino",
        "ScriptType: v4.00+",
        "WrapStyle: 0",
        "ScaledBorderAndShadow: yes",
        "PlayResX: 384",
        "PlayResY: 288",
        "",
        "[V4+ Styles]",
        "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding",
        "Style: Default,Arial,16,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,1,0,2,10,10,10,1",
        "",
        "[Events]",
        "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text",
        "");

    /// <summary>ASS counts centiseconds: H:MM:SS.cc, rounded to the nearest.</summary>
    static string AssTs(long ms)
    {
        long cs = (ms + 5) / 10;
        return string.Create(CultureInfo.InvariantCulture, $"{cs / 360_000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}");
    }

    // a literal backslash before N, n, h or a brace would turn into a line break, a space or an escape: a word joiner
    // (invisible) after it keeps it a backslash; braces themselves use libass's \{ \}
    static readonly Regex AssBackslash = new(@"\\(?=[Nnh{}])", RegexOptions.Compiled);

    static string WriteAss(IReadOnlyList<Cue> cues)
    {
        var sb = new StringBuilder(AssHeader);
        foreach (var c in cues)
        {
            var text = AssBackslash.Replace(c.Text, "\\⁠").Replace("{", @"\{").Replace("}", @"\}")
                .Replace("<i>", @"{\i1}").Replace("</i>", @"{\i0}").Replace("<b>", @"{\b1}").Replace("</b>", @"{\b0}")
                .Replace("<u>", @"{\u1}").Replace("</u>", @"{\u0}").Replace("\n", @"\N");
            sb.Append("Dialogue: 0,").Append(AssTs(c.Start)).Append(',').Append(AssTs(c.End)).Append(",Default,,0,0,0,,")
              .Append(c.Top ? @"{\an8}" : "").Append(text).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Just the words, one line per subtitle line, without repeats.</summary>
    static string WriteText(IReadOnlyList<Cue> cues)
    {
        var lines = new List<string>();
        foreach (var c in cues)
            foreach (var l in Basic.Replace(c.Text, "").Split('\n'))
            {
                var t = l.Trim();
                if (t.Length > 0 && (lines.Count == 0 || lines[^1] != t)) lines.Add(t);
            }
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
