using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Convertino.Native;
using Convertino.Overlay;
using Convertino.Services;

namespace Convertino;

/// <summary>
/// Developer self-tests for the clipboard and capture flowers. The real clipboard is never touched: synthetic payloads
/// (as an app would put them there: PNG, DIB, CF_HDROP, text …) go through the same reader, offers and petals, and the
/// bloomed flowers are rendered to PNGs. Nothing on screen is read either: the capture uses a made-up desktop.
/// </summary>
static class DevClip
{
    /// <summary>Convertino.exe --clip-selftest outDir → results.txt, clip-*.png, screenshot.png; captured files in outDir\captures.</summary>
    public static void ClipSelfTest(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var captures = Path.Combine(outDir, "captures");
        if (Directory.Exists(captures)) Directory.Delete(captures, true);
        Captures.FolderOverride = captures;
        var report = new List<string>();
        int failed = 0;
        void Check(bool ok, string what)
        {
            if (!ok) failed++;
            report.Add($"{(ok ? "PASS" : "FAIL")}  {what}");
        }
        string Ids(Offer o) => string.Join(" ", o.Pages.Rings[0].Petals.Select(a => a.Id));

        var desktop = Desktop(1600, 1000);
        var sample = Path.Combine(outDir, "copied.txt");
        File.WriteAllText(sample, "a file copied in Explorer");
        var wallpaper = @"C:\Windows\Web\Wallpaper\Windows\img0.jpg";
        if (!File.Exists(wallpaper))
        {
            // CI images may have no wallpaper: a made-up photo instead
            wallpaper = Path.Combine(outDir, "photo.jpg");
            var enc = new JpegBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(desktop));
            using var fs = File.Create(wallpaper);
            enc.Save(fs);
        }

        var cases = new (string Name, DataObject Data, string Expect, Func<Offer?, bool> Ok)[]
        {
            ("png", Data(("PNG", new MemoryStream(Png(desktop, 640, 400, alpha: true)))), "Image",
                o => o != null && o.Pages.FirstRing == "Quick" && Ids(o).StartsWith("copyimg ocr readqr annotate") && o.Scratch != null),
            ("dib32", Data((DataFormats.Dib, new MemoryStream(Dib(desktop, 500, 320, 32)))), "Image",
                o => o != null && o.HubTitle == "500×320"),
            ("dib24", Data((DataFormats.Dib, new MemoryStream(Dib(desktop, 333, 211, 24)))), "Image",
                o => o != null && o.HubTitle == "333×211"),
            ("hdrop", Data((DataFormats.FileDrop, new[] { wallpaper, @"C:\no\such\file.png" })), "Files",
                o => o != null && o.Files.Count == 1 && o.Pages.Rings[0].Name == "Convert"),
            ("hdrop-mixed", Data((DataFormats.FileDrop, new[] { wallpaper, sample })), "Files",
                o => o != null && o.Files.Count == 2),
            ("text", Data((DataFormats.UnicodeText, "Meeting notes\n- ship 1.4\n- write the changelog")), "Text",
                o => o != null && Ids(o) == "clip-txt clip-md clip-qr" && o.HubTitle == "9 words"),
            ("url", Data((DataFormats.UnicodeText, "https://github.com/hasanzadee/convertino")), "Text",
                o => o != null && Ids(o).StartsWith("clip-qr ") && o.HubTitle == "github.com" && o.Pages.Badge == "URL"),
            ("json", Data((DataFormats.UnicodeText, "{\"name\":\"Convertino\",\"rings\":[\"Convert\",\"Tools\"]}")), "Text",
                o => o != null && Ids(o).Contains("clip-json")),
            ("cells", Data((DataFormats.UnicodeText, "Name\tQty\r\nApples\t3\r\nPears, ripe\t5\r\n")), "Text",
                o => o != null && Ids(o).Contains("clip-csv")),
            // Office puts a picture of the copied cells beside their text: the text is what was meant
            ("office", Data((DataFormats.UnicodeText, "Total\t42"), ("Csv", new MemoryStream("Total,42"u8.ToArray())), (DataFormats.Dib, new MemoryStream(Dib(desktop, 120, 40, 24)))), "Text",
                o => o != null && o.Pages.Badge == "TEXT"),
            ("empty", Data(), "nothing", o => o == null),
        };

