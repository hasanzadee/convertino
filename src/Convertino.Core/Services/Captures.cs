using ImageMagick;

namespace Convertino.Services;

/// <summary>What the clipboard holds, as each UI reads it from its OS (files win over a picture, a picture over text).</summary>
public abstract record ClipContent
{
    /// <summary>Files or folders copied in Explorer / Finder / a file manager.</summary>
    public sealed record Files(IReadOnlyList<string> Paths) : ClipContent;
    /// <summary>A picture, already as PNG.</summary>
    public sealed record Image(byte[] Png) : ClipContent;
    public sealed record Text(string Value) : ClipContent;
}

/// <summary>
/// Where pasted pictures and screenshots become files: a real folder (Pictures/Convertino), not temp, named
/// "Screenshot 2026-10-20 14.32.05.png" so they sort by time; results of the flower land next to them as usual.
/// </summary>
public static class Captures
{
    /// <summary>Self-tests point this at their own folder.</summary>
    public static string? FolderOverride { get; set; }

    /// <summary>
    /// Pictures/Convertino: the Pictures known folder on Windows (wherever it was moved), XDG_PICTURES_DIR on Linux
    /// (from user-dirs.dirs), ~/Pictures on macOS.
    /// </summary>
    public static string Folder
    {
        get
        {
            var dir = FolderOverride;
            if (string.IsNullOrEmpty(dir))
            {
                var pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures, Environment.SpecialFolderOption.DoNotVerify);
                if (string.IsNullOrEmpty(pics)) pics = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
                dir = Path.Combine(pics, "Convertino");
            }
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>"Folder/Clipboard 2026-10-20 14.32.05.png" (" (2)" if two land in the same second).</summary>
    public static string NewPath(string what, string ext, string? dir = null) =>
        Paths.Unique(dir ?? Folder, $"{what} {DateTime.Now:yyyy-MM-dd HH.mm.ss}", ext);

    /// <summary>Any picture a clipboard holds (PNG, TIFF, BMP …) as PNG; a fully transparent alpha (DIBs often carry 0 there) is dropped.</summary>
    public static byte[] ToPng(byte[] image)
    {
        using var img = new MagickImage(image);
        Opaque(img);
        return Png(img);
    }

    /// <summary>A Windows CF_DIB / CF_DIBV5 block (a BMP without its 14-byte file header) as PNG.</summary>
    public static byte[] DibToPng(byte[] dib)
    {
        if (dib.Length < 40) throw new InvalidDataException("Not a bitmap");
        int headerSize = BitConverter.ToInt32(dib, 0), bits = BitConverter.ToInt16(dib, 14), compression = BitConverter.ToInt32(dib, 16);
        int colors = BitConverter.ToInt32(dib, 32);
        if (colors == 0 && bits <= 8) colors = 1 << bits;
        // a plain 40-byte header keeps its BI_BITFIELDS masks after it; V4/V5 headers hold them inside
        int masks = headerSize == 40 ? compression switch { 3 => 12, 6 => 16, _ => 0 } : 0;
        int offset = 14 + headerSize + masks + colors * 4;
        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(offset).CopyTo(bmp, 10);
        dib.CopyTo(bmp, 14);
        using var img = new MagickImage(bmp, new MagickReadSettings { Format = MagickFormat.Bmp });
        Opaque(img);
        return Png(img);
    }

    /// <summary>Raw top-down BGRA pixels (a screen grab) as PNG, without alpha.</summary>
    public static byte[] BgraToPng(byte[] bgra, int w, int h)
    {
        using var img = Images.FromBgra(bgra, w, h);
        img.Alpha(AlphaAction.Off);
        return Png(img);
    }

    /// <summary>Without the date chunks ImageMagick adds: the same picture always gives the same bytes (one file for it, see ClipFlower).</summary>
    static byte[] Png(MagickImage img)
    {
        img.Settings.SetDefine(MagickFormat.Png, "compression-level", "6");
        img.Settings.SetDefine(MagickFormat.Png, "exclude-chunks", "date,tIME");
        return img.ToByteArray(MagickFormat.Png);
    }

    static void Opaque(MagickImage img)
    {
        if (!img.HasAlpha) return;
        var (px, _, _) = Images.ToBgra(img);
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) return;
        img.Alpha(AlphaAction.Off);
    }
}
