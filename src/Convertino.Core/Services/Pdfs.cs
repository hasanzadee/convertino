using System.IO;
using System.Text;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Convertino.Services;

public static class Pdfs
{
    /// <summary>Render every page to PNG bytes at the given DPI (platform renderer).</summary>
    public static Task<List<byte[]>> RenderPages(string path, double dpi, CancellationToken ct, Action<double>? progress = null, int maxPages = int.MaxValue)
        => Platform.Current.RenderPdf(Path.GetFullPath(path), dpi, ct, progress, maxPages);

    public static async Task ToImages(Job job, string ext)
    {
        int n = job.Inputs.Count;
        for (int f = 0; f < n; f++)
        {
            int idx = f;
            await SaveImages(job, job.Inputs[f], job.Inputs[f], ext, 200, p => job.Report(idx, n, p * 0.8));
        }
    }

    /// <summary>
    /// Render <paramref name="pdf"/> and save the pages beside <paramref name="src"/>:
    /// one page → "name.png", several → folder "name (PNG)/name - 01.png".
    /// </summary>
    public static async Task SaveImages(Job job, string src, string pdf, string ext, double dpi, Action<double>? progress = null)
    {
        var pages = await RenderPages(pdf, dpi, job.Token, progress);
        var stem = Path.GetFileNameWithoutExtension(src);
        var dir = Path.GetDirectoryName(src)!;
        string outDir = dir;
        if (pages.Count > 1)
        {
            outDir = Paths.UniqueDir(dir, $"{stem} ({ext.ToUpperInvariant()})");
            Directory.CreateDirectory(outDir);
        }
        await Task.Run(() =>
        {
            for (int i = 0; i < pages.Count; i++)
            {
                var dst = pages.Count == 1 ? Paths.Beside(src, ext) : Path.Combine(outDir, $"{stem} - {i + 1:D2}.{ext}");
                if (ext == "png") File.WriteAllBytes(dst, pages[i]);
                else
                {
                    using var img = new MagickImage(pages[i]);
                    Images.PrepareFor(img, ext);
                    img.Write(dst, Images.FormatOf(ext));
                }
                if (pages.Count == 1) job.Outputs.Add(dst);
            }
        }, job.Token);
        if (pages.Count > 1) job.Outputs.Add(outDir);
        job.Summary = pages.Count == 1 ? Images.SizeSummary(job) : L.N(pages.Count, "{0} page", "{0} pages");
    }

    public static string ExtractText(string path)
    {
        var sb = new StringBuilder();
        using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
        foreach (var page in doc.GetPages())
        {
            var t = ContentOrderTextExtractor.GetText(page);
            sb.AppendLine(t.TrimEnd());
            sb.AppendLine();
        }
        return sb.ToString().Trim();
    }

    public static async Task ToText(Job job)
    {
        int n = job.Inputs.Count;
        bool ocrUsed = false;
        for (int i = 0; i < n; i++)
        {
            job.Report(i, n);
            var src = job.Inputs[i];
            string text = "";
            int pageCount = 1;
            try
            {
                text = await Task.Run(() => ExtractText(src), job.Token);
                using var d = UglyToad.PdfPig.PdfDocument.Open(src);
                pageCount = d.NumberOfPages;
            }
            catch (Exception ex) { Log.Error("pdfpig", ex); }
            if (text.Length < 25 * pageCount)
            {
                // scanned PDF — read it with Windows OCR instead
                var pages = await RenderPages(src, 220, job.Token, p => job.Report(i, n, p * 0.5));
                var sb = new StringBuilder();
                foreach (var png in pages) sb.AppendLine((await Platform.Current.Ocr(png)).Text).AppendLine();
                text = sb.ToString().Trim();
                ocrUsed = true;
            }
            var dst = Paths.Beside(src, "txt");
            await File.WriteAllTextAsync(dst, text, new UTF8Encoding(true), job.Token);
            job.Outputs.Add(dst);
        }
        job.Summary = ocrUsed ? L.T("Scanned pages — read with OCR") : Images.SizeSummary(job);
    }

