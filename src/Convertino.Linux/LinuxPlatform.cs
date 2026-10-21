using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Convertino.Services;
using ImageMagick;
using Orientation = Avalonia.Layout.Orientation;
using FontWeight = Avalonia.Media.FontWeight;
using TextAlignment = Avalonia.Media.TextAlignment;
using FontStyle = Avalonia.Media.FontStyle;

namespace Convertino;

/// <summary>Linux implementation of the core's platform services (poppler, tesseract, LibreOffice, xdg).</summary>
public sealed class LinuxPlatform : IPlatform
{
    readonly Lazy<IOffice?> _office = new(() => LibreOffice.Find());
    public IOffice? Office => _office.Value;

    /// <summary>Any live window gives us the X11 clipboard; null in headless (CLI) mode.</summary>
    public static TopLevel? ClipboardHost { get; set; }

    public static Func<string, Func<Job, Task>?>? Editors { get; set; }

    public IJobUi Ui { get; set; } = new ConsoleJobUi();

    public Task<List<byte[]>> RenderPdf(string path, double dpi, CancellationToken ct, Action<double>? progress = null, int maxPages = int.MaxValue)
        => Poppler.Render(path, dpi, ct, progress, maxPages);

    public Task<OcrResult> Ocr(byte[] image) => Tesseract.Recognize(image);

    public void SetClipboardText(string text)
    {
        if (ClipboardHost?.Clipboard is { } cb)
        {
            Dispatcher.UIThread.Invoke(() => cb.SetTextAsync(text)).Wait(3000);
            return;
        }
        // headless: hand it to whatever clipboard tool the session has
        foreach (var (exe, args) in new[] { ("wl-copy", ""), ("xclip", "-selection clipboard"), ("xsel", "--clipboard --input") })
            if (Paths.Which(exe) is { } p && Pipe(p, args, text)) return;
        throw new InvalidOperationException("No clipboard available (install wl-clipboard or xclip)");
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
        SetClipboardText(string.Join("\n", list.Select(f => new Uri(f).AbsoluteUri)));
    }

    /// <summary>
    /// Pictures go through the session's own clipboard tool on Wayland (wl-copy) and X11 (xclip): both keep serving
    /// the PNG after Convertino exits, which a CLI run needs, and every app that pastes images reads image/png.
    /// </summary>
    public void SetClipboardImage(byte[] png)
    {
        bool wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        bool x11 = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
        var tools = new List<(string exe, string args)>();
        if (wayland || !x11) tools.Add(("wl-copy", "--type image/png"));
        if (x11 || !wayland) tools.Add(("xclip", "-selection clipboard -t image/png -i"));
        foreach (var (exe, args) in tools)
            if (Paths.Which(exe) is { } p && PipeBytes(p, args, png)) return;
        throw new InvalidOperationException(wayland && !x11 ? "Copy image needs wl-clipboard: sudo apt install wl-clipboard"
            : x11 && !wayland ? "Copy image needs xclip: sudo apt install xclip"
            : "Copy image needs wl-clipboard (Wayland) or xclip (X11)");
    }

    /// <summary>
    /// The clipboard through Avalonia while the app runs (no extra tools needed on X11), otherwise wl-paste (Wayland)
    /// or xclip (X11), e.g. for "convertino clip".
    /// </summary>
    public async Task<ClipContent?> ReadClipboard()
    {
        if (ClipboardHost?.Clipboard is { } cb)
        {
            // the data on the UI thread, decoding a big picture off it
            try { return await Task.Run(await Dispatcher.UIThread.InvokeAsync(() => ReadAvalonia(cb))); }
            // the session's tools may still manage
            catch (Exception ex) { Log.Error("clipboard (Avalonia)", ex); }
        }
        return await Task.Run(ReadWithTools);
    }

    /// <summary>Formats a document or sheet adds beside its text (LibreOffice / browsers offer a picture of the cells too): then the text is what was meant.</summary>
    static bool Document(IEnumerable<string> types) =>
        types.Any(t => t.StartsWith("application/x-openoffice", StringComparison.Ordinal) || t is "text/rtf" or "text/richtext" or "application/rtf" or "text/csv");

