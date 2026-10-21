namespace Convertino;

/// <summary>
/// "Save results": next to the original (the default), in a subfolder of its folder, or in one fixed folder. Petals keep
/// writing beside their source; once one has finished, its results are moved here. Editors, clipboard petals and those
/// that work where the file is by design (Extract here, Join) are left alone, and watch folders keep their own Out.
/// </summary>
public static class Output
{
    public const string Beside = "beside";

    /// <summary>The setting as a place: <see cref="Beside"/>, a subfolder name, or a full folder path.</summary>
    public static string Global
    {
        get
        {
            var s = Settings.Current;
            return s.SaveTo switch
            {
                "subfolder" => Watches.ValidOut(s.SaveSubfolder) ?? "Converted",
                "folder" when Valid(s.SaveFolder) is { } dir && dir != Beside && Path.IsPathFullyQualified(dir) => dir,
                _ => Beside,
            };
        }
    }

    /// <summary>
    /// A place as written in recipes.json or after --out: "beside", a plain subfolder name ("Web") or a full folder path
    /// ("D:\Web", "~/Web"). Null when it's none of these (a relative path, "..").
    /// </summary>
    public static string? Valid(string? place)
    {
        place = place?.Trim();
        if (string.IsNullOrEmpty(place)) return null;
        if (place.Equals(Beside, StringComparison.OrdinalIgnoreCase)) return Beside;
        if (place == "~" || place.StartsWith("~/") || place.StartsWith("~\\"))
            place = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), place.Length > 2 ? place[2..] : "");
        try
        {
            if (Path.IsPathFullyQualified(place)) return Path.GetFullPath(place);
        }
        catch { return null; }
        return Watches.ValidOut(place);
    }

    /// <summary>"next to the original", "in Web", "in D:\Web" — for the panels and the CLI.</summary>
    public static string Describe(string? place) => Valid(place) switch
    {
        null or Beside => "next to the original",
        var p when Path.IsPathFullyQualified(p) => "in " + p,
        var p => $"in a {p} subfolder",
    };

    /// <summary>Petals whose results stay where they are made.</summary>
    static bool Keeps(PetalAction a) => a.Interactive || a.Quick || a.Id is "extract" or "join";

    /// <summary>Where a petal's results go: its own choice (a recipe's), else the setting; always beside for those that keep their place.</summary>
    public static string For(PetalAction a) => Keeps(a) ? Beside : Valid(a.Out) ?? Global;

    /// <summary>The folder that results of a file in <paramref name="sourceDir"/> go to.</summary>
    public static string DirFor(string? place, string sourceDir) =>
        place is null or Beside ? sourceDir : Path.IsPathFullyQualified(place) ? place : Path.Combine(sourceDir, place);

    /// <summary>
    /// Move a finished job's results to <see cref="Job.Place"/> and point <see cref="Job.Outputs"/> at their new homes.
    /// Only results that sit beside an input are moved (a recipe puts its own in place); a name that's taken gets " (2)".
    /// A result that can't be moved stays where it is.
    /// </summary>
    public static void Place(Job job)
    {
        var place = job.Place;
        if (place is null or Beside || job.Outputs.Count == 0 || Keeps(job.Action)) return;
        var cmp = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var dirs = job.Inputs.Select(i => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(i)) ?? "").ToHashSet(cmp);
        var inputs = job.Inputs.ToHashSet(cmp);
        for (int i = 0; i < job.Outputs.Count; i++)
        {
            var o = job.Outputs[i];
            var dir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(o)) ?? "";
            if (!dirs.Contains(dir) || inputs.Contains(o)) continue;
            try { job.Outputs[i] = MoveInto(o, DirFor(place, dir)); }
            catch (Exception ex) { Log.Error($"save results to {place}", ex); }
        }
    }

    /// <summary>Move a file or folder into <paramref name="dir"/> (created if needed), as <paramref name="name"/> or its own name, " (2)" if taken; also across drives.</summary>
    public static string MoveInto(string path, string dir, string? name = null)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        Directory.CreateDirectory(dir);
        name ??= Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (Directory.Exists(path))
        {
            var dst = Paths.UniqueDir(dir, name);
            try { Directory.Move(path, dst); }
            catch (IOException) when (!SameRoot(path, dst))
            {
                CopyDir(path, dst);
                Directory.Delete(path, true);
            }
            return dst;
        }
        var file = Paths.Unique(dir, Path.GetFileNameWithoutExtension(name), Path.GetExtension(name));
        File.Move(path, file);
        return file;
    }

    static bool SameRoot(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>Copy a folder tree, leaving out links (they could point anywhere).</summary>
    public static void CopyDir(string src, string dst, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src))
        {
            ct.ThrowIfCancellationRequested();
            if (new FileInfo(f).LinkTarget != null) continue;
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        }
        foreach (var d in Directory.EnumerateDirectories(src))
        {
            if (new DirectoryInfo(d).LinkTarget != null) continue;
            CopyDir(d, Path.Combine(dst, Path.GetFileName(d)), ct);
        }
    }
}