        var offers = new Dictionary<string, Offer>();
        foreach (var (name, data, expect, ok) in cases)
        {
            ClipContent? content = null;
            Offer? offer = null;
            string error = "";
            try
            {
                content = ClipReader.Read(data);
                offer = ClipFlower.For(content);
            }
            catch (Exception ex) { error = " · " + ex.GetType().Name + ": " + ex.Message; }
            string kind = content switch { ClipContent.Files => "Files", ClipContent.Image => "Image", ClipContent.Text => "Text", _ => "nothing" };
            Check(kind == expect && ok(offer) && error == "",
                $"{name,-12} → {kind,-7} {(offer == null ? "" : $"hub \"{offer.HubTitle}\" / \"{offer.HubSub}\" · {Ids(offer)}")}{error}");
            if (offer == null) continue;
            offers[name] = offer;
            Render(offer, Path.Combine(outDir, $"clip-{name}.png"));
        }

        // a pasted picture is saved to the captures folder with a time-stamped name; the same picture again (the last one) reuses it
        if (offers.TryGetValue("png", out var pngOffer))
        {
            var f = pngOffer.Files[0];
            Check(Path.GetDirectoryName(f) == captures && Regex.IsMatch(Path.GetFileName(f), @"^Clipboard \d{4}-\d\d-\d\d \d\d\.\d\d\.\d\d\.png$"), $"pasted picture saved as {Path.GetFileName(f)}");
        }
        if (offers.TryGetValue("dib24", out var lastPicture))
        {
            Thread.Sleep(1100); // a second later: the PNG must come out byte for byte the same
            var again = ClipFlower.For(ClipReader.Read(cases[2].Data));
            Check(again?.Files[0] == lastPicture.Files[0] && again.Scratch == null, $"same picture again → same file ({Path.GetFileName(lastPicture.Files[0])}), kept");
        }

        // text petals write real files into the captures folder
        if (offers.TryGetValue("text", out var textOffer))
            foreach (var id in new[] { "clip-txt", "clip-md", "clip-qr" }) RunPetal(textOffer, id, Check);
        if (offers.TryGetValue("json", out var jsonOffer)) RunPetal(jsonOffer, "clip-json", Check, f => File.ReadAllText(f).Contains("\n  \"name\": \"Convertino\""));
        if (offers.TryGetValue("cells", out var cellsOffer)) RunPetal(cellsOffer, "clip-csv", Check, f => File.ReadAllText(f).Contains("\"Pears, ripe\",5"));

        // cleanup: a pasted picture nobody used goes away, the temp text always does
        if (offers.TryGetValue("dib32", out var unused))
        {
            var f = unused.Files[0];
            unused.Closed(used: false);
            Check(!File.Exists(f), "unused pasted picture removed when the flower closes");
        }
        if (textOffer != null)
        {
            textOffer.Closed(used: true);
            Check(!File.Exists(textOffer.Files[0]), "temp text removed after its petal ran");
        }

        // a screen capture: crop of the (made-up) frozen desktop → Screenshot … .png → the image flower, Quick ring first
        {
            int w = desktop.PixelWidth, h = desktop.PixelHeight;
            var px = new byte[w * h * 4];
            new FormatConvertedBitmap(desktop, PixelFormats.Bgr32, null, 0).CopyPixels(px, w * 4, 0);
            var area = new RECT { Left = 420, Top = 260, Right = 1180, Bottom = 700 };
            var (cut, cw, ch) = RegionCapture.Crop(px, new RECT { Right = w, Bottom = h }, area);
            var path = Captures.NewPath("Screenshot", "png");
            File.WriteAllBytes(path, Captures.BgraToPng(cut, cw, ch));
            var shot = ClipFlower.ForScreenshot(path);
            Check(shot.HubTitle == "760×440" && shot.Scratch == null && Ids(shot).StartsWith("copyimg ocr readqr annotate crop redact to-jpg to-webp to-pdf"),
                $"screenshot 760×440 → {Path.GetFileName(path)} · {string.Join(" · ", shot.Pages.Rings.Select(r => $"{r.Name} ({r.Petals.Count})"))}");
            Render(shot, Path.Combine(outDir, "screenshot.png"));
        }

        // shortcuts: parsing, labels, the panel's choices
        Check(Hotkey.Parse("Win+Shift+X") == new Hotkey(HotMods.Super | HotMods.Shift, "X") && Hotkey.Parse("win+shift+x")!.Value.Label == "Win+Shift+X", "Win+Shift+X parses, shows as Win+Shift+X");
        Check(Hotkey.Parse("Ctrl+Alt+F12")?.ToString() == "Ctrl+Alt+F12" && Hotkey.Parse("Shift") == null && Hotkey.Parse("Ctrl+X+Y") == null && Hotkey.Parse("Super+F25") == null, "Ctrl+Alt+F12 ok; Shift, Ctrl+X+Y, F25 rejected");
        Check(Hotkey.Clipboard.Next(false) == new Hotkey(HotMods.Super | HotMods.Shift, "Z") && Hotkey.Clipboard.Next(false).Next(false) == Hotkey.Clipboard, "clipboard shortcut cycles X → Z → X");
        Check(new Settings().ClipboardKey.Label == "Win+Shift+X" && new Settings().CaptureKey.Label == "Win+Shift+A" && new Settings { ClipboardHotkey = "nonsense" }.ClipboardKey == Hotkey.Clipboard,
            "defaults Win+Shift+X / Win+Shift+A; garbage in settings → default");

        // RegisterHotKey for real, with a combination nobody presses (and gone again right away): taken twice → "another app uses it"
        using (var keys = new Convertino.Input.Hotkeys())
        {
            var odd = new Hotkey(HotMods.Ctrl | HotMods.Alt | HotMods.Shift | HotMods.Super, "F24");
            int fired = 0;
            var first = keys.Register(odd, () => fired++);
            var second = keys.Register(odd, () => { });
            if (first == null) keys.DevPress(0);
            Check(first == null && second == "another app uses it" && fired == 1, $"RegisterHotKey {odd.Label}: {first ?? "ok"}; again: {second ?? "ok"}; WM_HOTKEY ran it {fired}×");
        }

        report.Add(failed == 0 ? "ALL PASS" : $"{failed} FAILED");
        File.WriteAllLines(Path.Combine(outDir, "results.txt"), report);
        Log.Info("DEV clip selftest: " + (failed == 0 ? "all pass" : $"{failed} failed"));
        Captures.FolderOverride = null;
    }

    /// <summary>Convertino.exe --capture-selftest out.png: the capture overlay mid-drag (1.5× monitor) and idle (1×), over a made-up desktop.</summary>
    public static void CaptureSelfTest(string outPath) => RegionCapture.DevRender(outPath, Desktop(1600, 1000));

    static void RunPetal(Offer offer, string id, Action<bool, string> check, Func<string, bool>? content = null)
    {
        var action = offer.Pages.Rings.SelectMany(r => r.Petals).FirstOrDefault(a => a.Id == id);
        if (action == null) { check(false, $"{id}: no such petal"); return; }
        try
        {
            // off the UI thread: RunHeadless resumes on the context it started on, which this wait blocks
            var job = Task.Run(() => Jobs.RunHeadless(action, offer.Files)).GetAwaiter().GetResult();
            var f = job.Outputs.FirstOrDefault();
            bool ok = f != null && File.Exists(f) && new FileInfo(f).Length > 0 && Path.GetDirectoryName(f) == Captures.Folder && (content?.Invoke(f) ?? true);
            check(ok, $"{id,-10} → {(f == null ? "nothing" : Path.GetFileName(f))}");
        }
        catch (Exception ex) { check(false, $"{id}: {ex.Message}"); }
    }

    static DataObject Data(params (string Format, object Value)[] items)
    {
        var d = new DataObject();
        foreach (var (f, v) in items) d.SetData(f, v);
        return d;
    }

    /// <summary>The flower for an offer, first ring, over the same wallpaper as --snapshot.</summary>
    static void Render(Offer offer, string outPath)
    {
        var view = new FlowerView { Width = RadialWindow.SizeDip, Height = RadialWindow.SizeDip };
        var rings = offer.Pages.Rings;
        view.HubTitle = offer.HubTitle ?? Paths.HumanSize(Paths.SizeOf(offer.Files[0]));
        view.HubSub = offer.HubSub ?? offer.Pages.Title;
        view.HubBadge = offer.Pages.Badge;
        view.Thumb = Shell.Thumbnail(offer.Files[0], 96);
        view.Appear();
        view.SetMode(HubMode.Info);
        view.SetPages(rings.Count, 0);
        view.SetPetals(rings[0].Petals, 1);
        view.SetHover(0);
        Dev.Render(view, outPath);
    }

    // ---------- synthetic payloads ----------

    /// <summary>A made-up desktop: the warm wallpaper and a window with some text, so crops and the overlay look real.</summary>
    static BitmapSource Desktop(int w, int h)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawDrawing(Convertino.Editors.BackgroundEditor.Backdrops[0].Paint(new Rect(0, 0, w, h), null));
            var win = new Rect(w * .22, h * .2, w * .56, h * .56);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x2e)), null, win, 12, 12);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)), null, new Rect(win.X, win.Y, win.Width, 40), 12, 12);
            var face = new Typeface("Segoe UI");
            void Text(string s, double x, double y, double size, Color c) =>
                dc.DrawText(new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, new SolidColorBrush(c), 1), new Point(x, y));
            Text("Release notes — Convertino 1.4", win.X + 18, win.Y + 9, 16, Color.FromRgb(0xcd, 0xd6, 0xf4));
            string[] lines = ["Copy anything, press Win+Shift+X: the flower opens for it.", "Win+Shift+A captures an area of the screen.", "Results go straight back to the clipboard with Copy.", "Pictures and screenshots land in Pictures\\Convertino."];
            for (int i = 0; i < lines.Length; i++) Text(lines[i], win.X + 24, win.Y + 70 + i * 34, 20, Color.FromRgb(0xba, 0xc2, 0xde));
            dc.DrawRoundedRectangle(new LinearGradientBrush(Color.FromRgb(0xcb, 0xa6, 0xf7), Color.FromRgb(0xf5, 0xc2, 0xe7), 0), null, new Rect(win.X + 24, win.Bottom - 70, 160, 42), 10, 10);
            Text("Download", win.X + 62, win.Bottom - 62, 18, Color.FromRgb(0x11, 0x11, 0x1b));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>A PNG of part of the desktop; with alpha: rounded transparent corners, like a copied app icon or sticker.</summary>
    static byte[] Png(BitmapSource desktop, int w, int h, bool alpha)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushClip(alpha ? new RectangleGeometry(new Rect(0, 0, w, h), 40, 40) : new RectangleGeometry(new Rect(0, 0, w, h)));
            dc.DrawImage(desktop, new Rect(-300, -180, desktop.PixelWidth, desktop.PixelHeight));
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>CF_DIB as apps put it: a BITMAPINFOHEADER and bottom-up rows (32-bit with a zero alpha byte, or 24-bit padded to 4 bytes).</summary>
    static byte[] Dib(BitmapSource desktop, int w, int h, int bits)
    {
        var crop = new CroppedBitmap(new FormatConvertedBitmap(desktop, PixelFormats.Bgr32, null, 0), new Int32Rect(380, 240, w, h));
        var px = new byte[w * h * 4];
        crop.CopyPixels(px, w * 4, 0);
        int stride = bits == 32 ? w * 4 : (w * 3 + 3) & ~3;
        var dib = new byte[40 + stride * h];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(w).CopyTo(dib, 4);
        BitConverter.GetBytes(h).CopyTo(dib, 8);   // positive: bottom-up
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)bits).CopyTo(dib, 14);
        BitConverter.GetBytes(stride * h).CopyTo(dib, 20);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int src = (y * w + x) * 4, dst = 40 + (h - 1 - y) * stride + x * (bits / 8);
                dib[dst] = px[src]; dib[dst + 1] = px[src + 1]; dib[dst + 2] = px[src + 2];
                if (bits == 32) dib[dst + 3] = 0;
            }
        return dib;
    }
}
