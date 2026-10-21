using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Convertino.Services;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using IDataObject = System.Windows.IDataObject;

namespace Convertino.Native;

/// <summary>
/// An OLE drag's data object as the core reads it: formats by name, their HGLOBAL / IStream payloads, and virtual files
/// (FileGroupDescriptorW + FileContents by index), which WPF's own IDataObject can't fetch. Everything goes through the
/// COM interface, as the browser offered it.
/// </summary>
public sealed class ComDrag(IDataObject data) : IDragData
{
    readonly ComIDataObject? _com = data as ComIDataObject;
    readonly Dictionary<string, short> _ids = new(StringComparer.OrdinalIgnoreCase);
    List<string>? _formats;

    /// <summary>A browser's drag (links, pictures, virtual files), or null for anything else. <paramref name="files"/>: its CF_HDROP paths.</summary>
    public static WebDrop? Read(IDataObject data, IReadOnlyList<string>? files) => WebDrop.Read(new ComDrag(data), files);

    public IReadOnlyList<string> Formats => _formats ??= Enumerate();

    List<string> Enumerate()
    {
        var list = new List<string>();
        try
        {
            var e = _com?.EnumFormatEtc(DATADIR.DATADIR_GET);
            var one = new FORMATETC[1];
            var got = new int[1];
            while (e != null && e.Next(1, one, got) == 0 && got[0] == 1)
                if (Name(one[0].cfFormat) is { } n && _ids.TryAdd(n, one[0].cfFormat)) list.Add(n);
        }
        catch { }
        if (list.Count == 0)
            try { list.AddRange(data.GetFormats(false)); } catch { }
        return list;
    }

    static string? Name(short cf)
    {
        ushort id = unchecked((ushort)cf);
        switch (id)
        {
            case 1: return "Text";
            case 8: return "DeviceIndependentBitmap";
            case 13: return "UnicodeText";
            case 15: return "FileDrop";
            case 17: return "CF_DIBV5";
        }
        if (id < 0xC000) return null;
        var sb = new StringBuilder(256);
        return GetClipboardFormatName(id, sb, sb.Capacity) > 0 ? sb.ToString() : null;
    }

    short Id(string name)
    {
        if (_ids.TryGetValue(name, out var id)) return id;
        uint cf = name switch
        {
            "Text" => 1, "DeviceIndependentBitmap" => 8, "UnicodeText" => 13, "FileDrop" => 15, "CF_DIBV5" => 17,
            _ => RegisterClipboardFormat(name),
        };
        return unchecked((short)cf);
    }

    public byte[]? Bytes(string format, int max)
    {
        using var ms = new MemoryStream();
        return Copy(Id(format), -1, ms, max) ? ms.ToArray() : null;
    }

