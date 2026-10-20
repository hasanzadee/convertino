using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Convertino.Overlay;

namespace Convertino;

/// <summary>Developer helper: render the flower to a PNG without a live drag.</summary>
static class Dev
{
    /// <summary>Run one action headless: Convertino.exe --run &lt;actionId&gt; file…  → results in the log.</summary>
    public static async Task Run(string id, string[] files)
    {
        files = files.Select(Path.GetFullPath).ToArray();
        var all = Catalog.All(files).ToList();
        var action = all.FirstOrDefault(a => a.Id == id);
        if (action == null)
        {
            Log.Info($"DEV run: no action '{id}'. Available: {string.Join(", ", all.Select(a => a.Id))}");
            return;
        }
        var job = new Job { Inputs = files, Action = action, Token = CancellationToken.None };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await Task.Run(() => action.Run(job));
            Log.Info($"DEV run {id} OK {sw.ElapsedMilliseconds} ms → {string.Join(" | ", job.Outputs)} :: {job.Title} :: {job.Summary}");
        }
        catch (Exception ex) { Log.Error($"DEV run {id} FAILED", ex); }
    }

    /// <summary>
    /// Convertino.exe --snapshot out.png [theme] [img|tools|more|pdf|video] [hover]: one ring of the flower as a PNG.
    /// img, tools and more are the three rings of an image (Convert, Tools, More).
    /// </summary>
    public static void Snapshot(string outPath, string kind, int hover)
    {
        var sample = kind switch
        {
            "pdf" => @"C:\Windows\Help\sample.pdf",
            "tools" or "more" => @"C:\Windows\Web\Wallpaper\Windows\img0.jpg",
            "video" => @"C:\x\clip.mp4",
            _ => @"C:\Windows\Web\Wallpaper\Windows\img0.jpg",
        };
        var pages = Catalog.For([sample]);
        var view = new FlowerView { Width = RadialWindow.SizeDip, Height = RadialWindow.SizeDip };
        view.Measure(new Size(view.Width, view.Height));
        view.Arrange(new Rect(0, 0, view.Width, view.Height));

        // fake wallpaper behind it: warm gradient like the macOS demo
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
        {
            var lg = new LinearGradientBrush(Color.FromRgb(0xf7, 0x9d, 0x3c), Color.FromRgb(0xe8, 0x3a, 0x5a), 60);
            dc.DrawRectangle(lg, null, new Rect(0, 0, view.Width, view.Height));
            var rg = new RadialGradientBrush(Color.FromArgb(180, 0x8a, 0x7c, 0xf0), Colors.Transparent) { Center = new Point(.8, .9), GradientOrigin = new Point(.8, .9), RadiusX = .6, RadiusY = .6 };
            dc.DrawRectangle(rg, null, new Rect(0, 0, view.Width, view.Height));
        }
        var bgBmp = new RenderTargetBitmap((int)view.Width, (int)view.Height, 96, 96, PixelFormats.Pbgra32);
        bgBmp.Render(bg);
        var blurred = new RenderTargetBitmap((int)view.Width, (int)view.Height, 96, 96, PixelFormats.Pbgra32);
        var blurHost = new System.Windows.Controls.Image { Source = bgBmp, Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 30 } };
        blurHost.Measure(new Size(view.Width, view.Height)); blurHost.Arrange(new Rect(0, 0, view.Width, view.Height));
        blurred.Render(blurHost);
        view.Backdrop = blurred;

        view.HubTitle = "1.1 MB";
        view.HubSub = Path.GetFileName(sample);
        view.HubBadge = Kinds.Badge(sample);
        view.Appear();
        view.SetMode(HubMode.Info);
        var rings = pages.Rings;
        var ring = kind switch { "tools" => pages.Tools, "more" => pages.More, _ => pages.Convert };
        int page = Math.Max(0, rings.ToList().IndexOf(ring));
        view.SetPages(rings.Count, page);
        view.SetPetals(rings[page], 1);
        view.SetHover(hover);
        view.Settle();
        view.Measure(new Size(view.Width, view.Height));
        view.Arrange(new Rect(0, 0, view.Width, view.Height));
        view.UpdateLayout();

        var scale = 2.0;
        var rtb = new RenderTargetBitmap((int)(view.Width * scale), (int)(view.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var root = new DrawingVisual();
        using (var dc = root.RenderOpen()) dc.DrawImage(bgBmp, new Rect(0, 0, view.Width, view.Height));
        rtb.Render(root);
        rtb.Render(view);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(outPath);
        enc.Save(fs);
    }
}
