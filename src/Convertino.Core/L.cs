using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Convertino;

/// <summary>
/// UI strings, gettext style: the English text is the key. <c>L.T("Drop on a petal")</c>, <c>L.T("Convert to {0}", "PNG")</c>,
/// <c>L.N(n, "{0} file", "{0} files")</c> look the text up in the chosen language's <c>Lang/&lt;code&gt;.json</c>
/// (embedded) and fall back to the English when it isn't translated yet, so new strings work before anyone translates them.
/// <para>
/// A plural entry's key is the English singular and its value lists the language's forms (Russian: one, few, many).
/// <c>{0:l}</c> in a template lowercases the first letter of a text argument ("Couldn't {0:l}" + "Convert to PNG").
/// </para>
/// The command line never calls <see cref="Use"/> with anything but English, so its output and the log stay English.
/// </summary>
public static class L
{
    public sealed record Language(string Code, string Name);

    sealed class Table(string code, CultureInfo culture, Dictionary<string, string[]> map)
    {
        public readonly string Code = code;
        public readonly CultureInfo Culture = culture;
        public readonly Dictionary<string, string[]> Map = map;
        public readonly Formatter Fmt = new(culture);
        public List<(Regex Re, string Key)>? Templates;
    }

    const string Prefix = "Convertino.Lang.";
    static readonly Formatter EnglishFmt = new(CultureInfo.GetCultureInfo("en-US"));
    static Table _t = new("en", EnglishCulture(), []);
    static IReadOnlyList<Language>? _all;
    /// <summary>Translated text → the English it came from, so what gets saved (Recent, stats.json) stays language-neutral.</summary>
    static readonly ConcurrentDictionary<string, string> Back = new();

    /// <summary>The language in use: "en", "ru", "pt-BR" …</summary>
    public static string Code => _t.Code;

    /// <summary>Dates and numbers in the UI language (never set as the thread culture: converters keep the invariant one).</summary>
    public static CultureInfo Culture => _t.Culture;

    /// <summary>Raised on the thread that called <see cref="Use"/> after the language changed.</summary>
    public static event Action? Changed;

    /// <summary>English plus every embedded translation, by native name.</summary>
    public static IReadOnlyList<Language> All => _all ??= Discover();

    static IReadOnlyList<Language> Discover()
    {
        var list = new List<Language> { new("en", "English") };
        foreach (var code in Codes())
            try
            {
                using var doc = JsonDocument.Parse(Read(code)!);
                list.Add(new(code, doc.RootElement.TryGetProperty("@name", out var n) ? n.GetString() ?? code : code));
            }
            catch (Exception ex) { Log.Error($"lang {code}", ex); }
        return [.. list.OrderBy(l => l.Name, StringComparer.Ordinal)];
    }

