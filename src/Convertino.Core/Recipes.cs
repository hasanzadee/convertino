using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Convertino.Services;

namespace Convertino;

/// <summary>A chain of petals that shows up as one petal, e.g. "Web-ready" = Full HD → WEBP → Clean.</summary>
public sealed class Recipe
{
    public string Name { get; set; } = "";
    /// <summary>Icon key, as the petals use ("recipe", "globe", "send" …).</summary>
    public string? Icon { get; set; }
    /// <summary>Petal ids as in <c>convertino actions</c> ("fithd", "to-webp", "meta", "cmd-optipng"), in order: each step takes the results of the one before.</summary>
    public List<string> Steps { get; set; } = [];
    /// <summary>Extensions ("jpg") or kinds ("image", "pdf") it's offered for; worked out from the steps when a recipe is saved without it.</summary>
    public List<string> For { get; set; } = [];
    /// <summary>Where its results go instead of the "Save results" setting: "beside", a subfolder name, or a full folder path.</summary>
    public string? Out { get; set; }
}

/// <summary>One of your own programs as a petal, run once per file and never through a shell.</summary>
public sealed class CustomCommand
{
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
    /// <summary>Extensions or kinds it's offered for; empty = any file.</summary>
    public List<string> For { get; set; } = [];
    /// <summary>A name looked up on PATH ("ffmpeg") or a full path.</summary>
    public string Program { get; set; } = "";
    /// <summary>One entry per argument, passed on as it is, with {in} {name} {ext} {dir} {out} filled in.</summary>
    public List<string> Args { get; set; } = [];
    /// <summary>The file it makes, e.g. "{name}.min.{ext}", relative to the file's folder; {out} names it (with " (2)" if taken). None: it makes no file.</summary>
    public string? Output { get; set; }
    /// <summary>Seconds before it's stopped.</summary>
    public int Timeout { get; set; } = 300;
}

/// <summary>What recipes.json holds.</summary>
public sealed class RecipeFile
{
    public List<Recipe> Recipes { get; set; } = [];
    public List<CustomCommand> Commands { get; set; } = [];
}

/// <summary>
/// Recipes and custom commands live in recipes.json beside the settings, edited by the panel, the CLI or by hand (it can
/// run programs, so it's only ever read from there). They show up together on a ring of their own after Convert.
/// </summary>
public static class Recipes
{
    public const int MaxSteps = 12;
    /// <summary>Off: no recipe ring at all (the paging self-test counts the built-in rings).</summary>
    public static bool Enabled { get; set; } = true;
    public static string FilePath => Path.Combine(Paths.AppData, "recipes.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // names and paths stay readable for hand edits
    };

    /// <summary>Icons the panel offers for a recipe (all of them exist in every UI).</summary>
    public static readonly string[] IconChoices = ["recipe", "globe", "send", "squareimg", "compress", "picture", "volume", "zip"];

    // ---------- storage ----------

    static readonly object CacheGate = new();
    static RecipeFile? _cache;
    static (DateTime, long) _stamp;

    /// <summary>recipes.json exists but can't be read: nothing from it is offered, and the next save keeps it as .bad-…</summary>
    public static bool Broken { get; private set; }

