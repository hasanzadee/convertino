using System.Diagnostics;
using System.Text.RegularExpressions;
using ImageMagick;
using Convertino.Services;

namespace Convertino;

/// <summary>
/// What the flower knows before you drop. While it is open, the petals' real actions run ahead of you:
/// first on a small copy of the picture (within a blink the hub shows the result and format petals an estimated
/// size), then on a full copy in a sandbox (the exact size). Drop on a petal whose full run is done and its
/// result is simply moved into place — the conversion has already happened.
/// </summary>
public sealed class Preview : IDisposable
{
    /// <summary>What a petal would give. <see cref="Bgra"/> is a small picture of the result (null when it isn't an image).</summary>
    public sealed record Look(byte[]? Bgra, int W, int H, bool Alpha, long? Bytes, bool Exact, string? Dims, string? Note);

    /// <summary>A full result handed to <see cref="Jobs"/> on drop.</summary>
    public sealed class Handoff
    {
        internal readonly Entry Entry;
        internal readonly Preview Owner;

        internal Handoff(Entry entry, Preview owner) { Entry = entry; Owner = owner; }

        /// <summary>Wait for the run (it may still be going), then move its outputs beside the originals.
        /// False = nothing usable (failed, or the source changed meanwhile): run the action the normal way.</summary>
        public async Task<bool> Into(Job job)
        {
            var e = Entry;
            e.Forward = job.Report;
            try
            {
                var r = await e.Full!.WaitAsync(job.Token);
                return r != null && Owner.MoveInto(r, job);
            }
            catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
            {
                e.Cts.Cancel(); // cancelled on the card: stop the run we were waiting for too
                throw;
            }
            catch (Exception ex) { Log.Error("preview handoff", ex); return false; }
            finally
            {
                e.Forward = null;
                // done with it, or gave up waiting: its sandbox goes once the run is over
                _ = e.Full!.ContinueWith(_ => Owner.Release(e), TaskScheduler.Default);
            }
        }
    }

    internal sealed class Entry
    {
        public required PetalAction Action;
        public Look? Look;
        public Task? Proxy;
        public Task<Result?>? Full;
        public volatile Action<double>? Forward;
        /// <summary>Its own, so a run handed to a drop survives the flower closing.</summary>
        public readonly CancellationTokenSource Cts = new();
        public bool Claimed;
        public string Dir = "";
    }

    internal sealed record Result(string Dir, List<string> Copies, List<string> Outputs, string? Summary, string? Title, long? Saved);

    /// <summary>Raised on a worker thread whenever a petal's <see cref="Look"/> changes.</summary>
    public event Action? Changed;

    /// <summary>Total size of the dropped files, for "−84%".</summary>
    public long InputBytes { get; }

    readonly IReadOnlyList<string> _files;
    readonly List<(long Length, DateTime Written)> _stamps;
    readonly long _pixels;
    readonly string _inDims;
    readonly string _root;
    readonly CancellationTokenSource _cts = new();
    readonly Dictionary<string, Entry> _entries = [];
    readonly object _gate = new();
    readonly SemaphoreSlim _fastSignal = new(0), _fullSignal = new(0);
    readonly Task _fastLane, _fullLane;
    List<Entry> _ring = [];
    Entry? _hover;
    long _hoverSince;
    string? _proxy;      // the small copy every quick run starts from
    string? _tiny;       // half of that again: two sizes give the curve to scale estimates along
    long _proxyPixels, _tinyPixels;
    bool _proxyFailed, _disposed, _lanesDone;
    int _claims;         // handed to a drop and not yet released
    readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the session is disposed, its runs are over and its sandbox is gone.</summary>
    public Task Closed => _closed.Task;

    // big enough to judge a result in a 108-DIP hub on a 2× screen
    const int LensSide = 224, ProxySide = 640, TinySide = 320;
    // full runs start by themselves (format petals) only for ordinary photos; anything bigger waits for a hover
    const long AutoFullPixels = 16_000_000, AutoFullBytes = 40L << 20;
    const long MaxFullBytes = 160L << 20;
    const long MaxPixels = 120_000_000;
    const int HoverDwellMs = 180, HeavyDwellMs = 450;

