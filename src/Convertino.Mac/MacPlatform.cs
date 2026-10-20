using System.Diagnostics;
using System.Security;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Convertino.Native;
using Convertino.Services;
using ImageMagick;

namespace Convertino;

/// <summary>macOS implementation of the core's platform services (CoreGraphics PDF, Vision OCR, LibreOffice, open/pbcopy, launchd).</summary>
public sealed class MacPlatform : IPlatform
{
    public const string BundleId = "io.github.hasanzadee.convertino";

    readonly Lazy<IOffice?> _office = new(() => LibreOffice.Find());
    public IOffice? Office => _office.Value;

    /// <summary>Any live window gives us NSPasteboard through Avalonia; null in headless (CLI) mode.</summary>
    public static TopLevel? ClipboardHost { get; set; }

    public static Func<string, Func<Job, Task>?>? Editors { get; set; }

    public IJobUi Ui { get; set; } = new ConsoleJobUi();

    /// <summary>
    /// Apps started from Finder or launchd get a bare PATH; add Homebrew (Apple silicon and Intel) and MacPorts
    /// so ffmpeg, tesseract, pdftoppm and gs are found like they are in Terminal.
    /// </summary>
    public static void ExtendPath()
    {
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin:/usr/sbin:/sbin").Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var dir in new[] { "/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin" })
            if (!path.Contains(dir) && Directory.Exists(dir)) path.Add(dir);
        Environment.SetEnvironmentVariable("PATH", string.Join(':', path));
    }

