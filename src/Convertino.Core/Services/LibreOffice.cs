using System.Diagnostics;
using ImageMagick;

namespace Convertino.Services;

/// <summary>
/// Headless LibreOffice (<c>soffice --convert-to</c>) — the document engine on Linux and on Windows
/// machines without Microsoft Office. Runs with a private profile so it never fights a LibreOffice
/// window the user has open.
/// </summary>
public sealed class LibreOffice : IOffice
{
    readonly string _soffice;
    static readonly SemaphoreSlim Gate = new(1, 1);

    LibreOffice(string soffice) => _soffice = soffice;

    public string Name => "LibreOffice";

    public static LibreOffice? Find()
    {
        var p = Paths.Which("soffice") ?? Paths.Which("libreoffice");
        if (p == null && OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                var c = Path.Combine(root, "LibreOffice", "program", "soffice.exe");
                if (File.Exists(c)) { p = c; break; }
            }
        }
        if (p == null && !OperatingSystem.IsWindows())
            foreach (var c in new[] { "/usr/bin/soffice", "/usr/lib/libreoffice/program/soffice", "/opt/libreoffice/program/soffice", "/snap/bin/libreoffice",
                         "/Applications/LibreOffice.app/Contents/MacOS/soffice", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications/LibreOffice.app/Contents/MacOS/soffice") })
                if (File.Exists(c)) { p = c; break; }
        return p == null ? null : new LibreOffice(p);
    }

    public bool Supports(Kind kind) => kind is Kind.Word or Kind.Excel or Kind.Slides or Kind.Text or Kind.Markdown or Kind.Pdf;

    static string Filter(Kind kind, string ext) => (kind, ext) switch
    {
        (Kind.Excel, "pdf") => "pdf:calc_pdf_Export",
        (Kind.Excel, "xlsx") => "xlsx:Calc MS Excel 2007 XML",
        (Kind.Excel, "csv") => "csv:Text - txt - csv (StarCalc):44,34,76,1",
        (Kind.Excel, "ods") => "ods",
        (Kind.Excel, "html") => "html:HTML (StarCalc)",
        (Kind.Slides, "pdf") => "pdf:impress_pdf_Export",
        (Kind.Slides, "pptx") => "pptx:Impress MS PowerPoint 2007 XML",
        (Kind.Slides, "odp") => "odp",
        (_, "pdf") => "pdf:writer_pdf_Export",
        (_, "docx") => "docx:MS Word 2007 XML",
        (_, "doc") => "doc:MS Word 97",
        (_, "odt") => "odt",
        (_, "rtf") => "rtf",
        (_, "txt") => "txt:Text (encoded):UTF8",
        (_, "html") => "html:XHTML Writer File:UTF8",
        _ => ext,
    };

    public async Task Convert(Job job, string ext, Func<string, string>? prepare = null)
    {
        await Gate.WaitAsync(job.Token);
        try
        {
            for (int i = 0; i < job.Inputs.Count; i++)
            {
                job.Token.ThrowIfCancellationRequested();
                job.Report(i, job.Inputs.Count, 0.1);
                var src = job.Inputs[i];
                var kind = Kinds.Of(src);
                var prepared = prepare?.Invoke(src);
                try
                {
                    if (kind == Kind.Slides && ext == "png")
                    {
                        job.Outputs.Add(await SlidesToPng(src, job.Token));
                        continue;
                    }
                    string input = prepared ?? src;
                    string? infilter = kind switch
                    {
                        Kind.Pdf => "writer_pdf_import",
                        Kind.Markdown => "HTML (StarWriter)",
                        _ => null,
                    };
                    var produced = await RunConvert(input, Filter(kind, ext), ext, infilter, job.Token);
                    var dst = Paths.Beside(src, ext);
                    File.Move(produced, dst);
                    job.Outputs.Add(dst);
                }
                finally
                {
                    if (prepared != null) try { File.Delete(prepared); } catch { }
                }
            }
            job.Summary ??= Images.SizeSummary(job);
        }
        finally { Gate.Release(); }
    }

    async Task<string> SlidesToPng(string src, CancellationToken ct)
    {
        var pdf = await RunConvert(src, "pdf:impress_pdf_Export", "pdf", null, ct);
        try
        {
            var pages = await Pdfs.RenderPages(pdf, 150, ct);
            var dir = Paths.UniqueDir(Path.GetDirectoryName(src)!, Path.GetFileNameWithoutExtension(src) + " (slides)");
            Directory.CreateDirectory(dir);
            for (int p = 0; p < pages.Count; p++)
                await File.WriteAllBytesAsync(Path.Combine(dir, $"Slide {p + 1:D2}.png"), pages[p], ct);
            return dir;
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(pdf)!, true); } catch { } }
    }

    /// <summary>Converts into a fresh temp folder and returns the produced file.</summary>
    async Task<string> RunConvert(string input, string filter, string ext, string? infilter, CancellationToken ct)
    {
        var outDir = Path.Combine(Paths.Temp, "lo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outDir);
        var profile = new Uri(Path.Combine(Paths.Temp, "lo-profile")).AbsoluteUri;
        var inf = infilter != null ? $"--infilter=\"{infilter}\" " : "";
        var args = $"-env:UserInstallation={profile} --headless --norestore --nologo --nolockcheck {inf}--convert-to \"{filter}\" --outdir \"{outDir}\" \"{input}\"";
        var psi = new ProcessStartInfo(_soffice, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync(ct);
        _ = p.StandardOutput.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        var produced = Directory.GetFiles(outDir).FirstOrDefault(f => f.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase))
                       ?? Directory.GetFiles(outDir).FirstOrDefault();
        if (produced == null)
        {
            var err = (await errTask).Trim();
            throw new IOException(L.T("LibreOffice couldn't convert this file") + (err.Length > 0 ? $": {err}" : ""));
        }
        return produced;
    }

    /// <summary>Document → page images (via a temporary PDF).</summary>
    public async Task ToImages(Job job, string ext)
    {
        for (int i = 0; i < job.Inputs.Count; i++)
        {
            var src = job.Inputs[i];
            job.Report(i, job.Inputs.Count, 0.1);
            var pdf = await RunConvert(src, Filter(Kinds.Of(src), "pdf"), "pdf", null, job.Token);
            try { await Pdfs.SaveImages(job, src, pdf, ext, 170); }
            finally { try { Directory.Delete(Path.GetDirectoryName(pdf)!, true); } catch { } }
        }
    }
}