    Preview(IReadOnlyList<string> files, long pixels, string inDims)
    {
        _inDims = inDims;
        _files = files;
        _stamps = files.Select(f => { var fi = new FileInfo(f); return (fi.Length, fi.LastWriteTimeUtc); }).ToList();
        InputBytes = _stamps.Sum(s => s.Length);
        _pixels = pixels;
        _root = Path.Combine(Paths.Temp, "preview", Guid.NewGuid().ToString("N")[..12]);
        _fastLane = Task.Run(() => Lane(fast: true));
        _fullLane = Task.Run(() => Lane(fast: false));
    }

    /// <summary>A preview session for these files, or null when they aren't something it can work ahead on (only pictures for now).</summary>
    public static Preview? For(IReadOnlyList<string> files)
    {
        try
        {
            if (!Settings.Current.LivePreview || files.Count == 0 || files.Count > 24) return null;
            if (files.Any(f => Kinds.Of(f) != Kind.Image || !File.Exists(f))) return null;
            // the sandbox mirrors the source folder's name (some actions name results after it), so one folder only
            var dir = Path.GetDirectoryName(Path.GetFullPath(files[0]));
            if (files.Any(f => !string.Equals(Path.GetDirectoryName(Path.GetFullPath(f)), dir, StringComparison.OrdinalIgnoreCase))) return null;
            long pixels = 0;
            string dims = "";
            foreach (var f in files)
            {
                using var probe = new MagickImage();
                probe.Ping(f);
                pixels += (long)probe.Width * probe.Height;
                if (dims == "") dims = files.Count == 1 ? $"{probe.Width}×{probe.Height}" : "-";
            }
            if (pixels <= 0 || pixels > MaxPixels) return null;
            SweepOld();
            return new Preview(files.Select(Path.GetFullPath).ToList(), pixels, dims);
        }
        catch (Exception ex) { Log.Info($"preview off: {ex.Message}"); return null; }
    }

    #region what the flower calls

    /// <summary>The ring now on screen: quick looks for all of it, exact sizes for its format petals.</summary>
    public void Show(IReadOnlyList<PetalAction> ring)
    {
        lock (_gate) _ring = ring.Where(Works).Select(EntryFor).ToList();
        Wake();
    }

    /// <summary>The petal under the cursor (null = none): it jumps the queue, and after a short dwell its full run starts.</summary>
    public void Hover(PetalAction? a)
    {
        lock (_gate)
        {
            _hover = a != null && Works(a) ? EntryFor(a) : null;
            _hoverSince = Environment.TickCount64;
        }
        Wake();
        if (a != null)
            foreach (var ms in new[] { HoverDwellMs, HeavyDwellMs })
                _ = Task.Delay(ms + 10).ContinueWith(_ => Wake(), TaskScheduler.Default);
    }

    public Look? Get(PetalAction a)
    {
        lock (_gate) return _entries.TryGetValue(a.Id, out var e) ? e.Look : null;
    }

    /// <summary>True while the hovered petal's result is being worked out and there's nothing to show yet.</summary>
    public bool Working(PetalAction a)
    {
        lock (_gate)
            return _entries.TryGetValue(a.Id, out var e) && e.Look?.Bgra == null && e.Look?.Note == null
                && ((e.Proxy != null && !e.Proxy.IsCompleted) || (e.Full != null && !e.Full.IsCompleted));
    }

