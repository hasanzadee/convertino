using System.Runtime.InteropServices;
using Convertino.Services;

namespace Convertino.Native;

/// <summary>Pictures on the general pasteboard (NSPasteboard), which Avalonia's clipboard can't hold.</summary>
public static class Pasteboard
{
    /// <summary>
    /// One pasteboard item with the PNG (keeps transparency: Messages, Slack, Notes, browsers …) and a TIFF made
    /// from it by NSImage for older apps. The data is copied to the pasteboard server, so it outlives a CLI run.
    /// </summary>
    public static void SetImage(byte[] png)
    {
        if (!ObjC.Load("AppKit")) throw new InvalidOperationException("AppKit isn't available");
        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            var pb = ObjC.Send(ObjC.Class("NSPasteboard"), "generalPasteboard");
            if (pb == IntPtr.Zero) throw new InvalidOperationException("No pasteboard");
            ObjC.Send(pb, "clearContents");
            var data = ObjC.NSData(png);
            if (!Set(pb, data, "public.png")) throw new InvalidOperationException("The pasteboard refused the image");
            var image = ObjC.Send(ObjC.Send(ObjC.Class("NSImage"), "alloc"), "initWithData:", data);
            if (image == IntPtr.Zero) return;
            try
            {
                var tiff = ObjC.Send(image, "TIFFRepresentation");
                if (tiff != IntPtr.Zero) Set(pb, tiff, "public.tiff");
            }
            finally { ObjC.Release(image); }
        }
        finally { ObjC.objc_autoreleasePoolPop(pool); }
    }

    /// <summary>
    /// What the general pasteboard holds: files copied in Finder (one item each, with a file URL — often a
    /// file-reference URL, resolved through NSURL), a picture (PNG, TIFF, JPEG → PNG), or text. A picture that comes with
    /// rich text or table cells (Pages, Numbers, Excel) loses to the text: the text is what was meant.
    /// </summary>
    public static ClipContent? Read()
    {
        if (!ObjC.Load("AppKit")) throw new InvalidOperationException("AppKit isn't available");
        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            var pb = ObjC.Send(ObjC.Class("NSPasteboard"), "generalPasteboard");
            if (pb == IntPtr.Zero) return null;
            var typesArray = ObjC.Send(pb, "types");
            var types = new List<string>();
            for (nuint i = 0; i < ObjC.Count(typesArray); i++)
                if (ObjC.Str(ObjC.At(typesArray, i)) is { } t) types.Add(t);

            var files = new List<string>();
            var items = ObjC.Send(pb, "pasteboardItems");
            for (nuint i = 0; i < ObjC.Count(items); i++)
            {
                var url = ObjC.Send(ObjC.At(items, i), "stringForType:", ObjC.NSString("public.file-url"));
                if (url == IntPtr.Zero) continue;
                var nsurl = ObjC.Send(ObjC.Class("NSURL"), "URLWithString:", url);
                var real = nsurl == IntPtr.Zero ? IntPtr.Zero : ObjC.Send(nsurl, "filePathURL");
                if (real != IntPtr.Zero && ObjC.Str(ObjC.Send(real, "path")) is { Length: > 0 } path && (File.Exists(path) || Directory.Exists(path)))
                    files.Add(path);
            }
            if (files.Count > 0) return new ClipContent.Files(files);

            var text = ObjC.Str(ObjC.Send(pb, "stringForType:", ObjC.NSString("public.utf8-plain-text")));
            bool hasText = !string.IsNullOrWhiteSpace(text);
            bool document = hasText && types.Any(t => t is "public.rtf" or "com.apple.flat-rtfd" or "public.utf8-tab-separated-values-text" || t.StartsWith("com.microsoft.", StringComparison.Ordinal));
            if (!document)
                foreach (var t in new[] { "public.png", "public.tiff", "public.jpeg" })
                    if (types.Contains(t) && Bytes(ObjC.Send(pb, "dataForType:", ObjC.NSString(t))) is { Length: > 8 } b)
                        return new ClipContent.Image(t == "public.png" ? b : Captures.ToPng(b));
            return hasText ? new ClipContent.Text(text!) : null;
        }
        finally { ObjC.objc_autoreleasePoolPop(pool); }
    }

    static byte[]? Bytes(IntPtr data)
    {
        if (data == IntPtr.Zero) return null;
        int n = (int)ObjC.SendUInt(data, "length");
        var buf = new byte[n];
        if (n > 0) Marshal.Copy(ObjC.Send(data, "bytes"), buf, 0, n);
        return buf;
    }

    // setData:forType: returns a BOOL; only its low byte is defined
    static bool Set(IntPtr pb, IntPtr data, string type) => ((long)ObjC.Send(pb, "setData:forType:", data, ObjC.NSString(type)) & 0xff) != 0;
}
