using System.Text;
using System.Xml.Linq;
using ImageMagick;
using QRCoder;
using ZXing;

namespace Convertino.Services;

/// <summary>QR codes both ways: read one from an image (ZXing.Net), make one from a text or link file (QRCoder).</summary>
public static class Qr
{
    // ---------- read ----------

    public static async Task Read(Job job)
    {
        var texts = new List<string>();
        for (int i = 0; i < job.Inputs.Count; i++)
        {
            job.Token.ThrowIfCancellationRequested();
            job.Report(i, job.Inputs.Count);
            var f = job.Inputs[i];
            texts.AddRange(await Task.Run(() => Decode(f), job.Token));
        }
        texts = texts.Distinct().ToList();
        if (texts.Count == 0) { job.Title = L.T("No QR code found"); job.Summary = L.T("Try a sharper or tighter screenshot"); return; }
        var text = string.Join(Environment.NewLine, texts);
        Platform.Current.SetClipboardText(text);
        job.Title = texts.Count == 1 ? L.T("QR text copied") : L.N(texts.Count, "{0} QR code copied", "{0} QR codes copied");
        job.Summary = Ocr.Preview(text);
    }

    /// <summary>Every code found in the image; tried at a few sizes because tiny or huge codes decode better rescaled.</summary>
    public static List<string> Decode(string path)
    {
        using var img = Images.Load(path);
        // transparent PNGs: dark modules on nothing would read as all-black
        img.BackgroundColor = MagickColors.White;
        img.Alpha(AlphaAction.Remove);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options =
            {
                TryHarder = true,
                TryInverted = true,
                PossibleFormats = [BarcodeFormat.QR_CODE, BarcodeFormat.DATA_MATRIX, BarcodeFormat.AZTEC],
            },
        };
        uint side = Math.Max(img.Width, img.Height);
        var sizes = new List<uint> { Math.Min(side, 2000) };
        if (side > 1000) sizes.Add(1000);
        if (side > 600) sizes.Add(600);
        if (side < 500) sizes.Add(side * 2);   // tiny crops: bigger modules decode better
        foreach (var target in sizes)
        {
            using var c = (MagickImage)img.Clone();
            if (target != side) c.Resize(new MagickGeometry(target, target));
            var (px, w, h) = Images.ToBgra(c);
            var found = reader.DecodeMultiple(new RGBLuminanceSource(px, w, h, RGBLuminanceSource.BitmapFormat.BGRA32));
            if (found is { Length: > 0 })
                return found.Select(r => r.Text).Where(t => !string.IsNullOrEmpty(t)).ToList();
        }
        return [];
    }

    // ---------- make ----------

    public static bool CanMake(string path) =>
        File.Exists(path) && Kinds.Ext(path) is "txt" or "url" or "webloc" && new FileInfo(path).Length is > 0 and < 64_000;

    public static async Task Make(Job job)
    {
        foreach (var src in job.Inputs)
        {
            job.Token.ThrowIfCancellationRequested();
            var text = Content(src);
            if (text.Length == 0) throw new InvalidOperationException(L.En("The file is empty"));
            var dst = Paths.Beside(src, "png", "(QR)");
            await File.WriteAllBytesAsync(dst, Png(text), job.Token);
            job.Outputs.Add(dst);
            job.Summary = Ocr.Preview(text);
        }
    }

    /// <summary>A crisp black-on-white QR PNG about 1000 px wide, with the standard quiet zone.</summary>
    public static byte[] Png(string text)
    {
        bool ascii = text.All(ch => ch < 128);
        using var gen = new QRCodeGenerator();
        QRCodeData data;
        try { data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M, forceUtf8: !ascii); }
        catch (QRCoder.Exceptions.DataTooLongException)
        {
            try { data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.L, forceUtf8: !ascii); }
            catch (QRCoder.Exceptions.DataTooLongException)
            {
                throw new InvalidOperationException(L.En("Too long for a QR code ({0:N0} bytes; about 2,900 fit)", Encoding.UTF8.GetByteCount(text)));
            }
        }
        using (data)
        {
            int modules = data.ModuleMatrix.Count;
            int ppm = Math.Clamp(1000 / modules, 4, 24);
            return new PngByteQRCode(data).GetGraphic(ppm);
        }
    }

    /// <summary>What goes into the code: the link of a .url / .webloc shortcut, or the text of a .txt.</summary>
    public static string Content(string path)
    {
        switch (Kinds.Ext(path))
        {
            case "url":
                foreach (var line in File.ReadLines(path))
                    if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) return line[4..].Trim();
                throw new InvalidOperationException(L.En("No URL= line in this shortcut"));
            case "webloc":
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length > 8 && Encoding.ASCII.GetString(bytes, 0, 8) == "bplist00")
                {
                    if (BplistStrings(bytes).FirstOrDefault(s => s.Contains("://")) is { } link) return link.Trim();
                }
                else
                {
                    try
                    {
                        // <dict><key>URL</key><string>https://…</string></dict>
                        var keys = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Descendants("key").ToList();
                        if (keys.FirstOrDefault(k => k.Value == "URL")?.ElementsAfterSelf().FirstOrDefault() is { } s && s.Value.Trim() is { Length: > 0 } url)
                            return url;
                    }
                    catch (System.Xml.XmlException) { }
                }
                throw new InvalidOperationException(L.En("No link in this .webloc"));
            }
            default:
                return File.ReadAllText(path).Trim();
        }
    }

    /// <summary>The strings of a binary property list (newer Finder .webloc files).</summary>
    static List<string> BplistStrings(byte[] b)
    {
        var list = new List<string>();
        try
        {
            static long BE(byte[] b, int at, int n) { long v = 0; for (int i = 0; i < n; i++) v = v << 8 | b[at + i]; return v; }
            int t = b.Length - 32;
            int offSize = b[t + 6];
            long count = BE(b, t + 8, 8), table = BE(b, t + 24, 8);
            if (offSize is < 1 or > 8 || count < 0 || count > b.Length || table < 8 || table >= t) return list;
            for (long i = 0; i < count && table + (i + 1) * offSize <= t; i++)
            {
                int o = (int)BE(b, (int)(table + i * offSize), offSize);
                int type = b[o] >> 4, len = b[o] & 0xF, start = o + 1;
                if (type is not (5 or 6)) continue;   // 5 = ASCII string, 6 = UTF-16 string
                if (len == 0xF)
                {
                    int n = 1 << (b[start] & 0xF);
                    len = (int)BE(b, start + 1, n);
                    start += 1 + n;
                }
                int size = type == 6 ? len * 2 : len;
                if (start + size > b.Length) continue;
                list.Add(type == 5 ? Encoding.ASCII.GetString(b, start, size) : Encoding.BigEndianUnicode.GetString(b, start, size));
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException) { }   // malformed file: what was read so far
        return list;
    }
}
