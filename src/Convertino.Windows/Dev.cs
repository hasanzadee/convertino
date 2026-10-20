using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Convertino.Input;
using Convertino.Native;
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
    /// Convertino.exe --snapshot out.png [theme] [img|tools|more|formats|pdf|video] [hover]: one ring of the flower as a PNG.
    /// img, tools, more and formats are the four rings of an image (Convert, Tools, More tools, More formats).
    /// </summary>
    public static void Snapshot(string outPath, string kind, int hover)
    {
        var sample = kind switch
        {
            "pdf" => @"C:\Windows\Help\sample.pdf",
            "video" => @"C:\x\clip.mp4",
            _ => @"C:\Windows\Web\Wallpaper\Windows\img0.jpg",
        };
        var pages = Catalog.For([sample]);
        var view = new FlowerView { Width = RadialWindow.SizeDip, Height = RadialWindow.SizeDip };
        view.HubTitle = "1.1 MB";
        view.HubSub = Path.GetFileName(sample);
        view.HubBadge = Kinds.Badge(sample);
        view.Appear();
        view.SetMode(HubMode.Info);
        var rings = pages.Rings.ToList();
        string name = kind switch { "tools" => "Tools", "more" => "More tools", "formats" => "More formats", _ => "Convert" };
        int page = Math.Max(0, rings.FindIndex(r => r.Name == name));
        view.SetPages(rings.Count, page);
        view.SetPetals(rings[page].Petals, 1);
        view.SetHover(hover);
        Render(view, outPath);
    }

    /// <summary>The flower over a fake warm wallpaper (like the macOS demo), at 2×, animations settled.</summary>
    static void Render(FlowerView view, string outPath)
    {
        var size = new Size(RadialWindow.SizeDip, RadialWindow.SizeDip);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
        {
            var lg = new LinearGradientBrush(Color.FromRgb(0xf7, 0x9d, 0x3c), Color.FromRgb(0xe8, 0x3a, 0x5a), 60);
            dc.DrawRectangle(lg, null, new Rect(size));
            var rg = new RadialGradientBrush(Color.FromArgb(180, 0x8a, 0x7c, 0xf0), Colors.Transparent) { Center = new Point(.8, .9), GradientOrigin = new Point(.8, .9), RadiusX = .6, RadiusY = .6 };
            dc.DrawRectangle(rg, null, new Rect(size));
        }
        var bgBmp = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bgBmp.Render(bg);
        var blurred = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        var blurHost = new System.Windows.Controls.Image { Source = bgBmp, Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 30 } };
        blurHost.Measure(size); blurHost.Arrange(new Rect(size));
        blurred.Render(blurHost);
        view.Backdrop = blurred;

        view.Settle();
        view.Measure(size);
        view.Arrange(new Rect(size));
        view.UpdateLayout();

        var scale = 2.0;
        var rtb = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var root = new DrawingVisual();
        using (var dc = root.RenderOpen()) dc.DrawImage(bgBmp, new Rect(size));
        rtb.Render(root);
        rtb.Render(view);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(outPath);
        enc.Save(fs);
    }

    /// <summary>
    /// Convertino.exe --paging-selftest outDir: feeds the real hook callbacks (GlobalInput) the WM_MOUSEWHEEL, VK_TAB and Shift
    /// data Windows sends mid-drag and checks that the real overlay (OverlayController) cycles every ring, that Tab and the
    /// wheel over the flower are swallowed and nothing else is. Touches nothing on the desktop: no hooks are installed, no
    /// input is injected, the flower window is never shown. Writes results.txt and one PNG per ring (from the overlay's own view).
    /// </summary>
    public static void PagingSelfTest(string outDir)
    {
        Directory.CreateDirectory(outDir);
        const int VK_LSHIFT = 0xA0, VK_TAB = 0x09, VK_ESCAPE = 0x1B;
        var input = new GlobalInput(Application.Current.Dispatcher); // never started: no hooks
        var overlay = new OverlayController(input);
        var at = new RECT { Left = 100, Top = 100, Right = 560, Bottom = 560 };
        var inside = new POINT(330, 330);
        var outside = new POINT(900, 330);
        var report = new List<string>();
        int failed = 0;

        void Pump() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        void Step(string what, Func<IntPtr> act, bool swallowed, string ring, int pauseMs = 260)
        {
            Thread.Sleep(pauseMs); // wheel bursts end after 220 ms, Tab repeats after 140 ms
            var r = act();
            Pump();
            bool ok = (r != IntPtr.Zero) == swallowed && overlay.DevRing.StartsWith(ring, StringComparison.Ordinal);
            if (!ok) failed++;
            report.Add($"{(ok ? "PASS" : "FAIL")}  {what,-32} → {overlay.DevRing,-16}  {(r != IntPtr.Zero ? "swallowed" : "passed on")}" +
                       (ok ? "" : $"   (expected {ring}…, {(swallowed ? "swallowed" : "passed on")})"));
        }
        void Shot(string name) => Render(overlay.DevView, Path.Combine(outDir, name + ".png"));
        IntPtr Both(Func<IntPtr> a, Func<IntPtr> b) { var r = a(); Pump(); return r | b(); }

        overlay.DevBloom([@"C:\Windows\Web\Wallpaper\Windows\img0.jpg"], at);
        Pump();
        report.Add($"      open an image mid-drag             → {overlay.DevRing}");
        Shot("ring1");
        Step("wheel down over the flower", () => input.DevWheel(inside, -120), true, "2/");
        Step("wheel down again, same burst", () => input.DevWheel(inside, -120), true, "2/", pauseMs: 40);
        Shot("ring2"); // after the burst: rendering takes longer than the burst gap
        Step("Tab (down + up)", () => Both(() => input.DevKey(VK_TAB, false), () => input.DevKey(VK_TAB, true)), true, "3/");
        Shot("ring3");
        Step("Tab held (key repeat)", () => Both(() => input.DevKey(VK_TAB, false), () => input.DevKey(VK_TAB, false)), true, "4/");
        Step("Tab up", () => input.DevKey(VK_TAB, true), true, "4/", pauseMs: 40);
        Shot("ring4");
        Step("wheel down: wraps to Convert", () => input.DevWheel(inside, -120), true, "1/");
        Step("wheel up: wraps back", () => input.DevWheel(inside, 120), true, "4/");
        Step("Shift down", () => input.DevKey(VK_LSHIFT, false), false, "4/");
        Step("  + Tab: previous", () => Both(() => input.DevKey(VK_TAB, false), () => input.DevKey(VK_TAB, true)), true, "3/");
        Step("  Shift up: no extra flip", () => input.DevKey(VK_LSHIFT, true), false, "3/");
        Step("Shift tapped: next", () => Both(() => input.DevKey(VK_LSHIFT, false), () => input.DevKey(VK_LSHIFT, true)), false, "4/");
        Step("wheel off the flower", () => input.DevWheel(outside, -120), false, "4/");
        Step("Esc closes it", () => Both(() => input.DevKey(VK_ESCAPE, false), () => input.DevKey(VK_ESCAPE, true)), false, "4/");
        Step("Tab after closing", () => Both(() => input.DevKey(VK_TAB, false), () => input.DevKey(VK_TAB, true)), false, "");
        Step("wheel after closing", () => input.DevWheel(inside, -120), false, "");

        // a list longer than Pages.MaxPetals carries on in a second ring of the same name, spread evenly
        var many = Enumerable.Range(1, 23).Select(i => Catalog.Tool($"t{i}", $"T{i}", "tag", "test", _ => Task.CompletedTask)).ToList();
        var split = new Pages([], many, "", "").Rings;
        string got = string.Join(" · ", split.Select(r => $"{r.Name} ({r.Petals.Count})"));
        bool splitOk = got == "Tools (8) · Tools 2 (8) · Tools 3 (7)";
        if (!splitOk) failed++;
        report.Add($"{(splitOk ? "PASS" : "FAIL")}  23 tools                         → {got}");

        report.Add(failed == 0 ? "ALL PASS" : $"{failed} FAILED");
        File.WriteAllLines(Path.Combine(outDir, "results.txt"), report);
        Log.Info("DEV paging selftest: " + (failed == 0 ? "all pass" : $"{failed} failed"));
    }
}