    static IEnumerable<string> Codes() => typeof(L).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(Prefix) && n.EndsWith(".json"))
        .Select(n => n[Prefix.Length..^5]);

    static string? Read(string code)
    {
        using var s = typeof(L).Assembly.GetManifestResourceStream(Prefix + code + ".json");
        if (s == null) return null;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>
    /// Switch language: a code from <see cref="All"/>, or null / "auto" for the OS UI language (English if there's no translation).
    /// <c>CONVERTINO_LANG</c> overrides the setting (screenshots, tests).
    /// </summary>
    public static void Use(string? setting)
    {
        if (Environment.GetEnvironmentVariable("CONVERTINO_LANG") is { Length: > 0 } env) setting = env;
        Apply(Resolve(setting));
    }

    /// <summary>Back to the English source texts (the command line).</summary>
    public static void Reset() => Apply("en");

    static void Apply(string code)
    {
        if (code == _t.Code) return;
        Table t;
        if (code == "en") t = new("en", EnglishCulture(), []);
        else
        {
            var map = new Dictionary<string, string[]>();
            try
            {
                using var doc = JsonDocument.Parse(Read(code) ?? "{}");
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Name.StartsWith('@')) continue;
                    string[] forms = p.Value.ValueKind == JsonValueKind.Array
                        ? [.. p.Value.EnumerateArray().Select(v => v.GetString() ?? "")]
                        : [p.Value.GetString() ?? ""];
                    if (forms.All(f => f.Length > 0)) map[p.Name] = forms;
                }
            }
            catch (Exception ex) { Log.Error($"lang {code}", ex); }
            t = new(code, CultureOf(code), map);
        }
        _t = t;
        Log.Info($"language {code} ({t.Map.Count} strings)");
        Changed?.Invoke();
    }

    /// <summary>"auto" → the OS UI language when there's a translation for it; an unknown code → English.</summary>
    public static string Resolve(string? setting)
    {
        var codes = All.Select(l => l.Code).ToList();
        if (!string.IsNullOrEmpty(setting) && setting != "auto")
            return codes.FirstOrDefault(c => string.Equals(c, setting, StringComparison.OrdinalIgnoreCase)) ?? "en";
        // pt-PT → pt-BR, zh-CN / zh-TW → zh-Hans, az-Latn-AZ → az …
        for (var c = CultureInfo.CurrentUICulture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            if (codes.FirstOrDefault(x => string.Equals(x, c.Name, StringComparison.OrdinalIgnoreCase)) is { } exact) return exact;
            if (c.Parent.Name.Length == 0 && codes.FirstOrDefault(x => x.Split('-')[0].Equals(c.Name, StringComparison.OrdinalIgnoreCase)) is { } near) return near;
        }
        return "en";
    }

    static CultureInfo EnglishCulture() =>
        CultureInfo.CurrentCulture.TwoLetterISOLanguageName == "en" ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo("en-US");

    static CultureInfo CultureOf(string code)
    {
        try { return CultureInfo.GetCultureInfo(code); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    // ---------- lookup ----------

    /// <summary>The translation of <paramref name="key"/>, or the key itself.</summary>
    public static string T(string key)
    {
        var t = _t;
        if (!t.Map.TryGetValue(key, out var forms)) return key;
        Remember(forms[0], key);
        return forms[0];
    }

    /// <summary>The translation of a template with {0} {1} … filled in (numbers and dates in the UI culture).</summary>
    public static string T(string key, params object?[] args)
    {
        var t = _t;
        string english = Format(EnglishFmt, key, key, args);
        if (!t.Map.TryGetValue(key, out var forms)) return english;
        return Remember(Format(t.Fmt, forms[0], key, args), english);
    }

    /// <summary>Count-dependent text: {0} is <paramref name="n"/>, more arguments follow as {1} ….</summary>
    public static string N(long n, string one, string other, params object?[] args)
    {
        var t = _t;
        object?[] all = [n, .. args];
        string english = Format(EnglishFmt, n == 1 ? one : other, one, all);
        if (!t.Map.TryGetValue(one, out var forms)) return english;
        return Remember(Format(t.Fmt, forms[Math.Min(forms.Length - 1, Plural(t.Code, n))], one, all), english);
    }

    static string Remember(string shown, string english)
    {
        if (shown != english)
        {
            if (Back.Count > 4096) Back.Clear();
            Back[shown] = english;
        }
        return shown;
    }

    static string Format(Formatter f, string template, string key, object?[] args)
    {
        try { return string.Format(f, template, args); }
        catch (FormatException)
        {
            try { return string.Format(EnglishFmt, key, args); }
            catch (FormatException) { return key; }
        }
    }

    /// <summary>The English a translated text came from (or the text itself): what Recent and stats.json store.</summary>
    public static string English(string shown) => Back.TryGetValue(shown, out var e) ? e : shown;

    /// <summary>
    /// Show text that was saved in English ("Convert to PNG" in Recent, an Activity caption): the exact key,
    /// or a template whose {0}s match ("Convert to {0}"), or the text as it is.
    /// </summary>
    public static string Saved(string english)
    {
        var t = _t;
        if (t.Code == "en" || string.IsNullOrEmpty(english)) return english;
        if (t.Map.ContainsKey(english)) return T(english);
        t.Templates ??= [.. t.Map.Keys.Where(IsTemplate).OrderByDescending(k => Regex.Replace(k, @"\{\d+[^}]*\}", "").Length).Select(k => (Pattern(k), k))];
        foreach (var (re, key) in t.Templates)
            if (re.Match(english) is { Success: true } m)
            {
                var groups = m.Groups.Cast<Group>().Skip(1).ToList();
                var args = new object?[groups.Max(g => int.Parse(g.Name[1..])) + 1];
                foreach (var g in groups) args[int.Parse(g.Name[1..])] = g.Value;
                return T(key, args);
            }
        return english;
    }

    /// <summary>Only templates with real words around their {0}s take part ("{0} · {1}" would match anything).</summary>
    static bool IsTemplate(string key) =>
        key.Contains('{') && Regex.Replace(key, @"\{\d+[^}]*\}", "").Count(char.IsLetter) >= 3 && !key.Contains("{{");

    static Regex Pattern(string key)
    {
        var seen = new HashSet<int>();
        var re = Regex.Replace(Regex.Escape(key), @"\\\{(\d+)[^}]*}", m =>
        {
            int i = int.Parse(m.Groups[1].Value);
            return seen.Add(i) ? $"(?<a{i}>.+?)" : $"\\k<a{i}>";
        });
        return new Regex("^" + re + "$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    // ---------- plurals, dates ----------

    /// <summary>How many plural forms a language's JSON lists (CLDR cardinal categories that matter for whole numbers).</summary>
    public static int Forms(string code) => code.Split('-')[0] switch
    {
        "ru" or "uk" => 3,
        "ja" or "zh" or "az" or "tr" => 1,
        _ => 2,
    };

    /// <summary>Which form of a plural entry fits <paramref name="n"/>.</summary>
    public static int Plural(string code, long n)
    {
        n = Math.Abs(n);
        return code.Split('-')[0] switch
        {
            "ru" or "uk" => n % 10 == 1 && n % 100 != 11 ? 0 : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? 1 : 2,
            "ja" or "zh" or "az" or "tr" => 0,
            "fr" or "pt" => n <= 1 ? 0 : 1,
            _ => n == 1 ? 0 : 1,
        };
    }

    /// <summary>A short day: "6 Oct", "6 окт.", "10月6日"; with the weekday: "Mon 6 Oct", "10月6日 (月)".</summary>
    public static string Date(DateTime d, bool weekday = false)
    {
        var c = Culture;
        var md = c.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM");
        if (c.TwoLetterISOLanguageName == "en") md = "d MMM";
        var pattern = !weekday ? md : c.TwoLetterISOLanguageName is "ja" or "zh" ? md + " (ddd)" : "ddd " + md;
        return d.ToString(pattern, c);
    }

    /// <summary>"Convert to PNG" → "convert to PNG", but "PNG-yə çevir" stays as it is.</summary>
    public static string LowerFirst(string s, CultureInfo? c = null) =>
        s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) ? char.ToLower(s[0], c ?? Culture) + s[1..] : s;

    sealed class Formatter(CultureInfo culture) : IFormatProvider, ICustomFormatter
    {
        public object? GetFormat(Type? type) => type == typeof(ICustomFormatter) ? this : culture.GetFormat(type);

        public string Format(string? format, object? arg, IFormatProvider? provider) => arg switch
        {
            string s when format == "l" => LowerFirst(s, culture),
            IFormattable f => f.ToString(format, culture),
            null => "",
            _ => arg.ToString() ?? "",
        };
    }
}
