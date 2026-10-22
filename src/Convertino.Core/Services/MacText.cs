using System.Diagnostics;
using System.Text;

namespace Convertino.Services;

/// <summary>
/// Word documents on a Mac with no office suite: macOS's own text system does it. <c>textutil</c> converts between
/// DOCX, DOC, RTF, ODT, HTML and plain text, and PDF comes from AppKit's print engine (the document laid out and
/// "printed" to a file, paginated the way TextEdit prints) driven by a small JavaScript-for-Automation script.
/// Plain layouts come out well; complex ones (floating pictures, text boxes, tracked changes) look better with LibreOffice,
/// which is used instead whenever it's installed.
/// </summary>
public sealed class MacText : IOffice
{
    const string TextUtil = "/usr/bin/textutil", OsaScript = "/usr/bin/osascript";
    static readonly SemaphoreSlim Gate = new(2, 2);

    public string Name => "macOS";

    public static MacText? Find() => OperatingSystem.IsMacOS() && File.Exists(TextUtil) && File.Exists(OsaScript) ? new MacText() : null;

    public bool Supports(Kind kind) => kind is Kind.Word or Kind.Text or Kind.Markdown;

    public async Task Convert(Job job, string ext, Func<string, string>? prepare = null)
    {
        ext = ext.ToLowerInvariant();
        for (int i = 0; i < job.Inputs.Count; i++)
        {
            job.Token.ThrowIfCancellationRequested();
            job.Report(i, job.Inputs.Count, 0.1);
            var src = job.Inputs[i];
            var prepared = prepare?.Invoke(src);
            var work = Work();
            try
            {
                var produced = await Make(prepared ?? src, ext, work, job.Token);
                var dst = Paths.Beside(src, ext);
                File.Move(produced, dst);
                job.Outputs.Add(dst);
            }
            finally
            {
                if (prepared != null) try { File.Delete(prepared); } catch { }
                try { Directory.Delete(work, true); } catch { }
            }
        }
        job.Summary ??= Images.SizeSummary(job);
    }

    /// <summary>Document → page images (via a temporary PDF).</summary>
    public async Task ToImages(Job job, string ext)
    {
        for (int i = 0; i < job.Inputs.Count; i++)
        {
            var src = job.Inputs[i];
            job.Report(i, job.Inputs.Count, 0.1);
            var work = Work();
            try
            {
                var pdf = await Make(src, "pdf", work, job.Token);
                await Pdfs.SaveImages(job, src, pdf, ext, 170);
            }
            finally { try { Directory.Delete(work, true); } catch { } }
        }
    }

    static string Work()
    {
        var d = Path.Combine(Paths.Temp, "mactext-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Converts <paramref name="input"/> into <paramref name="work"/> and returns the file it made.</summary>
    static async Task<string> Make(string input, string ext, string work, CancellationToken ct)
    {
        var inExt = Kinds.Ext(input);
        // plain text and HTML say nothing about their encoding: read them as UTF-8 when they are (Markdown's HTML always is)
        bool utf8 = inExt is "html" or "htm" || (inExt is "txt" or "log" or "md" or "markdown" && IsUtf8(input));
        if (ext != "pdf")
        {
            var outFile = Path.Combine(work, "out." + ext);
            var args = new List<string>();
            if (utf8) args.AddRange(["-inputencoding", "UTF-8"]);
            args.AddRange(["-convert", ext == "txt" ? "txt" : ext]);
            if (ext is "txt" or "html") args.AddRange(["-encoding", "UTF-8"]);
            args.AddRange(["-output", outFile, input]);
            await Run(TextUtil, args, ct);
            return outFile;
        }

        // PDF: rich text first when the source is plain (so the print engine gets the encoding right), then print it
        var printable = input;
        if (inExt is not ("docx" or "doc" or "rtf" or "rtfd" or "odt" or "wordml" or "webarchive"))
        {
            printable = Path.Combine(work, "in.rtf");
            var args = new List<string>();
            if (utf8) args.AddRange(["-inputencoding", "UTF-8"]);
            args.AddRange(["-convert", "rtf", "-output", printable, input]);
            await Run(TextUtil, args, ct);
        }
        var script = Path.Combine(work, "print.js");
        await File.WriteAllTextAsync(script, PrintScript, ct);
        var pdf = Path.Combine(work, "out.pdf");
        await Run(OsaScript, ["-l", "JavaScript", script, Path.GetFullPath(printable), pdf], ct);
        if (!File.Exists(pdf) || new FileInfo(pdf).Length == 0) throw new IOException(L.En("macOS couldn't print this document to PDF"));
        return pdf;
    }

    static bool IsUtf8(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return true;
        }
        catch { return false; }
    }

    static async Task Run(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        await Gate.WaitAsync(ct);
        try
        {
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync(ct);
            _ = p.StandardOutput.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
            if (p.ExitCode != 0)
            {
                var msg = (await err).Trim();
                Log.Error($"{Path.GetFileName(exe)} exit {p.ExitCode}: {msg}");
                throw new IOException(msg.Length > 0 ? L.En("macOS couldn't convert this document: {0}", msg) : L.En("macOS couldn't convert this document"));
            }
        }
        finally { Gate.Release(); }
    }

    /// <summary>osascript -l JavaScript print.js in out.pdf: the document laid out by AppKit and printed to a PDF file (A4 or Letter as the Mac is set).</summary>
    const string PrintScript = """
        ObjC.import('AppKit');
        function run(argv) {
          const err = Ref();
          const text = $.NSAttributedString.alloc.initWithURLOptionsDocumentAttributesError($.NSURL.fileURLWithPath(argv[0]), $({}), null, err);
          if (!text || text.isNil()) throw new Error('cannot read the document');
          const info = $.NSPrintInfo.sharedPrintInfo.copy;
          info.jobDisposition = $.NSPrintSaveJob;
          info.dictionary.setObjectForKey($.NSURL.fileURLWithPath(argv[1]), $.NSPrintJobSavingURL);
          info.horizontalPagination = $.NSPrintingPaginationModeFit;
          info.verticalPagination = $.NSPrintingPaginationModeAutomatic;
          info.verticallyCentered = false;
          info.leftMargin = 72; info.rightMargin = 72; info.topMargin = 72; info.bottomMargin = 72;
          const width = info.paperSize.width - info.leftMargin - info.rightMargin;
          const view = $.NSTextView.alloc.initWithFrame($.NSMakeRect(0, 0, width, info.paperSize.height));
          view.textStorage.setAttributedString(text);
          view.verticallyResizable = true;
          view.horizontallyResizable = false;
          view.textContainer.containerSize = $.NSMakeSize(width, 1e7);
          view.textContainer.widthTracksTextView = true;
          view.layoutManager.ensureLayoutForTextContainer(view.textContainer);
          view.sizeToFit;
          const op = $.NSPrintOperation.printOperationWithViewPrintInfo(view, info);
          op.showsPrintPanel = false;
          op.showsProgressPanel = false;
          if (!op.runOperation) throw new Error('printing failed');
          return 'ok';
        }
        """;
}
