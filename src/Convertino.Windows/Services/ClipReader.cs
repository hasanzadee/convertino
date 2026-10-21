using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Convertino.Services;

/// <summary>The Windows clipboard as <see cref="ClipContent"/>: CF_HDROP files, then a picture (PNG, DIB, DIBV5, bitmap), then text.</summary>
public static class ClipReader
{
    /// <summary>What the clipboard held, before any decoding: taken on the UI thread, turned into <see cref="ClipContent"/> on a worker.</summary>
    sealed record Raw(IReadOnlyList<string>? Files, byte[]? Png, byte[]? Dib, BitmapSource? Bitmap, string? Text, bool Secret);

    /// <summary>Read the system clipboard: the data on the UI thread (retried while another app holds it), the decoding off it.</summary>
    public static async Task<ClipContent?> ReadClipboard()
    {
        var raw = Application.Current.Dispatcher.Invoke(() =>
        {
            for (int i = 0; ; i++)
            {
                try { return Grab(Clipboard.GetDataObject()); }
                catch (Exception ex) when (i < 9 && ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException)
                {
                    Log.Info("clipboard busy: " + ex.Message);
                    Thread.Sleep(40);
                }
            }
        });
        return raw == null ? null : await Task.Run(() => Decode(raw));
    }

    /// <summary>What a data object holds (the self-test feeds it synthetic ones).</summary>
    public static ClipContent? Read(IDataObject? d) => Grab(d) is { } raw ? Decode(raw) : null;

    /// <summary>
    /// Files win; then a picture, unless the copy came from a document or a sheet (Office puts a picture of the cells
    /// beside their text — the text is what was meant); then text. Password managers mark what they copy
    /// (ExcludeClipboardContentFromMonitorProcessing, CanIncludeInClipboardHistory = 0, Clipboard Viewer Ignore): secret.
    /// </summary>
    static Raw? Grab(IDataObject? d)
    {
        if (d == null) return null;
        try
        {
            if (d.GetDataPresent(DataFormats.FileDrop) && d.GetData(DataFormats.FileDrop) is string[] files)
            {
                var real = files.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
                if (real.Count > 0) return new Raw(real, null, null, null, null, false);
            }
        }
        catch (Exception ex) { Log.Error("clipboard files", ex); }

        string? text = null;
        try { if (d.GetDataPresent(DataFormats.UnicodeText)) text = d.GetData(DataFormats.UnicodeText) as string; }
        catch (Exception ex) { Log.Error("clipboard text", ex); }
        bool hasText = !string.IsNullOrWhiteSpace(text);
        bool secret = Has(d, "ExcludeClipboardContentFromMonitorProcessing") || Has(d, "Clipboard Viewer Ignore")
            || Bytes(d, "CanIncludeInClipboardHistory") is { Length: >= 4 } allow && BitConverter.ToInt32(allow, 0) == 0;
        bool document = hasText && (Has(d, "Csv") || Has(d, DataFormats.Rtf) || Has(d, "XML Spreadsheet") || Has(d, "Link Source"));

        if (!document && !secret)
        {
            foreach (var f in new[] { "PNG", "image/png" })
                if (Bytes(d, f) is { Length: > 8 } png) return new Raw(null, png, null, null, null, false);
            // CF_DIB, then CF_DIBV5 (id 17: WPF maps its name to that id only once asked by number); Windows makes each from the other, and from CF_BITMAP
            foreach (var f in new[] { DataFormats.Dib, DataFormats.GetDataFormat(17).Name })
                if (Bytes(d, f) is { Length: > 40 } dib) return new Raw(null, null, dib, null, null, false);
            try
            {
                if (d.GetDataPresent(DataFormats.Bitmap) && d.GetData(DataFormats.Bitmap) is BitmapSource bmp)
                {
                    var frozen = bmp.CanFreeze ? bmp : new WriteableBitmap(bmp);
                    frozen.Freeze();
                    return new Raw(null, null, null, frozen, null, false);
                }
            }
            catch (Exception ex) { Log.Error("clipboard bitmap", ex); }
        }
        return hasText ? new Raw(null, null, null, null, text, secret) : null;
    }

    /// <summary>The slow part, off the UI thread: a 3-monitor PrtScn is ~100 MB of DIB to turn into PNG.</summary>
    static ClipContent? Decode(Raw r)
    {
        if (r.Files != null) return new ClipContent.Files(r.Files);
        if (r.Png != null) return new ClipContent.Image(r.Png);
        if (r.Dib != null)
            try { return new ClipContent.Image(Captures.DibToPng(r.Dib)); }
            catch (Exception ex) { Log.Error("clipboard DIB", ex); }
        if (r.Bitmap != null)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(r.Bitmap));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return new ClipContent.Image(ms.ToArray());
        }
        return r.Text != null ? new ClipContent.Text(r.Text, r.Secret) : null;
    }

    static bool Has(IDataObject d, string format)
    {
        try { return d.GetDataPresent(format); } catch { return false; }
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
