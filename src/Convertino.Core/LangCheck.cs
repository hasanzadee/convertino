using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Convertino;

/// <summary>
/// <c>convertino lang-check [checkout]</c>: what each <c>src/Convertino.Core/Lang/*.json</c> still lacks, and whether the files are sound.
/// Fails (exit 1) on malformed JSON, keys whose English no longer appears anywhere in the sources, {n} placeholders that
/// differ from the English, a wrong number of plural forms, or an interpolated string handed to L.T / L.N.
/// Untranslated strings are only listed: they show in English until someone translates them.
/// </summary>
public static class LangCheck
{
    /// <summary>Calls whose literal arguments are UI text, by 0-based argument position (L.N: its singular is the key).</summary>
    static readonly Dictionary<string, int[]> Calls = new()
    {
        ["L.T"] = [0], ["L.N"] = [1], ["Tool"] = [1, 3], ["Catalog.Tool"] = [1, 3], ["Editor"] = [1, 3],
    };

    public static int Run(string? dir, TextWriter o)
    {
        var root = Path.GetFullPath(dir ?? ".");
        while (root != null && !Directory.Exists(Path.Combine(root, "src", "Convertino.Core", "Lang")))
            root = Path.GetDirectoryName(root);
        if (root == null)
        {
            o.WriteLine("lang-check runs inside a Convertino checkout (looks for src/Convertino.Core/Lang)");
            return 2;
        }
        var src = Path.Combine(root, "src");
        var errors = new List<string>();
        var literals = new HashSet<string>(StringComparer.Ordinal);
        var wanted = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Contains("/bin/") || rel.Contains("/obj/")) continue;
            // Dev.cs only builds test petals: its literals count as sources, not as UI text to translate
            Scan(File.ReadAllText(file), rel, literals, rel.EndsWith("/Dev.cs") ? [] : wanted, errors);
        }

        var langs = new SortedDictionary<string, Dictionary<string, string[]>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(src, "Convertino.Core", "Lang"), "*.json"))
        {
            var code = Path.GetFileNameWithoutExtension(file);
            var map = Load(file, code, errors);
            if (map == null) continue;
            langs[code] = map;
            foreach (var (key, forms) in map)
            {
                if (!literals.Contains(key)) errors.Add($"{code}: \"{key}\" isn't in the sources any more (renamed or removed?)");
                var want = Holes(key);
                foreach (var f in forms)
                    if (!Holes(f).SetEquals(want))
                        errors.Add($"{code}: \"{key}\" → \"{f}\" has placeholders {Show(Holes(f))}, the English has {Show(want)}");
                if (forms.Length > 1 && forms.Length != L.Forms(code))
                    errors.Add($"{code}: \"{key}\" lists {forms.Length} plural forms, {code} has {L.Forms(code)}");
            }
        }

        // everything marked for translation in the code, plus whatever some language already translates
        foreach (var map in langs.Values) wanted.UnionWith(map.Keys);
        o.WriteLine($"{wanted.Count} UI strings");
        foreach (var (code, map) in langs)
        {
            var missing = wanted.Where(k => !map.ContainsKey(k)).ToList();
            o.WriteLine($"{code,-8} {wanted.Count - missing.Count,4}/{wanted.Count} translated" + (missing.Count > 0 ? $", {missing.Count} missing:" : ""));
            foreach (var k in missing) o.WriteLine($"           {k}");
        }
        foreach (var e in errors) o.WriteLine("error: " + e);
        o.WriteLine(errors.Count == 0 ? "ok" : $"{errors.Count} error(s)");
        return errors.Count == 0 ? 0 : 1;
    }

    static Dictionary<string, string[]>? Load(string file, string code, List<string> errors)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { errors.Add($"{code}: not a JSON object"); return null; }
            if (!doc.RootElement.TryGetProperty("@name", out var name) || name.ValueKind != JsonValueKind.String)
                errors.Add($"{code}: needs \"@name\", the language's own name (\"Deutsch\")");
            var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name.StartsWith('@')) continue;
                if (map.ContainsKey(p.Name)) { errors.Add($"{code}: \"{p.Name}\" appears twice"); continue; }
                string[]? forms = p.Value.ValueKind switch
                {
                    JsonValueKind.String => [p.Value.GetString()!],
                    JsonValueKind.Array when p.Value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String) => [.. p.Value.EnumerateArray().Select(v => v.GetString()!)],
                    _ => null,
                };
                if (forms == null || forms.Length == 0 || forms.Any(f => f.Length == 0)) { errors.Add($"{code}: \"{p.Name}\" needs a text or a list of texts"); continue; }
                map[p.Name] = forms;
            }
            return map;
        }
        catch (JsonException ex)
        {
            errors.Add($"{code}: malformed JSON ({ex.Message})");
            return null;
        }
    }

    static HashSet<int> Holes(string s) =>
        [.. Regex.Matches(s.Replace("{{", "").Replace("}}", ""), @"\{(\d+)(?:[,:][^}]*)?\}").Select(m => int.Parse(m.Groups[1].Value))];

    static string Show(HashSet<int> h) => h.Count == 0 ? "none" : string.Join(" ", h.Order().Select(i => $"{{{i}}}"));

    // ---------- a small C# lexer: string literals, and the calls they're handed to ----------

    enum K { Word, Punct, Str }
    readonly record struct Tok(K Kind, string Text, bool Interpolated, int Line);

    static void Scan(string code, string file, HashSet<string> literals, SortedSet<string> wanted, List<string> errors)
    {
        var toks = Lex(code);
        foreach (var t in toks) if (t.Kind == K.Str) literals.Add(t.Text);
        for (int i = 0; i < toks.Count; i++)
        {
            // Name ( or Name . Name (
            if (toks[i].Kind != K.Word) continue;
            string name = toks[i].Text;
            int j = i + 1;
            if (j + 1 < toks.Count && toks[j].Text == "." && toks[j + 1].Kind == K.Word) { name += "." + toks[j + 1].Text; j += 2; }
            if (i > 0 && toks[i - 1].Text == ".") continue; // x.Tool( is someone else's Tool
            if (j >= toks.Count || toks[j].Text != "(" || !Calls.TryGetValue(name, out var positions)) continue;
            var args = Args(toks, j);
            foreach (var p in positions)
            {
                if (p >= args.Count || args[p].Count != 1 || args[p][0].Kind != K.Str) continue;
                var s = args[p][0];
                if (s.Interpolated && name.StartsWith("L.")) errors.Add($"{file}:{s.Line}: {name} gets an interpolated string, which can never match a key: pass {{0}} and the value instead");
                else if (!s.Interpolated) wanted.Add(s.Text);
            }
        }
    }

    /// <summary>The token lists of a call's arguments, split at top-level commas.</summary>
    static List<List<Tok>> Args(List<Tok> toks, int open)
    {
        var args = new List<List<Tok>> { new() };
        int depth = 0;
        for (int i = open + 1; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.Kind == K.Punct && t.Text is "(" or "[" or "{") depth++;
            else if (t.Kind == K.Punct && t.Text is ")" or "]" or "}")
            {
                if (depth == 0) break;
                depth--;
            }
            else if (t.Kind == K.Punct && t.Text == "," && depth == 0) { args.Add([]); continue; }
            args[^1].Add(t);
        }
        return args;
    }

    static List<Tok> Lex(string s)
    {
        var toks = new List<Tok>();
        int i = 0, line = 1;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && At(s, i + 1) == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (c == '/' && At(s, i + 1) == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? s.Length : end + 2;
                line += Count(s, i, end);
                i = end;
                continue;
            }
            if (c == '\'')
            {
                int j = i + 1;
                while (j < s.Length && s[j] != '\'') j += s[j] == '\\' ? 2 : 1;
                i = j + 1;
                continue;
            }
            // string prefixes: $ @ $@ @$ and raw """, any number of $
            int p = i;
            int dollars = 0;
            bool verbatim = false;
            while (p < s.Length && (s[p] == '$' || s[p] == '@'))
            {
                if (s[p] == '$') dollars++; else verbatim = true;
                p++;
            }
            if (At(s, p) == '"' && (p > i || c == '"'))
            {
                int start = line;
                var (text, end) = ReadString(s, p, dollars, verbatim);
                line += Count(s, i, end);
                toks.Add(new(K.Str, text, dollars > 0, start));
                i = end;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int j = i;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                toks.Add(new(K.Word, s[i..j], false, line));
                i = j;
                continue;
            }
            toks.Add(new(K.Punct, c.ToString(), false, line));
            i++;
        }
        return toks;
    }

    static char At(string s, int i) => i < s.Length ? s[i] : '\0';
    static int Count(string s, int from, int to) { int n = 0; for (int k = from; k < to && k < s.Length; k++) if (s[k] == '\n') n++; return n; }

    /// <summary>Reads the literal starting at the opening quote; interpolation holes become {0}, {1} ….</summary>
    static (string Text, int End) ReadString(string s, int q, int dollars, bool verbatim)
    {
        var sb = new StringBuilder();
        int quotes = 0;
        while (At(s, q + quotes) == '"') quotes++;
        bool raw = quotes >= 3 && !verbatim;
        int i = raw ? q + quotes : q + 1;
        int hole = 0;
        if (raw)
        {
            var close = new string('"', quotes);
            int end = s.IndexOf(close, i, StringComparison.Ordinal);
            end = end < 0 ? s.Length : end;
            return (s[i..end], Math.Min(s.Length, end + quotes));
        }
        if (quotes == 2 && !verbatim) return ("", q + 2); // ""
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '"')
            {
                if (verbatim && At(s, i + 1) == '"') { sb.Append('"'); i += 2; continue; }
                return (sb.ToString(), i + 1);
            }
            if (!verbatim && c == '\\')
            {
                char n = At(s, i + 1);
                switch (n)
                {
                    case 'n': sb.Append('\n'); i += 2; break;
                    case 'r': sb.Append('\r'); i += 2; break;
                    case 't': sb.Append('\t'); i += 2; break;
                    case '0': sb.Append('\0'); i += 2; break;
                    case 'u' when i + 6 <= s.Length: sb.Append((char)Convert.ToInt32(s.Substring(i + 2, 4), 16)); i += 6; break;
                    default: sb.Append(n); i += 2; break;
                }
                continue;
            }
            if (dollars > 0 && c == '{')
            {
                if (At(s, i + 1) == '{') { sb.Append('{'); i += 2; continue; }
                // skip the expression, nested braces, parentheses and strings included
                int depth = 0;
                i++;
                while (i < s.Length)
                {
                    char e = s[i];
                    if (e == '"' || (e is '$' or '@' && At(s, i + 1) is '"' or '$' or '@'))
                    {
                        int p = i, d = 0;
                        bool v = false;
                        while (s[p] is '$' or '@') { if (s[p] == '$') d++; else v = true; p++; }
                        i = ReadString(s, p, d, v).End;
                        continue;
                    }
                    if (e == '\'') { i += At(s, i + 1) == '\\' ? 4 : 3; continue; }
                    if (e is '(' or '[' or '{') depth++;
                    else if (e is ')' or ']') depth--;
                    else if (e == '}') { if (depth == 0) break; depth--; }
                    i++;
                }
                sb.Append('{').Append(hole++).Append('}');
                i++;
                continue;
            }
            if (dollars > 0 && c == '}' && At(s, i + 1) == '}') { sb.Append('}'); i += 2; continue; }
            sb.Append(c);
            i++;
        }
        return (sb.ToString(), s.Length);
    }
}