    /// <summary>On drop: the full run for this petal if one was started (done or still going), for <see cref="Jobs.Start"/>.</summary>
    public Handoff? Take(PetalAction a)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(a.Id, out var e) || e.Full == null || e.Claimed) return null;
            e.Claimed = true;
            _claims++;
            return new Handoff(e, this);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _cts.Cancel();
        lock (_gate) foreach (var e in _entries.Values.Where(e => !e.Claimed)) e.Cts.Cancel();
        Wake();
        // let running encoders finish (most can't be interrupted), then clear the sandbox but what a drop took
        _ = Task.WhenAll(_fastLane, _fullLane).ContinueWith(_ =>
        {
            List<Entry> all;
            lock (_gate) { all = [.. _entries.Values]; _lanesDone = true; }
            foreach (var e in all.Where(e => !e.Claimed && e.Dir != "")) TryDeleteDir(e.Dir);
            try
            {
                foreach (var d in Directory.Exists(_root) ? Directory.GetDirectories(_root) : [])
                    if (!Path.GetFileName(d).StartsWith("f-", StringComparison.Ordinal)) TryDeleteDir(d);
            }
            catch { }
            Tidy();
        }, TaskScheduler.Default);
    }

    #endregion

    #region policy

    // format petals: the size is the whole point
    static bool IsFormat(PetalAction a) => a.Id.StartsWith("to-", StringComparison.Ordinal) && a.Id != "to-pdf";

    /// <summary>Petals whose quick run on the small copy is meaningful (a picture of the result, a size to scale up).</summary>
    static bool Quick(PetalAction a) => IsFormat(a) || a.Id is "half" or "gray" or "square" or "rounded" or "compress";

    /// <summary>Petals that are safe to run ahead: a new file beside the source, nothing else touched (no clipboard, no window).</summary>
    static bool Works(PetalAction a) => !a.Interactive && !a.Quick
        && (Quick(a) || a.Id is "fithd" or "messenger" or "meta" or "cutout" or "to-pdf");

    // the AI cut-out costs seconds of CPU/GPU: only for a petal the cursor rests on
    static bool Heavy(PetalAction a) => a.Id == "cutout";

    // size estimates only where the size is what you pick the petal for
    static bool Estimates(PetalAction a) => IsFormat(a) || a.Id == "compress";

    bool AutoFull(Entry e) => IsFormat(e.Action) && _pixels <= AutoFullPixels && InputBytes <= AutoFullBytes;

    #endregion

    #region workers

    Entry EntryFor(PetalAction a)
    {
        if (!_entries.TryGetValue(a.Id, out var e)) _entries[a.Id] = e = new Entry { Action = a };
        return e;
    }

    void Wake()
    {
        try { _fastSignal.Release(); _fullSignal.Release(); } catch (ObjectDisposedException) { }
    }

    async Task Lane(bool fast)
    {
        var signal = fast ? _fastSignal : _fullSignal;
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            Entry? next;
            lock (_gate) next = fast ? NextQuick() : NextFull();
            if (next == null)
            {
                try { await signal.WaitAsync(ct); } catch (OperationCanceledException) { break; }
                continue;
            }
            try
            {
                if (fast) await RunQuick(next, ct);
                else await RunFull(next, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Info($"preview {next.Action.Id}: {ex.Message}"); }
        }
    }

    Entry? NextQuick()
    {
        if (_proxyFailed || _disposed) return null;
        if (_hover is { Proxy: null } h && Quick(h.Action)) return Mark(h);
        return _ring.FirstOrDefault(e => e.Proxy == null && Quick(e.Action)) is { } r ? Mark(r) : null;

        static Entry Mark(Entry e) { e.Proxy = Task.CompletedTask; return e; }
    }

    Entry? NextFull()
    {
        if (_disposed || InputBytes > MaxFullBytes) return null;
        long dwell = Environment.TickCount64 - _hoverSince;
        if (_hover is { Full: null } h && dwell >= (Heavy(h.Action) ? HeavyDwellMs : HoverDwellMs)) return h;
        return _ring.FirstOrDefault(e => e.Full == null && AutoFull(e));
    }

    async Task RunQuick(Entry e, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource();
        lock (_gate) e.Proxy = tcs.Task;
        try
        {
            if (_proxy == null && !MakeProxy()) return;
            var dir = Path.Combine(_root, "q-" + Safe(e.Action.Id), Path.GetFileName(Path.GetDirectoryName(_files[0])) ?? "src");
            Directory.CreateDirectory(dir);
            var input = Path.Combine(dir, Path.GetFileName(_proxy!));
            File.Copy(_proxy!, input, true);
            var job = new Job { Inputs = [input], Action = e.Action, Token = ct };
            var sw = Stopwatch.StartNew();
            await e.Action.Run(job);
            ct.ThrowIfCancellationRequested();
            var outFile = job.Outputs.FirstOrDefault(File.Exists);
            Look look;
            if (outFile == null) look = new Look(null, 0, 0, false, null, false, null, job.Title);
            else
            {
                var (bgra, w, h, alpha) = Lens(outFile);
                long? est = null;
                // a picture no bigger than the small copy: its size is the real one already
                if (Estimates(e.Action) && _tiny == null) est = Extrapolate(Paths.SizeOf(outFile), _proxyPixels, 0, 0);
                if (Estimates(e.Action) && _tiny != null)
                {
                    // the same action on a copy half as wide: how the size grows with pixels depends on the picture and
                    // the format (a smooth wallpaper in AVIF barely grows, noise in PNG grows almost linearly) — follow that curve
                    var tdir = Path.Combine(_root, "t-" + Safe(e.Action.Id), Path.GetFileName(Path.GetDirectoryName(_files[0])) ?? "src");
                    Directory.CreateDirectory(tdir);
                    var tin = Path.Combine(tdir, Path.GetFileName(_tiny));
                    File.Copy(_tiny, tin, true);
                    var tjob = new Job { Inputs = [tin], Action = e.Action, Token = ct };
                    await e.Action.Run(tjob);
                    var tout = tjob.Outputs.FirstOrDefault(File.Exists);
                    est = Extrapolate(Paths.SizeOf(outFile), _proxyPixels, tout == null ? 0 : Paths.SizeOf(tout), _tinyPixels);
                }
                look = new Look(bgra, w, h, alpha, est, false, null, null);
            }
            lock (_gate) { if (e.Look?.Exact != true) e.Look = look; }
            Log.Info($"preview quick {e.Action.Id} {sw.ElapsedMilliseconds} ms");
            Changed?.Invoke();
        }
        finally { tcs.TrySetResult(); }
    }

    async Task RunFull(Entry e, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<Result?>();
        lock (_gate) e.Full = tcs.Task;
        Result? result = null;
        try
        {
            e.Dir = Path.Combine(_root, "f-" + Safe(e.Action.Id));
            var dir = Path.Combine(e.Dir, Path.GetFileName(Path.GetDirectoryName(_files[0])) ?? "src");
            Directory.CreateDirectory(dir);
            var copies = new List<string>();
            foreach (var f in _files)
            {
                var c = Path.Combine(dir, Path.GetFileName(f));
                File.Copy(f, c, true);
                copies.Add(c);
            }
            var job = new Job { Inputs = copies, Action = e.Action, Token = e.Cts.Token, OnProgress = p => e.Forward?.Invoke(p) };
            var sw = Stopwatch.StartNew();
            await e.Action.Run(job);
            e.Cts.Token.ThrowIfCancellationRequested();
            result = new Result(dir, copies, [.. job.Outputs], job.Summary, job.Title, job.Saved);
            long bytes = job.Outputs.Sum(Paths.SizeOf);
            var first = job.Outputs.FirstOrDefault(o => File.Exists(o) && Kinds.Of(o) == Kind.Image);
            string? dims = null;
            byte[]? bgra = null; int w = 0, h = 0; bool alpha = false;
            if (first != null)
            {
                (bgra, w, h, alpha) = Lens(first);
                try { using var p = new MagickImage(); p.Ping(first); dims = $"{p.Width}×{p.Height}"; } catch { }
            }
            lock (_gate)
            {
                var keep = e.Look;
                e.Look = new Look(bgra ?? keep?.Bgra, bgra != null ? w : keep?.W ?? 0, bgra != null ? h : keep?.H ?? 0, bgra != null ? alpha : keep?.Alpha ?? false,
                    job.Outputs.Count > 0 ? bytes : null, true, dims, job.Outputs.Count == 0 ? job.Title : null);
            }
            Log.Info($"preview full {e.Action.Id} {sw.ElapsedMilliseconds} ms → {Paths.HumanSize(bytes)}");
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Info($"preview full {e.Action.Id} failed: {ex.Message}");
            result = null;
        }
        finally { tcs.TrySetResult(result); }
    }

    /// <summary>The first picture, shrunk to <see cref="ProxySide"/>, in its own format when that can be written (so "Compress" behaves as it would).</summary>
    bool MakeProxy()
    {
        try
        {
            var src = _files[0];
            var ext = Kinds.Ext(src);
            using var img = LoadSmall(src, ProxySide);
            img.Resize(new MagickGeometry(ProxySide, ProxySide) { Greater = true });
            // FormatOf says PNG for anything it doesn't know (SVG, PSD, camera raw …): those become a PNG copy
            var fmt = Images.FormatOf(ext);
            bool same = (ext == "png" || fmt != MagickFormat.Png) && Images.CanWrite(fmt);
            var dir = Path.Combine(_root, "proxy");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, same ? Path.GetFileName(src) : Path.GetFileNameWithoutExtension(src) + ".png");
            if (same && ext is "jpg" or "jpeg" or "jfif") img.Quality = 92;
            if (same && ext == "webp") img.Quality = 90;
            img.Write(path, same ? fmt : MagickFormat.Png);
            _proxyPixels = (long)img.Width * img.Height;
            _proxy = path;
            if (img.Width > TinySide || img.Height > TinySide)
            {
                img.Resize(new MagickGeometry(TinySide, TinySide) { Greater = true });
                var tdir = Path.Combine(dir, "tiny");
                Directory.CreateDirectory(tdir);
                var tiny = Path.Combine(tdir, Path.GetFileName(path));
                img.Write(tiny, same ? fmt : MagickFormat.Png);
                _tinyPixels = (long)img.Width * img.Height;
                _tiny = tiny;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"preview proxy: {ex.Message}");
            _proxyFailed = true;
            return false;
        }
    }

    /// <summary>bytes ≈ a·pixels^k through the two small runs, out to the real pixel count (and from the first picture to all).</summary>
    long Extrapolate(long big, long bigPx, long small, long smallPx)
    {
        double k = 0.8;
        if (small > 0 && big > 0 && bigPx > smallPx * 1.5)
            k = Math.Clamp(Math.Log((double)big / small) / Math.Log((double)bigPx / smallPx), 0.0, 1.05);
        double perFile = (double)_pixels / _files.Count;
        return (long)(big * Math.Pow(perFile / Math.Max(1, bigPx), k) * _files.Count);
    }

    /// <summary>Decode at a reduced size where the codec can (JPEG's DCT scaling makes a 50 MP photo a ~40 ms job).</summary>
    static MagickImage LoadSmall(string path, int side)
    {
        if (Kinds.Ext(path) is "jpg" or "jpeg" or "jfif")
        {
            var rs = new MagickReadSettings();
            rs.SetDefine(MagickFormat.Jpeg, "size", $"{side * 2}x{side * 2}");
            var img = new MagickImage(path, rs);
            img.AutoOrient();
            return img;
        }
        return Images.Load(path);
    }

    static (byte[] bgra, int w, int h, bool alpha) Lens(string path)
    {
        using var img = LoadSmall(path, LensSide);
        img.Resize(new MagickGeometry(LensSide, LensSide) { Greater = true });
        bool alpha = img.HasAlpha && !img.IsOpaque;
        var (bgra, w, h) = Images.ToBgra(img);
        return (bgra, w, h, alpha);
    }

    #endregion

    #region handoff

    static readonly Regex Numbered = new(@"^(?<base>.*) \((?<n>\d+)\)$", RegexOptions.Compiled);

    /// <summary>Move a full run's outputs beside the originals, named as the real run would have named them there.</summary>
    internal bool MoveInto(Result r, Job job)
    {
        // the source changed since we copied it: the result is stale
        for (int i = 0; i < _files.Count; i++)
        {
            var fi = new FileInfo(_files[i]);
            if (!fi.Exists || fi.Length != _stamps[i].Length || fi.LastWriteTimeUtc != _stamps[i].Written) return false;
        }
        if (job.Inputs.Count != _files.Count || !job.Inputs.SequenceEqual(_files, StringComparer.OrdinalIgnoreCase)) return false;

        var target = Path.GetDirectoryName(_files[0])!;
        var tops = new List<string>();
        foreach (var o in r.Outputs)
        {
            var rel = Path.GetRelativePath(r.Dir, o);
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return false;
            var top = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (!tops.Contains(top)) tops.Add(top);
        }
        var copyNames = r.Copies.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var top in tops)
        {
            var from = Path.Combine(r.Dir, top);
            if (Directory.Exists(from))
            {
                var to = Paths.UniqueDir(target, Unnumber(top, "", copyNames));
                MoveDir(from, to);
                moved[from] = to;
            }
            else if (File.Exists(from))
            {
                var ext = Path.GetExtension(top);
                var to = Paths.Unique(target, Unnumber(Path.GetFileNameWithoutExtension(top), ext, copyNames), ext);
                File.Move(from, to);
                moved[from] = to;
            }
        }
        foreach (var o in r.Outputs)
        {
            var hit = moved.Keys.FirstOrDefault(k => o.Equals(k, StringComparison.OrdinalIgnoreCase)
                || o.StartsWith(k + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (hit != null) job.Outputs.Add(moved[hit] + o[hit.Length..]);
        }
        job.Summary = r.Summary;
        job.Title = r.Title;
        job.Saved = r.Saved;
        Log.Info($"preview handoff {job.Action.Id}: {r.Outputs.Count} output(s) ready on drop");
        return true;
    }

    /// <summary>"photo (2).jpg" only got its number because the sandbox copy "photo.jpg" sat there; beside the original, numbering starts over.</summary>
    static string Unnumber(string stem, string ext, HashSet<string?> copies)
    {
        var m = Numbered.Match(stem);
        return m.Success && copies.Contains(m.Groups["base"].Value + ext) ? m.Groups["base"].Value : stem;
    }

    static void MoveDir(string from, string to)
    {
        try { Directory.Move(from, to); return; }
        catch (IOException) { } // another volume
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst);
        }
        TryDeleteDir(from);
    }

    /// <summary>A drop is done with its run: its sandbox goes, and the whole session's once nothing else holds it.</summary>
    internal void Release(Entry e)
    {
        if (e.Dir != "") TryDeleteDir(e.Dir);
        lock (_gate) _claims--;
        Tidy();
    }

    void Tidy()
    {
        lock (_gate) if (!_lanesDone || _claims > 0) return;
        TryDeleteDir(_root);
        _closed.TrySetResult();
    }

    static void TryDeleteDir(string d)
    {
        try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
    }

    /// <summary>Leftovers of a crash or a kill: sandboxes older than an hour.</summary>
    static void SweepOld()
    {
        try
        {
            var root = Path.Combine(Paths.Temp, "preview");
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.EnumerateDirectories(root))
                if (Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddHours(-1)) TryDeleteDir(d);
        }
        catch { }
    }

    static string Safe(string id) => string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    #endregion

    #region text

    /// <summary>"≈ 250 KB · −84%" (estimate) or "250 KB · −84%"; tools that change the size lead with "2016×1512".</summary>
    public string? SizeLine(Look l, bool withDims = true)
    {
        if (l.Bytes is not long b) return l.Note;
        var size = (l.Exact ? "" : "≈ ") + Paths.HumanSize(b);
        string pct = "";
        if (InputBytes > 0)
        {
            double d = (b - InputBytes) * 100.0 / InputBytes;
            pct = Math.Abs(d) < 0.5 ? " · same" : d < 0 ? $" · −{-d:0}%" : $" · +{d:0}%";
        }
        // dims only when the petal changes them (Half, Square …), and only for one picture
        return withDims && l.Dims != null && _inDims != "-" && l.Dims != _inDims ? $"{l.Dims} · {size}" : size + pct;
    }

    /// <summary>Smaller than the source?</summary>
    public bool Smaller(Look l) => l.Bytes is long b && b < InputBytes;

    #endregion

    #region dev / CLI

    /// <summary>Work everything out for these petals and wait (convertino preview, --snapshot).</summary>
    public async Task Settle(IReadOnlyList<PetalAction> petals, bool full, int timeoutMs = 120_000)
    {
        Show(petals);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            bool done;
            lock (_gate)
            {
                var es = _ring;
                done = es.All(e => (!Quick(e.Action) || (e.Proxy?.IsCompleted ?? false) || _proxyFailed)
                    && (!full || (e.Full?.IsCompleted ?? false)));
                if (full)
                    foreach (var e in es.Where(e => e.Full == null))
                    {
                        // nothing starts these by itself (tools, big pictures): queue them as if hovered, one by one
                        if (_hover == null || _hover.Full != null && _hover.Full.IsCompleted) { _hover = e; _hoverSince = 0; }
                    }
            }
            if (done) return;
            Wake();
            await Task.Delay(40);
        }
    }

    #endregion
}
