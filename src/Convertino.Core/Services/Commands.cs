using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Convertino.Services;

/// <summary>
/// Your own programs as petals (recipes.json "commands"). Each file gets its own run, with the arguments handed over one
/// by one (ProcessStartInfo.ArgumentList): no shell and no command line glued together from strings, so a file name can't
/// add options or commands. Output goes to the log; a non-zero exit shows its last error line.
/// </summary>
public static partial class Commands
{
    public static List<PetalAction> For(IReadOnlyList<string> files, RecipeFile file) =>
        file.Commands.Where(c => Recipes.Matches(c.For, files)).Select(Petal).ToList();

    public static PetalAction Petal(CustomCommand c) => new()
    {
        Id = Recipes.IdOf(c),
        Label = c.Name.Length > 12 ? c.Name[..11].TrimEnd() + "…" : c.Name,
        Icon = string.IsNullOrWhiteSpace(c.Icon) ? "terminal" : c.Icon,
        Verb = $"Run {c.Name}",
        Short = c.Name,
        Run = job => Run(c, job),
    };

    [GeneratedRegex(@"\{(in|name|ext|dir|out)\}")]
    private static partial Regex Placeholder();

    /// <summary>Fill in {in} {name} {ext} {dir} {out} in one pass, so a file name that contains "{out}" stays as it is.</summary>
    static string Expand(string template, Dictionary<string, string> vars) =>
        Placeholder().Replace(template, m => vars.GetValueOrDefault(m.Groups[1].Value, m.Value));

