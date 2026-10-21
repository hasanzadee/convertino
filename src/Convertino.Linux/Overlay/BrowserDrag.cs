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