    public Task<string?>? Save(WebItem item, string dir, string name)
    {
        if (item.Source != WebSource.Virtual) return null;
        var path = Path.Combine(dir, name);
        bool ok;
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            ok = Copy(Id("FileContents"), item.Index, fs, Fetch.Cap);
            // an HGLOBAL can be bigger than what it holds; the descriptor knows the real size
            if (ok && item.Size > 0 && fs.Length > item.Size) fs.SetLength(item.Size);
        }
        if (!ok) FileTools.TryDeleteFile(path);
        return Task.FromResult(ok ? path : null);
    }

    /// <summary>One format (or virtual file <paramref name="index"/>) into <paramref name="dst"/>; false when it's missing or over <paramref name="max"/>.</summary>
    bool Copy(short cf, int index, Stream dst, long max)
    {
        if (_com == null || cf == 0) return false;
        var fmt = new FORMATETC { cfFormat = cf, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = TYMED.TYMED_HGLOBAL | TYMED.TYMED_ISTREAM };
        STGMEDIUM medium;
        try { _com.GetData(ref fmt, out medium); }
        catch { return false; }
        try
        {
            if (medium.tymed == TYMED.TYMED_HGLOBAL && medium.unionmember != IntPtr.Zero)
            {
                long size = (long)GlobalSize(medium.unionmember);
                if (size > max) return false;
                var p = GlobalLock(medium.unionmember);
                if (p == IntPtr.Zero) return false;
                try
                {
                    var buf = new byte[size];
                    Marshal.Copy(p, buf, 0, (int)size);
                    dst.Write(buf);
                }
                finally { GlobalUnlock(medium.unionmember); }
                return true;
            }
            if (medium.tymed == TYMED.TYMED_ISTREAM && medium.unionmember != IntPtr.Zero)
            {
                var stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
                var read = Marshal.AllocHGlobal(4);
                try
                {
                    try { stream.Seek(0, 0 /*STREAM_SEEK_SET*/, IntPtr.Zero); } catch { }
                    var buf = new byte[1 << 16];
                    long total = 0;
                    while (true)
                    {
                        Marshal.WriteInt32(read, 0);
                        stream.Read(buf, buf.Length, read);
                        int n = Marshal.ReadInt32(read);
                        if (n <= 0) break;
                        total += n;
                        if (total > max) return false;
                        dst.Write(buf, 0, n);
                    }
                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(read);
                    Marshal.ReleaseComObject(stream);
                }
            }
            return false;
        }
        catch (Exception ex) { Log.Info($"drag format {cf} [{index}]: {ex.Message}"); return false; }
        finally { ReleaseStgMedium(ref medium); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClipboardFormatName(uint format, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string format);
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
    [DllImport("ole32.dll")] static extern void ReleaseStgMedium(ref STGMEDIUM medium);
}

/// <summary>
/// Dev (--browser-drop-selftest): an OLE data object laid out the way a browser lays its drag out — formats as HGLOBALs,
/// virtual files as HGLOBALs (Chrome) or IStreams (Firefox), CF_HDROP for its temp copies — handed to WPF's DataObject
/// exactly like a foreign drag, so the overlay's own code reads it.
/// </summary>
sealed class FakeBrowserDrag : ComIDataObject
{
    const int DV_E_FORMATETC = unchecked((int)0x80040064), DV_E_TYMED = unchecked((int)0x80040069), E_NOTIMPL = unchecked((int)0x80004001);
    const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003), DATA_S_SAMEFORMATETC = 0x00040130;

    readonly List<(short Cf, int Index, byte[] Data, bool Stream)> _entries = [];

    public FakeBrowserDrag(DragCase c)
    {
        foreach (var (f, d) in c.Formats) Add(f, -1, d, false);
        if (c.Virtual.Count > 0)
        {
            Add("FileGroupDescriptorW", -1, WebSelfTest.Descriptor(c.Virtual.Select(v => (v.Name, (long)v.Data.Length)).ToList()), false);
            for (int i = 0; i < c.Virtual.Count; i++) Add("FileContents", i, c.Virtual[i].Data, c.Virtual[i].Stream);
        }
        if (c.Local.Count > 0) Add("FileDrop", -1, DropFiles(c.Local), false);
    }

    void Add(string format, int index, byte[] data, bool stream)
    {
        short cf = format switch
        {
            "Text" => 1, "DeviceIndependentBitmap" => 8, "UnicodeText" => 13, "FileDrop" => 15, "CF_DIBV5" => 17,
            _ => unchecked((short)RegisterClipboardFormat(format)),
        };
        _entries.Add((cf, index, data, stream));
    }

    /// <summary>DROPFILES (20 bytes, wide) + NUL-separated paths + a final NUL.</summary>
    static byte[] DropFiles(IEnumerable<string> paths)
    {
        var names = Encoding.Unicode.GetBytes(string.Concat(paths.Select(p => p + "\0")) + "\0");
        var b = new byte[20 + names.Length];
        BitConverter.GetBytes(20).CopyTo(b, 0);
        BitConverter.GetBytes(1).CopyTo(b, 16);
        names.CopyTo(b, 20);
        return b;
    }

    (short Cf, int Index, byte[] Data, bool Stream)? Find(FORMATETC f)
    {
        foreach (var e in _entries)
            if (e.Cf == f.cfFormat && (e.Index == f.lindex || (e.Index == -1 && f.lindex is -1 or 0))) return e;
        return null;
    }

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        var e = Find(format) ?? throw new COMException("no such format", DV_E_FORMATETC);
        if (e.Stream && (format.tymed & TYMED.TYMED_ISTREAM) != 0)
        {
            Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(HGlobal(e.Data), true, out var stream));
            // exactly the file, like the browser's stream (an HGLOBAL may be rounded up)
            var s = (IStream)Marshal.GetObjectForIUnknown(stream);
            s.SetSize(e.Data.Length);
            Marshal.ReleaseComObject(s);
            medium = new STGMEDIUM { tymed = TYMED.TYMED_ISTREAM, unionmember = stream };
            return;
        }
        if ((format.tymed & TYMED.TYMED_HGLOBAL) == 0) throw new COMException("no such medium", DV_E_TYMED);
        medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = HGlobal(e.Data) };
    }

    static IntPtr HGlobal(byte[] data)
    {
        var h = GlobalAlloc(0x0042 /*GMEM_MOVEABLE|GMEM_ZEROINIT*/, (UIntPtr)Math.Max(1, data.Length));
        var p = GlobalLock(h);
        Marshal.Copy(data, 0, p, data.Length);
        GlobalUnlock(h);
        return h;
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new COMException("", E_NOTIMPL);
    public int QueryGetData(ref FORMATETC format) => Find(format) is { } e && (format.tymed & (e.Stream ? TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL : TYMED.TYMED_HGLOBAL)) != 0 ? 0 : DV_E_FORMATETC;
    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut) { formatOut = formatIn; return DATA_S_SAMEFORMATETC; }
    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) { if (release) ReleaseStgMedium(ref medium); } // drop descriptions
    public IEnumFORMATETC EnumFormatEtc(DATADIR direction) =>
        new Formats(_entries.Where(e => e.Index <= 0).Select(e => new FORMATETC
        {
            cfFormat = e.Cf, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = e.Stream ? TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL : TYMED.TYMED_HGLOBAL,
        }).GroupBy(f => f.cfFormat).Select(g => g.First()).ToArray());
    public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection) { connection = 0; return OLE_E_ADVISENOTSUPPORTED; }
    public void DUnadvise(int connection) => throw new COMException("", OLE_E_ADVISENOTSUPPORTED);
    public int EnumDAdvise(out IEnumSTATDATA enumAdvise) { enumAdvise = null!; return OLE_E_ADVISENOTSUPPORTED; }

    sealed class Formats(FORMATETC[] all, int at = 0) : IEnumFORMATETC
    {
        int _at = at;
        public int Next(int celt, FORMATETC[] rgelt, int[] pceltFetched)
        {
            int n = 0;
            while (n < celt && _at < all.Length) rgelt[n++] = all[_at++];
            if (pceltFetched is { Length: > 0 }) pceltFetched[0] = n;
            return n == celt ? 0 : 1;
        }
        public int Skip(int celt) { _at = Math.Min(all.Length, _at + celt); return _at < all.Length ? 0 : 1; }
        public int Reset() { _at = 0; return 0; }
        public void Clone(out IEnumFORMATETC newEnum) => newEnum = new Formats(all, _at);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string format);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
    [DllImport("ole32.dll")] static extern int CreateStreamOnHGlobal(IntPtr hGlobal, bool deleteOnRelease, out IntPtr stream);
    [DllImport("ole32.dll")] static extern void ReleaseStgMedium(ref STGMEDIUM medium);
}
