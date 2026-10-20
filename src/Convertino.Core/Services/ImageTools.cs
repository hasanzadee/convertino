using System.Diagnostics;
using System.Text;
using ImageMagick;
using ImageMagick.Drawing;

namespace Convertino.Services;

/// <summary>
/// One-tap image tools: messenger-ready JPEG, Instagram square, rounded screenshot, palette, favicon set,
/// copy as picture, QR (here and in <see cref="Qr"/>), plus the multi-image ones in <see cref="ImageSets"/>.
/// None of them asks anything; results land beside the source.
/// </summary>
public static class ImageTools
{
    /// <summary>Every tool of this pack that applies to these files, most useful first.</summary>
    public static List<PetalAction> Petals(IReadOnlyList<string> files)
    {
        var list = new List<PetalAction>();
        int n = files.Count;
        if (files.All(f => Kinds.Of(f) == Kind.Image))
        {
            list.Add(Catalog.Tool("messenger", "Messenger", "send", n == 1 ? "Shrink for messengers" : $"Shrink {n} for messengers", Messenger));
            if (n == 1)
            {
                list.Add(Catalog.Tool("copyimg", "Copy", "image", "Copy image", CopyImage, quick: true));
                list.Add(Catalog.Tool("square", "Square", "squareimg", "Square on a blurred fill", Square));
                list.Add(Catalog.Tool("rounded", "Rounded", "rounded", "Round corners + shadow", Rounded));
                list.Add(Catalog.Tool("palette", "Palette", "palette", "Copy colour palette", Palette));
                list.Add(Catalog.Tool("favicon", "Favicon", "globe", "Make a favicon set", Favicon));
                list.Add(Catalog.Tool("readqr", "Read QR", "qr", "Copy QR code text", Qr.Read));
            }
            else
            {
                if (n == 2) list.Add(Catalog.Tool("compare", "Compare", "compare", "Highlight differences", ImageSets.Compare));
                list.Add(Catalog.Tool("sheet", "Contact", "grid", "Contact sheet", ImageSets.ContactSheet));
                list.Add(Catalog.Tool("slideshow", "Slideshow", "slideshow", "Slideshow GIF + WEBP", ImageSets.Slideshow));
                list.Add(Catalog.Tool("square", "Square", "squareimg", $"Square {n} on a blurred fill", Square));
                list.Add(Catalog.Tool("rounded", "Rounded", "rounded", $"Round corners of {n}", Rounded));
            }
        }
        if (n == 1 && Qr.CanMake(files[0]))
            list.Add(Catalog.Tool("qr", "QR", "qr", "Make a QR code", Qr.Make));
        return list;
    }

    // ---------- for messenger ----------

    public static async Task Messenger(Job job)
    {
        int n = job.Inputs.Count;
        long before = 0, after = 0;
        bool gps = false;
        string dims = "";
        for (int i = 0; i < n; i++)
        {
            job.Token.ThrowIfCancellationRequested();
            job.Report(i, n);
            var src = job.Inputs[i];
            var r = await Task.Run(() => MessengerOne(src), job.Token);
            job.Outputs.Add(r.dst);
            before += Paths.SizeOf(src);
            after += Paths.SizeOf(r.dst);
            gps |= r.gps;
            dims = r.dims;
        }
        job.Summary = (n == 1 ? dims + " · " : "") + $"{Paths.HumanSize(before)} → {Paths.HumanSize(after)}" + (gps ? " · GPS removed" : "");
    }