/// <summary>PDF page rendering with poppler's <c>pdftoppm</c>, or ImageMagick + Ghostscript as a fallback.</summary>
public static class Poppler
{
    public static async Task<List<byte[]>> Render(string path, double dpi, CancellationToken ct, Action<double>? progress, int maxPages)
    {
        var pdftoppm = Paths.Which("pdftoppm");
        if (pdftoppm != null)
        {
            var dir = Path.Combine(Paths.Temp, "pdf-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                var last = maxPages == int.MaxValue ? "" : $"-l {maxPages} ";
                var args = $"-png -r {(int)Math.Round(dpi)} {last}\"{path}\" \"{Path.Combine(dir, "p")}\"";
                progress?.Invoke(0.1);
                var (code, _, err) = await Task.Run(() => Tesseract.Run(pdftoppm, args, null, 300000), ct);
                if (code != 0) throw new IOException("pdftoppm: " + err.Trim());
                var files = Directory.GetFiles(dir, "p-*.png").OrderBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal).ToList();
                var list = new List<byte[]>();
                foreach (var f in files) list.Add(await File.ReadAllBytesAsync(f, ct));
                return list;
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // Ghostscript delegate
        return await Task.Run(() =>
        {
            var rs = new MagickReadSettings { Density = new Density(dpi), BackgroundColor = MagickColors.White };
            if (maxPages != int.MaxValue) { rs.FrameIndex = 0; rs.FrameCount = (uint)maxPages; }
            using var col = new MagickImageCollection(path, rs);
            var list = new List<byte[]>();
            foreach (var page in col)
            {
                ct.ThrowIfCancellationRequested();
                page.Alpha(AlphaAction.Remove);
                list.Add(page.ToByteArray(MagickFormat.Png));
            }
            return list;
        }, ct);
    }
}
