using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows.Threading;
using Convertino.Services;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using IDataObject = System.Windows.IDataObject;

namespace Convertino.Native;

/// <summary>A drop source that can hand its data over after the drop returns (Explorer's async drop protocol).</summary>
[ComImport, Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IDataObjectAsyncCapability
{
    void SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool doOpAsync);
    void GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool isOpAsync);
    void StartOperation(IntPtr reserved);
    void InOperation([MarshalAs(UnmanagedType.Bool)] out bool inAsyncOp);
    void EndOperation(int result, IntPtr reserved, uint effects);
}

/// <summary>A compound file (Outlook hands a dragged mail over as one). Only CreateStream, CopyTo and Commit are called; the rest keep the vtable order.</summary>
[ComImport, Guid("0000000B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IStorage
{
    void CreateStream([MarshalAs(UnmanagedType.LPWStr)] string name, uint mode, uint reserved1, uint reserved2, out IStream stream);
    void OpenStream();
    void CreateStorage();
    void OpenStorage();
    void CopyTo(uint ciidExclude, IntPtr rgiidExclude, IntPtr snbExclude, IStorage dest);
    void MoveElementTo();
    void Commit(uint flags);
    void Revert();
    void EnumElements();
    void DestroyElement();
    void RenameElement();
    void SetElementTimes();
    void SetClass();
    void SetStateBits();
    void Stat();
}

/// <summary>
/// An OLE drag's data object as the core reads it: formats by name, their HGLOBAL / IStream payloads, and virtual files
/// (FileGroupDescriptorW + FileContents by index, IStorage too), which WPF's own IDataObject can't fetch. Everything goes
/// through the COM interface, as the source offered it, in slices rather than one big buffer. A source that supports the
/// async drop protocol (Chrome, Firefox, Explorer) keeps its data alive after the drop, so virtual files are copied then,
/// a slice at a time between UI work, and the source's drag loop isn't held up.
/// </summary>
public sealed class ComDrag(IDataObject data) : IDragData
{
    const int Slice = 1 << 20;
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
        return CopyTo(format, ms, max) ? ms.ToArray() : null;
    }

    public bool CopyTo(string format, Stream dst, long max) => Pump(Id(format), -1, dst, null, max, 0, slices: false).GetAwaiter().GetResult();

    public Task<string?>? Save(WebItem item, string dir, string name)
    {
        if (item.Source != WebSource.Virtual) return null;
        // the descriptor says how big it is: nothing over the cap is copied at all
        if (item.Size > Fetch.Cap) return Task.FromException<string?>(new Fetch.Refused($"Too big ({Paths.HumanSize(item.Size)}; {Paths.HumanSize(Fetch.Cap)} at most)"));
        var path = Path.Combine(dir, name);
        var later = Async();
        if (later != null)
            try { later.StartOperation(IntPtr.Zero); }
            catch { later = null; }
        // without the async protocol the data is only guaranteed during the drop: copy it now
        if (later == null) return Task.FromResult(SaveFile(item, path, slices: false).GetAwaiter().GetResult() ? path : null);
        return SaveLater(later, item, path);
    }

    async Task<string?> SaveLater(IDataObjectAsyncCapability source, WebItem item, string path)
    {
        bool ok = false;
        try
        {
            await Task.Yield(); // the drop handler returns first
            ok = await SaveFile(item, path, slices: true);
            return ok ? path : null;
        }
        finally
        {
            try { source.EndOperation(ok ? 0 : E_FAIL, IntPtr.Zero, ok ? 1u /*DROPEFFECT_COPY*/ : 0); } catch { }
        }
    }

    async Task<bool> SaveFile(WebItem item, string path, bool slices)
    {
        bool ok;
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            ok = await Pump(Id("FileContents"), item.Index, fs, path, Fetch.Cap, item.Size, slices);
        if (!ok) FileTools.TryDeleteFile(path);
        return ok;
    }

    /// <summary>
    /// The drag's source when it supports the async drop protocol and has it on. WPF wraps a foreign drag's COM object
    /// in its own DataObject (which doesn't pass this interface on), so it's looked up a couple of fields down.
    /// </summary>
    IDataObjectAsyncCapability? Async()
    {
        try
        {
            if (Find(data, 3) is not { } source) return null;
            source.GetAsyncMode(out bool on);
            return on ? source : null;
        }
        catch { return null; }

        static IDataObjectAsyncCapability? Find(object o, int depth)
        {
            if (o is IDataObjectAsyncCapability a) return a;
            if (depth == 0 || Marshal.IsComObject(o)) return null;
            for (var t = o.GetType(); t != null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    if (!f.FieldType.IsValueType && f.FieldType != typeof(string) && f.GetValue(o) is { } v && !ReferenceEquals(v, o) && Find(v, depth - 1) is { } found)
                        return found;
            return null;
        }
    }

    /// <summary>
    /// One format (or virtual file <paramref name="index"/>) into <paramref name="dst"/>, a slice at a time (with
    /// <paramref name="slices"/>: letting the UI thread breathe between them); false when it's missing or over <paramref name="max"/>.
    /// <paramref name="path"/>: where <paramref name="dst"/> writes, for a compound file (IStorage) that copies itself there.
    /// </summary>
    async Task<bool> Pump(short cf, int index, Stream dst, string? path, long max, long size, bool slices)
    {
        if (_com == null || cf == 0) return false;
        var tymed = TYMED.TYMED_HGLOBAL | TYMED.TYMED_ISTREAM | (path != null ? TYMED.TYMED_ISTORAGE : 0);
        var fmt = new FORMATETC { cfFormat = cf, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = tymed };
        STGMEDIUM medium;
        try { _com.GetData(ref fmt, out medium); }
        catch { return false; }
        try
        {
            var buf = new byte[Slice];
            if (medium.tymed == TYMED.TYMED_HGLOBAL && medium.unionmember != IntPtr.Zero)
            {
                // an HGLOBAL can be bigger than what it holds; the descriptor knows the real size
                long len = (long)GlobalSize(medium.unionmember);
                if (size > 0 && size < len) len = size;
                if (len > max) return false;
                var p = GlobalLock(medium.unionmember);
                if (p == IntPtr.Zero) return false;
                try
                {
                    for (long off = 0; off < len; off += Slice)
                    {
                        int n = (int)Math.Min(Slice, len - off);
                        Marshal.Copy(p + (nint)off, buf, 0, n);
                        dst.Write(buf, 0, n);
                        if (slices) await Dispatcher.Yield(DispatcherPriority.Background);
                    }
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
                        if (slices) await Dispatcher.Yield(DispatcherPriority.Background);
                    }
                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(read);
                    Marshal.ReleaseComObject(stream);
                }
            }
            if (medium.tymed == TYMED.TYMED_ISTORAGE && medium.unionmember != IntPtr.Zero && path != null)
            {
                // a compound file (an Outlook mail as .msg): it copies itself into a new one at the path
                dst.Dispose();
                FileTools.TryDeleteFile(path);
                var src = (IStorage)Marshal.GetObjectForIUnknown(medium.unionmember);
                try
                {
                    Marshal.ThrowExceptionForHR(StgCreateDocfile(path, StgCreate, 0, out var copy));
                    try
                    {
                        src.CopyTo(0, IntPtr.Zero, IntPtr.Zero, copy);
                        copy.Commit(0);
                    }
                    finally { Marshal.ReleaseComObject(copy); }
                }
                finally { Marshal.ReleaseComObject(src); }
                return new FileInfo(path).Length <= max;
            }
            return false;
        }
        catch (Exception ex) { Log.Info($"drag format {cf} [{index}]: {ex.Message}"); return false; }
        finally { ReleaseStgMedium(ref medium); }
    }

    const int E_FAIL = unchecked((int)0x80004005);
    internal const uint StgCreate = 0x1000 | 0x2 | 0x10; // STGM_CREATE | STGM_READWRITE | STGM_SHARE_EXCLUSIVE

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClipboardFormatName(uint format, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string format);
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
    [DllImport("ole32.dll")] static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("ole32.dll", CharSet = CharSet.Unicode)] internal static extern int StgCreateDocfile(string? name, uint mode, uint reserved, out IStorage storage);
}

