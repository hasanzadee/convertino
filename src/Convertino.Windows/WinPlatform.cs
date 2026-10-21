using System.Windows;
using System.Windows.Threading;
using Convertino.Editors;
using Convertino.Native;
using Convertino.Services;
using Convertino.Ui;
using Windows.Storage;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf;

namespace Convertino;

/// <summary>Windows implementation of the core's platform services.</summary>
public sealed class WinPlatform : IPlatform
{
    readonly Lazy<IOffice?> _office = new(() => (IOffice?)MsOfficeConverter.Find() ?? LibreOffice.Find());

    public IOffice? Office => _office.Value;
    public IJobUi Ui { get; } = new WinJobUi();

    public async Task<List<byte[]>> RenderPdf(string path, double dpi, CancellationToken ct, Action<double>? progress = null, int maxPages = int.MaxValue)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var doc = await WinPdf.PdfDocument.LoadFromFileAsync(file);
        var list = new List<byte[]>();
        uint n = (uint)Math.Min(doc.PageCount, maxPages);
        for (uint i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(i / (double)n);
            using var page = doc.GetPage(i);
            using var ras = new InMemoryRandomAccessStream();
            var opts = new WinPdf.PdfPageRenderOptions
            {
                // page size is in DIPs (1/96 in)
                DestinationWidth = (uint)Math.Max(1, page.Size.Width * dpi / 96.0),
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            };
            await page.RenderToStreamAsync(ras, opts);
            var bytes = new byte[ras.Size];
            using (var reader = new DataReader(ras.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)ras.Size);
                reader.ReadBytes(bytes);
            }
            list.Add(bytes);
        }
        return list;
    }

    public Task<OcrResult> Ocr(byte[] image) => WinOcr.Recognize(image);

    public void SetClipboardText(string text) => Clip.SetText(text);
    public void SetClipboardFiles(IEnumerable<string> files) => Clip.SetFiles(files);
    public void SetClipboardImage(byte[] png) => Clip.SetPng(png);
    public Task<ClipContent?> ReadClipboard() => ClipReader.ReadClipboard();

    public Func<Job, Task>? Editor(string id) => id switch
    {
        "crop" => EditorHost.Crop,
        "adjust" => EditorHost.Adjust,
        "annotate" => EditorHost.Annotate,
        "addbg" => EditorHost.Background,
        "redact" => EditorHost.Redact,
        _ => null,
    };

    public void Reveal(string path) => Shell.Reveal(path);
    public void Open(string path) => Shell.Open(path);
    public void SetAutostart(bool on) => AutoStart.Apply(on);
}

sealed class WinJobUi : IJobUi
{
    public void Post(Action a) => Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, a);

    public IJobView Show(PetalAction action, IReadOnlyList<string> files, ScreenPoint where)
    {
        var t = Toasts.Create(new POINT(where.X, where.Y));
        t.SetRunning(action.Verb + "…", files.Count == 1 ? System.IO.Path.GetFileName(files[0]) : $"{files.Count} files", Icons.Glyph(action.Icon) ?? "");
        Toasts.Show(t);
        return new View(t);
    }

    sealed class View(ToastWindow t) : IJobView
    {
        public Action? OnCancel { set => t.OnCancel = value; }
        public void Progress(double p) => t.SetProgress(p);
        public void Dismiss() => t.Dismiss();

        public async void Done(string title, string summary, IReadOnlyList<string> outputs, string? icon)
        {
            t.Outputs.AddRange(outputs);
            System.Windows.Media.Imaging.BitmapSource? thumb = null;
            if (outputs.Count > 0)
            {
                var o = outputs[0];
                thumb = await Task.Run(() => Shell.Thumbnail(o, 96));
            }
            t.SetDone(title, summary, thumb, Icons.Glyph(icon) ?? "");
        }

        public void Failed(string title, string message, string? icon) => t.SetDone(title, message, null, Icons.Glyph(icon) ?? "", error: true);
    }
}