    /// <summary>
    /// The file, or null when recipes.json isn't valid JSON (so nobody overwrites it by accident). When it can't be read
    /// right now (a sync tool or an editor holds it, no access) this throws instead: that isn't a broken file.
    /// </summary>
    static RecipeFile? TryLoad()
    {
        if (!File.Exists(FilePath)) return new();
        var text = File.ReadAllText(FilePath);
        try
        {
            var file = JsonSerializer.Deserialize<RecipeFile>(text, Json) ?? new();
            file.Recipes = (file.Recipes ?? []).Where(r => r != null && !string.IsNullOrWhiteSpace(r.Name)).ToList();
            foreach (var r in file.Recipes)
            {
                r.Name = r.Name.Trim();
                r.Steps = (r.Steps ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
                r.For = (r.For ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            }
            file.Commands = (file.Commands ?? []).Where(c => c != null && !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.Program)).ToList();
            foreach (var c in file.Commands)
            {
                c.Name = c.Name.Trim();
                c.Args = (c.Args ?? []).Select(a => a ?? "").ToList();
                c.For = (c.For ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            }
            return file;
        }
        catch (JsonException ex) { Log.Error("recipes load", ex); return null; }
    }

    static (DateTime, long) Stamp()
    {
        try
        {
            var fi = new FileInfo(FilePath);
            return fi.Exists ? (fi.LastWriteTimeUtc, fi.Length) : (default, -1);
        }
        catch { return (default, -2); }
    }

    /// <summary>What recipes.json holds now; read again only when it changed. Treat it as read-only (edits go through <see cref="Save"/> …).</summary>
    public static RecipeFile Load()
    {
        lock (CacheGate)
        {
            var stamp = Stamp();
            if (_cache != null && stamp == _stamp) return _cache;
            RecipeFile? file;
            try { file = TryLoad(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // held by something else for a moment: keep what we had, read it again next time
                Log.Error("recipes read", ex);
                return _cache ?? new();
            }
            Broken = file == null;
            _cache = file ?? new();
            _stamp = stamp;
            return _cache;
        }
    }

    // the CLI and the app can both edit the file: one at a time, each with its own temp file
    static T Edit<T>(Func<RecipeFile, T> change)
    {
        using var gate = Lock();
        RecipeFile? file;
        for (int attempt = 1; ; attempt++)
        {
            // held by something else for a moment: try again, and in the end fail rather than take it for broken
            try { file = TryLoad(); break; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 5) { Thread.Sleep(250); }
        }
        if (file == null)
        {
            // keep the unreadable file for the user instead of silently replacing their recipes
            var bad = FilePath + $".bad-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(FilePath, bad, true);
            Log.Info($"recipes: unreadable recipes.json kept as {bad}");
            file = new();
        }
        var result = change(file);
        var tmp = $"{FilePath}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json));
        File.Move(tmp, FilePath, true);
        lock (CacheGate) _cache = null;
        return result;
    }

    /// <summary>
    /// recipes.json.lock, held open exclusively while the file is edited. A lock file and not a named mutex: on Linux
    /// and macOS those are per login session, so the CLI in a terminal and the app wouldn't see each other's.
    /// </summary>
    static FileStream? Lock()
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try { return new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < until) { Thread.Sleep(50); }
            catch (Exception ex) { Log.Error("recipes lock", ex); return null; } // go on without it rather than not at all
        }
    }

    /// <summary>Create recipes.json (empty lists) if it isn't there, for "Open recipes.json".</summary>
    public static string EnsureFile()
    {
        if (!File.Exists(FilePath)) Edit(_ => 0);
        return FilePath;
    }

    /// <summary>
    /// Save a recipe, replacing one with the same name, once it's checked: every step has to take what the step before it
    /// makes. Without <see cref="Recipe.For"/> it's filled in with the kinds of file the whole chain works on.
    /// Throws <see cref="ArgumentException"/> with the reason when it can't run.
    /// </summary>
    public static Recipe Save(Recipe r)
    {
        r.Name = r.Name.Trim();
        r.Steps = r.Steps.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (string.IsNullOrWhiteSpace(r.Icon)) r.Icon = null;
        return Edit(file =>
        {
            if (Check(r, file) is { } problem) throw new ArgumentException(problem);
            int at = file.Recipes.FindIndex(x => IdOf(x) == IdOf(r));
            if (at >= 0) file.Recipes[at] = r; else file.Recipes.Add(r);
            return r;
        });
    }

    public static bool Remove(string name) => Edit(file => file.Recipes.RemoveAll(r => IdOf(r) == IdOf(name)) > 0);

    public static bool RemoveCommand(string name) => Edit(file => file.Commands.RemoveAll(c => IdOf(c) == "cmd-" + Slug(name) || IdOf(c) == name) > 0);

    /// <summary>A recipe by its name or id.</summary>
    public static Recipe? Find(string nameOrId) => Load().Recipes.FirstOrDefault(r => IdOf(r) == IdOf(nameOrId));

    public static string IdOf(Recipe r) => "recipe-" + Slug(r.Name);
    public static string IdOf(CustomCommand c) => "cmd-" + Slug(c.Name);
    public static string IdOf(string nameOrId) => nameOrId.StartsWith("recipe-", StringComparison.Ordinal) ? nameOrId : "recipe-" + Slug(nameOrId);

    /// <summary>A recipe's petal: it puts its results in place itself.</summary>
    public static bool IsRecipe(PetalAction a) => a.Id.StartsWith("recipe-", StringComparison.Ordinal);

    static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.Trim().ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        var s = Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
        return s.Length == 0 ? "x" : s;
    }

    // ---------- templates ----------