    /// <summary>KeePassXC, KWallet and other password managers mark what they copy (KDE's hint; Klipper keeps it out of its history).</summary>
    static bool Secret(IEnumerable<string> types) => types.Contains("x-kde-passwordManagerHint");

    static readonly string[] PictureTypes = ["image/png", "image/jpeg", "image/bmp", "image/tiff", "image/webp"];

    /// <summary>Reads what's there (UI thread); the returned step decodes it (any thread).</summary>
    static async Task<Func<ClipContent?>> ReadAvalonia(IClipboard cb)
    {
        var types = (await cb.GetDataFormatsAsync()).Select(f => f.Identifier).ToList();
        if (Overlay.OverlayController.Diag) Log.Info("clipboard types: " + string.Join(", ", types));
        if (await cb.TryGetFilesAsync() is { Length: > 0 } items)
        {
            var paths = items.Select(i => i.TryGetLocalPath()).OfType<string>().Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (paths.Count > 0) return () => new ClipContent.Files(paths);
        }
        var text = await cb.TryGetTextAsync();
        bool hasText = !string.IsNullOrWhiteSpace(text), secret = Secret(types);
        if (!(hasText && Document(types)) && !secret)
        {
            foreach (var mime in PictureTypes)
                if (types.Contains(mime) && await cb.TryGetValueAsync(DataFormat.CreateBytesPlatformFormat(mime)) is { Length: > 8 } bytes)
                    return () => new ClipContent.Image(mime == "image/png" ? bytes : Captures.ToPng(bytes));
            if (types.Any(t => t.StartsWith("image/", StringComparison.Ordinal)) && await cb.TryGetBitmapAsync() is { } bmp)
                return () =>
                {
                    using var ms = new MemoryStream();
                    bmp.Save(ms, PngBitmapEncoderOptions.Default);
                    return new ClipContent.Image(ms.ToArray());
                };
        }
        return () => hasText ? new ClipContent.Text(text!, secret) : null;
    }

