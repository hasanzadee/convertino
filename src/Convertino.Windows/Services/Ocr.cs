using System.Windows;
using System.Windows.Media.Imaging;
using ImageMagick;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Convertino.Services;

/// <summary>Built-in Windows OCR — offline, fast, uses whatever language packs are installed. Falls back to Tesseract.</summary>
public static class WinOcr
{
    public static async Task<OcrResult> Recognize(byte[] imageBytes)
    {
        var engines = Engines();
        if (engines.Count == 0) return await Tesseract.Recognize(imageBytes);
        imageBytes = FitForEngine(imageBytes, out double k);
        var bmp = await Decode(imageBytes);

        // one engine per installed language, run side by side; keep the one whose script fits best
        var results = await Task.WhenAll(engines.Select(async e => (e.tag, r: await e.engine.RecognizeAsync(bmp))));
        OcrResult? best = null;
        double bestScore = -1;
        foreach (var (tag, r) in results)
        {
            var text = string.Join("\n", r.Lines.Select(l => l.Text));
            double score = Ocr.Score(text, tag);
            if (score <= bestScore) continue;
            bestScore = score;
            best = new OcrResult(text,
                r.Lines.Select(l => Scale(Union(l.Words.Select(w => w.BoundingRect)), k)).ToList(),
                r.Lines.SelectMany(l => l.Words).Select(w => Scale(ToRect(w.BoundingRect), k)).ToList());
        }
        return best!;
    }

    static byte[] FitForEngine(byte[] bytes, out double k)
    {
        k = 1;
        using var img = new MagickImage(bytes);
        uint max = OcrEngine.MaxImageDimension;
        if (Math.Max(img.Width, img.Height) <= max) return bytes;
        k = Math.Max(img.Width, img.Height) / (double)max;
        img.Resize(new MagickGeometry(max, max));
        return img.ToByteArray(MagickFormat.Png);
    }

    static List<(string tag, OcrEngine engine)> _engines = [];

    static List<(string tag, OcrEngine engine)> Engines()
    {
        if (_engines.Count > 0) return _engines;
        var list = new List<(string, OcrEngine)>();
        foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
        {
            var e = OcrEngine.TryCreateFromLanguage(new Language(lang.LanguageTag));
            if (e != null) list.Add((lang.LanguageTag, e));
        }
        return _engines = list;
    }

    static async Task<SoftwareBitmap> Decode(byte[] bytes)
    {
        var ras = new InMemoryRandomAccessStream();
        using (var w = new DataWriter(ras))
        {
            w.WriteBytes(bytes);
            await w.StoreAsync();
            await w.FlushAsync();
            w.DetachStream();
        }
        ras.Seek(0);
        var dec = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
        return await dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    static RectD ToRect(Windows.Foundation.Rect r) => new(r.X, r.Y, r.Width, r.Height);
    static RectD Scale(RectD r, double k) => new(r.X * k, r.Y * k, r.Width * k, r.Height * k);

    static RectD Union(IEnumerable<Windows.Foundation.Rect> rects)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var r in rects)
        {
            x0 = Math.Min(x0, r.X); y0 = Math.Min(y0, r.Y);
            x1 = Math.Max(x1, r.X + r.Width); y1 = Math.Max(y1, r.Y + r.Height);
        }
        return x0 == double.MaxValue ? default : new RectD(x0, y0, x1 - x0, y1 - y0);
    }
}

public static class Clip
{
    static void Retry(Action a)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            for (int i = 0; ; i++)
            {
                try { a(); return; }
                // another app may hold the clipboard for a moment; a locked screen keeps it shut, so say so
                catch (Exception ex) when (i < 9) { Log.Info("clipboard busy: " + ex.Message); Thread.Sleep(40); }
            }
        });
    }

    public static void SetText(string text) => Retry(() => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true));

    public static void SetImage(BitmapSource bmp) => Retry(() => Clipboard.SetImage(bmp));

    /// <summary>
    /// The picture in two shapes: "PNG" keeps transparency for apps that read it (Office, browsers, Telegram, Discord …),
    /// a plain bitmap on white serves the rest, since most of them ignore a bitmap's alpha and would show black.
    /// Both are built on the caller's (worker) thread; only handing them over runs on the UI thread, with the retries.
    /// </summary>
    public static void SetPng(byte[] png)
    {
        var frame = System.Windows.Media.Imaging.BitmapFrame.Create(new System.IO.MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var (px, w, h) = Wpf.Pixels(frame);
        for (int i = 0; i < px.Length; i += 4)
        {
            int a = px[i + 3];
            if (a == 255) continue;
            for (int c = 0; c < 3; c++) px[i + c] = (byte)((px[i + c] * a + 255 * (255 - a) + 127) / 255);
            px[i + 3] = 255;
        }
        var opaque = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, px, w * 4);
        opaque.Freeze();
        var data = new DataObject();
        data.SetData("PNG", new System.IO.MemoryStream(png), false);
        data.SetImage(opaque);
        Retry(() => Clipboard.SetDataObject(data, true));
    }

    public static void SetFiles(IEnumerable<string> files)
    {
        var col = new System.Collections.Specialized.StringCollection();
        col.AddRange(files.ToArray());
        Retry(() => Clipboard.SetFileDropList(col));
    }
}

/// <summary>WPF ⇄ Magick pixel bridges.</summary>
public static class Wpf
{
    public static BitmapSource ToBitmapSource(MagickImage img)
    {
        var (px, w, h) = Images.ToBgra(img);
        var bmp = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    public static (byte[] bgra, int w, int h) Pixels(BitmapSource src)
    {
        var conv = src.Format == System.Windows.Media.PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var px = new byte[w * h * 4];
        conv.CopyPixels(px, w * 4, 0);
        return (px, w, h);
    }

    public static MagickImage FromBitmapSource(BitmapSource src)
    {
        var (px, w, h) = Pixels(src);
        return Images.FromBgra(px, w, h);
    }

    public static Point P(ScreenPoint p) => new(p.X, p.Y);
}
