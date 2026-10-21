using System.IO;
using System.Windows;
using ImageMagick;
using Convertino.Services;

namespace Convertino.Editors;

/// <summary>Opens an editor window for a dropped image and turns its result into job output.</summary>
public static class EditorHost
{
    public static Task Crop(Job job) => Open(job, (src, img) => new CropEditor(src, img));
    public static Task Adjust(Job job) => Open(job, (src, img) => new AdjustEditor(src, img));
    public static Task Annotate(Job job) => Open(job, (src, img) => new AnnotateEditor(src, img));
    public static Task Background(Job job) => Open(job, (src, img) => new BackgroundEditor(src, img));
    public static Task Redact(Job job) => Open(job, (src, img) => new RedactEditor(src, img));

    /// <summary>Dev self-test: build each editor, simulate an edit, save.</summary>
    public static async Task<List<string>> DevSaveAll(string src)
    {
        var made = new List<string>();
        foreach (var make in new Func<string, MagickImage, Editor>[]
        {
            (s, i) => new CropEditor(s, i), (s, i) => new RedactEditor(s, i), (s, i) => new AnnotateEditor(s, i),
            (s, i) => new AdjustEditor(s, i), (s, i) => new BackgroundEditor(s, i),
        })
        {
            using var img = Images.Load(src);
            var ed = make(src, img);
            ed.DevPrepare();
            try { made.Add(await ed.Save() ?? "(null)"); }
            catch (Exception ex) { made.Add($"{ed.Title} FAILED: {ex.Message}"); Log.Error("dev save " + ed.Title, ex); }
        }
        return made;
    }

    static async Task Open(Job job, Func<string, MagickImage, Editor> make)
    {
        var src = job.Inputs[0];
        MagickImage img;
        try { img = await Task.Run(() => Images.Load(src)); }
        catch (Exception ex) { throw new IOException(L.En("Couldn't open this image: {0}", ex.Message), ex); }

        using (img)
        {
            var editor = make(src, img);
            var (work, _, scale) = Native.Win32.MonitorAt(new Native.POINT(job.Where.X, job.Where.Y));
            double maxW = work.Width / scale * 0.72, maxH = work.Height / scale * 0.82;
            // size the window to the picture's aspect, leaving room for the controls
            double chrome = 46 + 150 + 70;
            double aspect = img.Width / (double)img.Height;
            double h = Math.Min(maxH, Math.Max(520, Math.Min(img.Height, 900) + chrome));
            double w = Math.Clamp((h - chrome) * aspect + 32, 640, maxW);
            var shell = new EditorShell(editor, $"{Path.GetFileName(src)} · {img.Width}×{img.Height}", new Size(w, h));
            shell.ShowNear(new Native.POINT(job.Where.X, job.Where.Y));
            var result = await shell.Result;
            if (result != null)
            {
                job.Outputs.Add(result);
                job.Summary = $"{editor.Title} · {Paths.HumanSize(Paths.SizeOf(result))}";
            }
            else job.Silent = true;
        }
    }
}
