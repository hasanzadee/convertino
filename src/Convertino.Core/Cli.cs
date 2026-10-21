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
        args.Length > 0 && args[0] is "convert" or "run" or "actions" or "model" or "watch" or "preview" or "recipe" or "--help" or "-h" or "help" or "--version" or "-v";

    public static async Task<int> Run(string[] args, TextWriter? output = null)
    {
        var o = output ?? Console.Out;
        try
        {
            // --out <place> for convert, run and recipe run: where results go (default: next to the original)
            string? outPlace = null;
            if (args[0] is "convert" or "run" || args is ["recipe", "run", ..])
            {
                int at = Array.IndexOf(args, "--out");
                if (at > 0)
                {
                    if (at == args.Length - 1 || Output.Valid(args[at + 1]) is not { } valid)
                    {
                        o.WriteLine("--out takes beside, a subfolder name (Converted) or a full folder path");
                        return 2;
                    }
                    outPlace = valid;
                    args = [.. args.Take(at), .. args.Skip(at + 2)];
                }
            }
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
                    if (p.Recipes.Count > 0) o.WriteLine("  recipes: " + string.Join("  ", p.Recipes.Select(a => a.Id)));
                    if (p.Tools.Count > 0) o.WriteLine("  tools:   " + string.Join("  ", p.Tools.Select(a => a.Interactive ? $"({a.Id})" : a.Id)));
                    if (p.More.Count > 0) o.WriteLine("  more:    " + string.Join("  ", p.More.Select(a => a.Interactive ? $"({a.Id})" : a.Id)));
                    if (p.MoreFormats.Count > 0) o.WriteLine("  formats: " + string.Join("  ", p.MoreFormats.Select(a => a.Id)));
                    o.WriteLine("  (ids in parentheses open an editor and need the desktop app)");
                    return 0;
                }
                case "convert":
                {
                    int to = Array.IndexOf(args, "--to");
                    if (to < 0 || to == args.Length - 1) { o.WriteLine("usage: convertino convert <files…> --to <format>"); return 2; }
                    var fmt = args[to + 1].TrimStart('.').ToLowerInvariant();
                    var rest = args.Skip(1).Where((_, i) => i + 1 != to && i + 1 != to + 1);
                    return await RunAction("to-" + fmt, rest, o, outPlace);
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
                    return await RunAction(args[1], args.Skip(2), o, outPlace);
                case "watch":
                    return await Watch(args.Skip(1).ToArray(), o);
                case "preview":
                    return await PreviewCmd(args.Skip(1).ToArray(), o);
                case "recipe":
                    return await Recipe(args.Skip(1).ToArray(), o, outPlace);
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

    static async Task<int> RunAction(string id, IEnumerable<string> paths, TextWriter o, string? outPlace = null)
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
        return await RunJob(action, files, o, outPlace);
    }

    /// <summary>Results stay next to the original unless --out (or the recipe itself) says otherwise: the panel's "Save results" is for drops.</summary>
    static async Task<int> RunJob(PetalAction action, List<string> files, TextWriter o, string? outPlace)
    {
        var job = await Jobs.RunHeadless(action, files, place: Output.For(action, Output.Beside, outPlace));
        Output.Place(job); // what a run that failed half-way did make goes there too
        foreach (var f in job.Outputs) o.WriteLine(f);
        if (!string.IsNullOrEmpty(job.Title)) o.WriteLine(job.Title);
        if (!string.IsNullOrEmpty(job.Summary)) o.WriteLine(job.Summary);
        return job.Failed ? 1 : 0;
    }

    /// <summary>
    /// What the flower works out while it's open: quick look + estimate, then the exact size, per petal.
    /// With --take &lt;id&gt; it also "drops" on that petal: the finished run is moved beside the source, as on a real drop.
    /// </summary>
    static async Task<int> PreviewCmd(string[] a, TextWriter o)
    {
        int ti = Array.IndexOf(a, "--take");
        string? take = ti >= 0 && ti + 1 < a.Length ? a[ti + 1] : null;
        bool full = a.Contains("--full") || take != null;
        var files = Files(a.Where((x, i) => x is not ("--full" or "--verbose") && (ti < 0 || (i != ti && i != ti + 1))), o);
        if (files == null) return 2;
        Settings.Current.LivePreview = true;
        if (a.Contains("--verbose")) Log.Echo = true;
        var pv = Preview.For(files);
        if (pv == null) { o.WriteLine("no live preview for these files (pictures only, all in one folder)"); return 2; }
        try { return await PreviewRun(pv, files, full, take, o); }
        finally
        {
            pv.Dispose();
            await Task.WhenAny(pv.Closed, Task.Delay(10_000)); // leave no sandbox behind in the temp folder
        }
    }

    static async Task<int> PreviewRun(Preview pv, List<string> files, bool full, string? take, TextWriter o)
    {
        var all = Catalog.All(files).ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await pv.Settle(all, full);
        o.WriteLine($"{files.Count} file(s), {Paths.HumanSize(pv.InputBytes)}, worked out in {sw.ElapsedMilliseconds} ms");
        foreach (var act in all)
        {
            var l = pv.Get(act);
            if (l == null) continue;
            o.WriteLine($"  {act.Id,-12} {(l.Bgra != null ? $"look {l.W}x{l.H}" : "no look"),-14} {pv.SizeLine(l) ?? "-"}");
        }
        if (take == null) return 0;
        var action = all.FirstOrDefault(x => x.Id == take);
        var handoff = action == null ? null : pv.Take(action);
        if (action == null || handoff == null) { o.WriteLine($"nothing to take for '{take}'"); return 2; }
        var job = new Job { Inputs = files, Action = action };
        if (!await handoff.Into(job)) { o.WriteLine("handoff refused"); return 1; }
        foreach (var f in job.Outputs) o.WriteLine(f);
        if (!string.IsNullOrEmpty(job.Summary)) o.WriteLine(job.Summary);
        return 0;
    }
    const string RecipeUsage = """
        usage: convertino recipe list
               convertino recipe add <name> --steps <a,b,c> [--for <jpg,png|image>] [--out <beside|subfolder|full path>] [--icon <key>]
               convertino recipe remove <name>
               convertino recipe run <name> <files…> [--out <beside|subfolder|full path>]
        steps are petal ids from "convertino actions", e.g. fithd,to-webp,meta
        """;

    static async Task<int> Recipe(string[] a, TextWriter o, string? outPlace)
    {
        if (a.Length == 0) { o.WriteLine(RecipeUsage); return 2; }
        switch (a[0])
        {
            case "list":
            {
                var book = Recipes.Load();
                if (Recipes.Broken) o.WriteLine($"{Recipes.FilePath} can't be read - fix it, or the next change starts it afresh (the old one is kept as .bad-…)");
                if (book.Recipes.Count == 0 && book.Commands.Count == 0)
                    o.WriteLine("no recipes yet - add one with: convertino recipe add Web-ready --steps fithd,to-webp,meta");
                foreach (var r in book.Recipes)
                {
                    var (steps, problem) = Recipes.Describe(r);
                    o.WriteLine($"{r.Name}  ({Recipes.IdOf(r)})  →  {string.Join(" → ", steps)}   for {Recipes.ForText(r.For)}"
                        + (r.Out != null ? $", results {Output.Describe(r.Out)}" : ""));
                    if (problem != null) o.WriteLine($"    can't run: {problem}");
                }
                foreach (var c in book.Commands)
                    o.WriteLine($"{c.Name}  ({Recipes.IdOf(c)})  runs {c.Program} {string.Join(' ', c.Args.Select(Quote))}   for {Recipes.ForText(c.For)}"
                        + (Services.Commands.Resolve(c.Program, out var why) == null ? $"   [{why}]" : ""));
                return 0;
            }
            case "add":
            {
                if (a.Length < 2 || a[1].StartsWith("--")) { o.WriteLine(RecipeUsage); return 2; }
                var r = new Recipe { Name = a[1] };
                for (int i = 2; i < a.Length; i++)
                {
                    string? Next() => i + 1 < a.Length ? a[++i] : null;
                    switch (a[i])
                    {
                        case "--steps": r.Steps = List(Next()); break;
                        case "--for": r.For = List(Next()); break;
                        case "--out": r.Out = Next(); break;
                        case "--icon": r.Icon = Next(); break;
                        default: o.WriteLine($"unknown option {a[i]}"); o.WriteLine(RecipeUsage); return 2;
                    }
                }
                try { r = Recipes.Save(r); }
                catch (ArgumentException ex) { o.WriteLine($"can't save {r.Name}: {ex.Message}"); return 2; }
                o.WriteLine($"saved {r.Name} ({Recipes.IdOf(r)}): {string.Join(" → ", Recipes.Describe(r).Steps)}, for {Recipes.ForText(r.For)}"
                    + $", results {Output.Describe(r.Out ?? Output.Global)}");
                o.WriteLine("it's on the Recipes ring of the flower now (one flip from Convert)");
                return 0;
            }
            case "remove":
            {
                if (a.Length < 2) { o.WriteLine(RecipeUsage); return 2; }
                bool removed = Recipes.Remove(a[1]);
                o.WriteLine(removed ? $"removed {a[1]}" : $"no recipe called {a[1]}");
                return removed ? 0 : 2;
            }
            case "run":
            {
                if (a.Length < 3) { o.WriteLine(RecipeUsage); return 2; }
                if (Recipes.Find(a[1]) is not { } r) { o.WriteLine($"no recipe called {a[1]} - see: convertino recipe list"); return 2; }
                var files = Files(a.Skip(2), o);
                if (files == null) return 2;
                if (Recipes.PetalFor(r, files, out var problem) is not { } action) { o.WriteLine($"can't run {r.Name} here: {problem}"); return 2; }
                return await RunJob(action, files, o, outPlace);
            }
        }
        o.WriteLine(RecipeUsage);
        return 2;
    }

    static List<string> List(string? csv) => (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

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
              convertino preview <images…> [--full]        what each petal would give: estimated, then exact sizes
                                  [--take <petal>]             … and "drop" on one: its finished run moved beside the image
              convertino recipe add <name> --steps a,b,c   chain petals into one, e.g. --steps fithd,to-webp,meta
                                                           (also: recipe list|remove|run; custom commands: recipes.json)
              … --out <subfolder|full path>                convert, run, recipe run: put results there (default: next to the original)
              convertino model install                     download the offline AI model for Cut out (224 MB)
              convertino --version
            """);
    }
}
