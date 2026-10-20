using System.IO;
using ImageMagick;

namespace Convertino.Services;

public static class Images
{
    static HashSet<MagickFormat>? _writable;

    public static bool CanWrite(MagickFormat f)
    {
        _writable ??= MagickNET.SupportedFormats.Where(x => x.SupportsWriting).Select(x => x.Format).ToHashSet();
        return _writable.Contains(f);
    }

    public static void WarmUp() => Task.Run(() =>
    {
        try { _ = CanWrite(MagickFormat.Png); using var i = new MagickImage(MagickColors.White, 2, 2); }
        catch (Exception ex) { Log.Error("magick warmup", ex); }
    });

    public static MagickFormat FormatOf(string ext) => ext.TrimStart('.').ToLowerInvariant() switch
    {
        "png" => MagickFormat.Png,
        "jpg" or "jpeg" or "jfif" => MagickFormat.Jpeg,
        "webp" => MagickFormat.WebP,
        "avif" => MagickFormat.Avif,
        "heic" or "heif" => MagickFormat.Heic,
        "ico" => MagickFormat.Ico,
        "tif" or "tiff" => MagickFormat.Tiff,
        "bmp" => MagickFormat.Bmp,
        "gif" => MagickFormat.Gif,
        _ => MagickFormat.Png,
    };

    public static MagickImage Load(string path)
    {
        var ext = Kinds.Ext(path);
        var rs = new MagickReadSettings();
        if (ext == "svg") { rs.Density = new Density(192); rs.BackgroundColor = MagickColors.None; }
        if (ext is "ico" or "tif" or "tiff")
        {
            using var col = new MagickImageCollection(path, rs);
            var best = col.OrderByDescending(f => (long)f.Width * f.Height).First();
            var one = new MagickImage(best);
            one.AutoOrient();
            return one;
        }
        var img = new MagickImage(path, rs);
        img.AutoOrient();
        return img;
    }

    /// <summary>Raw BGRA pixels (8-bit) for UI previews.</summary>
    public static (byte[] bgra, int w, int h) ToBgra(MagickImage img)
    {
        using var copy = (MagickImage)img.Clone();
        copy.ColorSpace = ColorSpace.sRGB;
        return (copy.ToByteArray(MagickFormat.Bgra), (int)copy.Width, (int)copy.Height);
    }

    public static MagickImage FromBgra(byte[] bgra, int w, int h) =>
        new(bgra, new MagickReadSettings { Width = (uint)w, Height = (uint)h, Format = MagickFormat.Bgra });

    public static void PrepareFor(MagickImage img, string ext, int? quality = null)
    {
        ext = ext.TrimStart('.').ToLowerInvariant();
        switch (ext)
        {
            case "jpg" or "jpeg" or "jfif":
                Flatten(img);
                img.Quality = (uint)(quality ?? Settings.Current.JpegQuality);
                img.Settings.Interlace = Interlace.Jpeg;
                break;
            case "bmp":
                Flatten(img);
                break;
            case "webp":
                img.Quality = (uint)(quality ?? 88);
                img.Settings.SetDefine(MagickFormat.WebP, "method", "5");
                break;
            case "avif":
                img.Quality = (uint)(quality ?? 62);
                img.Settings.SetDefine(MagickFormat.Heic, "speed", "7");
                break;
            case "heic" or "heif":
                img.Quality = (uint)(quality ?? 70);
                break;
            case "tif" or "tiff":
                img.Settings.Compression = CompressionMethod.LZW;
                break;
            case "ico":
            {
                uint side = Math.Max(img.Width, img.Height);
                if (img.Width != img.Height)
                {
                    img.BackgroundColor = MagickColors.None;
                    img.Extent(side, side, Gravity.Center);
                }
                if (side > 256) img.Resize(256, 256);
                img.Settings.SetDefine(MagickFormat.Ico, "auto-resize", "256,128,64,48,32,16");
                break;
            }
        }
    }

    static void Flatten(MagickImage img)
    {
        if (!img.HasAlpha) return;
        img.BackgroundColor = MagickColors.White;
        img.Alpha(AlphaAction.Remove);
        img.Alpha(AlphaAction.Off);
    }

