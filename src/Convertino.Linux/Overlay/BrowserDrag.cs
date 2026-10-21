using System.Reflection;
using System.Text;
using Avalonia.Input;

namespace Convertino.Overlay;

/// <summary>
/// An XDND drag (through Avalonia) as the core reads it, by MIME type: text/x-moz-url, _NETSCAPE_URL, text/uri-list,
/// text/html, image/png … Avalonia keeps only local files of a text/uri-list, so its raw bytes (http and data: links)
/// are asked of the X11 selection reader directly.
/// </summary>
public sealed class AvaloniaDrag(IDataTransfer data) : IDragData
{
    const string Utf8Text = "text/plain;charset=utf-8", UriList = "text/uri-list";
    List<string>? _formats;

    /// <summary>A browser's drag (links, pictures), or null for anything else. <paramref name="files"/>: the local paths it offers.</summary>
    public static WebDrop? Read(IDataTransfer data, IReadOnlyList<string>? files) => WebDrop.Read(new AvaloniaDrag(data), files);

    public IReadOnlyList<string> Formats => _formats ??= data.Formats
        .Select(f => f.Equals(DataFormat.Text) ? Utf8Text : f.Equals(DataFormat.File) ? UriList : f.Equals(DataFormat.Bitmap) ? null : f.Identifier)
        .OfType<string>().Distinct().ToList();

    public byte[]? Bytes(string format, int max)
    {
        try
        {
            byte[]? b;
            if (format == Utf8Text && data.Contains(DataFormat.Text)) b = data.TryGetText() is { } t ? Encoding.UTF8.GetBytes(t) : null;
            else
            {
                var f = DataFormat.CreateBytesPlatformFormat(format);
                b = data.Contains(f) ? data.TryGetValue(f) : format == UriList ? RawSelection(f) : null;
            }
            return b != null && b.Length <= max ? b : null;
        }
        catch (Exception ex) { Log.Info($"drag format {format}: {ex.Message}"); return null; }
    }

    /// <summary>
    /// X11 direct save (XDS), what Firefox and Chromium offer for a picture: the file:// address goes into the XdndDirectSave0
    /// property of the source's window, then the XdndDirectSave0 target is asked for; "S" means the browser writes its
    /// original file there (with its own cookies), which may finish only after the drop.
    /// </summary>
    public Task<string?>? Save(WebItem item, string dir, string name)
    {
        if (item.Source != WebSource.Promise) return null;
        try
        {
            // Avalonia's XDND transfer knows the source window (internal type, public property)
            if (data.GetType().GetProperty("SourceWindow")?.GetValue(data) is not nint source || source == 0) return null;
            var path = Path.Combine(dir, name);
            var uri = Encoding.UTF8.GetBytes(new Uri(path).AbsoluteUri);
            var dpy = Native.X11.XOpenDisplay(IntPtr.Zero);
            if (dpy == IntPtr.Zero) return null;
            try
            {
                XChangeProperty(dpy, source, XInternAtom(dpy, "XdndDirectSave0", false), XInternAtom(dpy, "text/plain", false), 8, 0, uri, uri.Length);
                Native.X11.XSync(dpy, false);
            }
            finally { Native.X11.XCloseDisplay(dpy); }
            var reply = Bytes("XdndDirectSave0", 64);
            char answer = reply is { Length: > 0 } ? (char)reply[0] : '-';
            Log.Info($"direct save {name}: {answer}");
            if (answer == 'S') return Settled(path);
            // "F": take the file's bytes from application/octet-stream instead (Chromium's answer)
            if (answer == 'F' && Bytes("application/octet-stream", 512 << 20) is { Length: > 0 } bytes)
            {
                File.WriteAllBytes(path, bytes);
                return Task.FromResult<string?>(path);
            }
            return null;
        }
        catch (Exception ex) { Log.Error("direct save", ex); return null; }
    }

    /// <summary>The file once the browser is done with it: there, and the same size for 0.6 s.</summary>
    static async Task<string?> Settled(string path)
    {
        long last = -1;
        int steady = 0;
        for (int i = 0; i < 600; i++)
        {
            await Task.Delay(200);
            long len = File.Exists(path) ? new FileInfo(path).Length : -1;
            if (len > 0 && len == last) { if (++steady >= 3) return path; }
            else steady = 0;
            last = len;
        }
        return File.Exists(path) ? path : null;
    }

    [System.Runtime.InteropServices.DllImport("libX11.so.6")]
    static extern IntPtr XInternAtom(IntPtr dpy, string name, bool onlyIfExists);
    [System.Runtime.InteropServices.DllImport("libX11.so.6")]
    static extern int XChangeProperty(IntPtr dpy, IntPtr w, IntPtr property, IntPtr type, int format, int mode, byte[] data, int count);

    /// <summary>
    /// <c>convertino --browser-drop-selftest &lt;dir&gt;</c>: the self-test's drags (as Firefox and Chrome offer them over XDND)
    /// filled into Avalonia data transfers and read through this class, then dropped on petals (<see cref="WebSelfTest"/>).
    /// </summary>
    public static int SelfTest(string dir) => WebSelfTest.Run(dir, "x11", c =>
    {
        var item = new DataTransferItem();
        foreach (var (f, b) in c.Formats)
            if (f == Utf8Text) item.Set(DataFormat.Text, Encoding.UTF8.GetString(b));
            else item.Set(DataFormat.CreateBytesPlatformFormat(f), b);
        var dt = new DataTransfer();
        dt.Add(item);
        return (new AvaloniaDrag(dt), c.Local);
    }).GetAwaiter().GetResult();

    /// <summary>A format Avalonia maps to something else (text/uri-list → files), read as raw bytes from the XDND selection.</summary>
    byte[]? RawSelection(DataFormat<byte[]> f)
    {
        foreach (var item in data.Items)
        {
            // only the X11 reader's item: it asks the drag source for whatever it's given
            if (item.GetType().Name != "DragDropDataTransferItem") continue;
            var raw = item.GetType().GetMethod("TryGetRawCore", BindingFlags.Instance | BindingFlags.NonPublic);
            if (raw?.Invoke(item, [f]) is byte[] b) return b;
        }
        return null;
    }
}