    static ClipContent? ReadWithTools()
    {
        bool wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) && Paths.Which("wl-paste") != null;
        Func<string?, byte[]?> get;
        if (wayland) get = type => Output("wl-paste", type == null ? ["--list-types"] : ["--no-newline", "--type", type]);
        else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) && Paths.Which("xclip") != null)
            get = type => Output("xclip", ["-selection", "clipboard", "-o", "-t", type ?? "TARGETS"]);
        else throw new InvalidOperationException("Reading the clipboard needs wl-clipboard (Wayland) or xclip (X11)");

        var types = Encoding.UTF8.GetString(get(null) ?? []).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (types.Count == 0) return null;
        foreach (var t in new[] { "x-special/gnome-copied-files", "text/uri-list" })
            if (types.Contains(t) && get(t) is { } uris && ClipFlower.UriList(Encoding.UTF8.GetString(uris)) is { Count: > 0 } files)
                return new ClipContent.Files(files);
        string? text = null;
        foreach (var t in new[] { "text/plain;charset=utf-8", "UTF8_STRING", "text/plain", "STRING" })
            if (types.Contains(t) && get(t) is { Length: > 0 } b) { text = Encoding.UTF8.GetString(b); break; }
        bool hasText = !string.IsNullOrWhiteSpace(text), secret = Secret(types);
        if (!(hasText && Document(types)) && !secret)
            foreach (var mime in PictureTypes)
                if (types.Contains(mime) && get(mime) is { Length: > 8 } bytes)
                    return new ClipContent.Image(mime == "image/png" ? bytes : Captures.ToPng(bytes));
        return hasText ? new ClipContent.Text(text!, secret) : null;
    }

    static byte[]? Output(string exe, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(Paths.Which(exe) ?? exe) { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            using var ms = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return null; }
            copy.Wait(2000);
            return p.ExitCode == 0 ? ms.ToArray() : null;
        }
        catch (Exception ex) { Log.Error(exe, ex); return null; }
    }

    static bool PipeBytes(string exe, string args, byte[] input)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            p.StandardInput.BaseStream.Write(input);
            p.StandardInput.Close();
            // the tool forks a child that serves the selection; a quick non-zero exit means it found no display
            return !p.WaitForExit(3000) || p.ExitCode == 0;
        }
        catch (Exception ex) { Log.Error(exe, ex); return false; }
    }

    static bool Pipe(string exe, string args, string input)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            p.StandardInput.Write(input);
            p.StandardInput.Close();
            // wl-copy/xclip fork into the background to serve the selection
            p.WaitForExit(1500);
            return true;
        }
        catch { return false; }
    }

    public Func<Job, Task>? Editor(string id) => Editors?.Invoke(id);

    public void Reveal(string path)
    {
        // freedesktop FileManager1 selects the file in Nautilus / Dolphin / Nemo / Thunar …
        var uri = new Uri(path).AbsoluteUri;
        if (Run("gdbus", $"call --session --dest org.freedesktop.FileManager1 --object-path /org/freedesktop/FileManager1 --method org.freedesktop.FileManager1.ShowItems \"['{uri}']\" \"\"")) return;
        if (Run("dbus-send", $"--session --print-reply --dest=org.freedesktop.FileManager1 /org/freedesktop/FileManager1 org.freedesktop.FileManager1.ShowItems array:string:\"{uri}\" string:\"\"")) return;
        Open(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!);
    }

    public void Open(string path) => Run("xdg-open", $"\"{path}\"", wait: false);

    static bool Run(string exe, string args, bool wait = true)
    {
        var p = Paths.Which(exe);
        if (p == null) return false;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(p, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            if (!wait) return true;
            proc.WaitForExit(3000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    public void SetAutostart(bool on)
    {
        try
        {
            var dir = Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "autostart");
            var file = Path.Combine(dir, "convertino.desktop");
            if (!on) { if (File.Exists(file)) File.Delete(file); return; }
            Directory.CreateDirectory(dir);
            var exe = Environment.ProcessPath ?? "convertino";
            File.WriteAllText(file, $"""
                [Desktop Entry]
                Type=Application
                Name=Convertino
                Comment=Drag a file, tap Shift — convert and edit from a radial menu
                Exec="{exe}"
                Icon=convertino
                Terminal=false
                X-GNOME-Autostart-enabled=true
                """);
        }
        catch (Exception ex) { Log.Error("autostart", ex); }
    }

    // ---------- thumbnails ----------

    /// <summary>Freedesktop thumbnail cache first (instant for anything the file manager has shown), then render one.</summary>
    public static Bitmap? Thumbnail(string path, int px)
    {
        try
        {
            var uri = new Uri(path).AbsoluteUri;
            var md5 = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(uri))).ToLowerInvariant();
            var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } c ? c
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
            foreach (var size in new[] { "large", "x-large", "normal" })
            {
                var t = Path.Combine(cache, "thumbnails", size, md5 + ".png");
                if (File.Exists(t) && File.GetLastWriteTimeUtc(t) >= File.GetLastWriteTimeUtc(path).AddSeconds(-2))
                    return Decode(File.ReadAllBytes(t), px);
            }
            byte[]? png = Kinds.Of(path) switch
            {
                Kind.Image => FromMagick(path, px),
                Kind.Pdf => Poppler.Render(path, 40, CancellationToken.None, null, 1).Result.FirstOrDefault(),
                Kind.Video => VideoFrame(path, px),
                _ => null,
            };
            return png == null ? null : Decode(png, px);
        }
        catch { return null; }
    }

    static byte[] FromMagick(string path, int px)
    {
        using var img = Images.Load(path);
        img.Thumbnail(new MagickGeometry((uint)px * 2, (uint)px * 2));
        return img.ToByteArray(MagickFormat.Png);
    }

    static byte[]? VideoFrame(string path, int px)
    {
        if (Media.FFmpeg == null) return null;
        var tmp = Path.Combine(Paths.Temp, $"t-{Guid.NewGuid():N}.png");
        try
        {
            var psi = new ProcessStartInfo(Media.FFmpeg, $"-v error -ss 1 -i \"{path}\" -frames:v 1 -vf scale={px * 2}:-2 -y \"{tmp}\"") { UseShellExecute = false, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            p.WaitForExit(5000);
            return File.Exists(tmp) ? File.ReadAllBytes(tmp) : null;
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    static Bitmap Decode(byte[] png, int px)
    {
        using var ms = new MemoryStream(png);
        return Bitmap.DecodeToWidth(ms, px * 2, BitmapInterpolationMode.HighQuality);
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