    /// <summary>One-click recipes for the panel.</summary>
    public static IEnumerable<Recipe> Templates()
    {
        yield return new() { Name = "Web-ready", Icon = "globe", Steps = ["fithd", "to-webp", "meta"], For = ["image"] };
        // with Ghostscript the PDF itself is squeezed; without it the pictures are, before they go in
        yield return new() { Name = "Email PDF", Icon = "send", Steps = Pdfs.CanCompress ? ["to-pdf", "compress"] : ["compress", "to-pdf"], For = ["image"] };
        yield return new() { Name = "Insta square", Icon = "squareimg", Steps = ["square", "to-jpg"], For = ["image"] };
        yield return new() { Name = "Podcast", Icon = "volume", Steps = ["normalize", "to-mp3"], For = ["audio"] };
    }

    /// <summary>Templates not added yet whose steps all work on this computer (Podcast needs ffmpeg).</summary>
    public static List<Recipe> Offered()
    {
        var file = Load();
        return Templates().Where(t => file.Recipes.All(r => IdOf(r) != IdOf(t)) && Check(t, file) == null).ToList();
    }

    // ---------- what a chain of petals makes ----------

    // a type is an extension ("jpg"), a folder, nothing (the step only copies something), or null: can't be told ahead
    const string FolderType = "/", NoFile = "";

    /// <summary>Kinds a recipe can be for, each with stand-in extensions: the catalog says which petals a type gets without a real file.</summary>
    static readonly (Kind Kind, string Name, string[] Types)[] Samples =
    [
        (Kind.Image, "images", ["jpg", "png"]), (Kind.Pdf, "PDFs", ["pdf"]), (Kind.Video, "videos", ["mp4", "mov"]),
        (Kind.Audio, "audio", ["mp3", "wav"]), (Kind.Word, "documents", ["docx", "rtf"]), (Kind.Excel, "spreadsheets", ["xlsx", "csv"]),
        (Kind.Slides, "slides", ["pptx"]), (Kind.Text, "text", ["txt"]), (Kind.Markdown, "Markdown", ["md"]), (Kind.Data, "data", ["json", "yaml"]),
        (Kind.Subtitles, "subtitles", ["srt", "vtt"]), (Kind.Archive, "archives", ["zip"]), (Kind.Folder, "folders", [FolderType]),
    ];

