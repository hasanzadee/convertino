using System.Runtime.InteropServices;

namespace Convertino.Native;

/// <summary>
/// The drag pasteboard as the core reads it, by UTI (public.url, public.html, public.png, the promised file's type …), and
/// file promises — how Safari and Chrome hand over a dragged picture — received through NSFilePromiseReceiver at the drop.
/// </summary>
public sealed class MacDrag : IDragData
{
    const string DragName = "Apple CFPasteboard drag"; // NSPasteboardNameDrag
    readonly IntPtr _pb;
    List<string>? _types;

    MacDrag(IntPtr pb) => _pb = pb;

    /// <summary>The drag in progress (the drag pasteboard is one per session, alive for as long as the app is).</summary>
    public static MacDrag? Drag()
    {
        if (!ObjC.Load("AppKit")) return null;
        var pb = ObjC.Send(ObjC.Class("NSPasteboard"), "pasteboardWithName:", ObjC.NSString(DragName));
        return pb == IntPtr.Zero ? null : new MacDrag(pb);
    }

    /// <summary>A browser's drag (links, pictures, file promises), or null for anything else. <paramref name="files"/>: the local paths it offers.</summary>
    public static WebDrop? Read(IReadOnlyList<string>? files) => Drag() is { } d ? WebDrop.Read(d, files) : null;

    public IReadOnlyList<string> Formats => _types ??= Types();

    List<string> Types()
    {
        var list = new List<string>();
        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            var arr = ObjC.Send(_pb, "types");
            for (nuint i = 0; i < ObjC.Count(arr); i++)
                if (ObjC.Str(ObjC.At(arr, i)) is { } t) list.Add(t);
        }
        catch (Exception ex) { Log.Info("drag pasteboard types: " + ex.Message); }
        finally { ObjC.objc_autoreleasePoolPop(pool); }
        return list;
    }

    public byte[]? Bytes(string format, int max)
    {
        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            var data = ObjC.Send(_pb, "dataForType:", ObjC.NSString(format));
            if (data == IntPtr.Zero) return null;
            long len = (long)ObjC.SendUInt(data, "length");
            if (len > max) return null;
            var b = new byte[len];
            if (len > 0) Marshal.Copy(ObjC.Send(data, "bytes"), b, 0, (int)len);
            return b;
        }
        catch (Exception ex) { Log.Info($"drag pasteboard {format}: {ex.Message}"); return null; }
        finally { ObjC.objc_autoreleasePoolPop(pool); }
    }

    // ---------- file promises ----------

    static readonly Dictionary<string, TaskCompletionSource<string?>> Pending = [];
    static IntPtr _queue, _reader;

    /// <summary>Ask the drag source to write its promised file into <paramref name="dir"/> (during the drop; the source picks the name); the task ends when it has.</summary>
    public Task<string?>? Save(WebItem item, string dir, string name)
    {
        if (item.Source != WebSource.Promise) return null;
        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            var cls = ObjC.Class("NSFilePromiseReceiver");
            if (cls == IntPtr.Zero) return null;
            var receivers = ObjC.Send(_pb, "readObjectsForClasses:options:", ObjC.Send(ObjC.Class("NSArray"), "arrayWithObject:", cls), IntPtr.Zero);
            // nothing to receive: a pasteboard without a live source (the self-test), or a source that went away
            if (ObjC.Count(receivers) == 0) return null;
            if (_queue == IntPtr.Zero) _queue = ObjC.New("NSOperationQueue");
            if (_reader == IntPtr.Zero) _reader = GlobalBlock();
            var t = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Pending) Pending[Norm(Path.GetFullPath(dir))] = t;
            var dest = ObjC.Send(ObjC.Class("NSURL"), "fileURLWithPath:", ObjC.NSString(dir));
            var options = ObjC.Send(ObjC.Class("NSDictionary"), "dictionary");
            Msg(ObjC.At(receivers, 0), ObjC.Sel("receivePromisedFilesAtDestination:options:operationQueue:reader:"), dest, options, _queue, _reader);
            return t.Task;
        }
        catch (Exception ex) { Log.Error("file promise", ex); return null; }
        finally { ObjC.objc_autoreleasePoolPop(pool); }
    }

    /// <summary>/private/var/… and /var/… are the same folder.</summary>
    static string Norm(string p) => (p.StartsWith("/private/var/", StringComparison.Ordinal) ? p[8..] : p).TrimEnd('/');

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    static extern void Msg(IntPtr o, IntPtr sel, IntPtr a, IntPtr b, IntPtr c, IntPtr d);

    [StructLayout(LayoutKind.Sequential)]
    struct BlockDescriptor { public nuint Reserved, Size; }

    [StructLayout(LayoutKind.Sequential)]
    struct BlockLiteral { public IntPtr Isa; public int Flags, Reserved; public IntPtr Invoke, Descriptor; }

    /// <summary>A global (never copied, never freed) Objective-C block: void (^)(NSURL *file, NSError *error).</summary>
    static unsafe IntPtr GlobalBlock()
    {
        var isa = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_NSConcreteGlobalBlock");
        var desc = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
        desc->Size = (nuint)sizeof(BlockLiteral);
        var block = (BlockLiteral*)NativeMemory.AllocZeroed((nuint)sizeof(BlockLiteral));
        block->Isa = isa;
        block->Flags = 1 << 28; // BLOCK_IS_GLOBAL
        block->Invoke = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnPromise;
        block->Descriptor = (IntPtr)desc;
        return (IntPtr)block;
    }

    [UnmanagedCallersOnly]
    static void OnPromise(IntPtr block, IntPtr file, IntPtr error)
    {
        try
        {
            var path = file != IntPtr.Zero ? ObjC.Str(ObjC.Send(file, "path")) : null;
            var why = error != IntPtr.Zero ? ObjC.Str(ObjC.Send(error, "localizedDescription")) : null;
            TaskCompletionSource<string?>? t = null;
            lock (Pending)
            {
                // the drop it belongs to: the folder it was written into (or the only one waiting)
                var key = path != null ? Norm(Path.GetDirectoryName(path)!) : null;
                if (key == null || !Pending.ContainsKey(key)) key = Pending.Count == 1 ? Pending.Keys.First() : null;
                if (key != null && Pending.Remove(key, out var found)) t = found;
            }
            if (why != null) Log.Info("file promise: " + why);
            t?.TrySetResult(why == null ? path : null);
        }
        catch (Exception ex) { Log.Error("file promise", ex); }
    }

    // ---------- self-test ----------

    /// <summary>
    /// <c>convertino --browser-drop-selftest &lt;dir&gt;</c>: the self-test's drags (Safari / Chrome types) written to a private
    /// pasteboard and read through this class, then dropped on petals (<see cref="WebSelfTest"/>). A file promise there has
    /// no source behind it, so its case checks the fall-back to the picture's URL.
    /// </summary>
    public static int SelfTest(string dir)
    {
        if (!ObjC.Load("AppKit")) return 1;
        var boards = new List<IntPtr>();
        try
        {
            return WebSelfTest.Run(dir, "mac", c =>
            {
                var pb = ObjC.Send(ObjC.Class("NSPasteboard"), "pasteboardWithUniqueName");
                boards.Add(pb);
                ObjC.Send(pb, "clearContents");
                foreach (var (f, b) in c.Formats) ObjC.Send(pb, "setData:forType:", ObjC.NSData(b), ObjC.NSString(f));
                return (new MacDrag(pb), c.Local);
            }).GetAwaiter().GetResult();
        }
        finally
        {
            foreach (var pb in boards) try { ObjC.Send(pb, "releaseGlobally"); } catch { }
        }
    }
}
