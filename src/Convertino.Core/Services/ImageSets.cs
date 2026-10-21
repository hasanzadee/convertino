using ImageMagick;
using ImageMagick.Drawing;

namespace Convertino.Services;

/// <summary>Tools for several images at once: contact sheet, slideshow, compare.</summary>
public static class ImageSets
{
    /// <summary>A fast, auto-oriented copy no bigger than <paramref name="max"/> px (JPEGs decode at reduced size).</summary>
    static MagickImage LoadSmall(string path, uint max)
    {
        MagickImage img;
        // libjpeg can decode big photos at 1/2…1/8 size, much faster; but it would also scale small ones up
        if (Kinds.Ext(path) is "jpg" or "jpeg" or "jfif" && new MagickImageInfo(path) is var info && Math.Max(info.Width, info.Height) > max * 2)
        {
            var rs = new MagickReadSettings();
            rs.SetDefine(MagickFormat.Jpeg, "size", $"{max}x{max}");
            img = new MagickImage(path, rs);
            img.AutoOrient();
        }
        else img = Images.Load(path);
        img.Resize(new MagickGeometry(max, max) { Greater = true });
        ImageTools.ToSrgbStripped(img);
        return img;
    }

    /// <summary>Thumbnails for every file, decoded in parallel; null where a file couldn't be read.</summary>
    static MagickImage?[] LoadAll(Job job, IReadOnlyList<string> files, uint max, double share)
    {
        var res = new MagickImage?[files.Count];
        int done = 0;
        // four decoders at a time: more barely helps (Magick is multi-threaded inside) and multiplies the memory peak
        var opts = new ParallelOptions { CancellationToken = job.Token, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 4) };
        try
        {
            Parallel.For(0, files.Count, opts, i =>
            {
                try { res[i] = LoadSmall(files[i], max); }
                catch (Exception ex) { Log.Error($"load {files[i]}", ex); }
                job.Report(Interlocked.Increment(ref done) * share / files.Count);
            });
        }
        catch
        {
            foreach (var t in res) t?.Dispose();
            throw;
        }
        return res;
    }

    static List<string> Sorted(Job job) => job.Inputs.OrderBy(f => f, ImageTools.NaturalOrder.Instance).ToList();

    // beyond these a sheet or an animation gets unwieldy (and memory-hungry); the card says when files were left out
    const int MaxSheet = 400, MaxFrames = 60;

    static string Capped(int used, int total) => used < total ? " · " + L.T("first {0} of {1}", used, total) : "";

    // ---------- contact sheet ----------

    public static async Task ContactSheet(Job job)
    {
        var all = Sorted(job);
        var files = all.Take(MaxSheet).ToList();
        var dst = await Task.Run(() => Sheet(job, files), job.Token);
        job.Outputs.Add(dst);
        job.Summary = L.N(files.Count, "{0} image", "{0} images") + $" · {Paths.HumanSize(Paths.SizeOf(dst))}" + Capped(files.Count, all.Count);
    }

    static string Sheet(Job job, List<string> files)
    {
        const int cellW = 320, thumbH = 240, gap = 16, margin = 28;
        bool labels = ImageTools.Text.Font != null;
        int labelH = labels ? 30 : 0, headerH = labels ? 52 : 0;
        int n = files.Count;
        int cols = n <= 3 ? n : Math.Min(12, (int)Math.Ceiling(Math.Sqrt(n)));
        int rows = (n + cols - 1) / cols;
        int width = margin * 2 + cols * cellW + (cols - 1) * gap;
        int height = margin * 2 + headerH + rows * (thumbH + labelH) + (rows - 1) * gap;

        var thumbs = LoadAll(job, files, cellW, 0.85);
        try
        {
            using var sheet = new MagickImage(MagickColors.White, (uint)width, (uint)height);
            sheet.Density = new Density(72);
            var dir = Path.GetDirectoryName(files[0])!;
            if (labels)
            {
                var folder = Path.GetFileName(dir) is { Length: > 0 } d ? d : dir;
                ImageTools.Text.Draw(sheet, $"{folder} — {n} images", margin, margin + 26, 24, "#1E1E28", TextAlignment.Left);
            }

            var cells = new Drawables().FillColor(new MagickColor("#F1F1F4"));
            for (int i = 0; i < n; i++)
            {
                int x = margin + i % cols * (cellW + gap), y = margin + headerH + i / cols * (thumbH + labelH + gap);
                cells.Rectangle(x, y, x + cellW - 1, y + thumbH - 1);
            }
            cells.Draw(sheet);

            for (int i = 0; i < n; i++)
            {
                job.Token.ThrowIfCancellationRequested();
                int x = margin + i % cols * (cellW + gap), y = margin + headerH + i / cols * (thumbH + labelH + gap);
                if (thumbs[i] is { } t)
                {
                    t.Resize(new MagickGeometry(cellW, thumbH));
                    t.BackgroundColor = new MagickColor("#F1F1F4");
                    t.Alpha(AlphaAction.Remove);
                    sheet.Composite(t, x + (cellW - (int)t.Width) / 2, y + (thumbH - (int)t.Height) / 2, CompositeOperator.Over);
                }
                else ImageTools.Text.Draw(sheet, "can't read", x + cellW / 2.0, y + thumbH / 2.0 + 5, 15, "#9A9AA6");
                if (labels)
                {
                    var name = ImageTools.Text.Fit(sheet, Path.GetFileName(files[i]), 15, cellW - 8);
                    ImageTools.Text.Draw(sheet, name, x + cellW / 2.0, y + thumbH + 21, 15, "#3C3C46");
                }
            }
            job.Report(0.95);

            var dst = Paths.Unique(dir, "Contact sheet", "jpg");
            sheet.Quality = 88;
            sheet.Settings.Interlace = Interlace.Jpeg;
            sheet.Write(dst, MagickFormat.Jpeg);
            return dst;
        }
        finally { foreach (var t in thumbs) t?.Dispose(); }
    }

    // ---------- slideshow ----------

    public static async Task Slideshow(Job job)
    {
        var all = Sorted(job);
        var files = all.Take(MaxFrames).ToList();
        var (gif, webp, frames, size) = await Task.Run(() => MakeSlideshow(job, files), job.Token);
        job.Outputs.Add(gif);
        job.Outputs.Add(webp);
        job.Summary = L.N(frames, "{0} frame", "{0} frames") + " · " + L.T("1 s each") + $" · {size} · GIF {Paths.HumanSize(Paths.SizeOf(gif))}, WEBP {Paths.HumanSize(Paths.SizeOf(webp))}"
            + Capped(files.Count, all.Count);
    }

    static (string gif, string webp, int frames, string size) MakeSlideshow(Job job, List<string> files)
    {
        var thumbs = LoadAll(job, files, 1080, 0.4);
        try
        {
            var ok = thumbs.Where(t => t != null).Select(t => t!).ToList();
            if (ok.Count < 2) throw new InvalidOperationException(L.T("Need at least two readable images"));
            // every frame shares the first picture's shape; the rest are letterboxed into it
            double aspect = ok[0].Width / (double)ok[0].Height;
            uint longSide = Math.Min(1080u, ok.Max(t => Math.Max(t.Width, t.Height)));
            uint w = aspect >= 1 ? longSide : (uint)Math.Round(longSide * aspect);
            uint h = aspect >= 1 ? (uint)Math.Round(longSide / aspect) : longSide;
            w += w % 2; h += h % 2;

            var dir = Path.GetDirectoryName(files[0])!;
            // frames are made in place, one copy per picture: the collection takes the images themselves
            using var col = new MagickImageCollection();
            foreach (var f in ok)
            {
                Frame(f, w, h);
                col.Add(f);
            }
            var webp = Paths.Unique(dir, "Slideshow", "webp");
            foreach (var f in col) f.Quality = 80;
            col.Write(webp, MagickFormat.WebP);
            job.Report(0.7);

            // GIF: 256 colours a frame, so a smaller canvas keeps it shareable
            if (Math.Max(w, h) > 720)
            {
                double s = 720.0 / Math.Max(w, h);
                uint gw = (uint)Math.Round(w * s), gh = (uint)Math.Round(h * s);
                foreach (var f in col) { f.Resize(new MagickGeometry(gw, gh) { IgnoreAspectRatio = true }); f.ResetPage(); }
            }
            foreach (var f in col) f.Quantize(new QuantizeSettings { Colors = 256, DitherMethod = DitherMethod.FloydSteinberg });
            var gif = Paths.Unique(dir, "Slideshow", "gif");
            col.Write(gif, MagickFormat.Gif);
            return (gif, webp, ok.Count, $"{w}×{h}");
        }
        finally { foreach (var t in thumbs) t?.Dispose(); }
    }

    /// <summary>Letterbox one picture into a w×h animation frame, 1 s, looping forever.</summary>
    static void Frame(MagickImage f, uint w, uint h)
    {
        f.Resize(new MagickGeometry(w, h));
        f.BackgroundColor = MagickColors.Black;
        f.Alpha(AlphaAction.Remove);
        f.Extent(w, h, Gravity.Center);
        f.ResetPage();
        f.AnimationDelay = 100;
        f.AnimationTicksPerSecond = 100;
        f.AnimationIterations = 0;
        f.GifDisposeMethod = GifDisposeMethod.None;
    }

    // ---------- compare ----------

    public static async Task Compare(Job job)
    {
        if (job.Inputs.Count != 2) throw new InvalidOperationException(L.T("Compare needs exactly two images"));
        var r = await Task.Run(() => Diff(job.Inputs[0], job.Inputs[1]), job.Token);
        string pct = r.differ == 0 ? "0%" : r.share < 0.0001 ? "<0.01%" : r.share < 0.01 ? $"{r.share * 100:0.##}%" : $"{r.share * 100:0.#}%";
        job.Summary = L.T("{0} of pixels differ", pct) + (r.areas > 0 ? " · " + L.N(r.areas, "{0} area", "{0} areas") : "") + (r.scaled ? " · " + L.T("sizes differed") : "");
        if (r.dst == null) { job.Title = L.T("No differences"); return; }
        job.Outputs.Add(r.dst);
    }

    static (string? dst, long differ, double share, int areas, bool scaled) Diff(string fa, string fb)
    {
        using var a = Images.Load(fa);
        using var b = Images.Load(fb);
        bool scaled = a.Width != b.Width || a.Height != b.Height;
        if (scaled)
        {
            // a retina and a normal capture of the same thing compare fine at one size; different shapes don't
            double ra = a.Width / (double)a.Height, rb = b.Width / (double)b.Height;
            if (Math.Abs(ra / rb - 1) > 0.01)
                throw new InvalidOperationException(L.T("Different shapes ({0} and {1}): Compare needs two images of the same proportions", $"{a.Width}×{a.Height}", $"{b.Width}×{b.Height}"));
            b.Resize(new MagickGeometry(a.Width, a.Height) { IgnoreAspectRatio = true });
        }
        var (pa, w, h) = Images.ToBgra(a);
        var (pb, _, _) = Images.ToBgra(b);

        // a channel off by more than 10/255 counts: ignores JPEG noise, catches any real edit
        const int tol = 10;
        var mask = new bool[w * h];
        long differ = 0;
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 4;
            if (pa[o + 3] == 0 && pb[o + 3] == 0) continue;
            int d = Math.Max(Math.Max(Math.Abs(pa[o] - pb[o]), Math.Abs(pa[o + 1] - pb[o + 1])),
                             Math.Max(Math.Abs(pa[o + 2] - pb[o + 2]), Math.Abs(pa[o + 3] - pb[o + 3])));
            if (d > tol) { mask[i] = true; differ++; }
        }
        double share = differ / (double)Math.Max(1, (long)w * h);
        if (differ == 0) return (null, 0, 0, 0, scaled);

        // the first image faded to a light grey, changed pixels in strong red
        var outPx = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 4;
            if (mask[i]) { outPx[o] = 60; outPx[o + 1] = 40; outPx[o + 2] = 235; outPx[o + 3] = 255; continue; }
            double al = pa[o + 3] / 255.0;
            double lum = (0.114 * pa[o] + 0.587 * pa[o + 1] + 0.299 * pa[o + 2]) * al + 255 * (1 - al);
            byte v = (byte)(lum * 0.3 + 255 * 0.7);
            outPx[o] = outPx[o + 1] = outPx[o + 2] = v;
            outPx[o + 3] = 255;
        }
        using var img = Images.FromBgra(outPx, w, h);

        // boxes around the changed areas, so a few changed pixels can't hide in a big screenshot
        var boxes = Areas(mask, w, h);
        if (boxes.Count is > 0 and <= 60)
        {
            double stroke = Math.Clamp(Math.Max(w, h) / 500.0, 2, 6);
            var d = new Drawables().StrokeColor(new MagickColor("#EB283C")).StrokeWidth(stroke).FillColor(MagickColors.Transparent);
            foreach (var (x0, y0, x1, y1) in boxes) d.Rectangle(x0, y0, x1, y1);
            d.Draw(img);
        }
        var dst = Paths.Beside(fa, "png", "(diff)");
        img.Write(dst, MagickFormat.Png);
        return (dst, differ, share, boxes.Count, scaled);
    }

    /// <summary>Bounding boxes of the changed regions: 16 px blocks, joined when they touch.</summary>
    static List<(int x0, int y0, int x1, int y1)> Areas(bool[] mask, int w, int h)
    {
        const int bs = 16;
        int bw = (w + bs - 1) / bs, bh = (h + bs - 1) / bs;
        var hit = new bool[bw * bh];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (mask[y * w + x]) hit[y / bs * bw + x / bs] = true;

        var seen = new bool[bw * bh];
        var boxes = new List<(int, int, int, int)>();
        var stack = new Stack<int>();
        for (int s = 0; s < hit.Length; s++)
        {
            if (!hit[s] || seen[s]) continue;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = 0, maxY = 0;
            stack.Push(s);
            seen[s] = true;
            while (stack.Count > 0)
            {
                int c = stack.Pop(), cx = c % bw, cy = c / bw;
                minX = Math.Min(minX, cx); maxX = Math.Max(maxX, cx); minY = Math.Min(minY, cy); maxY = Math.Max(maxY, cy);
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= bw || ny >= bh) continue;
                        int nb = ny * bw + nx;
                        if (hit[nb] && !seen[nb]) { seen[nb] = true; stack.Push(nb); }
                    }
            }
            const int pad = 6;
            boxes.Add((Math.Max(0, minX * bs - pad), Math.Max(0, minY * bs - pad),
                       Math.Min(w - 1, (maxX + 1) * bs + pad), Math.Min(h - 1, (maxY + 1) * bs + pad)));
        }
        return boxes;
    }
}