    static readonly Dictionary<string, Kind> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["photo"] = Kind.Image, ["picture"] = Kind.Image, ["doc"] = Kind.Word, ["document"] = Kind.Word,
        ["sheet"] = Kind.Excel, ["spreadsheet"] = Kind.Excel, ["presentation"] = Kind.Slides, ["subtitle"] = Kind.Subtitles,
    };

    /// <summary>"image", "images", "photos" → Kind.Image; null when it's an extension.</summary>
    static Kind? KindNamed(string s)
    {
        s = s.Trim().TrimStart('.').ToLowerInvariant();
        if (s.Length == 0 || Kinds.Of("x." + s) != Kind.Other) return null;
        foreach (var name in new[] { s, s.EndsWith('s') ? s[..^1] : s })
        {
            if (Aliases.TryGetValue(name, out var a)) return a;
            if (!name.All(char.IsLetter)) continue;
            if (Enum.TryParse<Kind>(name, true, out var k) && Enum.IsDefined(k) && k != Kind.Other) return k;
        }
        return null;
    }

    static string Canon(string ext) => ext switch
    {
        "jpeg" or "jfif" => "jpg", "tif" => "tiff", "heif" => "heic", "yml" => "yaml", "htm" => "html", "markdown" => "md", _ => ext,
    };

    static string TypeOf(string path) => Directory.Exists(path) ? FolderType : Kinds.Ext(path);

    /// <summary>"to-jpg" on a JPG: nothing to do, the step is passed over.</summary>
    static bool IsFormat(string id, string? type) => type is { Length: > 0 } && id.StartsWith("to-") && Canon(id[3..]) == Canon(type);

    /// <summary>The types an entry of <see cref="Recipe.For"/> stands for.</summary>
    static string[] TypesOf(string entry)
    {
        if (KindNamed(entry) is { } k) return Samples.FirstOrDefault(s => s.Kind == k).Types ?? [];
        var e = entry.Trim().TrimStart('.').ToLowerInvariant();
        return e.Length == 0 || e.Any(c => c is '/' or '\\' or ' ') ? [] : [e];
    }

    /// <summary>Does every file fit one of these extensions or kinds? An empty list takes any file, but folders only when "folder" is listed.</summary>
    public static bool Matches(IReadOnlyList<string> spec, IReadOnlyList<string> files) =>
        files.Count > 0 && files.All(f => spec.Count == 0 ? !Directory.Exists(f) : spec.Any(s => Matches(s, f)));

    static bool Matches(string entry, string path)
    {
        var kind = Kinds.Of(path);
        if (KindNamed(entry) is { } k) return kind == k;
        var e = Canon(entry.Trim().TrimStart('.').ToLowerInvariant());
        return kind != Kind.Folder && Canon(Kinds.Ext(path)) == e;
    }

    /// <summary>"images", "JPG, PNG", "any file" — what a recipe or command is for, in words (in the UI language).</summary>
    public static string ForText(IReadOnlyList<string> spec) =>
        spec.Count == 0 ? L.T("any file") : string.Join(", ", spec.Select(s => KindNamed(s) is { } k ? L.T(Samples.FirstOrDefault(x => x.Kind == k).Name ?? k.ToString()) : s.Trim().TrimStart('.').ToUpperInvariant()));

    /// <summary>"doesn't work on folders / PNG files / that" (a whole phrase per type: languages put the type in different cases).</summary>
    static string NotOn(string? type, bool sentence = false) => type switch
    {
        FolderType => sentence ? L.T("It doesn't work on folders") : L.T("doesn't work on folders"),
        null or NoFile => sentence ? L.T("It doesn't work on that") : L.T("doesn't work on that"),
        _ => sentence ? L.T("It doesn't work on {0} files", type.ToUpperInvariant()) : L.T("doesn't work on {0} files", type.ToUpperInvariant()),
    };

    /// <summary>A stand-in path of this type.</summary>
    static string Probe(string type)
    {
        var dir = Path.Combine(Paths.Temp, "probe");
        if (type != FolderType) return Path.Combine(dir, "sample." + type);
        var d = Path.Combine(dir, "folder");
        Directory.CreateDirectory(d);
        return d;
    }

    // petals whose result has the type of what went in
    static readonly HashSet<string> Same = ["half", "fithd", "gray", "meta", "clean", "pagenum", "rotate", "mute", "normalize", "trim", "pretty", "minify", "utf8", "lf", "crlf"];
    // petals that always make one type ("/" a folder, "" nothing but the clipboard)
    static readonly Dictionary<string, string> Fixed = new()
    {
        ["messenger"] = "jpg", ["rounded"] = "png", ["palette"] = "png", ["cutout"] = "png", ["cover"] = "png", ["frame"] = "png", ["qr"] = "png",
        ["subtitles"] = "srt", ["zip"] = "zip", ["merge"] = "pdf", ["vcompress"] = "mp4", ["720p"] = "mp4",
        ["extract"] = FolderType, ["pdfimages"] = FolderType, ["split"] = FolderType, ["favicon"] = FolderType,
        ["ocr"] = NoFile, ["readqr"] = NoFile, ["md5"] = NoFile, ["sha1"] = NoFile, ["sha512"] = NoFile, ["verify"] = NoFile,
    };

    /// <summary>What a step makes from a file of this type; null when that can't be told ahead (it has to be the last step).</summary>
    static string? Makes(PetalAction a, string type, RecipeFile file)
    {
        var id = a.Id;
        if (a.Quick) return NoFile;
        if (id.StartsWith("cmd-")) return file.Commands.FirstOrDefault(c => IdOf(c) == id) is { } c ? Commands.Makes(c, type) : null;
        if (id.StartsWith("to-"))
        {
            // a document's pages: one picture or a folder of them, depending on how many it has
            bool pages = Kinds.Of("x." + type) is Kind.Pdf or Kind.Word or Kind.Slides && Kinds.Of("x." + id[3..]) == Kind.Image;
            return pages ? null : id[3..];
        }
        if (Same.Contains(id)) return type;
        if (Fixed.TryGetValue(id, out var t)) return t;
        return id switch
        {
            "compress" when type == "pdf" => "pdf",
            "compress" => Canon(type) switch { "jpg" => "jpg", "png" or "webp" or "avif" or "gif" or "heic" => Canon(type), _ => "jpg" },
            "square" => type == "png" ? "png" : "jpg",
            _ => null,
        };
    }

    /// <summary>Built-in petals and your commands for these files: what steps are made of (never recipes, so one can't call itself).</summary>
    static List<PetalAction> StepsFor(IReadOnlyList<string> files, RecipeFile file) =>
        [.. Catalog.Builtin(files), .. Commands.For(files, file)];

    // English labels: the cache outlives a change of the UI language
    static readonly Dictionary<string, (string Label, string? Icon)> Known = [];

    /// <summary>A petal's English name without files at hand ("fithd" → "Full HD"), or null if no such petal exists.</summary>
    static (string Label, string? Icon)? Lookup(string id, RecipeFile file)
    {
        if (id.StartsWith("cmd-")) return file.Commands.FirstOrDefault(c => IdOf(c) == id) is { } c ? (c.Name, c.Icon ?? "terminal") : null;
        lock (Known)
        {
            if (Known.Count == 0)
                foreach (var (_, _, types) in Samples)
                    foreach (var t in types)
                        foreach (var a in Catalog.Builtin([Probe(t)]))
                            Known.TryAdd(a.Id, (a.EnglishLabel, a.Icon));
            if (Known.TryGetValue(id, out var k)) return k;
        }
        return id.StartsWith("to-") && id.Length > 3 ? (id[3..].ToUpperInvariant(), null) : null;
    }

    static string LabelOf(string id, RecipeFile file) => Lookup(id, file)?.Label is { } label ? L.T(label) : id;

    /// <summary>One step as it would run on a type: its petal (null when it's passed over or can't run) and what's wrong.</summary>
    public sealed record Step(string Id, string Label, PetalAction? Action, string? Problem);

    /// <summary>Follow the steps from these files (real ones or a stand-in): each step's petal, up to the first one that can't run; and the type the last one makes.</summary>
    static (List<Step> Steps, string? Type) Walk(IReadOnlyList<string> ids, IReadOnlyList<string> files, RecipeFile file, List<PetalAction>? first = null)
    {
        var steps = new List<Step>();
        string? type = TypeOf(files[0]);
        IReadOnlyList<string> current = files;
        string before = "";
        for (int i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            if (type is null or NoFile)
            {
                steps.Add(new(id, LabelOf(id, file), null, type == NoFile
                    ? L.T("can't follow {0}: it only copies to the clipboard, so it has to be the last step", before)
                    : L.T("can't follow {0}: what it makes can't be told ahead, so it has to be the last step", before)));
                return (steps, null);
            }
            var a = (i == 0 && first != null ? first : StepsFor(current, file)).FirstOrDefault(x => x.Id == id);
            if (a == null && IsFormat(id, type))
            {
                steps.Add(new(id, LabelOf(id, file), null, null)); // already that format: passed over
                before = LabelOf(id, file);
                continue;
            }
            if (a == null || a.Interactive)
            {
                steps.Add(new(id, a?.Label ?? LabelOf(id, file), null,
                    a != null ? L.T("opens an editor, so it can't be part of a recipe")
                    : Lookup(id, file) == null ? L.T("isn't a petal ('{0}'; see convertino actions)", id)
                    : NotOn(type)));
                return (steps, null);
            }
            steps.Add(new(id, a.Label, a, null));
            before = a.Label;
            type = Makes(a, type, file);
            if (type is { Length: > 0 }) current = [Probe(type)];
        }
        return (steps, type);
    }

    static string? FirstProblem(List<Step> walk) => FirstProblem(walk, out _);

    /// <summary>"Step 2 (Full HD) doesn't work on PDF files"; <paramref name="at"/> is that step's index.</summary>
    static string? FirstProblem(List<Step> walk, out int at) =>
        (at = walk.FindIndex(s => s.Problem != null)) >= 0 ? L.T("Step {0} ({1}) {2}", at + 1, walk[at].Label, walk[at].Problem) : null;

    /// <summary>
    /// Why this recipe can't be saved, or null. Each step has to take what the one before it makes, for every kind it's
    /// for; without <see cref="Recipe.For"/> it's filled in with the kinds the whole chain works on.
    /// </summary>
    public static string? Check(Recipe r, RecipeFile? file = null)
    {
        file ??= Load();
        if (string.IsNullOrWhiteSpace(r.Name)) return L.T("Give the recipe a name");
        if (r.Steps.Count == 0) return L.T("Add at least one step");
        if (r.Steps.Count > MaxSteps) return L.T("A recipe has at most {0} steps", MaxSteps);
        if (r.Out != null && Output.Valid(r.Out) == null) return L.T("Results go \"beside\" the original, into a subfolder (a plain name) or into a full folder path");
        string? Try(string[] types, out int at)
        {
            string? problem = null;
            at = -1;
            foreach (var t in types)
                if ((problem = FirstProblem(Walk(r.Steps, [Probe(t)], file).Steps, out at)) == null) return null;
            return problem;
        }
        if (r.For.Count > 0)
        {
            foreach (var entry in r.For)
            {
                var types = TypesOf(entry);
                if (types.Length == 0) return L.T("'{0}' isn't a file type or kind", entry);
                if (Try(types, out _) is { } p) return r.For.Count > 1 ? $"{p} ({ForText([entry])})" : p;
            }
            return null;
        }
        // a recipe that starts with one of your commands is for what that command is for
        if (file.Commands.FirstOrDefault(c => IdOf(c) == r.Steps[0]) is { For.Count: > 0 } command)
        {
            r.For = [.. command.For];
            return Check(r, file);
        }
        var kinds = new List<string>();
        string? why = null;
        foreach (var (kind, _, types) in Samples)
        {
            var p = Try(types, out int at);
            if (p == null) kinds.Add(kind.ToString().ToLowerInvariant());
            else if (why == null && at > 0) why = p; // the first step worked there: the most telling problem
        }
        if (kinds.Count == 0)
            return why ?? (Lookup(r.Steps[0], file) == null
                ? L.T("Step 1 ('{0}') isn't a petal (see convertino actions)", r.Steps[0])
                : L.T("Step 1 ({0}) doesn't work on any kind of file", LabelOf(r.Steps[0], file)));
        r.For = kinds;
        return null;
    }

    /// <summary>For the panels and <c>recipe list</c>: the steps' names, and what's wrong with it (null if nothing).</summary>
    public static (IReadOnlyList<string> Steps, string? Problem) Describe(Recipe r)
    {
        var file = Load();
        var type = r.For.Select(TypesOf).FirstOrDefault(t => t.Length > 0)?[0];
        var labels = type != null ? Walk(r.Steps, [Probe(type)], file).Steps.Select(s => s.Label).ToList() : [];
        labels.AddRange(r.Steps.Skip(labels.Count).Select(id => LabelOf(id, file)));
        var copy = new Recipe { Name = r.Name, Steps = r.Steps, For = [.. r.For], Out = r.Out };
        return (labels, Check(copy, file));
    }

    /// <summary>The kind of file a recipe is first of all for (where the builder starts when it's changed).</summary>
    public static Kind KindOf(Recipe r)
    {
        foreach (var e in r.For)
        {
            if (KindNamed(e) is { } k) return k;
            if (Kinds.Of("x." + e.Trim().TrimStart('.')) is var kind and not Kind.Other) return kind;
        }
        return Kind.Image;
    }

    /// <summary>Kinds the builder offers: those with at least one petal that can run in a recipe.</summary>
    public static List<(Kind Kind, string Name)> BuilderKinds()
    {
        var file = Load();
        return Samples.Where(s => s.Kind != Kind.Folder && s.Types.Any(t => StepsFor([Probe(t)], file).Any(a => !a.Interactive)))
            .Select(s => (s.Kind, L.T(s.Name))).ToList();
    }

    /// <summary>For the builder: the petals that can come after these steps, for files of this kind (none after a clipboard step).</summary>
    public static List<PetalAction> NextSteps(Kind kind, IReadOnlyList<string> steps)
    {
        var file = Load();
        var result = new List<PetalAction>();
        foreach (var t in Samples.FirstOrDefault(s => s.Kind == kind).Types ?? [])
        {
            var (walk, type) = Walk(steps, [Probe(t)], file);
            if (walk.Any(s => s.Problem != null) || type is null or NoFile) continue;
            foreach (var a in StepsFor([Probe(type)], file))
                if (!a.Interactive && result.All(x => x.Id != a.Id)) result.Add(a);
            if (steps.Count > 0) break; // the stand-ins differ only for the first step (a JPG has no "→ JPG")
        }
        return result;
    }

    /// <summary>The labels of these steps, as the builder shows them.</summary>
    public static List<string> Labels(IReadOnlyList<string> steps)
    {
        var file = Load();
        return steps.Select(id => LabelOf(id, file)).ToList();
    }

    // ---------- the recipe ring ----------

    /// <summary>The recipe ring for these files: recipes whose every step works on them, then the commands that take them.</summary>
    public static List<PetalAction> Petals(IReadOnlyList<string> files, Pages builtin)
    {
        var list = new List<PetalAction>();
        try
        {
            var file = Load();
            if (!Enabled || file.Recipes.Count == 0 && file.Commands.Count == 0) return list;
            var commands = Commands.For(files, file);
            List<PetalAction> first = [.. builtin.Convert, .. builtin.Tools, .. builtin.More, .. builtin.MoreFormats, .. commands];
            var ids = new HashSet<string>();
            foreach (var r in file.Recipes)
            {
                if (!ids.Add(IdOf(r)) || !Matches(r.For, files)) continue;
                var (walk, _) = Walk(r.Steps, files, file, first);
                if (walk.All(s => s.Problem == null)) list.Add(Petal(r, walk));
            }
            foreach (var c in commands)
                if (ids.Add(c.Id)) list.Add(c);
        }
        catch (Exception ex) { Log.Error("recipes", ex); }
        return list;
    }

    /// <summary>The recipe as a petal for these files, or the reason it doesn't apply to them.</summary>
    public static PetalAction? PetalFor(Recipe r, IReadOnlyList<string> files, out string? problem)
    {
        var file = Load();
        problem = null;
        if (!Matches(r.For, files)) { problem = L.T("{0} is for {1}", r.Name, ForText(r.For)); return null; }
        var (walk, _) = Walk(r.Steps, files, file, [.. Catalog.Builtin(files), .. Commands.For(files, file)]);
        problem = FirstProblem(walk);
        return problem == null ? Petal(r, walk) : null;
    }

    static PetalAction Petal(Recipe r, List<Step> walk) => new()
    {
        Id = IdOf(r),
        Label = r.Name.Length > 12 ? r.Name[..11].TrimEnd() + "…" : r.Name,
        Icon = string.IsNullOrWhiteSpace(r.Icon) ? "recipe" : r.Icon,
        Verb = string.Join(" → ", walk.Select(s => s.Label)),
        Short = r.Name,
        // the user's own name: what Recent and stats.json keep, in any language
        EnglishLabel = r.Name.Length > 12 ? r.Name[..11].TrimEnd() + "…" : r.Name, EnglishVerb = r.Name, EnglishShort = r.Name,
        Out = r.Out,
        Run = job => Run(r, job),
    };

    // ---------- running ----------

    /// <summary>
    /// Run the steps in turn, each on the results of the one before. They all work on copies in a folder of their own
    /// (named like the originals' folder: some petals name results after it), so nothing half-done ever lands beside the
    /// originals; only the last step's results come out, as "photo (Web-ready).webp". The folder goes in the end, also
    /// when cancelled or a step fails.
    /// </summary>
    static async Task Run(Recipe r, Job job)
    {
        var file = Load();
        int n = r.Steps.Count;
        if (Interlocked.Exchange(ref _swept, 1) == 0) _ = Task.Run(Sweep);
        var root = Path.Combine(Paths.Temp, "recipe-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var work = Path.Combine(root, FolderName(job.Inputs[0]));
            Directory.CreateDirectory(work);
            var current = new List<string>();
            foreach (var f in job.Inputs)
            {
                job.Token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(f));
                if (Directory.Exists(f))
                {
                    var d = Paths.UniqueDir(work, name);
                    await Task.Run(() =>
                    {
                        Output.CopyDir(f, d, job.Token, skip: root); // a folder that holds the temp folder mustn't copy into itself
                        foreach (var x in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)) Writable(x);
                    }, job.Token);
                    current.Add(d);
                }
                else
                {
                    var c = Paths.Unique(work, Path.GetFileNameWithoutExtension(name), Path.GetExtension(name));
                    await Task.Run(() => { File.Copy(f, c); Writable(c); }, job.Token);
                    current.Add(c);
                }
            }

            bool finished = false, changed = false;
            for (int s = 0; s < n; s++)
            {
                job.Token.ThrowIfCancellationRequested();
                job.Report(s, n);
                Directory.SetLastWriteTimeUtc(root, DateTime.UtcNow); // still in use: Sweep leaves it alone
                var id = r.Steps[s];
                var action = StepsFor(current, file).FirstOrDefault(a => a.Id == id);
                if (action == null && current.All(c => IsFormat(id, TypeOf(c)))) continue; // already that format
                var label = action?.Label ?? LabelOf(id, file);
                if (action == null || action.Interactive)
                {
                    Stop(job, r, s, label, action == null ? NotOn(TypeOf(current[0]), sentence: true) : L.T("It opens an editor"));
                    return;
                }
                bool last = s == n - 1;
                // a last step that makes no file (the clipboard, a command that opens or uploads it) gets the results
                // where they stay, so a copied path keeps working; or the originals, when no step changed anything
                if (last && (action.Quick || Makes(action, TypeOf(current[0]), file) == NoFile))
                {
                    current = changed ? Finish(r, job, current, root) : [.. job.Inputs];
                    finished = true;
                }
                int at = s;
                var step = new Job { Inputs = current, Action = action, Where = job.Where, Token = job.Token, OnProgress = p => job.Report(at, n, p) };
                try { await action.Run(step); }
                catch (OperationCanceledException) when (job.Token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Log.Error($"recipe {r.Name}: step {s + 1} ({id})", ex);
                    Stop(job, r, s, label, Jobs.Friendly(ex));
                    return;
                }
                if (step.Failed)
                {
                    Stop(job, r, s, label, string.Join(": ", new[] { step.Title, step.Summary }.Where(x => !string.IsNullOrEmpty(x))));
                    return;
                }
                var made = step.Outputs.Where(o => File.Exists(o) || Directory.Exists(o)).ToList();
                if (last && (finished || made.Count > 0))
                {
                    job.Title = step.Title;
                    if (finished) job.Summary = step.Summary;
                }
                // a step with nothing to do ("already as small as it gets") hands its files on as they are
                if (made.Count > 0 && !finished)
                {
                    current = made;
                    changed = true;
                }
            }
            // nothing changed at all: no copy of the original under the recipe's name
            if (!finished && changed) Finish(r, job, current, root);
            else if (!finished) job.Title = L.T("{0}: nothing to change", r.Name);
            job.Report(1);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); }
            catch (Exception ex) { Log.Error($"recipe {r.Name}: cleanup", ex); }
        }
    }

    static int _swept;

    /// <summary>Work folders of recipes that never finished (the app was killed, Ctrl+C), once a day old.</summary>
    static void Sweep()
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(Paths.Temp, "recipe-*"))
                if (Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddDays(-1))
                    try { Directory.Delete(d, true); } catch { }
        }
        catch (Exception ex) { Log.Error("recipes sweep", ex); }
    }

    /// <summary>A copy of a read-only original is read-only too: the work folder couldn't be removed on Windows, nor a result that is that copy.</summary>
    static void Writable(string path)
    {
        try { new FileInfo(path).IsReadOnly = false; }
        catch (Exception ex) { Log.Error("recipe copy", ex); }
    }

    static void Stop(Job job, Recipe r, int step, string label, string why)
    {
        // results already put in place for a last step that then failed: a recipe that stopped leaves nothing behind
        // (the outputs are only ever what this run created; the originals are never among them)
        foreach (var o in job.Outputs)
        {
            try
            {
                if (File.Exists(o)) File.Delete(o);
                else if (Directory.Exists(o)) Directory.Delete(o, true);
            }
            catch (Exception ex) { Log.Error($"recipe {r.Name}: remove {o}", ex); }
        }
        job.Outputs.Clear();
        job.Failed = true;
        job.Title = L.T("{0} stopped at step {1}: {2}", r.Name, step + 1, label);
        job.Summary = why;
        Log.Info($"recipe {r.Name}: step {step + 1} ({r.Steps[step]}) failed: {L.English(why)}");
    }

    static string FolderName(string path)
    {
        var dir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        var name = dir == null ? "" : Path.GetFileName(dir);
        return string.IsNullOrEmpty(name) ? "files" : name;
    }

    static bool IsUnder(string path, string root) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>Move the results out of the work folder to where the job's results go (beside the originals they came from, by default); they become its outputs.</summary>
    static List<string> Finish(Recipe r, Job job, List<string> current, string root)
    {
        var done = new List<string>();
        foreach (var c in current)
        {
            var moved = c; // a command told to write somewhere else on purpose keeps its place
            if (IsUnder(c, root))
            {
                var (dir, name) = FinalPlace(r, job.Inputs, c);
                moved = Output.MoveInto(c, Output.DirFor(job.Place, dir), name);
            }
            job.Outputs.Add(moved);
            done.Add(moved);
        }
        return done;
    }

    /// <summary>
    /// "photo (1920x1080) (clean).webp" made from photo.jpg → "photo (Web-ready).webp" beside photo.jpg: the original's
    /// name, what the steps added to it minus their "(…)" notes, and the recipe's name.
    /// </summary>
    static (string Dir, string Name) FinalPlace(Recipe r, IReadOnlyList<string> inputs, string path)
    {
        bool folder = Directory.Exists(path);
        var file = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        var ext = folder ? "" : Path.GetExtension(file);
        var stem = folder ? file : Path.GetFileNameWithoutExtension(file);
        var cmp = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        string origin = inputs[0];
        int best = -1;
        foreach (var i in inputs)
        {
            var s = Directory.Exists(i) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(i)) : Path.GetFileNameWithoutExtension(i);
            if (s.Length > best && stem.StartsWith(s, cmp)) { best = s.Length; origin = i; }
        }
        var head = best >= 0 ? stem[..best] : stem;
        var rest = best >= 0 ? Regex.Replace(stem[best..], @"\s*\([^()]*\)", "").TrimEnd() : "";
        var tag = string.Concat(r.Name.Split(Path.GetInvalidFileNameChars())).Replace('/', '-').Replace('\\', '-').Trim();
        return (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(origin))!, $"{head}{rest} ({tag}){ext}");
    }
}
