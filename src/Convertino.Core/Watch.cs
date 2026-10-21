using System.Text.Json;
using System.Threading.Channels;

namespace Convertino;

/// <summary>A folder whose new files get one petal applied automatically (e.g. every new image → PDF).</summary>
public sealed class WatchFolder
{
    public string Path { get; set; } = "";
    /// <summary>Petal id as in <c>convertino actions</c>: "to-pdf", "to-webp", "compress", "meta" …</summary>
    public string Action { get; set; } = "to-pdf";
    /// <summary>Subfolder the results go to (created on demand). Keeps the watched folder tidy and can't re-trigger.</summary>
    public string Out { get; set; } = "Converted";
}

/// <summary>
/// The watch list lives in watch.json beside the settings, not in settings.json, so the CLI can edit it while the
/// desktop app runs: the app reloads it on every change and only touches the entries that changed.
/// </summary>
public static class Watches
{
    public static string FilePath => System.IO.Path.Combine(Paths.AppData, "watch.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Petals that make sense for a folder inbox, with their short labels for the UIs.</summary>
    public static readonly (string Id, string Label)[] Presets =
    [
        ("to-pdf", "PDF"), ("to-jpg", "JPG"), ("to-png", "PNG"), ("to-webp", "WEBP"), ("compress", "Compress"), ("meta", "Clean"),
    ];

    /// <summary>The same idea under another id for some kinds ("Clean" is "meta" for images, "clean" for PDFs).</summary>
    public static IEnumerable<string> Aliases(string action) => action switch
    {
        "meta" or "clean" => ["meta", "clean"],
        _ => [action],
    };

    public static string LabelOf(string action) =>
        Presets.FirstOrDefault(p => p.Id == action).Label is { } label ? L.T(label) : action.StartsWith("to-") ? action[3..].ToUpperInvariant() : action;

    /// <summary>A plain subfolder name, or null: never empty, ".", "..", rooted or with separators, so results can't land back in the watched folder.</summary>
    public static string? ValidOut(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name is "." or "..") return null;
        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\')) return null;
        return name;
    }

    static string Norm(string p)
    {
        try { return System.IO.Path.GetFullPath(p).TrimEnd('\\', '/'); }
        catch { return p; }
    }

    static StringComparison PathCase => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    static bool Same(string a, string b) => string.Equals(Norm(a), Norm(b), PathCase);
    static string Key(WatchFolder w) => $"{(OperatingSystem.IsLinux() ? Norm(w.Path) : Norm(w.Path).ToUpperInvariant())}|{w.Action}|{ValidOut(w.Out) ?? "Converted"}";

