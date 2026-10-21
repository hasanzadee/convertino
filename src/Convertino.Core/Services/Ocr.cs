using System.Diagnostics;
using System.Globalization;
using System.Text;
using ImageMagick;

namespace Convertino.Services;

public sealed record OcrResult(string Text, List<RectD> Lines, List<RectD> Words);

public static class Ocr
{
    /// <summary>OCR an image file of any format; small screenshots are upscaled first (reads far better).</summary>
    public static async Task<string> RecognizeFile(string path, int maxDim = 10000)
    {
        byte[] png;
        using (var img = Images.Load(path))
        {
            if (Math.Max(img.Width, img.Height) > maxDim) img.Resize(new MagickGeometry((uint)maxDim, (uint)maxDim));
            if (Math.Max(img.Width, img.Height) < 900) img.Resize(new Percentage(200));
            png = img.ToByteArray(MagickFormat.Png);
        }
        return (await Platform.Current.Ocr(png)).Text;
    }

    public static string Preview(string text)
    {
        var t = string.Join(" ", text.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (t.Length == 0) return L.T("No text found");
        return t.Length > 70 ? t[..70] + "…" : t;
    }

    /// <summary>
    /// Prefer the result whose script matches the engine (Cyrillic for ru/uk/…, Latin otherwise).
    /// Used when several single-language engines run on the same image.
    /// </summary>
    public static double Score(string text, string tag)
    {
        int letters = 0, cyr = 0, lat = 0;
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch)) continue;
            letters++;
            if (ch is >= 'Ѐ' and <= 'ӿ') cyr++;
            else if (ch <= 'ɏ') lat++;
        }
        if (letters == 0) return 0;
        bool cyrEngine = tag.StartsWith("ru") || tag.StartsWith("uk") || tag.StartsWith("be") || tag.StartsWith("bg") || tag.StartsWith("sr");
        return letters * 0.2 + (cyrEngine ? cyr : lat);
    }
}

/// <summary>Tesseract CLI — the OCR engine on Linux, and the Windows fallback when no OCR language pack exists.</summary>
public static class Tesseract
{
    static string? _exe;
    static string? _langs;
    static bool _searched;

    public static bool Available { get { Find(); return _exe != null; } }

    static void Find()
    {
        if (_searched) return;
        _searched = true;
        _exe = Paths.Which("tesseract");
        if (_exe == null && OperatingSystem.IsWindows())
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tesseract.exe");
            if (File.Exists(p)) _exe = p;
        }
        if (_exe == null) return;
        try
        {
            var (code, outp, _) = Run(_exe, "--list-langs", null, 10000);
            var langs = outp.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => !l.Contains(' ') && l != "osd" && l != "equ").ToList();
            // keep it to languages people actually type; all installed ones would slow recognition down a lot
            var wanted = new[] { "eng", "rus", "ukr", "deu", "fra", "spa", "ita", "por", "tur", "aze", "pol", "nld" };
            var pick = wanted.Where(langs.Contains).Take(3).ToList();
            if (pick.Count == 0 && langs.Count > 0) pick.Add(langs[0]);
            _langs = string.Join('+', pick);
        }
        catch { _langs = "eng"; }
    }

    public static async Task<OcrResult> Recognize(byte[] image)
    {
        Find();
        if (_exe == null)
            throw new InvalidOperationException(OperatingSystem.IsWindows()
                ? L.En("No OCR available — add a language in Settings → Time & language, or install Tesseract")
                : OperatingSystem.IsMacOS() ? L.En("Tesseract is not installed ({0})", "brew install tesseract")
                : L.En("Tesseract is not installed ({0})", "sudo apt install tesseract-ocr tesseract-ocr-eng"));
        var tmp = Path.Combine(Paths.Temp, $"ocr-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(tmp, image);
        try
        {
            var lang = string.IsNullOrEmpty(_langs) ? "" : $"-l {_langs} ";
            var (code, tsv, err) = await Task.Run(() => Run(_exe, $"\"{tmp}\" stdout {lang}--psm 3 tsv", null, 120000));
            if (code != 0) throw new IOException(L.En("tesseract failed: {0}", err.Trim()));
            return ParseTsv(tsv);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    static OcrResult ParseTsv(string tsv)
    {
        var words = new List<(int block, int par, int line, RectD r, string text)>();
        foreach (var raw in tsv.Split('\n'))
        {
            var c = raw.TrimEnd('\r').Split('\t');
            if (c.Length < 12 || c[0] != "5") continue;
            var text = c[11].Trim();
            if (text.Length == 0) continue;
            double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
            words.Add((int.Parse(c[2]), int.Parse(c[3]), int.Parse(c[4]), new RectD(D(c[6]), D(c[7]), D(c[8]), D(c[9])), text));
        }
        var sb = new StringBuilder();
        var lines = new List<RectD>();
        foreach (var g in words.GroupBy(w => (w.block, w.par, w.line)))
        {
            sb.AppendLine(string.Join(' ', g.Select(w => w.text)));
            double x0 = g.Min(w => w.r.X), y0 = g.Min(w => w.r.Y);
            double x1 = g.Max(w => w.r.X + w.r.Width), y1 = g.Max(w => w.r.Y + w.r.Height);
            lines.Add(new RectD(x0, y0, x1 - x0, y1 - y0));
        }
        return new OcrResult(sb.ToString().Trim(), lines, words.Select(w => w.r).ToList());
    }

    internal static (int code, string stdout, string stderr) Run(string exe, string args, string? cwd, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        if (cwd != null) psi.WorkingDirectory = cwd;
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } throw new TimeoutException(L.En("{0} timed out", Path.GetFileName(exe))); }
        return (p.ExitCode, o.Result, e.Result);
    }
}
