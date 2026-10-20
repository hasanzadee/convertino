using System.Text.Json;

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
/// desktop app runs: the app reloads it on every change.
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

    public static string LabelOf(string action) =>
        Presets.FirstOrDefault(p => p.Id == action).Label ?? (action.StartsWith("to-") ? action[3..].ToUpperInvariant() : action);

    public static List<WatchFolder> Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<List<WatchFolder>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception ex) { Log.Error("watch load", ex); }
        return [];
    }

    public static void Save(List<WatchFolder> list)
    {
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
        File.Move(tmp, FilePath, true);
    }

    public static void Add(WatchFolder wf)
    {
        var list = Load();
        list.RemoveAll(w => Same(w.Path, wf.Path));
        list.Add(wf);
        Save(list);
    }

    public static bool Remove(string path)
    {
        var list = Load();
        int n = list.RemoveAll(w => Same(w.Path, path));
        if (n > 0) Save(list);
        return n > 0;
    }

    static bool Same(string a, string b) =>
        string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('\\', '/'), System.IO.Path.GetFullPath(b).TrimEnd('\\', '/'),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    // ---------- the desktop app's watchers ----------

    static readonly List<Watcher> Running = [];
    static FileSystemWatcher? _listWatcher;
    static Timer? _reload;
    static Action<Job, WatchFolder>? _done;

    /// <summary>The desktop apps' entry point: results go to Recent, the activity stats and (if enabled) a result card.</summary>
    public static void StartAll() => StartAll(Report);

    static void Report(Job job, WatchFolder wf)
    {
        if (job.Outputs.Count > 0) Settings.Current.AddRecent(job.Outputs[0], job.Action.Verb);
        Stats.Record(job);
        if (!Settings.Current.Notifications || job.Outputs.Count == 0) return;
        var ui = Platform.Current.Ui;
        var folder = System.IO.Path.GetFileName(wf.Path.TrimEnd('\\', '/'));
        ui.Post(() =>
        {
            var view = ui.Show(job.Action, job.Inputs, new ScreenPoint(0, 0));
            var title = job.Title ?? (job.Outputs.Count == 1 ? System.IO.Path.GetFileName(job.Outputs[0]) : $"{job.Outputs.Count} files ready");
            view.Done(title, $"Watched {folder}" + (job.Summary is { Length: > 0 } s ? " · " + s : ""), job.Outputs, job.Action.Icon);
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

    static void Reload()
    {
        lock (Running)
        {
            foreach (var w in Running) w.Dispose();
            Running.Clear();
            foreach (var wf in Load())
            {
                if (!Directory.Exists(wf.Path)) { Log.Info($"watch: {wf.Path} is missing, skipped"); continue; }
                try { Running.Add(new Watcher(wf, job => _done?.Invoke(job, wf))); }
                catch (Exception ex) { Log.Error($"watch {wf.Path}", ex); }
            }
            Log.Info($"watch: {Running.Count} folder(s)");
        }
    }
}

/// <summary>Applies one petal to every file that lands in a folder, once the file has finished arriving.</summary>
public sealed class Watcher : IDisposable
{
    readonly WatchFolder _wf;
    readonly Action<Job> _done;
    readonly FileSystemWatcher _fsw;
    readonly CancellationTokenSource _cts = new();
    readonly System.Threading.Channels.Channel<string> _queue = System.Threading.Channels.Channel.CreateUnbounded<string>();
    readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);

    public Watcher(WatchFolder wf, Action<Job> done)
    {
        _wf = wf;
        _done = done;
        _fsw = new FileSystemWatcher(wf.Path) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
        _fsw.Created += (_, e) => Enqueue(e.FullPath);
        _fsw.Renamed += (_, e) => Enqueue(e.FullPath); // browsers and copy tools write *.part / *.tmp first, then rename
        _fsw.Error += (_, e) => Log.Error($"watch {wf.Path}", e.GetException());
        _fsw.EnableRaisingEvents = true;
        _ = Task.Run(Loop);
        Log.Info($"watch: {wf.Path} → {wf.Action}");
    }

    void Enqueue(string path)
    {
        if (Skip(path)) return;
        lock (_pending) if (!_pending.Add(path)) return;
        _queue.Writer.TryWrite(path);
    }

    static readonly string[] Partial = [".tmp", ".part", ".partial", ".crdownload", ".download", ".opdownload", ".!ut", ".temp"];

    static bool Skip(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        if (name.StartsWith('.') || name.StartsWith("~$")) return true;
        if (Partial.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase))) return true;
        return Directory.Exists(path);
    }

    async Task Loop()
    {
        var ct = _cts.Token;
        try
        {
            await foreach (var path in _queue.Reader.ReadAllAsync(ct))
            {
                try { await Process(path, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { Log.Error($"watch {System.IO.Path.GetFileName(path)}", ex); }
                finally { lock (_pending) _pending.Remove(path); }
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task Process(string path, CancellationToken ct)
    {
        if (!await Settled(path, ct)) return;
        var action = Catalog.All([path]).FirstOrDefault(a => a.Id == _wf.Action);
        if (action == null || action.Interactive) { Log.Info($"watch: {_wf.Action} doesn't apply to {System.IO.Path.GetFileName(path)}"); return; }
        var job = await Jobs.RunHeadless(action, [path]);
        // results move into the Out subfolder: the watched folder stays an inbox and nothing we write re-triggers us
        var outDir = System.IO.Path.Combine(_wf.Path, _wf.Out);
        Directory.CreateDirectory(outDir);
        for (int i = 0; i < job.Outputs.Count; i++)
        {
            var o = job.Outputs[i];
            var name = System.IO.Path.GetFileName(o);
            if (Directory.Exists(o))
            {
                var dst = Paths.UniqueDir(outDir, name);
                Directory.Move(o, dst);
                job.Outputs[i] = dst;
            }
            else if (File.Exists(o))
            {
                var dst = Paths.Unique(outDir, System.IO.Path.GetFileNameWithoutExtension(name), System.IO.Path.GetExtension(name));
                File.Move(o, dst);
                job.Outputs[i] = dst;
            }
        }
        Log.Info($"watch: {System.IO.Path.GetFileName(path)} → {string.Join(" | ", job.Outputs)}");
        _done(job);
    }

    /// <summary>Wait until the file stops growing and nobody holds it open for writing (up to 10 minutes).</summary>
    static async Task<bool> Settled(string path, CancellationToken ct)
    {
        long last = -1;
        for (int i = 0; i < 600; i++)
        {
            await Task.Delay(i == 0 ? 600 : 1000, ct);
            if (!File.Exists(path)) return false;
            long size = new FileInfo(path).Length;
            if (size == last && size > 0)
            {
                try
                {
                    using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return true;
                }
                catch (IOException) { }
            }
            last = size;
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