    static Dictionary<string, string> Vars(string input, string? output) => new()
    {
        ["in"] = input,
        ["name"] = Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(input)),
        ["ext"] = Kinds.Ext(input),
        ["dir"] = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(input)) ?? "",
        ["out"] = output ?? "",
    };

    static bool WritesOut(CustomCommand c) => c.Args.Any(a => a.Contains("{out}"));

    /// <summary>The output pattern: its own, or "{name} (Name).{ext}" when the arguments say {out} without one; null when it makes no file.</summary>
    static string? Pattern(CustomCommand c) =>
        !string.IsNullOrWhiteSpace(c.Output) ? c.Output : WritesOut(c) ? $"{{name}} ({c.Name}).{{ext}}" : null;

    /// <summary>For recipes: the type it makes from a file of this type ("" none, null when the pattern has no extension).</summary>
    public static string? Makes(CustomCommand c, string type)
    {
        if (Pattern(c) is not { } p) return "";
        var ext = Kinds.Ext(Expand(p, Vars("sample." + type, null)));
        return ext.Length > 0 ? ext : null;
    }

    /// <summary>Where this run's result goes: the pattern beside the input; a free name when the program is told it through {out}.</summary>
    static string? OutputFor(CustomCommand c, string input)
    {
        if (Pattern(c) is not { } pattern) return null;
        var vars = Vars(input, null);
        var path = Expand(pattern, vars);
        if (path.StartsWith("~/")) path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        path = Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(vars["dir"], path));
        if (!WritesOut(c)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var ext = Path.GetExtension(path);
        return ext.Length > 0 ? Paths.Unique(dir, Path.GetFileNameWithoutExtension(path), ext) : Paths.UniqueDir(dir, Path.GetFileName(path));
    }

    /// <summary>
    /// The program's full path, or null with what's wrong. A bare name is looked up on PATH. Windows batch files are refused:
    /// they run through cmd.exe, which would read &amp;, | and ^ in a file name as commands of their own.
    /// </summary>
    public static string? Resolve(string program, out string problem)
    {
        problem = "";
        var p = program.Trim().Trim('"');
        if (p.StartsWith("~/")) p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p[2..]);
        string? found = null;
        if (Path.IsPathFullyQualified(p)) found = File.Exists(p) ? p : null;
        else if (p.Contains('/') || p.Contains('\\'))
        {
            problem = $"\"{program}\" needs to be a full path or a name on PATH";
            return null;
        }
        else
        {
            var exts = OperatingSystem.IsWindows() && !Path.HasExtension(p) ? new[] { ".exe", ".com" } : new[] { "" };
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var e in exts)
                {
                    try
                    {
                        var candidate = Path.Combine(dir.Trim().Trim('"'), p + e);
                        if (File.Exists(candidate)) { found = candidate; break; }
                    }
                    catch { }
                }
                if (found != null) break;
            }
        }
        if (found == null)
        {
            problem = $"{Path.GetFileName(p)} not found";
            return null;
        }
        if (OperatingSystem.IsWindows() && Path.GetExtension(found).ToLowerInvariant() is ".bat" or ".cmd")
        {
            problem = "Batch files can't be run safely";
            return null;
        }
        return found;
    }

    static async Task Run(CustomCommand c, Job job)
    {
        if (Resolve(c.Program, out var problem) is not { } exe)
        {
            job.Failed = true;
            job.Title = problem;
            job.Summary = problem.StartsWith("Batch")
                ? "cmd.exe would read & | ^ in file names as commands: point \"program\" at an .exe (or powershell.exe -File …)"
                : $"Install it, or give its full path in recipes.json ({c.Name})";
            return;
        }
        int n = job.Inputs.Count;
        for (int i = 0; i < n; i++)
        {
            job.Token.ThrowIfCancellationRequested();
            job.Report(i, n);
            var input = job.Inputs[i];
            var output = OutputFor(c, input);
            bool existed = output != null && (File.Exists(output) || Directory.Exists(output));
            var stamp = existed ? File.GetLastWriteTimeUtc(output!) : default;
            var vars = Vars(input, output);
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = vars["dir"],
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in c.Args) psi.ArgumentList.Add(Expand(a, vars));
            Log.Info($"cmd {c.Name}: {exe} [{string.Join("] [", psi.ArgumentList)}]");

            var (code, last) = await Execute(c, psi, job.Token, () =>
            {
                // stopped half-way: what it wrote is incomplete
                if (output != null && !existed) TryDelete(output);
            });
            string where = n > 1 ? Path.GetFileName(input) + ": " : "";
            if (code == null)
            {
                job.Failed = true;
                job.Title = $"{c.Name} took too long";
                job.Summary = $"{where}stopped after {c.Timeout} s (\"timeout\" in recipes.json)";
                return;
            }
            if (code != 0)
            {
                job.Failed = true;
                job.Title = $"{c.Name} failed";
                job.Summary = where + (last is { Length: > 0 } l ? (l.Length > 160 ? l[..160] + "…" : l) : $"exit code {code}");
                return;
            }
            if (output == null) continue;
            if (!File.Exists(output) && !Directory.Exists(output) || existed && File.GetLastWriteTimeUtc(output) == stamp)
            {
                job.Failed = true;
                job.Title = $"{c.Name} made nothing";
                job.Summary = $"{where}it finished, but {Path.GetFileName(output)} isn't there (check \"output\" in recipes.json)";
                return;
            }
            job.Outputs.Add(output);
        }
        if (job.Outputs.Count == 0) job.Title = $"{c.Name} finished";
    }

    /// <summary>Run to the end: the exit code (null after the timeout) and the last line it printed, errors first.</summary>
    static async Task<(int? Code, string? Last)> Execute(CustomCommand c, ProcessStartInfo psi, CancellationToken ct, Action stopped)
    {
        using var p = new Process { StartInfo = psi };
        string? lastErr = null, lastOut = null;
        int lines = 0;
        void Line(string? text, bool err)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (err) lastErr = text.Trim(); else lastOut = text.Trim();
            if (Interlocked.Increment(ref lines) <= 200) Log.Info($"cmd {c.Name}{(err ? " !" : ":")} {text}");
        }
        p.OutputDataReceived += (_, e) => Line(e.Data, false);
        p.ErrorDataReceived += (_, e) => Line(e.Data, true);
        try { p.Start(); }
        catch (Win32Exception ex) { return (-1, $"couldn't start {Path.GetFileName(psi.FileName)}: {ex.Message}"); }
        try { p.StandardInput.Close(); } catch { } // nothing to read: a program waiting for input gets end-of-file, not a hang
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(c.Timeout, 1, 24 * 3600)));
        try
        {
            await p.WaitForExitAsync(limit.Token);
            p.WaitForExit(); // let the last output lines arrive
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            stopped();
            ct.ThrowIfCancellationRequested(); // cancelled by the user: the job just goes away
            return (null, null);
        }
        return (p.ExitCode, lastErr ?? lastOut);
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch { }
    }
}