    /// <summary>The list, or null when watch.json exists but can't be read (so nobody overwrites it by accident).</summary>
    static List<WatchFolder>? TryLoad()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var list = JsonSerializer.Deserialize<List<WatchFolder?>>(File.ReadAllText(FilePath)) ?? [];
            return list.Where(w => w != null && !string.IsNullOrWhiteSpace(w.Path) && !string.IsNullOrWhiteSpace(w.Action)).Select(w => w!).ToList();
        }
        catch (Exception ex) { Log.Error("watch load", ex); return null; }
    }

    public static List<WatchFolder> Load() => TryLoad() ?? [];

    // CLI and app can both edit the list: one at a time, each with its own temp file
    static T Edit<T>(Func<List<WatchFolder>, T> change)
    {
        using var gate = new Mutex(false, "Convertino.WatchList." + Environment.UserName);
        bool owned = false;
        try { owned = gate.WaitOne(5000); } catch (AbandonedMutexException) { owned = true; }
        try
        {
            var list = TryLoad();
            if (list == null)
            {
                // keep the unreadable file for the user instead of silently replacing their list
                var bad = FilePath + $".bad-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Copy(FilePath, bad, true);
                Log.Info($"watch: unreadable watch.json kept as {bad}");
                list = [];
            }
            var result = change(list);
            var tmp = $"{FilePath}.{Environment.ProcessId}.tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
            File.Move(tmp, FilePath, true);
            return result;
        }
        finally { if (owned) gate.ReleaseMutex(); }
    }

    public static void Add(WatchFolder wf)
    {
        wf.Out = ValidOut(wf.Out) ?? throw new ArgumentException(L.T("The results folder needs a plain name, like Converted."));
        Edit(list => { list.RemoveAll(w => Same(w.Path, wf.Path)); list.Add(wf); return 0; });
    }

    public static bool Remove(string path) => Edit(list => list.RemoveAll(w => Same(w.Path, path)) > 0);

    // ---------- the desktop app's watchers ----------

    static readonly Dictionary<string, Watcher> Running = [];
    static FileSystemWatcher? _listWatcher;
    static Timer? _reload;
    static Action<Job, WatchFolder>? _done;

    /// <summary>The desktop apps' entry point: results go to Recent, the activity stats and (if enabled) a result card.</summary>
    public static void StartAll() => StartAll(Report);

    static void Report(Job job, WatchFolder wf)
    {
        Stats.Record(job);
        var ui = Platform.Current.Ui;
        var folder = System.IO.Path.GetFileName(wf.Path.TrimEnd('\\', '/'));
        ui.Post(() =>
        {
            // Recent is read by the panels on the UI thread: change it there too
            if (job.Outputs.Count > 0) Settings.Current.AddRecent(job.Outputs[0], job.Action.EnglishVerb);
            if (!Settings.Current.Notifications || job.Outputs.Count == 0) return;
            var view = ui.Show(job.Action, job.Inputs, new ScreenPoint(0, 0));
            var title = job.Title ?? (job.Outputs.Count == 1 ? System.IO.Path.GetFileName(job.Outputs[0]) : L.N(job.Outputs.Count, "{0} file ready", "{0} files ready"));
            view.Done(title, L.T("Watched {0}", folder) + (job.Summary is { Length: > 0 } s ? " · " + s : ""), job.Outputs, job.Action.Icon);
        });
    }

    /// <summary>Start a watcher per entry and follow watch.json from now on. <paramref name="done"/> runs on a worker thread.</summary>
    public static void StartAll(Action<Job, WatchFolder> done)
    {
        _done = done;
        Reload();
        try
        {
            _listWatcher = new FileSystemWatcher(Paths.AppData, "watch.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.Size,
            };
            FileSystemEventHandler h = (_, _) => _reload?.Change(400, Timeout.Infinite);
            _listWatcher.Changed += h;
            _listWatcher.Created += h;
            _listWatcher.Deleted += h;
            _listWatcher.Renamed += (_, _) => _reload?.Change(400, Timeout.Infinite);
            _reload = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
            _listWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Log.Error("watch list", ex); }
    }

    /// <summary>Stop watchers that left the list and start new ones; the rest keep running with their queues intact.</summary>
    static void Reload()
    {
        try
        {
            var list = TryLoad();
            if (list == null) { Log.Info("watch: watch.json is unreadable, keeping the current watchers"); return; }
            var wanted = new Dictionary<string, WatchFolder>();
            foreach (var w in list) wanted.TryAdd(Key(w), w);
            lock (Running)
            {
                foreach (var k in Running.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
                {
                    Running[k].Dispose();
                    Running.Remove(k);
                }
                foreach (var (k, wf) in wanted)
                {
                    if (Running.ContainsKey(k)) continue;
                    if (!Directory.Exists(wf.Path)) { Log.Info($"watch: {wf.Path} is missing, skipped"); continue; }
                    try { Running[k] = new Watcher(wf, job => _done?.Invoke(job, wf)); }
                    catch (Exception ex) { Log.Error($"watch {wf.Path}", ex); }
                }
                Log.Info($"watch: {Running.Count} folder(s)");
            }
        }
        catch (Exception ex) { Log.Error("watch reload", ex); }
    }
}

/// <summary>
/// Applies one petal to every file that lands in a folder. Each new file waits on its own until it has finished
/// arriving; conversions then run one at a time.
/// </summary>
public sealed class Watcher : IDisposable
{
    readonly WatchFolder _wf;
    readonly string _outDir;
    readonly Action<Job> _done;
    readonly FileSystemWatcher _fsw;
    readonly CancellationTokenSource _cts = new();
    readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _handled = new(StringComparer.OrdinalIgnoreCase);
    readonly DateTime _since = DateTime.UtcNow;

    public Watcher(WatchFolder wf, Action<Job> done)
    {
        _wf = wf;
        _done = done;
        _outDir = Path.Combine(wf.Path, Watches.ValidOut(wf.Out) ?? "Converted");
        _fsw = new FileSystemWatcher(wf.Path)
        {
            IncludeSubdirectories = false,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
        };
        _fsw.Created += (_, e) => Consider(e.FullPath);
        // browsers and copy tools write *.part / *.tmp first, then rename; a plain rename of a finished file isn't new
        _fsw.Renamed += (_, e) => { if (Skip(e.OldFullPath)) Consider(e.FullPath); };
        _fsw.Error += (_, e) =>
        {
            Log.Error($"watch {wf.Path} (rescanning)", e.GetException());
            Rescan();
        };
        _fsw.EnableRaisingEvents = true;
        _ = Task.Run(Worker);
        Log.Info($"watch: {wf.Path} → {wf.Action}");
    }

