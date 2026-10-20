using System.Text.Json;
using System.Text.Json.Serialization;

namespace Convertino;

public static class Paths
{
    /// <summary>%APPDATA%\Convertino on Windows, ~/Library/Application Support/Convertino on macOS, $XDG_CONFIG_HOME/convertino (~/.config/convertino) elsewhere.</summary>
    public static readonly string AppData = Resolve();

    static string Resolve()
    {
        string dir;
        if (OperatingSystem.IsWindows())
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Convertino");
        else if (OperatingSystem.IsMacOS())
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Convertino");
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(xdg)) xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            dir = Path.Combine(xdg, "convertino");
        }
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string Settings => Path.Combine(AppData, "settings.json");
    public static string LogFile => Path.Combine(AppData, "convertino.log");
    public static string Temp
    {
        get
        {
            var d = Path.Combine(Path.GetTempPath(), "convertino");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>"dir/name.ext", or "dir/name (2).ext" … if taken.</summary>
    public static string Unique(string dir, string baseName, string ext)
    {
        if (!ext.StartsWith('.')) ext = "." + ext;
        baseName = string.Concat(baseName.Split(Path.GetInvalidFileNameChars()));
        var p = Path.Combine(dir, baseName + ext);
        for (int i = 2; File.Exists(p) || Directory.Exists(p); i++)
            p = Path.Combine(dir, $"{baseName} ({i}){ext}");
        return p;
    }

    public static string UniqueDir(string dir, string name)
    {
        var p = Path.Combine(dir, name);
        for (int i = 2; File.Exists(p) || Directory.Exists(p); i++)
            p = Path.Combine(dir, $"{name} ({i})");
        return p;
    }

    /// <summary>Output next to the source: "dir/stem suffix.ext".</summary>
    public static string Beside(string src, string ext, string? suffix = null)
    {
        var dir = Path.GetDirectoryName(src)!;
        var stem = Path.GetFileNameWithoutExtension(src);
        if (!string.IsNullOrEmpty(suffix)) stem += " " + suffix;
        return Unique(dir, stem, ext);
    }

    public static string HumanSize(long bytes)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : v >= 100 ? $"{v:0} {u[i]}" : v >= 10 ? $"{v:0.#} {u[i]}" : $"{v:0.##} {u[i]}";
    }

    public static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (Directory.Exists(path))
                return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch { }
        return 0;
    }

    /// <summary>Find an executable on PATH (adds .exe on Windows).</summary>
    public static string? Which(string name)
    {
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var e in exts)
            {
                try
                {
                    var p = Path.Combine(dir.Trim().Trim('"'), name + e);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
        return null;
    }
}

public static class Log
{
    static readonly object Gate = new();
    public static bool Echo { get; set; }

    public static void Info(string msg) => Write("INF", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERR", ex == null ? msg : $"{msg}: {ex}");

    static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {msg}";
        if (Echo) Console.Error.WriteLine(line);
        try
        {
            lock (Gate)
            {
                var fi = new FileInfo(Paths.LogFile);
                if (fi.Exists && fi.Length > 2_000_000) fi.Delete();
                File.AppendAllText(Paths.LogFile, line + Environment.NewLine);
            }
        }
        catch { }
    }
}

public sealed class RecentItem
{
    public string Path { get; set; } = "";
    public string Label { get; set; } = "";
    public DateTime When { get; set; }
}

public sealed class Settings
{
    public bool Enabled { get; set; } = true;
    public string Theme { get; set; } = "Mocha";
    public bool Notifications { get; set; } = true;
    public bool DoubleShift { get; set; } = true;
    public bool LaunchAtStartup { get; set; } = true;
    public bool FirstRunDone { get; set; }
    public int JpegQuality { get; set; } = 90;
    public int CompressQuality { get; set; } = 72;
    public List<RecentItem> Recent { get; set; } = [];

