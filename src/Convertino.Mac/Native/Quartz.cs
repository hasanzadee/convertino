using System.Runtime.InteropServices;
using System.Text;
using ImageMagick;

namespace Convertino.Native;

/// <summary>CoreGraphics / CoreFoundation C functions (no Objective-C involved).</summary>
public static class Quartz
{
    const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    // ---- input state (polled, no event tap) ----

    public const int CombinedSessionState = 0;
    public const uint LeftButton = 0, RightButton = 1;
    public const ulong ShiftFlag = 0x00020000, ControlFlag = 0x00040000, OptionFlag = 0x00080000;
    public const ushort EscapeKey = 0x35;

    [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool CGEventSourceButtonState(int state, uint button);
    [DllImport(CG)] public static extern ulong CGEventSourceFlagsState(int state);
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool CGEventSourceKeyState(int state, ushort key);
    [DllImport(CG)] static extern IntPtr CGEventCreate(IntPtr source);
    [DllImport(CG)] static extern CGPoint CGEventGetLocation(IntPtr ev);

    /// <summary>Input Monitoring (macOS 10.15+): whether this app may observe the keyboard.</summary>
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool CGPreflightListenEventAccess();
    /// <summary>Adds the app to System Settings → Input Monitoring (and asks once).</summary>
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool CGRequestListenEventAccess();

    [DllImport(CF)] public static extern void CFRelease(IntPtr cf);

    /// <summary>Cursor in global top-left-origin points — the same space Avalonia's screens and window positions use on macOS.</summary>
    public static CGPoint Cursor()
    {
        var ev = CGEventCreate(IntPtr.Zero);
        if (ev == IntPtr.Zero) return default;
        try { return CGEventGetLocation(ev); }
        finally { CFRelease(ev); }
    }

    // ---- PDF ----

    [DllImport(CF)] static extern IntPtr CFURLCreateFromFileSystemRepresentation(IntPtr alloc, byte[] path, nint length, [MarshalAs(UnmanagedType.I1)] bool isDirectory);
    [DllImport(CG)] static extern IntPtr CGPDFDocumentCreateWithURL(IntPtr url);
    [DllImport(CG)] static extern void CGPDFDocumentRelease(IntPtr doc);
    [DllImport(CG)] static extern nint CGPDFDocumentGetNumberOfPages(IntPtr doc);
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] static extern bool CGPDFDocumentIsEncrypted(IntPtr doc);
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] static extern bool CGPDFDocumentIsUnlocked(IntPtr doc);
    [DllImport(CG)] static extern IntPtr CGPDFDocumentGetPage(IntPtr doc, nint pageNumber);
    [DllImport(CG)] static extern CGRect CGPDFPageGetBoxRect(IntPtr page, int box);
    [DllImport(CG)] static extern int CGPDFPageGetRotationAngle(IntPtr page);
    [DllImport(CG)] static extern IntPtr CGColorSpaceCreateDeviceRGB();
    [DllImport(CG)] static extern void CGColorSpaceRelease(IntPtr cs);
    [DllImport(CG)] static extern IntPtr CGBitmapContextCreate(IntPtr data, nint width, nint height, nint bitsPerComponent, nint bytesPerRow, IntPtr space, uint bitmapInfo);
    [DllImport(CG)] static extern void CGContextRelease(IntPtr c);
    [DllImport(CG)] static extern void CGContextSetRGBFillColor(IntPtr c, double r, double g, double b, double a);
    [DllImport(CG)] static extern void CGContextFillRect(IntPtr c, CGRect rect);
    [DllImport(CG)] static extern void CGContextTranslateCTM(IntPtr c, double tx, double ty);
    [DllImport(CG)] static extern void CGContextScaleCTM(IntPtr c, double sx, double sy);
    [DllImport(CG)] static extern void CGContextRotateCTM(IntPtr c, double angle);
    [DllImport(CG)] static extern void CGContextSetInterpolationQuality(IntPtr c, int quality);
    [DllImport(CG)] static extern void CGContextDrawPDFPage(IntPtr c, IntPtr page);

    const int CropBox = 1;
    const uint PremultipliedFirst = 2, ByteOrder32Little = 2 << 12;   // → BGRA in memory

    /// <summary>Render PDF pages to PNG with the system's own PDF engine (the one Preview uses).</summary>
    public static List<byte[]> RenderPdf(string path, double dpi, CancellationToken ct, Action<double>? progress, int maxPages)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        var url = CFURLCreateFromFileSystemRepresentation(IntPtr.Zero, bytes, bytes.Length, false);
        if (url == IntPtr.Zero) throw new IOException("Couldn't open " + path);
        var doc = CGPDFDocumentCreateWithURL(url);
        CFRelease(url);
        if (doc == IntPtr.Zero) throw new IOException("Not a readable PDF");
        var cs = CGColorSpaceCreateDeviceRGB();
        try
        {
            if (CGPDFDocumentIsEncrypted(doc) && !CGPDFDocumentIsUnlocked(doc)) throw new IOException("This PDF is password-protected");
            int n = (int)Math.Min(CGPDFDocumentGetNumberOfPages(doc), maxPages);
            var list = new List<byte[]>(n);
            double scale = dpi / 72.0;
            for (int i = 1; i <= n; i++)
            {
                ct.ThrowIfCancellationRequested();
                var page = CGPDFDocumentGetPage(doc, i);
                if (page == IntPtr.Zero) continue;
                list.Add(RenderPage(page, scale, cs));
                progress?.Invoke(i / (double)n);
            }
            return list;
        }
        finally
        {
            CGColorSpaceRelease(cs);
            CGPDFDocumentRelease(doc);
        }
    }

    static byte[] RenderPage(IntPtr page, double scale, IntPtr cs)
    {
        var box = CGPDFPageGetBoxRect(page, CropBox);
        int rot = ((CGPDFPageGetRotationAngle(page) % 360) + 360) % 360;
        bool sideways = rot is 90 or 270;
        double wPt = sideways ? box.Height : box.Width, hPt = sideways ? box.Width : box.Height;
        int w = Math.Max(1, (int)Math.Round(wPt * scale)), h = Math.Max(1, (int)Math.Round(hPt * scale));
        // keep a single page under ~400 MB of pixels whatever the DPI / page size
        if ((long)w * h > 100_000_000)
        {
            double k = Math.Sqrt(100_000_000.0 / ((long)w * h));
            w = Math.Max(1, (int)(w * k)); h = Math.Max(1, (int)(h * k)); scale *= k;
        }
        int stride = w * 4;
        var buf = Marshal.AllocHGlobal((nint)stride * h);
        try
        {
            var ctx = CGBitmapContextCreate(buf, w, h, 8, stride, cs, PremultipliedFirst | ByteOrder32Little);
            if (ctx == IntPtr.Zero) throw new IOException("Couldn't allocate the page bitmap");
            try
            {
                CGContextSetRGBFillColor(ctx, 1, 1, 1, 1);
                CGContextFillRect(ctx, new CGRect(0, 0, w, h));
                CGContextSetInterpolationQuality(ctx, 3);   // high
                CGContextScaleCTM(ctx, scale, scale);
                // the page's /Rotate is clockwise; CG's y axis points up
                switch (rot)
                {
                    case 90: CGContextTranslateCTM(ctx, 0, hPt); CGContextRotateCTM(ctx, -Math.PI / 2); break;
                    case 180: CGContextTranslateCTM(ctx, wPt, hPt); CGContextRotateCTM(ctx, Math.PI); break;
                    case 270: CGContextTranslateCTM(ctx, wPt, 0); CGContextRotateCTM(ctx, Math.PI / 2); break;
                }
                CGContextTranslateCTM(ctx, -box.X, -box.Y);
                CGContextDrawPDFPage(ctx, page);
            }
            finally { CGContextRelease(ctx); }

            var pixels = new byte[stride * h];
            Marshal.Copy(buf, pixels, 0, pixels.Length);
            using var img = new MagickImage(pixels, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.BGRA));
            img.Alpha(AlphaAction.Off);
            return img.ToByteArray(MagickFormat.Png);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
