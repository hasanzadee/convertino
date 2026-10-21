using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Convertino.Services;

/// <summary>The Windows clipboard as <see cref="ClipContent"/>: CF_HDROP files, then a picture (PNG, DIB, DIBV5, bitmap), then text.</summary>
public static class ClipReader
{
    /// <summary>Read the system clipboard (on the UI thread, retried while another app holds it).</summary>
    public static ClipContent? ReadClipboard() => Application.Current.Dispatcher.Invoke(() =>
    {
        for (int i = 0; ; i++)
        {
            try { return Read(Clipboard.GetDataObject()); }
            catch (Exception ex) when (i < 9 && ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException)
            {
                Log.Info("clipboard busy: " + ex.Message);
                Thread.Sleep(40);
            }
        }
    });

    /// <summary>
    /// What a data object holds. Files win; then a picture, unless the copy came from a document or a sheet (Office puts a
    /// picture of the cells beside their text — the text is what was meant); then text.
    /// </summary>
    public static ClipContent? Read(IDataObject? d)
    {
        if (d == null) return null;
        try
        {
            if (d.GetDataPresent(DataFormats.FileDrop) && d.GetData(DataFormats.FileDrop) is string[] files)
            {
                var real = files.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
                if (real.Count > 0) return new ClipContent.Files(real);
            }
        }
        catch (Exception ex) { Log.Error("clipboard files", ex); }

        string? text = null;
        try { if (d.GetDataPresent(DataFormats.UnicodeText)) text = d.GetData(DataFormats.UnicodeText) as string; }
        catch (Exception ex) { Log.Error("clipboard text", ex); }
        bool hasText = !string.IsNullOrWhiteSpace(text);
        bool document = hasText && (Has(d, "Csv") || Has(d, DataFormats.Rtf) || Has(d, "XML Spreadsheet") || Has(d, "Link Source"));

        if (!document && Picture(d) is { } png) return new ClipContent.Image(png);
        return hasText ? new ClipContent.Text(text!) : null;
    }

    static bool Has(IDataObject d, string format)
    {
        try { return d.GetDataPresent(format); } catch { return false; }
    }

    /// <summary>The picture as PNG: "PNG" as browsers, Office and Snipping Tool put it (keeps transparency), else a DIB.</summary>
    static byte[]? Picture(IDataObject d)
    {
        foreach (var f in new[] { "PNG", "image/png" })
            if (Bytes(d, f) is { Length: > 8 } png) return png;
        // CF_DIB, then CF_DIBV5 ("Format17"); Windows makes each from the other, and from CF_BITMAP
        foreach (var f in new[] { DataFormats.Dib, "Format17" })
            if (Bytes(d, f) is { Length: > 40 } dib)
                try { return Captures.DibToPng(dib); }
                catch (Exception ex) { Log.Error($"clipboard {f}", ex); }
        try
        {
            if (d.GetDataPresent(DataFormats.Bitmap) && d.GetData(DataFormats.Bitmap) is BitmapSource bmp)
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using var ms = new MemoryStream();
                enc.Save(ms);
                return ms.ToArray();
            }
        }
        catch (Exception ex) { Log.Error("clipboard bitmap", ex); }
        return null;
    }

    static byte[]? Bytes(IDataObject d, string format)
    {
        try
        {
            if (!d.GetDataPresent(format)) return null;
            return d.GetData(format) switch
            {
                MemoryStream ms => ms.ToArray(),
                Stream s => ReadAll(s),
                byte[] b => b,
                _ => null,
            };
        }
        catch (Exception ex) { Log.Error($"clipboard {format}", ex); return null; }
    }

    static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
