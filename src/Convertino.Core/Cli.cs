namespace Convertino;

/// <summary>
/// Command-line front end, shared by every platform:
/// <code>
///   convertino convert photo.heic --to jpg
///   convertino run compress *.png
///   convertino actions report.pdf
/// </code>
/// </summary>
public static class Cli
{
    public static readonly string Version = typeof(Cli).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static bool IsCommand(string[] args) =>
        args.Length > 0 && args[0] is "convert" or "run" or "actions" or "model" or "watch" or "--help" or "-h" or "help" or "--version" or "-v";

    public static async Task<int> Run(string[] args, TextWriter? output = null)
    {
        var o = output ?? Console.Out;
        try
        {
            switch (args[0])
            {
                case "--version" or "-v":
                    o.WriteLine($"Convertino {Version}");
                    return 0;
                case "--help" or "-h" or "help":
                    Help(o);
                    return 0;
                case "actions":
                {
                    var files = Files(args.Skip(1), o);
                    if (files == null) return 2;
                    var p = Catalog.For(files);
                    o.WriteLine($"{p.Title}");
                    o.WriteLine("  convert: " + string.Join("  ", p.Convert.Select(a => $"{a.Id}")));
                    if (p.Tools.Count > 0) o.WriteLine("  tools:   " + string.Join("  ", p.Tools.Select(a => a.Interactive ? $"({a.Id})" : a.Id)));
                    o.WriteLine("  (ids in parentheses open an editor and need the desktop app)");
                    return 0;
                }
                case "convert":
                {
                    int to = Array.IndexOf(args, "--to");
                    if (to < 0 || to == args.Length - 1) { o.WriteLine("usage: convertino convert <files…> --to <format>"); return 2; }
                    var fmt = args[to + 1].TrimStart('.').ToLowerInvariant();
                    var rest = args.Skip(1).Where((_, i) => i + 1 != to && i + 1 != to + 1);
                    return await RunAction("to-" + fmt, rest, o);
                }
                case "model":
                {
                    if (args.Length < 2 || args[1] is not ("install" or "status" or "remove"))
                    {
                        o.WriteLine("usage: convertino model install|status|remove   (the offline AI model behind Cut out)");
                        return 2;
                    }
                    if (!Services.Cutout.Supported) { o.WriteLine("Cut out isn't available on this Mac (ONNX Runtime has no Intel-Mac build) - it needs Apple silicon."); return 2; }
                    var path = Services.Cutout.ModelPath;
                    if (args[1] == "status") { o.WriteLine(File.Exists(path) ? $"installed: {path}" : "not installed - run: convertino model install"); return 0; }
                    if (args[1] == "remove") { if (File.Exists(path)) File.Delete(path); o.WriteLine("removed"); return 0; }
                    if (File.Exists(path)) { o.WriteLine($"already installed: {path}"); return 0; }
                    o.WriteLine($"Downloading BiRefNet-lite (MIT, {Paths.HumanSize(Services.Cutout.ModelBytes)}) from Hugging Face...");
                    int last = -1;
                    await Services.Cutout.Download((done, total) =>
                    {
                        int pct = (int)(done * 100 / Math.Max(1, total));
                        if (pct != last && pct % 5 == 0) { last = pct; o.Write($"\r  {pct}%  "); }
                    });
                    o.WriteLine($"\rinstalled: {path}");
                    return 0;
                }
                case "run":
                    if (args.Length < 3) { o.WriteLine("usage: convertino run <action-id> <files…>"); return 2; }
                    return await RunAction(args[1], args.Skip(2), o);
                case "watch":
                    return await Watch(args.Skip(1).ToArray(), o);
            }
        }
        catch (Exception ex)
        {
            o.WriteLine("error: " + Jobs.Friendly(ex));
            Log.Error("cli", ex);
            return 1;
        }
        return 2;
    }

    static async Task<int> RunAction(string id, IEnumerable<string> paths, TextWriter o)
    {
        var files = Files(paths, o);
        if (files == null) return 2;
        var action = Catalog.All(files).FirstOrDefault(a => a.Id == id);
        if (action == null)
        {
            o.WriteLine($"'{id}' isn't available for these files. Try: convertino actions {string.Join(' ', files.Select(Quote))}");
            return 2;
        }
        if (action.Interactive) { o.WriteLine($"'{id}' opens an editor — use the desktop app (drag + Shift)."); return 2; }
        var job = await Jobs.RunHeadless(action, files);
        foreach (var f in job.Outputs) o.WriteLine(f);
        if (!string.IsNullOrEmpty(job.Title)) o.WriteLine(job.Title);
        if (!string.IsNullOrEmpty(job.Summary)) o.WriteLine(job.Summary);
        return 0;
    }