    public static async Task Convert(Job job, string ext)
    {
        int n = job.Inputs.Count;
        for (int i = 0; i < n; i++)
        {
            job.Token.ThrowIfCancellationRequested();
            job.Report(i, n);
            var src = job.Inputs[i];
            var dst = Paths.Beside(src, ext);
            await Task.Run(() => ConvertOne(src, dst, ext), job.Token);
            job.Outputs.Add(dst);
        }
        job.Summary = SizeSummary(job);
    }

    static void ConvertOne(string src, string dst, string ext)
    {
        var srcExt = Kinds.Ext(src);
        if (srcExt is "gif" or "webp" && ext is "gif" or "webp")
        {
            using var col = new MagickImageCollection(src);
            if (col.Count > 1)
            {
                col.Coalesce();
                if (ext == "webp") foreach (var f in col) f.Quality = 85;
                else col.Optimize();
                col.Write(dst, FormatOf(ext));
                return;
            }
        }
        using var img = Load(src);
        PrepareFor(img, ext);
        img.Write(dst, FormatOf(ext));
    }

    public static string SizeSummary(Job job)
    {
        if (job.Outputs.Count == 1 && File.Exists(job.Outputs[0]))
        {
            var o = Paths.SizeOf(job.Outputs[0]);
            var i = job.Inputs.Count == 1 ? Paths.SizeOf(job.Inputs[0]) : 0;
            return i > 0 ? $"{Paths.HumanSize(i)} → {Paths.HumanSize(o)}" : Paths.HumanSize(o);
        }
        return $"{job.Outputs.Count} files";
    }

    // ---------- tools ----------

    public static async Task Compress(Job job)
    {
        long before = 0, after = 0;
        int n = job.Inputs.Count;
        for (int i = 0; i < n; i++)
        {
            job.Token.ThrowIfCancellationRequested();
            job.Report(i, n);
            var src = job.Inputs[i];
            var (dst, size) = await Task.Run(() => CompressOne(src), job.Token);
            before += Paths.SizeOf(src);
            after += size;
            if (dst != null) job.Outputs.Add(dst);
        }
        if (job.Outputs.Count == 0) { job.Summary = "Already as small as it gets"; job.Title = "Nothing to squeeze"; return; }
        job.Summary = $"{Paths.HumanSize(before)} → {Paths.HumanSize(after)}  (−{Math.Max(0, 100 - after * 100.0 / Math.Max(1, before)):0}%)";
    }