    [JsonIgnore] public static Settings Current { get; private set; } = new();

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Load()
    {
        try
        {
            var file = Paths.Settings;
            if (File.Exists(file))
                Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(file)) ?? new();
        }
        catch (Exception ex) { Log.Error("settings load", ex); Current = new(); }
    }

    static readonly object SaveGate = new();

    public void Save()
    {
        try
        {
            // jobs finish on worker threads: serialize under the Recent lock, and swap a finished file in
            // so a crash mid-write can't leave half a settings.json (Load would then reset everything)
            lock (SaveGate)
            {
                string json;
                lock (Recent) json = JsonSerializer.Serialize(this, Json);
                var tmp = Paths.Settings + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, Paths.Settings, true);
            }
        }
        catch (Exception ex) { Log.Error("settings save", ex); }
    }

    public void AddRecent(string path, string label)
    {
        lock (Recent)
        {
            Recent.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
            Recent.Insert(0, new RecentItem { Path = path, Label = label, When = DateTime.Now });
            if (Recent.Count > 8) Recent.RemoveRange(8, Recent.Count - 8);
        }
        Save();
    }
}

/// <summary>Colour themes shared by every UI. Colours are 0xAARRGGBB.</summary>
public sealed record Palette(
    string Name, bool IsDark,
    uint Tint, uint PetalTop, uint PetalBottom, uint Stroke,
    uint Text, uint SubText, uint AccentA, uint AccentB, uint AccentText,
    uint Hub, uint Shadow, uint Window, uint Surface, uint Danger)
{
    static uint C(uint rgb, double a = 1) => ((uint)Math.Round(a * 255) << 24) | (rgb & 0xFFFFFF);

    public static readonly Palette Mocha = new("Mocha", true,
        Tint: C(0x1e1e2e, .40), PetalTop: C(0x6c7086, .34), PetalBottom: C(0x181825, .50),
        Stroke: C(0xffffff, .13), Text: C(0xcdd6f4), SubText: C(0xa6adc8, .85),
        AccentA: C(0xcba6f7), AccentB: C(0xf5c2e7), AccentText: C(0x11111b),
        Hub: C(0x181825, .66), Shadow: C(0x000000, .42), Window: C(0x1e1e2e), Surface: C(0x313244),
        Danger: C(0xf38ba8));

    public static readonly Palette Latte = new("Latte", false,
        Tint: C(0xeff1f5, .55), PetalTop: C(0xffffff, .62), PetalBottom: C(0xe6e9ef, .52),
        Stroke: C(0xffffff, .75), Text: C(0x4c4f69), SubText: C(0x6c6f85, .9),
        AccentA: C(0x8839ef), AccentB: C(0xea76cb), AccentText: C(0xffffff),
        Hub: C(0xffffff, .66), Shadow: C(0x4c4f69, .28), Window: C(0xeff1f5), Surface: C(0xdce0e8),
        Danger: C(0xd20f39));

    public static readonly Palette Sunset = new("Sunset", false,
        Tint: C(0xfff1e6, .42), PetalTop: C(0xfff0e4, .66), PetalBottom: C(0xffd6bf, .52),
        Stroke: C(0xffffff, .8), Text: C(0x5b3423), SubText: C(0x8a5a44, .9),
        AccentA: C(0xff8a3d), AccentB: C(0xff3d68), AccentText: C(0xffffff),
        Hub: C(0xfff6ef, .7), Shadow: C(0x7a2e10, .30), Window: C(0xfff4ec), Surface: C(0xffe2d0),
        Danger: C(0xe5383b));

    public static readonly Palette Graphite = new("Graphite", true,
        Tint: C(0x0e0e12, .62), PetalTop: C(0x3a3a42, .55), PetalBottom: C(0x16161a, .6),
        Stroke: C(0xffffff, .12), Text: C(0xf2f2f5), SubText: C(0xb4b4bd, .85),
        AccentA: C(0xffffff), AccentB: C(0xc9cbd6), AccentText: C(0x111114),
        Hub: C(0x111114, .75), Shadow: C(0x000000, .55), Window: C(0x18181c), Surface: C(0x2a2a30),
        Danger: C(0xff6b6b));

    public static readonly Palette[] All = [Mocha, Latte, Sunset, Graphite];

    public static Palette Named(string name) => All.FirstOrDefault(p => p.Name == name) ?? Mocha;
}