    const string WatchUsage = """
        usage: convertino watch <folder> --to <format> | --run <action> [--out <subfolder>]   watch in the foreground (Ctrl+C stops)
               convertino watch add <folder> --to <format> | --run <action> [--out <subfolder>] watched by the desktop app
               convertino watch list
               convertino watch remove <folder>
        """;

    static async Task<int> Watch(string[] a, TextWriter o)
    {
        if (a.Length == 0) { o.WriteLine(WatchUsage); return 2; }
        switch (a[0])
        {
            case "list":
            {
                var list = Watches.Load();
                if (list.Count == 0) o.WriteLine("no watched folders - add one with: convertino watch add <folder> --to pdf");
                foreach (var w in list) o.WriteLine($"{w.Path}  →  {Watches.LabelOf(w.Action)} ({w.Action}), results in {w.Out}{(Directory.Exists(w.Path) ? "" : "   [missing]")}");
                return 0;
            }
            case "remove":
            {
                if (a.Length < 2) { o.WriteLine(WatchUsage); return 2; }
                bool removed = Watches.Remove(a[1]);
                o.WriteLine(removed ? $"stopped watching {Path.GetFullPath(a[1])}" : $"not watched: {a[1]}");
                return removed ? 0 : 2;
            }
            case "add":
            {
                var wf = ParseWatch(a.Skip(1).ToArray(), o);
                if (wf == null) return 2;
                Watches.Add(wf);
                o.WriteLine($"watching {wf.Path} → {Watches.LabelOf(wf.Action)}; results go to {Path.Combine(wf.Path, wf.Out)}");
                o.WriteLine("the desktop app picks this up right away (start it if it isn't running)");
                return 0;
            }
            default:
            {
                var wf = ParseWatch(a, o);
                if (wf == null) return 2;
                o.WriteLine($"watching {wf.Path} → {Watches.LabelOf(wf.Action)}; results go to {Path.Combine(wf.Path, wf.Out)}. Ctrl+C stops.");
                var stop = new TaskCompletionSource();
                ConsoleCancelEventHandler h = (_, e) => { e.Cancel = true; stop.TrySetResult(); };
                Console.CancelKeyPress += h;
                using (new Watcher(wf, job =>
                       {
                           lock (o) foreach (var f in job.Outputs) o.WriteLine($"{DateTime.Now:HH:mm:ss}  {f}" + (job.Summary is { Length: > 0 } s ? $"   {s}" : ""));
                       }))
                    await stop.Task;
                Console.CancelKeyPress -= h;
                return 0;
            }
        }
    }

    static WatchFolder? ParseWatch(string[] a, TextWriter o)
    {
        if (a.Length < 3) { o.WriteLine(WatchUsage); return null; }
        var dir = Path.GetFullPath(a[0]);
        if (!Directory.Exists(dir)) { o.WriteLine($"not a folder: {a[0]}"); return null; }
        string? action = null, outDir = "Converted";
        for (int i = 1; i < a.Length - 1; i++)
        {
            if (a[i] == "--to") action = "to-" + a[++i].TrimStart('.').ToLowerInvariant();
            else if (a[i] == "--run") action = a[++i];
            else if (a[i] == "--out") outDir = a[++i];
        }
        if (action == null) { o.WriteLine(WatchUsage); return null; }
        // results must land in a real subfolder, or they would be picked up again and converted forever
        if (Watches.ValidOut(outDir) is not { } outName) { o.WriteLine("--out takes a plain subfolder name, like Converted"); return null; }
        return new WatchFolder { Path = dir, Action = action, Out = outName };
    }

    static List<string>? Files(IEnumerable<string> paths, TextWriter o)
    {
        var list = new List<string>();
        foreach (var raw in paths)
        {
            var p = Path.GetFullPath(raw);
            if (!File.Exists(p) && !Directory.Exists(p)) { o.WriteLine($"not found: {raw}"); return null; }
            list.Add(p);
        }
        if (list.Count == 0) { o.WriteLine("no files given"); return null; }
        return list;
    }

    static string Quote(string p) => p.Contains(' ') ? $"\"{p}\"" : p;

    static void Help(TextWriter o)
    {
        o.WriteLine($"""
            Convertino {Version} — drag a file, tap Shift, pick a petal.

            Desktop:
              convertino                       start in the tray (drag any file + tap Shift)
              convertino menu <files…>         open the flower for these files (file-manager integration)

            Command line:
              convertino convert <files…> --to <format>    e.g. convertino convert IMG_1234.HEIC --to jpg
              convertino run <action> <files…>             e.g. convertino run compress *.png
              convertino actions <files…>                  list what's possible for these files
              convertino watch <folder> --to <format>      convert every new file in a folder (also: watch add|list|remove)
              convertino model install                     download the offline AI model for Cut out (224 MB)
              convertino --version
            """);
    }
}