    public static async Task Ocr_(Job job)
    {
        var all = new StringBuilder();
        int n = job.Inputs.Count;
        for (int i = 0; i < n; i++)
        {
            var src = job.Inputs[i];
            var pages = await RenderPages(src, 220, job.Token, p => job.Report(i, n, p * 0.5));
            for (int p = 0; p < pages.Count; p++)
            {
                job.Report(i, n, 0.5 + 0.5 * p / pages.Count);
                all.AppendLine((await Platform.Current.Ocr(pages[p])).Text).AppendLine();
            }
        }
        Platform.Current.SetClipboardText(all.ToString().Trim());
        job.Title = L.T("Text copied");
        job.Summary = Ocr.Preview(all.ToString());
    }

    public static async Task ImagesToPdf(Job job, bool merge)
    {
        var inputs = job.Inputs;
        if (!merge)
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                job.Report(i, inputs.Count);
                var dst = Paths.Beside(inputs[i], "pdf");
                await Task.Run(() => WriteImagesPdf([inputs[i]], dst, job), job.Token);
                job.Outputs.Add(dst);
            }
        }
        else
        {
            var first = inputs[0];
            var dir = Path.GetDirectoryName(first)!;
            var name = inputs.Count == 1 ? Path.GetFileNameWithoutExtension(first) : $"{Path.GetFileNameWithoutExtension(first)} +{inputs.Count - 1}";
            var dst = Paths.Unique(dir, name, "pdf");
            await Task.Run(() => WriteImagesPdf(inputs, dst, job), job.Token);
            job.Outputs.Add(dst);
        }
        job.Summary = Images.SizeSummary(job);
    }

    static void WriteImagesPdf(IReadOnlyList<string> images, string dst, Job job)
    {
        using var doc = new PdfDocument();
        for (int i = 0; i < images.Count; i++)
        {
            job.Token.ThrowIfCancellationRequested();
            job.Report(i, images.Count);
            var src = images[i];
            var ext = Kinds.Ext(src);
            byte[] data;
            int w, h;
            if (ext is "jpg" or "jpeg" or "jfif")
            {
                using var probe = new MagickImage();
                probe.Ping(src);
                var o = probe.GetExifProfile()?.GetValue(ExifTag.Orientation)?.Value ?? 1;
                if (o <= 1)
                {
                    data = File.ReadAllBytes(src);
                    w = (int)probe.Width; h = (int)probe.Height;
                }
                else (data, w, h) = Encode(src, "jpg");
            }
            else (data, w, h) = Encode(src, ext is "png" or "gif" or "bmp" or "ico" or "svg" ? "png" : "jpg");

            XImage xi;
            try { xi = XImage.FromStream(new MemoryStream(data)); }
            catch (Exception ex)
            {
                // PDFsharp's own JPEG reader refuses some perfectly normal files (e.g. no JFIF/Exif segment first):
                // hand it a freshly encoded baseline JPEG instead
                Log.Info($"pdf: re-encoding {Path.GetFileName(src)} ({ex.Message})");
                (data, w, h) = Encode(src, "jpg");
                xi = XImage.FromStream(new MemoryStream(data));
            }
            using (xi)
            {
                var page = doc.AddPage();
                page.Width = XUnit.FromPoint(w * 0.75);
                page.Height = XUnit.FromPoint(h * 0.75);
                using var gfx = XGraphics.FromPdfPage(page);
                gfx.DrawImage(xi, 0, 0, page.Width.Point, page.Height.Point);
            }
        }
        doc.Info.Creator = "Convertino";
        doc.Save(dst);
    }

    static (byte[], int, int) Encode(string src, string ext)
    {
        using var img = Images.Load(src);
        Images.PrepareFor(img, ext, ext == "jpg" ? 92 : null);
        return (img.ToByteArray(Images.FormatOf(ext)), (int)img.Width, (int)img.Height);
    }

    public static async Task Merge(Job job)
    {
        var first = job.Inputs[0];
        var dst = Paths.Unique(Path.GetDirectoryName(first)!, Path.GetFileNameWithoutExtension(first) + " (merged)", "pdf");
        await Task.Run(() =>
        {
            using var output = new PdfDocument();
            for (int i = 0; i < job.Inputs.Count; i++)
            {
                job.Token.ThrowIfCancellationRequested();
                job.Report(i, job.Inputs.Count);
                var src = job.Inputs[i];
                if (Kinds.Of(src) == Kind.Image)
                {
                    var tmp = Path.Combine(Path.GetTempPath(), $"petal-{Guid.NewGuid():N}.pdf");
                    WriteImagesPdf([src], tmp, job);
                    using (var imp = PdfReader.Open(tmp, PdfDocumentOpenMode.Import))
                        foreach (var p in imp.Pages) output.AddPage(p);
                    File.Delete(tmp);
                    continue;
                }
                using var input = PdfReader.Open(src, PdfDocumentOpenMode.Import);
                foreach (var p in input.Pages) output.AddPage(p);
            }
            output.Save(dst);
        }, job.Token);
        job.Outputs.Add(dst);
        job.Summary = L.N(job.Inputs.Count, "{0} file → 1 PDF", "{0} files → 1 PDF") + $" · {Paths.HumanSize(Paths.SizeOf(dst))}";
    }

    public static async Task Split(Job job)
    {
        foreach (var src in job.Inputs)
        {
            var stem = Path.GetFileNameWithoutExtension(src);
            var outDir = Paths.UniqueDir(Path.GetDirectoryName(src)!, $"{stem} (pages)");
            int count = 0;
            await Task.Run(() =>
            {
                using var input = PdfReader.Open(src, PdfDocumentOpenMode.Import);
                Directory.CreateDirectory(outDir);
                count = input.PageCount;
                for (int i = 0; i < input.PageCount; i++)
                {
                    job.Token.ThrowIfCancellationRequested();
                    job.Report(i, input.PageCount);
                    using var one = new PdfDocument();
                    one.AddPage(input.Pages[i]);
                    one.Save(Path.Combine(outDir, $"{stem} - {i + 1:D2}.pdf"));
                }
            }, job.Token);
            job.Outputs.Add(outDir);
            job.Summary = L.N(count, "{0} page", "{0} pages");
        }
    }

    public static async Task Rotate(Job job)
    {
        foreach (var src in job.Inputs)
        {
            var dst = Paths.Beside(src, "pdf", "(rotated)");
            await Task.Run(() =>
            {
                using var doc = PdfReader.Open(src, PdfDocumentOpenMode.Modify);
                foreach (var p in doc.Pages) p.Rotate = (p.Rotate + 90) % 360;
                doc.Save(dst);
            }, job.Token);
            job.Outputs.Add(dst);
        }
        job.Summary = L.T("Rotated 90° clockwise");
    }

    // ---------- compress (Ghostscript, when installed) ----------

    static string? _gs;
    static bool _gsSearched;

    /// <summary>Ghostscript's console binary: optional, like ffmpeg (AGPL, never bundled).</summary>
    public static string? Ghostscript
    {
        get
        {
            if (_gsSearched) return _gs;
            _gsSearched = true;
            var candidates = new List<string>();
            if (OperatingSystem.IsWindows())
            {
                foreach (var name in new[] { "gswin64c", "gswin32c", "gs" })
                    if (Paths.Which(name) is { } p) candidates.Add(p);
                foreach (var pf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
                {
                    try
                    {
                        var root = Path.Combine(Environment.GetFolderPath(pf), "gs");
                        if (Directory.Exists(root))
                            candidates.AddRange(Directory.GetDirectories(root).OrderByDescending(d => Version.TryParse(Path.GetFileName(d).TrimStart('g', 's', 'G', 'S'), out var v) ? v : new Version(0, 0))
                                .SelectMany(d => new[] { Path.Combine(d, "bin", "gswin64c.exe"), Path.Combine(d, "bin", "gswin32c.exe") }));
                    }
                    catch { }
                }
            }
            else
            {
                if (Paths.Which("gs") is { } p) candidates.Add(p);
                candidates.AddRange(["/usr/bin/gs", "/usr/local/bin/gs", "/opt/homebrew/bin/gs"]);
            }
            _gs = candidates.FirstOrDefault(File.Exists);
            Log.Info($"ghostscript: {_gs ?? "not found"}");
            return _gs;
        }
    }

    public static bool CanCompress => Ghostscript != null;

    /// <summary>Re-distill with Ghostscript's /ebook preset; keeps the result only when it's really smaller.</summary>
    public static async Task Compress(Job job)
    {
        var gs = Ghostscript ?? throw new InvalidOperationException(OperatingSystem.IsWindows()
            ? L.T("Ghostscript not found — install it with: {0}", "winget install ArtifexSoftware.GhostScript")
            : OperatingSystem.IsMacOS() ? L.T("Ghostscript not found — install it with: {0}", "brew install ghostscript")
            : L.T("Ghostscript not found — install it with your package manager (e.g. sudo apt install ghostscript)"));
        long before = 0, after = 0;
        int n = job.Inputs.Count, smaller = 0;
        for (int i = 0; i < n; i++)
        {
            var src = job.Inputs[i];
            int pages = 1;
            var dst = Paths.Beside(src, "pdf", "(compressed)");
            var tmp = Path.Combine(Path.GetDirectoryName(dst)!, $".{Guid.NewGuid():N}.pdf");
            int idx = i;
            try
            {
                await RunTool(gs, ["-sDEVICE=pdfwrite", "-dCompatibilityLevel=1.6", "-dPDFSETTINGS=/ebook", "-dNOPAUSE", "-dBATCH", "-dSAFER",
                    "-dDetectDuplicateImages=true", "-dCompressFonts=true", $"-sOutputFile={tmp.Replace("%", "%%")}", src],
                    line =>
                    {
                        // "Processing pages 1 through 12." then "Page 1", "Page 2" …
                        if (line.StartsWith("Processing pages ") && line.Split(' ') is [.., var last] && int.TryParse(last.TrimEnd('.'), out var total)) pages = Math.Max(1, total);
                        else if (line.StartsWith("Page ") && int.TryParse(line[5..].Trim(), out var p)) job.Report(idx, n, (double)p / pages);
                    },
                    job.Token);
                long a = Paths.SizeOf(src), b = Paths.SizeOf(tmp);
                before += a;
                if (b > 0 && b < a * 0.97)
                {
                    File.Move(tmp, dst);
                    job.Outputs.Add(dst);
                    after += b;
                    smaller++;
                }
                else after += a;
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }
        if (smaller == 0)
        {
            job.Title = n == 1 ? L.T("Already as small as it gets") : L.T("Already as small as they get");
            job.Summary = $"{Paths.HumanSize(before)} · " + L.T("Ghostscript couldn't shrink it");
            return;
        }
        int pct = before > 0 ? (int)Math.Round(100.0 * (before - after) / before) : 0;
        job.Summary = $"{Paths.HumanSize(before)} → {Paths.HumanSize(after)} (−{pct}%)";
    }

    static async Task RunTool(string exe, IEnumerable<string> args, Action<string>? onLine, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new System.Diagnostics.Process { StartInfo = psi };
        var err = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
        p.Start();
        try { p.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch { }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using (ct.Register(() => { try { p.Kill(true); } catch { } }))
            await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
        {
            var msg = err.ToString().Trim();
            throw new InvalidOperationException(L.T("{0} failed ({1})", Path.GetFileNameWithoutExtension(exe), p.ExitCode) + (msg.Length > 0 ? ": " + msg.Split('\n')[^1].Trim() : ""));
        }
    }

    // ---------- images out of a PDF ----------

    /// <summary>Every embedded image at its original quality: JPEG / JPEG 2000 streams as they are, the rest as PNG.</summary>
    public static async Task ExtractImages(Job job)
    {
        int total = 0, skipped = 0;
        for (int f = 0; f < job.Inputs.Count; f++)
        {
            var src = job.Inputs[f];
            var stem = Path.GetFileNameWithoutExtension(src);
            var outDir = Paths.UniqueDir(Path.GetDirectoryName(src)!, $"{stem} (images)");
            int count = 0;
            int fi = f;
            await Task.Run(() =>
            {
                using var doc = UglyToad.PdfPig.PdfDocument.Open(src);
                var seen = new HashSet<string>();
                int pageCount = doc.NumberOfPages;
                for (int p = 1; p <= pageCount; p++)
                {
                    job.Token.ThrowIfCancellationRequested();
                    job.Report(fi, job.Inputs.Count, (double)(p - 1) / pageCount);
                    int k = 0;
                    foreach (var img in doc.GetPage(p).GetImages())
                    {
                        if (img.IsImageMask) continue; // 1-bit stencils, not pictures
                        var raw = img.RawMemory.ToArray();
                        byte[]? data = null;
                        string ext = "png";
                        var filter = Filter(img);
                        if (filter == "DCTDecode" && raw.Length > 3 && raw[0] == 0xFF && raw[1] == 0xD8) { data = raw; ext = "jpg"; }
                        else if (filter == "JPXDecode" && raw.Length > 12) { data = raw; ext = raw[0] == 0xFF && raw[1] == 0x4F ? "j2k" : "jp2"; }
                        else if (img.TryGetPng(out var png)) data = png;
                        if (data == null) { skipped++; continue; }
                        if (!seen.Add(System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)))) continue;
                        if (count == 0) Directory.CreateDirectory(outDir);
                        k++;
                        File.WriteAllBytes(Path.Combine(outDir, $"{stem} - p{p:D2}-{k}.{ext}"), data);
                        count++;
                    }
                }
            }, job.Token);
            total += count;
            if (count > 0) job.Outputs.Add(outDir);
        }
        if (total == 0)
        {
            job.Title = L.T("No images in this PDF");
            job.Summary = skipped > 0 ? L.N(skipped, "{0} image uses an encoding that can't be saved", "{0} images use an encoding that can't be saved") : L.T("Only text and vector graphics");
            return;
        }
        job.Summary = L.N(total, "{0} image", "{0} images") + (skipped > 0 ? " · " + L.N(skipped, "{0} skipped (unsupported encoding)", "{0} skipped (unsupported encoding)") : "");
    }

    /// <summary>The image's only filter ("DCTDecode", "JPXDecode" …), or "" when there are none or several.</summary>
    static string Filter(UglyToad.PdfPig.Content.IPdfImage img)
    {
        if (!img.ImageDictionary.TryGet(UglyToad.PdfPig.Tokens.NameToken.Filter, out var t)) return "";
        return t switch
        {
            UglyToad.PdfPig.Tokens.NameToken n => n.Data,
            UglyToad.PdfPig.Tokens.ArrayToken { Data.Count: 1 } a when a.Data[0] is UglyToad.PdfPig.Tokens.NameToken n1 => n1.Data,
            _ => "",
        };
    }

    // ---------- cover ----------

    public static async Task Cover(Job job)
    {
        int n = job.Inputs.Count;
        for (int i = 0; i < n; i++)
        {
            var src = job.Inputs[i];
            int idx = i;
            var pages = await RenderPages(src, 150, job.Token, p => job.Report(idx, n, p), maxPages: 1);
            if (pages.Count == 0) throw new InvalidOperationException(L.T("Couldn't render the first page"));
            var dst = Paths.Beside(src, "png", "(cover)");
            await File.WriteAllBytesAsync(dst, pages[0], job.Token);
            job.Outputs.Add(dst);
        }
        job.Summary = n == 1 ? L.T("First page") + " · " + Paths.HumanSize(Paths.SizeOf(job.Outputs[0])) : L.N(n, "{0} cover", "{0} covers");
    }

    // ---------- page numbers ----------

    /// <summary>
    /// "3 / 12" centred at the bottom of every page, in the viewer's built-in Helvetica: no font file to embed, so it works
    /// the same on every OS. Rotated pages get the number at the edge that is at the bottom when viewed.
    /// </summary>
    public static async Task PageNumbers(Job job)
    {
        foreach (var src in job.Inputs)
        {
            var dst = Paths.Beside(src, "pdf", "(numbered)");
            int pages = 0;
            await Task.Run(() =>
            {
                using var doc = PdfReader.Open(src, PdfDocumentOpenMode.Modify);
                pages = doc.PageCount;
                var font = new PdfDictionary(doc);
                font.Elements.SetName("/Type", "/Font");
                font.Elements.SetName("/Subtype", "/Type1");
                font.Elements.SetName("/BaseFont", "/Helvetica");
                font.Elements.SetName("/Encoding", "/WinAnsiEncoding");
                doc.Internals.AddObject(font);
                for (int i = 0; i < pages; i++)
                {
                    job.Token.ThrowIfCancellationRequested();
                    job.Report(i, pages);
                    StampNumber(doc.Pages[i], font, $"{i + 1} / {pages}");
                }
                doc.Save(dst);
            }, job.Token);
            job.Outputs.Add(dst);
            job.Summary = L.N(pages, "{0} page numbered", "{0} pages numbered");
        }
    }

    static void StampNumber(PdfPage page, PdfDictionary font, string text)
    {
        const string name = "/CvtPageNo";
        var resources = page.Resources;
        var fonts = resources.Elements.GetDictionary("/Font");
        if (fonts == null)
        {
            fonts = new PdfDictionary(page.Owner);
            resources.Elements["/Font"] = fonts;
        }
        fonts.Elements[name] = font.Reference;

        var box = page.Elements.ContainsKey("/CropBox") ? page.CropBox : page.MediaBox;
        double x1 = Math.Min(box.X1, box.X2), y1 = Math.Min(box.Y1, box.Y2), x2 = Math.Max(box.X1, box.X2), y2 = Math.Max(box.Y1, box.Y2);
        double w = x2 - x1, h = y2 - y1;
        int rot = ((page.Rotate % 360) + 360) % 360;
        double viewW = rot is 90 or 270 ? h : w;
        double size = Math.Clamp(viewW / 60, 8, 48), margin = size * 2.2;
        // Helvetica advance widths (per 1000 em): digits 556, space and slash 278
        double tw = text.Sum(c => char.IsDigit(c) ? 556 : 278) * size / 1000;
        // text matrix per rotation: the run reads left-to-right at the bottom of the page as displayed
        var (a, b, c, d, e, f) = rot switch
        {
            90 => (0.0, 1.0, -1.0, 0.0, x2 - margin, y1 + h / 2 - tw / 2),
            180 => (-1.0, 0.0, 0.0, -1.0, x1 + w / 2 + tw / 2, y2 - margin),
            270 => (0.0, -1.0, 1.0, 0.0, x1 + margin, y1 + h / 2 + tw / 2),
            _ => (1.0, 0.0, 0.0, 1.0, x1 + w / 2 - tw / 2, y1 + margin),
        };
        string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        // wrap the page's own content in q/Q so a graphics state it leaves behind can't move or recolour the number
        page.Contents.PrependContent().CreateStream(Encoding.ASCII.GetBytes("q\n"));
        page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(
            $"Q\nq BT {name} {F(size)} Tf 0.35 g {F(a)} {F(b)} {F(c)} {F(d)} {F(e)} {F(f)} Tm ({text}) Tj ET Q\n"));
    }

    public static async Task Clean(Job job)
    {
        foreach (var src in job.Inputs)
        {
            var dst = Paths.Beside(src, "pdf", "(clean)");
            await Task.Run(() =>
            {
                using var doc = PdfReader.Open(src, PdfDocumentOpenMode.Modify);
                var info = doc.Info;
                info.Title = ""; info.Author = ""; info.Subject = ""; info.Keywords = ""; info.Creator = "";
                foreach (var key in info.Elements.Keys.ToList())
                    if (key is not ("/Producer" or "/CreationDate" or "/ModDate")) info.Elements.Remove(key);
                doc.Internals.Catalog.Elements.Remove("/Metadata");
                doc.Save(dst);
            }, job.Token);
            job.Outputs.Add(dst);
        }
        job.Summary = L.T("Author, title & XMP metadata removed");
    }
}