    static (string? path, long size) CompressOne(string src)
    {
        var ext = Kinds.Ext(src);
        long orig = Paths.SizeOf(src);
        string outExt = ext switch
        {
            "jpg" or "jpeg" or "jfif" => "jpg",
            "png" => "png",
            "webp" => "webp",
            "avif" => "avif",
            "heic" or "heif" => CanWrite(MagickFormat.Heic) ? "heic" : "jpg",
            "gif" => "gif",
            _ => "jpg",
        };
        using var img = Load(src);
        // icons, TGA, PSD, SVG … with real transparency would come out on white
        if (outExt == "jpg" && img.HasAlpha && !img.IsOpaque) outExt = "png";
        var dst = Paths.Beside(src, outExt, "(compressed)");
        int q = Settings.Current.CompressQuality;

        if (Math.Max(img.Width, img.Height) > 4096) img.Resize(new MagickGeometry(4096, 4096) { Greater = true });
        var icc = img.GetColorProfile();
        img.Strip();
        if (icc != null && icc.ColorSpace != ColorSpace.sRGB) img.SetProfile(icc);

        switch (outExt)
        {
            case "png":
                img.Write(dst, MagickFormat.Png);
                new ImageOptimizer { OptimalCompression = true }.LosslessCompress(dst);
                if (Paths.SizeOf(dst) > orig * 0.7)
                {
                    // pngquant-style: 256 colours with dithering — screenshots barely change
                    using var q8 = Load(src);
                    q8.Strip();
                    q8.Quantize(new QuantizeSettings { Colors = 256, DitherMethod = DitherMethod.FloydSteinberg });
                    q8.Write(dst, MagickFormat.Png8);
                    new ImageOptimizer { OptimalCompression = true }.LosslessCompress(dst);
                }
                break;
            case "gif":
            {
                using var col = new MagickImageCollection(src);
                col.Coalesce();
                col.Optimize();
                col.OptimizeTransparency();
                col.Write(dst);
                break;
            }
            default:
                PrepareFor(img, outExt, outExt switch { "avif" => 50, "heic" => 55, "webp" => q - 4, _ => q });
                if (outExt == "jpg") img.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "4:2:0");
                img.Write(dst, FormatOf(outExt));
                break;
        }
        long size = Paths.SizeOf(dst);
        if (size >= orig && outExt == ext)
        {
            File.Delete(dst);
            return (null, orig);
        }
        return (dst, size);
    }

    public static async Task StripMetadata(Job job)
    {
        int n = job.Inputs.Count, tags = 0;
        bool gps = false;
        for (int i = 0; i < n; i++)
        {
            job.Token.ThrowIfCancellationRequested();
            job.Report(i, n);
            var src = job.Inputs[i];
            var r = await Task.Run(() => StripOne(src), job.Token);
            job.Outputs.Add(r.path);
            tags += r.tags;
            gps |= r.gps;
        }
        job.Summary = (tags > 0 ? $"Removed {tags} tag{(tags == 1 ? "" : "s")}" : "Metadata removed") + (gps ? " · incl. GPS location" : "");
    }

    static (string path, int tags, bool gps) StripOne(string src)
    {
        var ext = Kinds.Ext(src);
        int tags = 0;
        bool gps = false;
        ushort orientation = 1;
        try
        {
            using var probe = new MagickImage();
            probe.Ping(src);
            var exif = probe.GetExifProfile();
            if (exif != null)
            {
                tags += exif.Values.Count();
                gps = exif.Values.Any(v => v.Tag.ToString().StartsWith("GPS", StringComparison.Ordinal));
                var o = exif.GetValue(ExifTag.Orientation);
                if (o != null) orientation = o.Value;
            }
            if (probe.GetXmpProfile() != null) tags++;
            if (probe.GetIptcProfile() is { } iptc) tags += iptc.Values.Count();
        }
        catch { }

        var dst = Paths.Beside(src, ext, "(clean)");
        if (ext is "jpg" or "jpeg" or "jfif" && orientation <= 1 && JpegStrip.TryLossless(src, dst))
            return (dst, tags, gps);

        using var img = Load(src);
        var icc = img.GetColorProfile();
        img.Strip();
        if (icc != null) img.SetProfile(icc);
        if (ext is "jpg" or "jpeg" or "jfif") img.Quality = 95;
        img.Write(dst);
        return (dst, tags, gps);
    }

    public static async Task MergeToPdf(Job job) => await Pdfs.ImagesToPdf(job, merge: true);
}

/// <summary>Drops EXIF/XMP/IPTC/comment segments from a JPEG without re-encoding a single pixel.</summary>
static class JpegStrip
{
    public static bool TryLossless(string src, string dst)
    {
        try
        {
            var b = File.ReadAllBytes(src);
            if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return false;
            using var o = new MemoryStream(b.Length);
            o.WriteByte(0xFF); o.WriteByte(0xD8);
            int i = 2;
            while (i + 4 <= b.Length)
            {
                if (b[i] != 0xFF) return false;
                byte m = b[i + 1];
                if (m == 0xDA) { o.Write(b, i, b.Length - i); break; } // start of scan: copy the rest verbatim
                int len = (b[i + 2] << 8) | b[i + 3];
                bool keep = true;
                if (m == 0xE1 || m == 0xED || m == 0xFE) keep = false; // APP1 Exif/XMP, APP13 IPTC, COM
                if (m == 0xE2)
                {
                    var tag = System.Text.Encoding.ASCII.GetString(b, i + 4, Math.Min(11, len - 2));
                    keep = tag.StartsWith("ICC_PROFILE", StringComparison.Ordinal);
                }
                if (m >= 0xE3 && m <= 0xEF && m != 0xEE) keep = false; // other APPn (keep Adobe APP14)
                if (keep) o.Write(b, i, len + 2);
                i += len + 2;
            }
            File.WriteAllBytes(dst, o.ToArray());
            return true;
        }
        catch { return false; }
    }
}
