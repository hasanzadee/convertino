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
        args.Length > 0 && args[0] is "convert" or "run" or "actions" or "model" or "--help" or "-h" or "help" or "--version" or "-v";

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
                    var shown = p.Convert.Concat(p.Tools).Select(a => a.Id).ToHashSet();
                    var more = Catalog.All(files).Select(a => a.Id).Where(id => !shown.Contains(id)).ToList();
                    if (more.Count > 0) o.WriteLine("  more:    " + string.Join("  ", more));
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
              convertino model install                     download the offline AI model for Cut out (224 MB)
              convertino --version
            """);
    }
}
