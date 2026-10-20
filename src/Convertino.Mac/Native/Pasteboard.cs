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

    // setData:forType: returns a BOOL; only its low byte is defined
    static bool Set(IntPtr pb, IntPtr data, string type) => ((long)ObjC.Send(pb, "setData:forType:", data, ObjC.NSString(type)) & 0xff) != 0;
}
