using System.Text.Json;

namespace Convertino;

/// <summary>Per-day tally of finished jobs, for the panel's Activity card. Lives in stats.json next to the settings.</summary>
public sealed class Stats
{
    public sealed class Day
    {
        /// <summary>Input files handled.</summary>
        public int Files { get; set; }
        /// <summary>Bytes the results came out smaller than their sources (jobs that grew don't subtract).</summary>
        public long Saved { get; set; }
        /// <summary>Uses per action caption ("→ JPG", "Cut out" …).</summary>
        public Dictionary<string, int> Actions { get; set; } = [];
    }

    /// <summary>Keyed by local date, yyyy-MM-dd.</summary>
    public Dictionary<string, Day> Days { get; set; } = [];

    const int KeepDays = 120;
    static readonly object Gate = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static string FilePath => Path.Combine(Paths.AppData, "stats.json");

    static string Key(DateTime d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static Stats Load(string? file = null)
    {
        try
        {
            file ??= FilePath;
            if (File.Exists(file)) return JsonSerializer.Deserialize<Stats>(File.ReadAllText(file)) ?? new();
        }
        catch (Exception ex) { Log.Error("stats load", ex); }
        return new();
    }

    /// <summary>Count a finished job. Re-reads the file first so a second process can't wipe our numbers.</summary>
    public static void Record(Job job)
    {
        try
        {
            long inBytes = job.Inputs.Sum(Paths.SizeOf);
            long outBytes = job.Outputs.Sum(Paths.SizeOf);
            long saved = job.Saved ?? (job.Outputs.Count > 0 && outBytes > 0 && inBytes > outBytes ? inBytes - outBytes : 0);
            // the English caption: stats.json mustn't split per UI language (the panel shows it translated)
            string caption = L.English(string.IsNullOrEmpty(job.Action.Short) ? job.Action.Label : job.Action.Short);
            lock (Gate)
            {
                var s = Load();
                var key = Key(DateTime.Now);
                if (!s.Days.TryGetValue(key, out var d)) s.Days[key] = d = new Day();
                d.Files += job.Inputs.Count;
                d.Saved += saved;
                d.Actions[caption] = d.Actions.GetValueOrDefault(caption) + 1;
                var cutoff = Key(DateTime.Now.AddDays(-KeepDays));
                foreach (var old in s.Days.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList()) s.Days.Remove(old);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
                File.Move(tmp, FilePath, true);
            }
        }
        catch (Exception ex) { Log.Error("stats save", ex); }
    }

    public Day On(DateTime date) => Days.TryGetValue(Key(date), out var d) ? d : new Day();

    /// <summary>The last n days, oldest first, ending today.</summary>
    public IReadOnlyList<(DateTime Date, Day Day)> Last(int n)
    {
        var today = DateTime.Today;
        return Enumerable.Range(0, n).Select(i => today.AddDays(i - n + 1)).Select(d => (d, On(d))).ToList();
    }

    /// <summary>Consecutive active days up to today (or up to yesterday, if nothing ran yet today).</summary>
    public int Streak()
    {
        var d = DateTime.Today;
        if (On(d).Files == 0) d = d.AddDays(-1);
        int n = 0;
        while (On(d).Files > 0) { n++; d = d.AddDays(-1); }
        return n;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Days.Values.All(d => d.Files == 0);

    /// <summary>Most used actions over the last n days.</summary>
    public IReadOnlyList<(string Caption, int Count)> Top(int days, int take) =>
        Last(days).SelectMany(x => x.Day.Actions)
            .GroupBy(kv => kv.Key)
            .Select(g => (g.Key, g.Sum(kv => kv.Value)))
            .OrderByDescending(x => x.Item2).ThenBy(x => x.Key)
            .Take(take).ToList();

    /// <summary>Plausible two weeks of use, for screenshots (--panel).</summary>
    public static Stats Demo()
    {
        var s = new Stats();
        int[] files = [3, 0, 7, 12, 5, 0, 0, 9, 14, 6, 21, 8, 11, 17];
        string[] acts = ["→ JPG", "Cut out", "→ PDF", "Compress", "→ WEBP", "Text", "Crop"];
        var rnd = new Random(7);
        for (int i = 0; i < files.Length; i++)
        {
            if (files[i] == 0) continue;
            var d = new Day { Files = files[i], Saved = files[i] * (long)rnd.Next(400_000, 2_600_000) };
            for (int k = 0; k < files[i]; k++)
            {
                var a = acts[Math.Min(acts.Length - 1, (int)Math.Floor(Math.Pow(rnd.NextDouble(), 1.8) * acts.Length))];
                d.Actions[a] = d.Actions.GetValueOrDefault(a) + 1;
            }
            s.Days[Key(DateTime.Today.AddDays(i - files.Length + 1))] = d;
        }
        return s;
    }
}
