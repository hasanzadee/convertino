using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Markdig;

namespace Convertino.Services;

public static class Misc
{
    public static async Task Zip(Job job)
    {
        // "photos/" from a shell's tab completion would otherwise zip into the folder itself, as ".zip"
        var inputs = job.Inputs.Select(Path.TrimEndingDirectorySeparator).ToList();
        var first = inputs[0];
        var dir = Path.GetDirectoryName(first)!;
        string name = inputs.Count == 1
            ? (Directory.Exists(first) ? Path.GetFileName(first) : Path.GetFileNameWithoutExtension(first))
            : (Path.GetFileName(dir) is { Length: > 0 } d ? d : "Archive");
        var dst = Paths.Unique(dir, name, "zip");
        await Task.Run(() =>
        {
            var files = new List<(string full, string entry)>();
            foreach (var input in inputs)
            {
                if (Directory.Exists(input))
                {
                    var root = Path.GetDirectoryName(input)!;
                    foreach (var f in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories))
                        files.Add((f, Path.GetRelativePath(root, f)));
                }
                else files.Add((input, Path.GetFileName(input)));
            }
            using var zip = ZipFile.Open(dst, ZipArchiveMode.Create);
            for (int i = 0; i < files.Count; i++)
            {
                job.Token.ThrowIfCancellationRequested();
                job.Report(i, files.Count);
                var ext = Path.GetExtension(files[i].full).ToLowerInvariant();
                var level = ext is ".jpg" or ".jpeg" or ".png" or ".mp4" or ".mp3" or ".zip" or ".7z" or ".webp" or ".mkv" or ".docx" or ".xlsx" or ".pptx"
                    ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                zip.CreateEntryFromFile(files[i].full, files[i].entry.Replace('\\', '/'), level);
            }
        }, job.Token);
        job.Outputs.Add(dst);
        long before = job.Inputs.Sum(Paths.SizeOf), after = Paths.SizeOf(dst);
        job.Summary = $"{Paths.HumanSize(before)} → {Paths.HumanSize(after)}";
    }

    public static async Task Extract(Job job)
    {
        foreach (var src in job.Inputs)
        {
            var dst = Paths.UniqueDir(Path.GetDirectoryName(src)!, Path.GetFileNameWithoutExtension(src));
            await Task.Run(() => ZipFile.ExtractToDirectory(src, dst), job.Token);
            job.Outputs.Add(dst);
        }
        job.Summary = "Extracted";
    }

    public static Task CopyPath(Job job)
    {
        Platform.Current.SetClipboardText(string.Join(Environment.NewLine, job.Inputs));
        job.Title = job.Inputs.Count == 1 ? "Path copied" : $"{job.Inputs.Count} paths copied";
        job.Summary = job.Inputs.Count == 1 ? job.Inputs[0] : string.Join(", ", job.Inputs.Select(Path.GetFileName));
        return Task.CompletedTask;
    }

    public static async Task Sha256(Job job)
    {
        var lines = new List<string>();
        foreach (var f in job.Inputs.Where(File.Exists))
        {
            await using var s = File.OpenRead(f);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(s, job.Token)).ToLowerInvariant();
            lines.Add(job.Inputs.Count == 1 ? hash : $"{hash}  {Path.GetFileName(f)}");
        }
        Platform.Current.SetClipboardText(string.Join(Environment.NewLine, lines));
        job.Title = "SHA-256 copied";
        job.Summary = lines.Count == 1 ? lines[0][..24] + "…" : $"{lines.Count} hashes";
    }

    public static async Task Base64(Job job)
    {
        var f = job.Inputs[0];
        var bytes = await File.ReadAllBytesAsync(f, job.Token);
        var mime = Path.GetExtension(f).ToLowerInvariant() switch
        {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp",
            ".svg" => "image/svg+xml", ".pdf" => "application/pdf", ".json" => "application/json", ".txt" => "text/plain",
            ".woff2" => "font/woff2", ".ico" => "image/x-icon",
            _ => "application/octet-stream",
        };
        Platform.Current.SetClipboardText($"data:{mime};base64,{Convert.ToBase64String(bytes)}");
        job.Title = "Data URI copied";
        job.Summary = $"{mime} · {Paths.HumanSize(bytes.Length)}";
    }

    public static async Task CopyFiles(Job job)
    {
        Platform.Current.SetClipboardFiles(job.Inputs);
        job.Title = "Copied to clipboard";
        job.Summary = job.Inputs.Count == 1 ? Path.GetFileName(job.Inputs[0]) : $"{job.Inputs.Count} items";
        await Task.CompletedTask;
    }

    // ---------- Markdown ----------

    static readonly MarkdownPipeline Md = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    const string Css = """
        body{font-family:'Segoe UI',Calibri,sans-serif;max-width:820px;margin:40px auto;padding:0 24px;color:#1f2328;line-height:1.6;font-size:15px}
        h1,h2,h3{line-height:1.25;margin-top:1.6em}h1{font-size:2em;border-bottom:1px solid #eaecef;padding-bottom:.3em}
        h2{font-size:1.5em;border-bottom:1px solid #eaecef;padding-bottom:.3em}
        code{font-family:'Cascadia Code',Consolas,monospace;background:#f3f4f6;padding:.15em .35em;border-radius:5px;font-size:.9em}
        pre{background:#f6f8fa;padding:14px 16px;border-radius:10px;overflow:auto}pre code{background:none;padding:0}
        blockquote{margin:0;padding:0 1em;color:#59636e;border-left:.25em solid #d1d9e0}
        table{border-collapse:collapse}td,th{border:1px solid #d1d9e0;padding:6px 13px}tr:nth-child(2n){background:#f6f8fa}
        img{max-width:100%}a{color:#0969da}
        """;

    public static string MarkdownToHtml(string mdPath)
    {
        var md = File.ReadAllText(mdPath);
        var body = Markdown.ToHtml(md, Md);
        var title = System.Net.WebUtility.HtmlEncode(Path.GetFileNameWithoutExtension(mdPath));
        return $"<!doctype html><html><head><meta charset=\"utf-8\"><title>{title}</title><style>{Css}</style></head><body>{body}</body></html>";
    }

    public static async Task MarkdownHtml(Job job)
    {
        foreach (var src in job.Inputs)
        {
            var dst = Paths.Beside(src, "html");
            await File.WriteAllTextAsync(dst, MarkdownToHtml(src), new UTF8Encoding(false), job.Token);
            job.Outputs.Add(dst);
        }
        job.Summary = Images.SizeSummary(job);
    }

    /// <summary>Markdown → temp HTML → office suite → PDF/DOCX (both lay out HTML nicely).</summary>
    public static Task MarkdownVia(Job job, string ext)
    {
        var office = Platform.Current.Office ?? throw new InvalidOperationException("No office suite found");
        return office.Convert(job, ext, src =>
        {
            // beside the source so relative image links keep working
            var tmp = Path.Combine(Path.GetDirectoryName(src)!, $"~convertino-{Guid.NewGuid():N}.html");
            File.WriteAllText(tmp, MarkdownToHtml(src), new UTF8Encoding(true));
            return tmp;
        });
    }
}