    public async Task<List<byte[]>> RenderPdf(string path, double dpi, CancellationToken ct, Action<double>? progress = null, int maxPages = int.MaxValue)
    {
        try { return await Task.Run(() => Quartz.RenderPdf(path, dpi, ct, progress, maxPages), ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error("CoreGraphics PDF, trying poppler/Ghostscript", ex);
            return await Poppler.Render(path, dpi, ct, progress, maxPages);
        }
    }

    public async Task<OcrResult> Ocr(byte[] image)
    {
        if (VisionOcr.Available)
        {
            try { return await Task.Run(() => VisionOcr.Recognize(image)); }
            catch (Exception ex) when (Tesseract.Available) { Log.Error("Vision OCR, trying Tesseract", ex); }
        }
        return await Tesseract.Recognize(image);
    }

    public void SetClipboardText(string text)
    {
        if (ClipboardHost?.Clipboard is { } cb)
        {
            Dispatcher.UIThread.Invoke(() => cb.SetTextAsync(text)).Wait(3000);
            return;
        }
        // headless: pbcopy reads UTF-8 only when the locale says so
        var psi = new ProcessStartInfo("/usr/bin/pbcopy") { RedirectStandardInput = true, UseShellExecute = false };
        psi.Environment["LANG"] = "en_US.UTF-8";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("pbcopy failed");
        p.StandardInput.Write(text);
        p.StandardInput.Close();
        p.WaitForExit(3000);
    }

    public void SetClipboardFiles(IEnumerable<string> files)
    {
        var list = files.ToList();
        if (ClipboardHost is { } host && host.Clipboard is { } cb)
        {
            Dispatcher.UIThread.Invoke(async () =>
            {
                var items = new List<IStorageItem>();
                foreach (var f in list)
                {
                    IStorageItem? it = Directory.Exists(f)
                        ? await host.StorageProvider.TryGetFolderFromPathAsync(f)
                        : await host.StorageProvider.TryGetFileFromPathAsync(f);
                    if (it != null) items.Add(it);
                }
                await cb.SetFilesAsync(items);
            }).Wait(3000);
            return;
        }
        // headless: Finder can paste what AppleScript puts there
        var refs = string.Join(", ", list.Select(f => $"(POSIX file \"{f.Replace("\\", "\\\\").Replace("\"", "\\\"")}\")"));
        if (!Run("/usr/bin/osascript", ["-e", $"set the clipboard to {{{refs}}}"]))
            SetClipboardText(string.Join("\n", list));
    }

    public void SetClipboardImage(byte[] png)
    {
        // AppKit wants the main thread once the app runs; a CLI run has no UI loop and may call it directly
        if (ClipboardHost != null) Dispatcher.UIThread.Invoke(() => Pasteboard.SetImage(png));
        else Pasteboard.SetImage(png);
    }

    public Func<Job, Task>? Editor(string id) => Editors?.Invoke(id);

    public void Reveal(string path) => Run("/usr/bin/open", ["-R", path], wait: false);

    public void Open(string path) => Run("/usr/bin/open", [path], wait: false);

    static bool Run(string exe, string[] args, bool wait = true)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            if (!wait) return true;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch (Exception ex) { Log.Error(Path.GetFileName(exe), ex); return false; }
    }

    // ---------- start at login: a LaunchAgent ----------

    public static string LaunchAgent => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", BundleId + ".plist");

    public void SetAutostart(bool on)
    {
        try
        {
            var file = LaunchAgent;
            if (!on) { if (File.Exists(file)) File.Delete(file); return; }
            var exe = Environment.ProcessPath ?? "";
            if (!exe.Contains(".app/Contents/MacOS/")) return;   // only for the installed app bundle, not dev builds
            var plist = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                  <key>Label</key><string>{BundleId}</string>
                  <key>ProgramArguments</key><array><string>{SecurityElement.Escape(exe)}</string></array>
                  <key>RunAtLoad</key><true/>
                  <key>LimitLoadToSessionType</key><string>Aqua</string>
                  <key>ProcessType</key><string>Interactive</string>
                </dict>
                </plist>

                """;
            // rewriting an unchanged agent would make macOS announce a "new login item" again
            if (File.Exists(file) && File.ReadAllText(file) == plist) return;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, plist);
        }
        catch (Exception ex) { Log.Error("autostart", ex); }
    }

    // ---------- thumbnails ----------

    /// <summary>Images via Magick, PDFs via CoreGraphics, everything else through Quick Look (qlmanage).</summary>
    public static Bitmap? Thumbnail(string path, int px)
    {
        try
        {
            byte[]? png = Kinds.Of(path) switch
            {
                Kind.Image => FromMagick(path, px),
                Kind.Pdf => Quartz.RenderPdf(path, 40, CancellationToken.None, null, 1).FirstOrDefault(),
                _ => QuickLook(path, px),
            };
            if (png == null) return null;
            using var ms = new MemoryStream(png);
            return Bitmap.DecodeToWidth(ms, px * 2, BitmapInterpolationMode.HighQuality);
        }
        catch { return null; }
    }

    static byte[] FromMagick(string path, int px)
    {
        using var img = Images.Load(path);
        img.Thumbnail(new MagickGeometry((uint)px * 2, (uint)px * 2));
        return img.ToByteArray(MagickFormat.Png);
    }

    static byte[]? QuickLook(string path, int px)
    {
        if (Directory.Exists(path)) return null;
        var dir = Path.Combine(Paths.Temp, "ql-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/qlmanage") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-t", "-s", (px * 2).ToString(), "-o", dir, path }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return null; }
            var made = Directory.GetFiles(dir, "*.png").FirstOrDefault();
            return made == null ? null : File.ReadAllBytes(made);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

/// <summary>Progress output for the command line.</summary>
sealed class ConsoleJobUi : IJobUi
{
    public void Post(Action a) => a();
    public IJobView Show(PetalAction action, IReadOnlyList<string> files, ScreenPoint where) => new View();

    sealed class View : IJobView
    {
        public Action? OnCancel { set { } }
        public void Progress(double p) { }
        public void Done(string title, string summary, IReadOnlyList<string> outputs, string? icon) { }
        public void Failed(string title, string message, string? icon) => Console.Error.WriteLine($"{title}: {message}");
        public void Dismiss() { }
    }
}