    void Consider(string path, bool rescan = false)
    {
        if (_cts.IsCancellationRequested || Skip(path)) return;
        lock (_pending)
        {
            if (rescan && _handled.Contains(path)) return;
            if (!_pending.Add(path)) return;
        }
        _ = Task.Run(() => SettleThenQueue(path));
    }

    /// <summary>After a buffer overflow: everything that appeared since this watcher started and wasn't handled yet.</summary>
    void Rescan()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_wf.Path))
            {
                try
                {
                    if (File.GetCreationTimeUtc(f) >= _since.AddSeconds(-2) || File.GetLastWriteTimeUtc(f) >= _since.AddSeconds(-2))
                        Consider(f, rescan: true);
                }
                catch { }
            }
        }
        catch (Exception ex) { Log.Error($"watch rescan {_wf.Path}", ex); }
    }

    static readonly string[] Partial = [".tmp", ".part", ".partial", ".crdownload", ".download", ".opdownload", ".!ut", ".temp"];

    static bool Skip(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith('.') || name.StartsWith("~$")) return true;
        if (Partial.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase))) return true;
        return Directory.Exists(path);
    }

    async Task SettleThenQueue(string path)
    {
        bool queued = false;
        try
        {
            if (await Settled(path, _cts.Token)) queued = _queue.Writer.TryWrite(path);
            else Log.Info($"watch: gave up on {Path.GetFileName(path)} (gone, empty or still growing after 10 minutes)");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error($"watch {Path.GetFileName(path)}", ex); }
        finally { if (!queued) lock (_pending) _pending.Remove(path); }
    }

    async Task Worker()
    {
        var ct = _cts.Token;
        try
        {
            await foreach (var path in _queue.Reader.ReadAllAsync(ct))
            {
                try { await Process(path); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { Log.Error($"watch {Path.GetFileName(path)}", ex); }
                finally
                {
                    lock (_pending) { _pending.Remove(path); _handled.Add(path); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task Process(string path)
    {
        if (!File.Exists(path)) return;
        var all = Catalog.All([path]).ToList();
        var action = Watches.Aliases(_wf.Action).Select(id => all.FirstOrDefault(a => a.Id == id)).FirstOrDefault(a => a != null);
        if (action == null || action.Interactive) { Log.Info($"watch: {_wf.Action} doesn't apply to {Path.GetFileName(path)}"); return; }
        var job = await Jobs.RunHeadless(action, [path]);
        // results move into the Out subfolder: the watched folder stays an inbox and nothing we write re-triggers us
        Directory.CreateDirectory(_outDir);
        for (int i = 0; i < job.Outputs.Count; i++)
        {
            var o = job.Outputs[i];
            var name = Path.GetFileName(o);
            if (Directory.Exists(o))
            {
                var dst = Paths.UniqueDir(_outDir, name);
                Directory.Move(o, dst);
                job.Outputs[i] = dst;
            }
            else if (File.Exists(o))
            {
                var dst = Paths.Unique(_outDir, Path.GetFileNameWithoutExtension(name), Path.GetExtension(name));
                File.Move(o, dst);
                job.Outputs[i] = dst;
            }
        }
        Log.Info($"watch: {Path.GetFileName(path)} → {string.Join(" | ", job.Outputs)}");
        _done(job);
    }

    /// <summary>
    /// Wait until the file has content and its size and time stop changing for two checks in a row (up to 10 minutes);
    /// on Windows it also mustn't be open for writing (locks are only advisory on Linux and macOS).
    /// </summary>
    static async Task<bool> Settled(string path, CancellationToken ct)
    {
        long lastSize = -1;
        DateTime lastWrite = default;
        int calm = 0;
        for (int i = 0; i < 600; i++)
        {
            await Task.Delay(i == 0 ? 600 : 1000, ct);
            if (!File.Exists(path)) return false;
            var fi = new FileInfo(path);
            if (fi.Length > 0 && fi.Length == lastSize && fi.LastWriteTimeUtc == lastWrite) calm++;
            else calm = 0;
            lastSize = fi.Length;
            lastWrite = fi.LastWriteTimeUtc;
            if (calm < 1) continue;
            try
            {
                using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return true;
            }
            catch (IOException) { calm = 0; }
            catch (UnauthorizedAccessException) { return false; }
        }
        return false;
    }

    public void Dispose()
    {
        _fsw.EnableRaisingEvents = false;
        _fsw.Dispose();
        _cts.Cancel();
        _queue.Writer.TryComplete();
    }
}