/// <summary>
/// Dev (--browser-drop-selftest): an OLE data object laid out the way a browser lays its drag out — formats as HGLOBALs,
/// virtual files as HGLOBALs (Chrome), IStreams (Firefox) or IStorages (Outlook), CF_HDROP for its temp copies, the async
/// drop protocol where the case asks for it — handed to WPF's DataObject exactly like a foreign drag, so the overlay's own
/// code reads it. It notes what a well-behaved target must do: start and end the async operation, and not pull the virtual
/// files inside the drop when it could have waited.
/// </summary>
sealed class FakeBrowserDrag : ComIDataObject, IDataObjectAsyncCapability
{
    const int DV_E_FORMATETC = unchecked((int)0x80040064), DV_E_TYMED = unchecked((int)0x80040069), E_NOTIMPL = unchecked((int)0x80004001);
    const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003), DATA_S_SAMEFORMATETC = 0x00040130;

    readonly List<(short Cf, int Index, byte[] Data, string Medium)> _entries = [];
    readonly bool _async;
    short _contents;
    bool _started, _ended, _dropped;
    int _reads;
    readonly List<string> _wrong = [];

    public FakeBrowserDrag(DragCase c)
    {
        _async = c.Async;
        foreach (var (f, d) in c.Formats) Add(f, -1, d, "hglobal");
        if (c.Virtual.Count > 0)
        {
            Add("FileGroupDescriptorW", -1, WebSelfTest.Descriptor(c.Virtual.Select(v => (v.Name, v.Declared ?? v.Data.Length)).ToList()), "hglobal");
            for (int i = 0; i < c.Virtual.Count; i++) Add("FileContents", i, c.Virtual[i].Data, c.Virtual[i].Medium);
            _contents = _entries[^1].Cf;
        }
        if (c.Local.Count > 0) Add("FileDrop", -1, DropFiles(c.Local), "hglobal");
        c.Dropped = () => _dropped = true;
        c.Verify = () =>
        {
            if (_wrong.Count > 0) return string.Join("; ", _wrong);
            if (_reads > 0 && c.Virtual.Any(v => v.Declared > Fetch.Cap)) return "copied a file its descriptor says is over the cap";
            if (_async && _reads > 0 && !(_started && _ended)) return $"the async drop wasn't {(_started ? "ended" : "started")}";
            return null;
        };
    }

    void Add(string format, int index, byte[] data, string medium)
    {
        short cf = format switch
        {
            "Text" => 1, "DeviceIndependentBitmap" => 8, "UnicodeText" => 13, "FileDrop" => 15, "CF_DIBV5" => 17,
            _ => unchecked((short)RegisterClipboardFormat(format)),
        };
        _entries.Add((cf, index, data, medium));
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

    (short Cf, int Index, byte[] Data, string Medium)? Find(FORMATETC f)
    {
        foreach (var e in _entries)
            if (e.Cf == f.cfFormat && (e.Index == f.lindex || (e.Index == -1 && f.lindex is -1 or 0))) return e;
        return null;
    }

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        var e = Find(format) ?? throw new COMException("no such format", DV_E_FORMATETC);
        if (e.Cf == _contents && e.Index >= 0)
        {
            _reads++;
            if (_async && _started && !_dropped) _wrong.Add("a virtual file was copied inside the drop although the source works async");
            if (_ended) _wrong.Add("a virtual file was read after the async operation ended");
        }
        if (e.Medium == "storage" && (format.tymed & TYMED.TYMED_ISTORAGE) != 0)
        {
            // a compound file with one stream, like a mail Outlook drags out
            Marshal.ThrowExceptionForHR(ComDrag.StgCreateDocfile(null, ComDrag.StgCreate | 0x04000000 /*STGM_DELETEONRELEASE*/, 0, out var stg));
            stg.CreateStream("__substg1.0_0037001F", ComDrag.StgCreate, 0, 0, out var s);
            s.Write(e.Data, e.Data.Length, IntPtr.Zero);
            Marshal.ReleaseComObject(s);
            stg.Commit(0);
            medium = new STGMEDIUM { tymed = TYMED.TYMED_ISTORAGE, unionmember = Marshal.GetComInterfaceForObject(stg, typeof(IStorage)) };
            Marshal.ReleaseComObject(stg);
            return;
        }
        if (e.Medium == "stream" && (format.tymed & TYMED.TYMED_ISTREAM) != 0)
        {
            Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(HGlobal(e.Data), true, out var stream));
            // exactly the file, like the browser's stream (an HGLOBAL may be rounded up)
            var s = (IStream)Marshal.GetObjectForIUnknown(stream);
            s.SetSize(e.Data.Length);
            Marshal.ReleaseComObject(s);
            medium = new STGMEDIUM { tymed = TYMED.TYMED_ISTREAM, unionmember = stream };
            return;
        }
        if (e.Medium != "hglobal" || (format.tymed & TYMED.TYMED_HGLOBAL) == 0) throw new COMException("no such medium", DV_E_TYMED);
        medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = HGlobal(e.Data) };
    }

    static TYMED Tymed(string medium) => medium switch { "storage" => TYMED.TYMED_ISTORAGE, "stream" => TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL, _ => TYMED.TYMED_HGLOBAL };

    static IntPtr HGlobal(byte[] data)
    {
        var h = GlobalAlloc(0x0042 /*GMEM_MOVEABLE|GMEM_ZEROINIT*/, (UIntPtr)Math.Max(1, data.Length));
        var p = GlobalLock(h);
        Marshal.Copy(data, 0, p, data.Length);
        GlobalUnlock(h);
        return h;
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new COMException("", E_NOTIMPL);
    public int QueryGetData(ref FORMATETC format) => Find(format) is { } e && (format.tymed & Tymed(e.Medium)) != 0 ? 0 : DV_E_FORMATETC;
    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut) { formatOut = formatIn; return DATA_S_SAMEFORMATETC; }
    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) { if (release) ReleaseStgMedium(ref medium); } // drop descriptions
    public IEnumFORMATETC EnumFormatEtc(DATADIR direction) =>
        new Formats(_entries.Where(e => e.Index <= 0).Select(e => new FORMATETC
        {
            cfFormat = e.Cf, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = Tymed(e.Medium),
        }).GroupBy(f => f.cfFormat).Select(g => g.First()).ToArray());
    public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection) { connection = 0; return OLE_E_ADVISENOTSUPPORTED; }
    public void DUnadvise(int connection) => throw new COMException("", OLE_E_ADVISENOTSUPPORTED);
    public int EnumDAdvise(out IEnumSTATDATA enumAdvise) { enumAdvise = null!; return OLE_E_ADVISENOTSUPPORTED; }

    // the async drop protocol, when the case's source supports it
    public void SetAsyncMode(bool doOpAsync) { }
    public void GetAsyncMode(out bool isOpAsync) => isOpAsync = _async;
    public void StartOperation(IntPtr reserved) { if (!_async) throw new COMException("", E_NOTIMPL); _started = true; }
    public void InOperation(out bool inAsyncOp) => inAsyncOp = _started && !_ended;
    public void EndOperation(int result, IntPtr reserved, uint effects) { _ended = true; if (result != 0) _wrong.Add($"the async drop ended with 0x{result:X8}"); }

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