    static (string dst, string dims, bool gps) MessengerOne(string src)
    {
        bool gps = HasGps(src);
        using var img = Images.Load(src);
        string from = $"{img.Width}×{img.Height}";
        img.Resize(new MagickGeometry(2560, 2560) { Greater = true });
        ToSrgbStripped(img);
        Images.PrepareFor(img, "jpg", 85);
        img.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "4:2:0");
        var dst = Paths.Beside(src, "jpg", "(messenger)");
        img.Write(dst, MagickFormat.Jpeg);
        string to = $"{img.Width}×{img.Height}";
        return (dst, from == to ? to : $"{from} → {to}", gps);
    }

    static bool HasGps(string path)
    {
        try
        {
            using var probe = new MagickImage();
            probe.Ping(path);
            return probe.GetExifProfile()?.Values.Any(v => v.Tag.ToString().StartsWith("GPS", StringComparison.Ordinal)) == true;
        }
        catch { return false; }
    }

    /// <summary>Bake any colour profile into sRGB pixels, then drop every profile and tag (EXIF, GPS, XMP, IPTC, comments).</summary>
    internal static void ToSrgbStripped(MagickImage img)
    {
        try
        {
            if (img.GetColorProfile() != null) img.TransformColorSpace(ColorProfiles.SRGB);
            else if (img.ColorSpace != ColorSpace.sRGB && img.ColorSpace != ColorSpace.Gray) img.ColorSpace = ColorSpace.sRGB;
        }
        catch (Exception ex) { Log.Error("to sRGB", ex); }
        img.Strip();
    }

    /// <summary>Drop tags (EXIF, GPS …) but keep the colour profile: for derived images that keep the source's pixels.</summary>
    static void StripKeepIcc(MagickImage img)
    {
        var icc = img.GetColorProfile();
        img.Strip();
        if (icc != null) img.SetProfile(icc);
    }

    // ---------- square ----------

    public static async Task Square(Job job)
    {
        int n = job.Inputs.Count, skipped = 0;
        string dims = "";
        for (int i = 0; i < n; i++)
        {
            job.Token.ThrowIfCancellationRequested();
            job.Report(i, n);
            var src = job.Inputs[i];
            var r = await Task.Run(() => SquareOne(src), job.Token);
            if (r.dst == null) { skipped++; continue; }
            job.Outputs.Add(r.dst);
            dims = r.dims;
        }
        if (job.Outputs.Count == 0) { job.Title = "Already square"; job.Summary = "Nothing to fill"; return; }
        job.Summary = n == 1 ? dims : $"{job.Outputs.Count} squared" + (skipped > 0 ? $" · {skipped} already square" : "");
    }

    static (string? dst, string dims) SquareOne(string src)
    {
        using var img = Images.Load(src);
        if (img.Width == img.Height) return (null, "");
        // the photo itself stays at full size up to 4096 px, the square grows around it
        img.Resize(new MagickGeometry(4096, 4096) { Greater = true });
        StripKeepIcc(img);
        uint side = Math.Max(img.Width, img.Height);

        // blur a small copy and blow it up: same look as a huge Gaussian, a fraction of the time
        using var bg = (MagickImage)img.Clone();
        bg.BackgroundColor = MagickColors.White;
        bg.Alpha(AlphaAction.Remove);
        const uint small = 96;
        bg.Resize(new MagickGeometry(small, small) { FillArea = true });
        bg.Crop(small, small, Gravity.Center);
        bg.ResetPage();
        bg.Blur(0, 5);
        bg.Resize(new MagickGeometry(side, side) { IgnoreAspectRatio = true });
        bg.Modulate(new Percentage(82), new Percentage(115), new Percentage(100));
        bg.Composite(img, Gravity.Center, CompositeOperator.Over);

        bool keepPng = Kinds.Ext(src) is "png" or "webp" or "gif" or "bmp" or "tif" or "tiff";
        var ext = keepPng ? "png" : "jpg";
        Images.PrepareFor(bg, ext, 92);
        var dst = Paths.Beside(src, ext, "(square)");
        bg.Write(dst, Images.FormatOf(ext));
        return (dst, $"{side}×{side}");
    }

    // ---------- rounded + shadow ----------

    public static async Task Rounded(Job job)
    {
        int n = job.Inputs.Count;
        for (int i = 0; i < n; i++)
        {
            job.Token.ThrowIfCancellationRequested();
            job.Report(i, n);
            var src = job.Inputs[i];
            job.Outputs.Add(await Task.Run(() => RoundedOne(src), job.Token));
        }
        job.Summary = n == 1 ? Images.SizeSummary(job) : $"{n} images";
    }

    static string RoundedOne(string src)
    {
        using var img = Images.Load(src);
        img.Resize(new MagickGeometry(4096, 4096) { Greater = true });
        StripKeepIcc(img);
        // a window capture with a transparent margin: round and shade the window, not the empty frame around it
        if (TransparentCorner(img)) { img.Trim(); img.ResetPage(); }
        int w = (int)img.Width, h = (int)img.Height;
        double radius = Math.Clamp(Math.Min(w, h) * 0.03, 6, 40);

        img.Alpha(AlphaAction.Set);
        using (var mask = new MagickImage(MagickColors.Transparent, (uint)w, (uint)h))
        {
            new Drawables().FillColor(MagickColors.White).RoundRectangle(0, 0, w - 1, h - 1, radius, radius).Draw(mask);
            img.Composite(mask, CompositeOperator.DstIn);
        }

        // soft shadow a little below, shaped by the picture's own alpha; blurred at a quarter size, then scaled up
        int sigma = (int)Math.Round(Math.Clamp(Math.Min(w, h) * 0.02, 8, 28));
        int dy = sigma / 2, pad = sigma * 2 + dy;
        int cw = w + 2 * pad, ch = h + 2 * pad;
        const int q = 4;
        using var canvas = new MagickImage(MagickColors.Transparent, (uint)Math.Max(1, cw / q), (uint)Math.Max(1, ch / q));
        using (var shape = (MagickImage)img.Clone())
        {
            shape.Evaluate(Channels.RGB, EvaluateOperator.Set, 0);
            shape.Evaluate(Channels.Alpha, EvaluateOperator.Multiply, 0.45);
            shape.Resize(new MagickGeometry((uint)Math.Max(1, w / q), (uint)Math.Max(1, h / q)) { IgnoreAspectRatio = true });
            canvas.Composite(shape, pad / q, (pad + dy) / q, CompositeOperator.Over);
        }
        canvas.Blur(0, sigma / (double)q);
        canvas.Resize(new MagickGeometry((uint)cw, (uint)ch) { IgnoreAspectRatio = true });
        canvas.Composite(img, pad, pad, CompositeOperator.Over);
        var icc = img.GetColorProfile();
        if (icc != null) canvas.SetProfile(icc);

        var dst = Paths.Beside(src, "png", "(rounded)");
        canvas.Write(dst, MagickFormat.Png);
        return dst;
    }

    static bool TransparentCorner(MagickImage img)
    {
        if (!img.HasAlpha) return false;
        using var corner = (MagickImage)img.Clone();
        corner.Crop(1, 1);
        // through ToBgra: 8-bit BGRA whatever the source's depth (1-bit palettes, 16-bit RGBA …)
        return Images.ToBgra(corner).bgra[3] == 0;
    }

    // ---------- palette ----------

    public static async Task Palette(Job job)
    {
        var src = job.Inputs[0];
        var (hex, dst) = await Task.Run(() => PaletteOne(src), job.Token);
        Platform.Current.SetClipboardText(string.Join(Environment.NewLine, hex));
        job.Outputs.Add(dst);
        job.Title = $"{hex.Count} colours copied";
        job.Summary = string.Join("  ", hex);
    }

    static (List<string> hex, string dst) PaletteOne(string src)
    {
        List<(byte r, byte g, byte b, double share)> colours;
        using (var img = LoadAtLeast(src, 512)) colours = Dominant(img);
        var hex = colours.Select(c => $"#{c.r:X2}{c.g:X2}{c.b:X2}").ToList();

        const int cell = 120, swatchH = 96;
        int labelH = Text.Font != null ? 32 : 0;
        using var strip = new MagickImage(MagickColors.White, (uint)(cell * hex.Count), (uint)(swatchH + labelH));
        strip.Density = new Density(72);
        for (int i = 0; i < hex.Count; i++)
        {
            using var sw = new MagickImage(new MagickColor(hex[i]), cell, swatchH);
            strip.Composite(sw, i * cell, 0);
            if (labelH > 0) Text.Draw(strip, hex[i], i * cell + cell / 2.0, swatchH + 22, 15, "#3C3C46");
        }
        var dst = Paths.Beside(src, "png", "(palette)");
        strip.Write(dst, MagickFormat.Png);
        return (hex, dst);
    }

    /// <summary>5–8 dominant colours, heaviest first: k-means in Lab on a small copy, near-duplicates merged.</summary>
    internal static List<(byte r, byte g, byte b, double share)> Dominant(MagickImage img)
    {
        using var small = (MagickImage)img.Clone();
        // point sampling: a filtered downscale would invent blends of neighbouring colours
        small.Sample(new MagickGeometry(200, 200) { Greater = true });
        ToSrgbStripped(small);   // HEX codes are sRGB; a P3 / Adobe RGB photo would drift otherwise
        var (px, w, h) = Images.ToBgra(small);
        var lab = new List<(double l, double a, double b)>(w * h);
        var rgb = new List<(byte r, byte g, byte b)>(w * h);
        for (int i = 0; i < w * h; i++)
        {
            if (px[i * 4 + 3] < 128) continue;   // transparent pixels aren't part of the picture
            byte r = px[i * 4 + 2], g = px[i * 4 + 1], b = px[i * 4];
            rgb.Add((r, g, b));
            lab.Add(Lab(r, g, b));
        }
        if (lab.Count == 0) throw new InvalidOperationException("The image is fully transparent");

        const int k = 10;
        var rnd = new Random(7);
        // k-means++ seeding: spread the starting centres over the colours that are actually there
        var centres = new List<(double l, double a, double b)> { lab[rnd.Next(lab.Count)] };
        var d2 = new double[lab.Count];
        while (centres.Count < k)
        {
            double sum = 0;
            for (int i = 0; i < lab.Count; i++) { d2[i] = centres.Min(c => Dist2(lab[i], c)); sum += d2[i]; }
            if (sum <= 0) break;
            double pick = rnd.NextDouble() * sum;
            int j = 0;
            for (; j < lab.Count - 1 && (pick -= d2[j]) > 0; j++) { }
            centres.Add(lab[j]);
        }

        var assign = new int[lab.Count];
        for (int iter = 0; iter < 16; iter++)
        {
            bool moved = false;
            for (int i = 0; i < lab.Count; i++)
            {
                int best = 0;
                double bd = double.MaxValue;
                for (int c = 0; c < centres.Count; c++)
                {
                    double d = Dist2(lab[i], centres[c]);
                    if (d < bd) { bd = d; best = c; }
                }
                if (assign[i] != best) { assign[i] = best; moved = true; }
            }
            for (int c = 0; c < centres.Count; c++)
            {
                double l = 0, a = 0, b = 0;
                int cnt = 0;
                for (int i = 0; i < lab.Count; i++)
                    if (assign[i] == c) { l += lab[i].l; a += lab[i].a; b += lab[i].b; cnt++; }
                if (cnt > 0) centres[c] = (l / cnt, a / cnt, b / cnt);
            }
            if (!moved && iter > 0) break;
        }

        var clusters = Enumerable.Range(0, centres.Count).Select(c =>
        {
            long r = 0, g = 0, b = 0;
            int cnt = 0;
            for (int i = 0; i < lab.Count; i++)
                if (assign[i] == c) { r += rgb[i].r; g += rgb[i].g; b += rgb[i].b; cnt++; }
            return (lab: centres[c], cnt, r: cnt == 0 ? 0 : r / cnt, g: cnt == 0 ? 0 : g / cnt, b: cnt == 0 ? 0 : b / cnt);
        }).Where(c => c.cnt > 0).OrderByDescending(c => c.cnt).ToList();

        // fold colours a viewer couldn't tell apart (ΔE < 10) into the heavier one
        var kept = new List<(double l, double a, double b, int cnt, long r, long g, long bl)>();
        foreach (var c in clusters)
        {
            int near = kept.FindIndex(x => Dist2((x.l, x.a, x.b), c.lab) < 100);
            if (near >= 0) { var x = kept[near]; kept[near] = x with { cnt = x.cnt + c.cnt }; }
            else kept.Add((c.lab.l, c.lab.a, c.lab.b, c.cnt, c.r, c.g, c.b));
        }
        kept = kept.OrderByDescending(x => x.cnt).ToList();
        double total = lab.Count;
        // up to 8; small ones only to reach 5, and never edge blends (a flat two-colour logo stays two colours)
        var picked = kept.Where((x, i) => x.cnt / total >= (i < 5 ? 0.005 : 0.015)).Take(8);
        return picked.Select(x => ((byte)x.r, (byte)x.g, (byte)x.bl, x.cnt / total)).ToList();
    }

    static double Dist2((double l, double a, double b) p, (double l, double a, double b) q)
    {
        double dl = p.l - q.l, da = p.a - q.a, db = p.b - q.b;
        return dl * dl + da * da + db * db;
    }

    static (double l, double a, double b) Lab(byte r8, byte g8, byte b8)
    {
        static double Lin(byte c) { double v = c / 255.0; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        double r = Lin(r8), g = Lin(g8), b = Lin(b8);
        double x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047;
        double y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        double z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
        double fx = F(x), fy = F(y), fz = F(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    // ---------- favicon ----------

    public static async Task Favicon(Job job)
    {
        var src = job.Inputs[0];
        var dir = await Task.Run(() => FaviconSet(src), job.Token);
        job.Outputs.Add(dir);
        job.Title = Path.GetFileName(dir);
        job.Summary = "favicon.ico (16/32/48) · 180 · 192 · 512 px + snippet";
    }

    static string FaviconSet(string src)
    {
        using var img = LoadAtLeast(src, 512);
        uint side = Math.Max(img.Width, img.Height);
        if (img.Width != img.Height)
        {
            img.BackgroundColor = MagickColors.None;
            img.Extent(side, side, Gravity.Center);
        }
        ToSrgbStripped(img);

        var dir = Paths.UniqueDir(Path.GetDirectoryName(src)!, Path.GetFileNameWithoutExtension(src) + " favicon");
        Directory.CreateDirectory(dir);
        MagickImage Sized(uint px)
        {
            var c = (MagickImage)img.Clone();
            c.Resize(new MagickGeometry(px, px) { IgnoreAspectRatio = true });
            return c;
        }

        using (var ico = new MagickImageCollection())
        {
            foreach (var px in new uint[] { 16, 32, 48 }) ico.Add(Sized(px));
            ico.Write(Path.Combine(dir, "favicon.ico"), MagickFormat.Ico);
        }
        using (var touch = Sized(180))
        {
            // iOS paints transparency black
            touch.BackgroundColor = MagickColors.White;
            touch.Alpha(AlphaAction.Remove);
            touch.Write(Path.Combine(dir, "apple-touch-icon.png"), MagickFormat.Png);
        }
        foreach (var px in new uint[] { 192, 512 })
        {
            using var c = Sized(px);
            c.Write(Path.Combine(dir, $"icon-{px}.png"), MagickFormat.Png);
        }
        File.WriteAllText(Path.Combine(dir, "site.webmanifest"), """
            {
              "icons": [
                { "src": "/icon-192.png", "sizes": "192x192", "type": "image/png" },
                { "src": "/icon-512.png", "sizes": "512x512", "type": "image/png" }
              ]
            }

            """);
        File.WriteAllText(Path.Combine(dir, "snippet.html"), """
            <!-- Copy the files in this folder to your site's root and these lines into <head> -->
            <link rel="icon" href="/favicon.ico" sizes="16x16 32x32 48x48">
            <link rel="icon" type="image/png" sizes="192x192" href="/icon-192.png">
            <link rel="apple-touch-icon" href="/apple-touch-icon.png">
            <link rel="manifest" href="/site.webmanifest">

            """);
        return dir;
    }

    /// <summary>Like <see cref="Images.Load"/>, but vector art (SVG) is rendered big enough for <paramref name="side"/> px.</summary>
    static MagickImage LoadAtLeast(string path, uint side)
    {
        var img = Images.Load(path);
        if (Kinds.Ext(path) != "svg" || Math.Max(img.Width, img.Height) >= side) return img;
        double scale = side * 2.0 / Math.Max(1, Math.Max(img.Width, img.Height));
        img.Dispose();
        var big = new MagickImage(path, new MagickReadSettings { Density = new Density(192 * scale), BackgroundColor = MagickColors.None });
        return big;
    }

    // ---------- copy as picture ----------

    /// <summary>Longest side put on the clipboard: a 48 MP photo would otherwise take ~300 MB there (PNG + bitmap).</summary>
    const uint MaxClipboardSide = 4096;

    public static async Task CopyImage(Job job)
    {
        var src = job.Inputs[0];
        var (png, dims, scaled) = await Task.Run(() =>
        {
            using var img = Images.Load(src);
            bool big = Math.Max(img.Width, img.Height) > MaxClipboardSide;
            if (big) img.Resize(new MagickGeometry(MaxClipboardSide, MaxClipboardSide) { Greater = true });
            // nothing private rides along into a chat
            ToSrgbStripped(img);
            img.Settings.SetDefine(MagickFormat.Png, "compression-level", "1");
            return (img.ToByteArray(MagickFormat.Png), $"{img.Width}×{img.Height}", big);
        }, job.Token);
        Platform.Current.SetClipboardImage(png);
        job.Title = "Image copied";
        job.Summary = (scaled ? $"scaled to {dims}" : dims) + " · paste it anywhere";
    }

    // ---------- shared ----------

    /// <summary>Labels drawn by ImageMagick with a system TrueType font; skipped when none is found.</summary>
    internal static class Text
    {
        static readonly Lazy<string?> _font = new(Find);
        public static string? Font => _font.Value;

        static string? Find()
        {
            string[] candidates = OperatingSystem.IsWindows()
                ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf"),
                   Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf")]
                : OperatingSystem.IsMacOS()
                ? ["/System/Library/Fonts/Supplemental/Arial.ttf", "/Library/Fonts/Arial Unicode.ttf", "/System/Library/Fonts/Helvetica.ttc"]
                : ["/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", "/usr/share/fonts/TTF/DejaVuSans.ttf",
                   "/usr/share/fonts/dejavu-sans-fonts/DejaVuSans.ttf", "/usr/share/fonts/dejavu/DejaVuSans.ttf",
                   "/usr/share/fonts/truetype/noto/NotoSans-Regular.ttf", "/usr/share/fonts/noto/NotoSans-Regular.ttf",
                   "/usr/share/fonts/google-noto/NotoSans-Regular.ttf",
                   "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf", "/usr/share/fonts/liberation-sans/LiberationSans-Regular.ttf"];
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            if (OperatingSystem.IsWindows() || Paths.Which("fc-match") is not { } fc) return null;
            try
            {
                using var p = Process.Start(new ProcessStartInfo(fc, "-f \"%{file}\" sans") { RedirectStandardOutput = true, UseShellExecute = false })!;
                var file = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000);
                return File.Exists(file) ? file : null;
            }
            catch { return null; }
        }

        /// <summary>Draw one line with its baseline at y; x is the centre (or the left edge with <paramref name="align"/> Left).</summary>
        public static void Draw(MagickImage img, string text, double x, double y, double size, string color, TextAlignment align = TextAlignment.Center)
        {
            if (Font is not { } font || text.Length == 0) return;
            try
            {
                new Drawables().Font(font).FontPointSize(size).FillColor(new MagickColor(color)).TextEncoding(Encoding.UTF8)
                    .TextAlignment(align).Text(x, y, text).Draw(img);
            }
            catch (Exception ex) { Log.Error("label", ex); }
        }

        /// <summary>The text, shortened with an ellipsis in the middle until it fits <paramref name="width"/> px.</summary>
        public static string Fit(MagickImage img, string text, double size, double width)
        {
            if (Font is not { } font) return text;
            try
            {
                img.Settings.Font = font;
                img.Settings.FontPointsize = size;
                double W(string s) => img.FontTypeMetrics(s)?.TextWidth ?? 0;
                if (W(text) <= width) return text;
                // keep the end too: "IMG_2024…_0042.jpg" tells photos apart better than "IMG_2024_10_1…"
                int lo = 1, hi = text.Length - 1;
                string Cut(int keep) => text[..(keep - keep / 3)] + "…" + text[^(keep / 3)..];
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    if (W(Cut(mid)) <= width) lo = mid; else hi = mid - 1;
                }
                return Cut(lo);
            }
            catch { return text.Length > 28 ? text[..18] + "…" + text[^9..] : text; }
        }
    }

    /// <summary>Natural file-name order: IMG_2 before IMG_10.</summary>
    internal sealed class NaturalOrder : IComparer<string>
    {
        public static readonly NaturalOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            x = Path.GetFileName(x) ?? ""; y = Path.GetFileName(y) ?? "";
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                    while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                    var a = x[si..i].TrimStart('0');
                    var b = y[sj..j].TrimStart('0');
                    int c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
