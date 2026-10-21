using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Tomlyn;
using Tomlyn.Model;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Convertino.Services;

/// <summary>
/// Structured data. JSON, JSON Lines, YAML, TOML and XML convert into one another through a JSON tree
/// (System.Text.Json nodes); CSV and XLSX come in and go out through <see cref="Sheets"/>.
/// The mapping rules are written down in docs/data-formats.md.
/// </summary>
public static partial class Data
{
    static readonly UTF8Encoding Utf8 = new(false);
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>"yml" → "yaml", "ndjson" → "jsonl", "xlsm" → "xlsx"; anything else as it is.</summary>
    public static string Canon(string ext) => ext switch { "yml" => "yaml", "ndjson" => "jsonl", "xlsm" => "xlsx", _ => ext };

    public static async Task Convert(Job job, string to)
    {
        string? note = null;
        for (int i = 0; i < job.Inputs.Count; i++)
        {
            job.Token.ThrowIfCancellationRequested();
            job.Report(i, job.Inputs.Count, 0.1);
            var src = job.Inputs[i];
            if (job.Inputs.Count > 1 && Canon(Kinds.Ext(src)) == to) continue;   // a mixed selection: this one is already there
            note = await Task.Run(() => ConvertOne(src, to, job.Outputs), job.Token);
        }
        job.Summary = job.Inputs.Count == 1 && note != null ? note : Images.SizeSummary(job);
    }

    static string? ConvertOne(string src, string to, List<string> outputs)
    {
        var from = Canon(Kinds.Ext(src));
        if (to is "csv" or "xlsx" or "md" or "html")
        {
            var grids = Sheets.IsSheet(src) ? Sheets.Read(src) : Sheets.FromTree(Read(src, from), Path.GetFileNameWithoutExtension(src));
            return Sheets.Write(grids, src, to, outputs);
        }
        var tree = Sheets.IsSheet(src) ? Sheets.ToTree(Sheets.Read(src)) : Read(src, from);
        var text = to switch
        {
            "json" => Json(tree, indent: true) + "\n",
            "jsonl" => JsonLines(tree),
            "yaml" => Yaml(tree),
            "toml" => Toml(tree),
            "xml" => Xml(tree),
            _ => throw new NotSupportedException(to),
        };
        var dst = Paths.Beside(src, to);
        File.WriteAllText(dst, text, Utf8);
        outputs.Add(dst);
        return null;
    }

    static JsonNode? Read(string path, string format) => format switch
    {
        "json" => ParseJson(File.ReadAllText(path)),
        "jsonl" => ParseJsonLines(File.ReadAllText(path)),
        "yaml" => ParseYaml(File.ReadAllText(path)),
        "toml" => ParseToml(File.ReadAllText(path)),
        "xml" => ParseXml(path),
        _ => throw new NotSupportedException(format),
    };

    // ---------- values shared by every format ----------

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][-+]?[0-9]+)?$")]
    private static partial Regex JsonNumber();

    /// <summary>A JSON number that keeps its exact text (1.50 stays 1.50).</summary>
    public static JsonNode NumberNode(string raw) => JsonNode.Parse(raw)!;

    /// <summary>
    /// Untyped text (XML, CSV) → JSON value: "true" / "false" become booleans, plain numbers without leading
    /// zeros and with at most 15 digits become numbers (so zip codes, phone numbers and ids stay text), the rest stays text.
    /// </summary>
    public static JsonNode Infer(string s)
    {
        if (s == "true") return true;
        if (s == "false") return false;
        if (s.Length <= 32 && JsonNumber().IsMatch(s) && s.Count(char.IsAsciiDigit) <= 15) return NumberNode(s);
        return s;
    }

    /// <summary>Nesting limit for every tree reader (JSON, YAML, XML): the walkers recurse, and a stack overflow can't be caught.</summary>
    const int MaxDepth = 512;

    static InvalidDataException Bad(string format, long? line, long? column, string message) =>
        new(line is { } l ? L.En("Not valid {0} (line {1}, column {2}): {3}", format, l, column ?? 1, message) : L.En("Not valid {0}: {1}", format, message));

    // ---------- JSON ----------

    static readonly JsonDocumentOptions JsonIn = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = MaxDepth };

    static JsonNode? ParseJson(string text)
    {
        try { return ParseJsonValue(text); }
        catch (JsonException ex)
        {
            // NDJSON saved as .json: one value per line
            if (TryJsonLines(text) is { } lines) return lines;
            throw Bad("JSON", ex.LineNumber + 1, ex.BytePositionInLine + 1, FirstSentence(ex.Message));
        }
    }

    /// <summary>Parses one JSON value. A key that appears twice keeps its last value, as in JavaScript's JSON.parse.</summary>
    static JsonNode? ParseJsonValue(string text)
    {
        using var doc = JsonDocument.Parse(text, JsonIn);
        return FromElement(doc.RootElement);
    }

    static JsonNode? FromElement(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var o = new JsonObject();
                foreach (var p in e.EnumerateObject()) o[p.Name] = FromElement(p.Value);
                return o;
            case JsonValueKind.Array:
                return new JsonArray(e.EnumerateArray().Select(FromElement).ToArray());
            case JsonValueKind.String: return e.GetString();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number: return NumberNode(e.GetRawText());
            default: return null;
        }
    }

    static JsonArray? TryJsonLines(string text)
    {
        var lines = text.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count < 2) return null;
        try { return new JsonArray(lines.Select(ParseJsonValue).ToArray()); }
        catch (JsonException) { return null; }
    }

    static JsonArray ParseJsonLines(string text)
    {
        var arr = new JsonArray();
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            try { arr.Add(ParseJsonValue(lines[i])); }
            catch (JsonException ex) { throw Bad("JSON Lines", i + 1, ex.BytePositionInLine + 1, FirstSentence(ex.Message)); }
        }
        return arr;
    }

    static string JsonLines(JsonNode? tree)
    {
        var sb = new StringBuilder();
        IEnumerable<JsonNode?> items = tree is JsonArray a ? a : [tree];
        foreach (var item in items) sb.Append(Json(item, indent: false)).Append('\n');
        return sb.ToString();
    }

    static string FirstSentence(string msg)
    {
        int cut = msg.IndexOf(" Path:", StringComparison.Ordinal);
        if (cut < 0) cut = msg.IndexOf(" LineNumber:", StringComparison.Ordinal);
        return (cut > 0 ? msg[..cut] : msg).TrimEnd('.', ' ');
    }

    /// <summary>JSON text for a tree, two-space indented or compact. Non-ASCII stays readable (emoji too); numbers keep their source text.</summary>
    public static string Json(JsonNode? node, bool indent)
    {
        var sb = new StringBuilder();
        WriteJson(sb, node, indent ? 0 : -1);
        return sb.ToString();
    }

    static void WriteJson(StringBuilder sb, JsonNode? node, int level)   // level −1: compact
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject o when o.Count == 0:
                sb.Append("{}");
                break;
            case JsonArray a when a.Count == 0:
                sb.Append("[]");
                break;
            case JsonObject o:
            {
                sb.Append('{');
                bool first = true;
                foreach (var (k, v) in o)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Break(sb, level, 1);
                    JsonString(sb, k);
                    sb.Append(level < 0 ? ":" : ": ");
                    WriteJson(sb, v, level < 0 ? -1 : level + 1);
                }
                Break(sb, level, 0);
                sb.Append('}');
                break;
            }
            case JsonArray a:
            {
                sb.Append('[');
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Break(sb, level, 1);
                    WriteJson(sb, a[i], level < 0 ? -1 : level + 1);
                }
                Break(sb, level, 0);
                sb.Append(']');
                break;
            }
            case JsonValue v:
                switch (v.GetValueKind())
                {
                    case JsonValueKind.String: JsonString(sb, v.GetValue<string>()); break;
                    case JsonValueKind.True: sb.Append("true"); break;
                    case JsonValueKind.False: sb.Append("false"); break;
                    case JsonValueKind.Null: sb.Append("null"); break;
                    default: sb.Append(v.ToJsonString()); break;
                }
                break;
        }
    }

    static void Break(StringBuilder sb, int level, int deeper)
    {
        if (level >= 0) sb.Append('\n').Append(' ', (level + deeper) * 2);
    }

    static void JsonString(StringBuilder sb, string s)
    {
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) sb.Append(c).Append(s[++i]);
                    else if (c < 0x20 || char.IsSurrogate(c) || c is '\u2028' or '\u2029') sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    // ---------- YAML (read: YamlDotNet with the YAML 1.2 core schema; write: below) ----------

    const string YamlTag = "tag:yaml.org,2002:";

    sealed class YamlBudget(int values) { public int Left = values; public readonly int Total = values; }

    static JsonNode? ParseYaml(string text)
    {
        var stream = new YamlStream();
        try
        {
            // YamlDotNet builds its node tree recursively: refuse deep nesting first, with its (iterative) event parser
            var parser = new Parser(new StringReader(text));
            for (int depth = 0; parser.MoveNext();)
            {
                if (parser.Current is YamlDotNet.Core.Events.MappingStart or YamlDotNet.Core.Events.SequenceStart && ++depth > MaxDepth)
                    throw new InvalidDataException(L.En("The {0} nests more than {1} levels deep", "YAML", MaxDepth));
                if (parser.Current is YamlDotNet.Core.Events.MappingEnd or YamlDotNet.Core.Events.SequenceEnd) depth--;
            }
            stream.Load(new StringReader(text));
        }
        catch (YamlException ex)
        {
            var msg = (ex.InnerException as YamlException ?? ex).Message;
            int cut = msg.LastIndexOf("): ", StringComparison.Ordinal);
            throw Bad("YAML", ex.Start.Line, ex.Start.Column, cut > 0 ? msg[(cut + 3)..] : msg);
        }
        // YamlDotNet's scanner throws this on some broken input, e.g. an unclosed [ list
        catch (InvalidOperationException) { throw Bad("YAML", null, null, "check the brackets and the indentation"); }
        // aliases repeat whole subtrees, so a few hundred bytes can stand for billions of values ("billion laughs"):
        // allow what a file without aliases could hold, and at least 2 million values
        var budget = new YamlBudget(Math.Max(2_000_000, text.Length / 2));
        // several documents (---) become a list
        var docs = stream.Documents.Select(d => FromYaml(d.RootNode, 0, budget)).ToArray();
        return docs.Length switch { 0 => null, 1 => docs[0], _ => new JsonArray(docs) };
    }

    static JsonNode? FromYaml(YamlNode node, int depth, YamlBudget budget)
    {
        if (depth > MaxDepth) throw new InvalidDataException(L.En("The YAML nests too deeply (or an alias contains itself)"));
        if (--budget.Left < 0) throw new InvalidDataException(L.En("The YAML's aliases expand to more than {0:N0} values (an alias bomb?)", budget.Total));
        switch (node)
        {
            case YamlScalarNode s:
            {
                var v = s.Value ?? "";
                var tag = s.Tag.IsEmpty ? "" : s.Tag.Value;
                if (tag == YamlTag + "str") return v;
                if (tag is "" && s.Style == ScalarStyle.Plain || tag.StartsWith(YamlTag) && tag[YamlTag.Length..] is "null" or "bool" or "int" or "float")
                    return CorePlain(v);
                return v;   // quoted, block or custom-tagged (!Ref, !!binary …): text
            }
            case YamlSequenceNode q:
                return new JsonArray(q.Children.Select(c => FromYaml(c, depth + 1, budget)).ToArray());
            case YamlMappingNode m:
            {
                var o = new JsonObject();
                var stated = m.Children.Keys.Where(k => !IsMergeKey(k)).Select(k => KeyText(k, budget)).ToHashSet();
                foreach (var (k, v) in m.Children)
                {
                    if (!IsMergeKey(k))
                    {
                        o[KeyText(k, budget)] = FromYaml(v, depth + 1, budget);
                        continue;
                    }
                    // "<<: *defaults" — keys stated in this mapping win, then the earlier merged mapping
                    foreach (var source in v is YamlSequenceNode list ? list.Children : [v])
                        if (FromYaml(source, depth + 1, budget) is JsonObject merged)
                            foreach (var (mk, mv) in merged.ToList())
                                if (!stated.Contains(mk) && !o.ContainsKey(mk))
                                {
                                    merged.Remove(mk);
                                    o[mk] = mv;
                                }
                }
                return o;
            }
            default:
                throw new InvalidDataException(L.En("Unsupported YAML node: {0}", node.NodeType.ToString()));
        }
    }

    static bool IsMergeKey(YamlNode k) => k is YamlScalarNode { Value: "<<", Style: ScalarStyle.Plain };

    static string KeyText(YamlNode k, YamlBudget budget) => k is YamlScalarNode s ? s.Value ?? "" : Json(FromYaml(k, 1, budget), indent: false);

    [GeneratedRegex(@"^[-+]?[0-9]+$")] private static partial Regex YamlInt();
    [GeneratedRegex(@"^0o[0-7]+$")] private static partial Regex YamlOct();
    [GeneratedRegex(@"^0x[0-9a-fA-F]+$")] private static partial Regex YamlHex();
    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")] private static partial Regex YamlFloat();

    /// <summary>A plain YAML scalar under the YAML 1.2 core schema (so yes / no / on / off stay text).</summary>
    static JsonNode? CorePlain(string v)
    {
        switch (v)
        {
            case "" or "~" or "null" or "Null" or "NULL": return null;
            case "true" or "True" or "TRUE": return true;
            case "false" or "False" or "FALSE": return false;
        }
        if (YamlInt().IsMatch(v)) return NumberNode(BigInteger.Parse(v, NumberStyles.AllowLeadingSign, Inv).ToString(Inv));
        if (YamlHex().IsMatch(v)) return NumberNode(BigInteger.Parse("0" + v[2..], NumberStyles.AllowHexSpecifier, Inv).ToString(Inv));
        if (YamlOct().IsMatch(v)) return NumberNode(v[2..].Aggregate(BigInteger.Zero, (n, c) => n * 8 + (c - '0')).ToString(Inv));
        if (YamlFloat().IsMatch(v))
        {
            if (JsonNumber().IsMatch(v)) return NumberNode(v);
            var d = double.Parse(v, NumberStyles.Float, Inv);
            if (double.IsFinite(d)) return NumberNode(d.ToString("R", Inv));
        }
        return v;   // includes .inf / .nan, which JSON can't hold
    }

    static string Yaml(JsonNode? tree)
    {
        var sb = new StringBuilder();
        switch (tree)
        {
            case JsonObject { Count: > 0 } o: YamlMap(sb, o, 0, sameLine: false); break;
            case JsonArray { Count: > 0 } a: YamlSeq(sb, a, 0, sameLine: false); break;
            default: YamlScalarLine(sb, tree, 2); break;
        }
        return sb.ToString();
    }

    static void YamlMap(StringBuilder sb, JsonObject o, int indent, bool sameLine)
    {
        bool first = true;
        foreach (var (k, v) in o)
        {
            if (!(first && sameLine)) sb.Append(' ', indent);
            first = false;
            YamlString(sb, k);
            sb.Append(':');
            YamlValue(sb, v, indent, afterDash: false);
        }
    }

    static void YamlSeq(StringBuilder sb, JsonArray a, int indent, bool sameLine)
    {
        bool first = true;
        foreach (var v in a)
        {
            if (!(first && sameLine)) sb.Append(' ', indent);
            first = false;
            sb.Append('-');
            YamlValue(sb, v, indent, afterDash: true);
        }
    }

    /// <summary>The value after "key:" or "-", through its line break(s). Lists under a key are indented; a mapping in a list starts on the dash's line.</summary>
    static void YamlValue(StringBuilder sb, JsonNode? v, int indent, bool afterDash)
    {
        switch (v)
        {
            case JsonObject { Count: > 0 } o:
                sb.Append(afterDash ? " " : "\n");
                YamlMap(sb, o, indent + 2, sameLine: afterDash);
                break;
            case JsonArray { Count: > 0 } a:
                sb.Append(afterDash ? " " : "\n");
                YamlSeq(sb, a, indent + 2, sameLine: afterDash);
                break;
            default:
                sb.Append(' ');
                YamlScalarLine(sb, v, indent + 2);
                break;
        }
    }

    static void YamlScalarLine(StringBuilder sb, JsonNode? v, int blockIndent)
    {
        switch (v)
        {
            case null: sb.Append("null\n"); return;
            case JsonObject: sb.Append("{}\n"); return;   // only empty ones get here
            case JsonArray: sb.Append("[]\n"); return;
        }
        var value = (JsonValue)v;
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                var s = value.GetValue<string>();
                if (LiteralOk(s)) { YamlLiteral(sb, s, blockIndent); return; }
                YamlString(sb, s);
                break;
            case JsonValueKind.True: sb.Append("true"); break;
            case JsonValueKind.False: sb.Append("false"); break;
            case JsonValueKind.Null: sb.Append("null"); break;
            default: sb.Append(YamlNumber(value.ToJsonString())); break;
        }
        sb.Append('\n');
    }

    /// <summary>1e5 → 1.0e+5: a YAML 1.1 reader (PyYAML) only sees a float when the mantissa has a dot and the exponent a sign.</summary>
    static string YamlNumber(string raw)
    {
        int e = raw.IndexOfAny(['e', 'E']);
        if (e < 0) return raw;
        string mantissa = raw[..e], exponent = raw[(e + 1)..];
        if (!mantissa.Contains('.')) mantissa += ".0";
        if (exponent[0] is not ('+' or '-')) exponent = "+" + exponent;
        return mantissa + "e" + exponent;
    }

    // text that a YAML 1.1 reader (PyYAML, js-yaml, Psych) would take for something else: bool words, 1_000, 1:20, dates …
    [GeneratedRegex(@"^(y|Y|yes|Yes|YES|n|N|no|No|NO|on|On|ON|off|Off|OFF|=|<<)$|^[-+]?[0-9][0-9_,:.]*$|^[-+]?0b[01_]+$|^[0-9]{4}-[0-9]{1,2}-[0-9]{1,2}([Tt ]|$)|^[-+]?\.(inf|Inf|INF|nan|NaN|NAN)$")]
    private static partial Regex YamlAmbiguous();

    static void YamlString(StringBuilder sb, string s)
    {
        if (PlainOk(s)) sb.Append(s);
        else YamlQuoted(sb, s);
    }

    static bool PlainOk(string s)
    {
        if (s.Length == 0 || char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1])) return false;
        if ("-?:,[]{}#&*!|>'\"%@`".Contains(s[0])) return false;
        if (s.EndsWith(':') || s.Contains(": ") || s.Contains(" #")) return false;
        for (int i = 0; i < s.Length; i++)
            if (s[i] < 0x20 || NeedsEscape(s, i)) return false;
        return !YamlAmbiguous().IsMatch(s) && CorePlain(s) is JsonValue jv && jv.GetValueKind() == JsonValueKind.String;
    }

    static bool NeedsEscape(string s, int i)
    {
        char c = s[i];
        if (char.IsHighSurrogate(c)) return i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]);
        if (char.IsLowSurrogate(c)) return i == 0 || !char.IsHighSurrogate(s[i - 1]);
        return c is < ' ' and not ('\t' or '\n') || c is >= '\u007f' and <= '\u009f' || c is '\ufeff' or '\u2028' or '\u2029' or '\ufffe' or '\uffff';
    }

    /// <summary>A multi-line string as a literal block (|, |- or |+), unless it needs escapes or starts with spaces.</summary>
    static bool LiteralOk(string s)
    {
        if (!s.Contains('\n') || s.Contains('\r') || s.All(c => c == '\n')) return false;
        if ((s.Split('\n').FirstOrDefault(l => l.Length > 0) ?? "").StartsWith(' ')) return false;
        for (int i = 0; i < s.Length; i++)
            if (NeedsEscape(s, i)) return false;
        return true;
    }

    static void YamlLiteral(StringBuilder sb, string s, int indent)
    {
        bool keep = s.EndsWith("\n\n"), clip = !keep && s.EndsWith('\n');
        sb.Append(keep ? "|+" : clip ? "|" : "|-").Append('\n');
        var lines = s.Split('\n');
        int count = s.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        for (int i = 0; i < count; i++)
        {
            if (lines[i].Length > 0) sb.Append(' ', indent).Append(lines[i]);
            sb.Append('\n');
        }
    }

    static void YamlQuoted(StringBuilder sb, string s)
    {
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\0': sb.Append("\\0"); break;
                default:
                    if (!NeedsEscape(s, i)) sb.Append(c);
                    else if (c <= 'ÿ') sb.Append("\\x").Append(((int)c).ToString("X2"));
                    else sb.Append("\\u").Append(((int)c).ToString("X4"));
                    break;
            }
        }
        sb.Append('"');
    }

    // ---------- TOML (Tomlyn) ----------

    [GeneratedRegex(@"^\((\d+),(\d+)\)\s*:\s*error\s*:\s*(.*)$", RegexOptions.Singleline)]
    private static partial Regex TomlError();

    static JsonNode? ParseToml(string text)
    {
        try { return FromToml(TomlSerializer.Deserialize<TomlTable>(text)); }
        catch (TomlException ex)
        {
            var m = TomlError().Match(ex.Message.Trim());
            throw m.Success ? Bad("TOML", long.Parse(m.Groups[1].Value), long.Parse(m.Groups[2].Value), m.Groups[3].Value.Trim())
                            : Bad("TOML", null, null, ex.Message);
        }
    }

    static JsonNode? FromToml(object? v) => v switch
    {
        null => null,
        TomlTable t => new JsonObject(t.Select(kv => KeyValuePair.Create(kv.Key, FromToml(kv.Value)))),
        TomlTableArray ta => new JsonArray(ta.Select(x => FromToml(x)).ToArray()),
        TomlArray a => new JsonArray(a.Select(FromToml).ToArray()),
        string s => s,
        bool b => b,
        long l => JsonValue.Create(l),
        // keep floats floats: 1.0 → 1.0, not 1; inf / nan have no JSON form and become text
        double d => double.IsFinite(d) ? NumberNode(TomlFloat(d)) : double.IsNaN(d) ? "nan" : d > 0 ? "inf" : "-inf",
        TomlDateTime dt => dt.ToString(),
        _ => System.Convert.ToString(v, Inv),
    };

    static string TomlFloat(double d)
    {
        var r = d.ToString("R", Inv);
        return r.Contains('.') || r.Contains('E') ? r : r + ".0";
    }

    static string Toml(JsonNode? tree)
    {
        // TOML is always a table at the top: a list goes under "items", a single value under "value"
        var root = tree as JsonObject ?? new JsonObject { [tree is JsonArray ? "items" : "value"] = tree?.DeepClone() };
        try { return TomlSerializer.Serialize(TomlTableOf(root, inline: false)); }
        catch (TomlException ex) when (ex.Message.Contains("depth")) { throw new InvalidDataException(L.En("TOML can't nest more than 64 levels deep")); }
    }

    static TomlTable TomlTableOf(JsonObject o, bool inline)
    {
        var t = new TomlTable(inline);
        foreach (var (k, v) in o)
            if (ToToml(v, inline) is { } x) t[k] = x;   // TOML has no null: such keys are left out
        return t;
    }

    static object? ToToml(JsonNode? n, bool inline)
    {
        switch (n)
        {
            case null:
                return null;
            case JsonObject o:
                return TomlTableOf(o, inline);
            case JsonArray a when !inline && a.Count > 0 && a.All(x => x is JsonObject):
            {
                var ta = new TomlTableArray();
                foreach (var x in a) ta.Add(TomlTableOf((JsonObject)x!, inline: false));
                return ta;
            }
            case JsonArray a:
            {
                // tables inside an array are written inline: {k = v}
                var arr = new TomlArray();
                foreach (var x in a)
                    if (ToToml(x, inline: true) is { } item) arr.Add(item);
                return arr;
            }
            default:
            {
                var v = (JsonValue)n;
                return v.GetValueKind() switch
                {
                    JsonValueKind.String => TomlString(v.GetValue<string>()),
                    JsonValueKind.Number => TomlNumber(v.ToJsonString()),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null,
                };
            }
        }
    }

    static object TomlNumber(string raw)
    {
        if (!raw.Contains('.') && !raw.Contains('e') && !raw.Contains('E'))
            return long.TryParse(raw, NumberStyles.AllowLeadingSign, Inv, out var l) ? l : raw;   // beyond 64 bits: keep the digits as text
        return double.TryParse(raw, NumberStyles.Float, Inv, out var d) && double.IsFinite(d) ? d : raw;
    }

    [GeneratedRegex(@"^([0-9]{4}-[0-9]{2}-[0-9]{2}(T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?(Z|[+-][0-9]{2}:[0-9]{2})?)?|[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?)$")]
    private static partial Regex TomlDateLike();

    /// <summary>Text that is exactly a TOML date / time (as TOML itself prints it) goes back as a TOML date, so dates survive TOML → JSON → TOML.</summary>
    static object TomlString(string s)
    {
        if (!TomlDateLike().IsMatch(s)) return s;
        try
        {
            if (TomlSerializer.Deserialize<TomlTable>("v = " + s)!["v"] is TomlDateTime dt && dt.ToString() == s) return dt;
        }
        catch (TomlException) { }
        return s;
    }

    // ---------- XML ----------
    //   <root>              → {"root": …}           repeated child elements → a list
    //   attributes          → "@name"               text next to attributes or children → "#text"
    //   <a/>                → null, <a></a> → ""     values are typed like CSV cells (numbers, true / false)
    //   prefixes            → "p:name", and xmlns declarations → "@xmlns" / "@xmlns:p", so namespaces survive the trip back

    static XmlReaderSettings XmlIn(bool keepComments, bool keepWhitespace) => new()
    {
        DtdProcessing = DtdProcessing.Parse,   // internal entities only: no resolver, and a cap against entity bombs
        XmlResolver = null,
        MaxCharactersFromEntities = 10_000_000,
        IgnoreWhitespace = !keepWhitespace,   // layout between elements; xml:space="preserve" content is kept either way
        IgnoreComments = !keepComments,
        IgnoreProcessingInstructions = !keepComments,
    };

    static XDocument LoadXml(string path, bool keepComments, bool keepWhitespace = false)
    {
        try
        {
            using var reader = XmlReader.Create(path, XmlIn(keepComments, keepWhitespace));
            return XDocument.Load(reader);
        }
        catch (XmlException ex) { throw Bad("XML", ex.LineNumber, ex.LinePosition, FirstXmlSentence(ex.Message)); }
    }

    static string FirstXmlSentence(string msg)
    {
        int cut = msg.IndexOf(". Line ", StringComparison.Ordinal);
        return cut > 0 ? msg[..cut] : msg.TrimEnd('.');
    }

    static JsonNode? ParseXml(string path)
    {
        var root = LoadXml(path, keepComments: false).Root ?? throw new InvalidDataException(L.En("The XML has no root element"));
        return new JsonObject { [XmlKey(root, root.Name)] = FromXml(root, 0) };
    }

    static string XmlKey(XElement scope, XName name)
    {
        var local = XmlConvert.DecodeName(name.LocalName);
        if (name.Namespace == XNamespace.None) return local;
        var prefix = name.Namespace == XNamespace.Xml ? "xml" : scope.GetPrefixOfNamespace(name.Namespace);
        return string.IsNullOrEmpty(prefix) ? local : prefix + ":" + local;
    }

    static JsonNode? FromXml(XElement e, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException(L.En("The {0} nests more than {1} levels deep", "XML", MaxDepth));
        var text = string.Concat(e.Nodes().OfType<XText>().Select(t => t.Value));
        if (!e.HasAttributes && !e.HasElements) return e.IsEmpty ? null : Infer(text);
        var o = new JsonObject();
        foreach (var a in e.Attributes())
        {
            if (a.IsNamespaceDeclaration) o[a.Name.Namespace == XNamespace.None ? "@xmlns" : "@xmlns:" + a.Name.LocalName] = a.Value;
            else o["@" + XmlKey(e, a.Name)] = Infer(a.Value);
        }
        foreach (var group in e.Elements().GroupBy(c => XmlKey(c, c.Name)))   // groups come in order of first appearance
            o[group.Key] = group.Count() == 1 ? FromXml(group.First(), depth + 1) : new JsonArray(group.Select(c => FromXml(c, depth + 1)).ToArray());
        if (!string.IsNullOrWhiteSpace(text)) o["#text"] = Infer(e.HasElements ? text.Trim() : text);
        return o;
    }

    static string Xml(JsonNode? tree)
    {
        // one key that isn't a list becomes the root element; anything else goes inside <root>
        var root = tree is JsonObject { Count: 1 } o && o.First() is var (key, value) && value is not JsonArray && !key.StartsWith('@') && key != "#text"
            ? XmlElement(key, value, XmlScope.Empty)
            : XmlElement("root", tree, XmlScope.Empty);
        return Utf8.GetString(SaveXml(new XDocument(new XDeclaration("1.0", "utf-8", null), root), indent: true));
    }

    static XElement XmlElement(string name, JsonNode? v, XmlScope scope)
    {
        var inner = v is JsonObject declares ? scope.With(declares) : scope;
        var el = new XElement(inner.Name(name, attribute: false));
        switch (v)
        {
            case null:
                break;   // <name />
            case JsonObject o:
                foreach (var (k, x) in o)
                {
                    if (k == "#text") el.Add(XmlText(x));
                    else if (k == "@xmlns") el.Add(new XAttribute("xmlns", XmlText(x)));
                    else if (k.StartsWith("@xmlns:", StringComparison.Ordinal)) el.Add(new XAttribute(XNamespace.Xmlns + k[7..], XmlText(x)));
                    else if (k.StartsWith('@')) el.Add(new XAttribute(inner.Name(k[1..], attribute: true), XmlText(x)));
                    else if (x is JsonArray items) foreach (var item in items) el.Add(XmlElement(k, item, inner));
                    else el.Add(XmlElement(k, x, inner));
                }
                break;
            case JsonArray a:
                foreach (var item in a) el.Add(XmlElement("item", item, inner));
                break;
            default:
                el.Add(XmlText(v));   // "" gives <name></name>
                break;
        }
        return el;
    }

    static string XmlText(JsonNode? v)
    {
        var s = v switch
        {
            null => "",
            JsonValue jv when jv.GetValueKind() == JsonValueKind.String => jv.GetValue<string>(),
            JsonValue jv => jv.ToJsonString(),
            _ => Json(v, indent: false),
        };
        // characters XML 1.0 can't hold at all (most control characters, broken surrogates) are dropped
        bool clean = true;
        for (int i = 0; i < s.Length && clean; i++)
            if (!XmlConvert.IsXmlChar(s[i]))
            {
                if (i + 1 < s.Length && XmlConvert.IsXmlSurrogatePair(s[i + 1], s[i])) i++;
                else clean = false;
            }
        if (clean) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (XmlConvert.IsXmlChar(s[i])) sb.Append(s[i]);
            else if (i + 1 < s.Length && XmlConvert.IsXmlSurrogatePair(s[i + 1], s[i])) sb.Append(s[i]).Append(s[++i]);
        }
        return sb.ToString();
    }

    static byte[] SaveXml(XDocument doc, bool indent)
    {
        var ms = new MemoryStream();
        var settings = new XmlWriterSettings { Indent = indent, IndentChars = "  ", NewLineChars = "\n", Encoding = Utf8, OmitXmlDeclaration = doc.Declaration == null };
        using (var w = XmlWriter.Create(ms, settings)) doc.Save(w);
        if (indent) ms.WriteByte((byte)'\n');
        return ms.ToArray();
    }

    /// <summary>Namespaces declared so far ("@xmlns", "@xmlns:p"), so "p:name" keys get their namespace back.</summary>
    sealed class XmlScope(XNamespace defaultNs, Dictionary<string, XNamespace> prefixes)
    {
        public static readonly XmlScope Empty = new(XNamespace.None, []);

        public XmlScope With(JsonObject o)
        {
            var d = defaultNs;
            Dictionary<string, XNamespace>? p = null;
            foreach (var (k, v) in o)
            {
                if (k == "@xmlns") d = XmlText(v);
                else if (k.StartsWith("@xmlns:", StringComparison.Ordinal)) (p ??= new(prefixes))[k[7..]] = XmlText(v);
            }
            return p == null && d == defaultNs ? this : new(d, p ?? prefixes);
        }

        public XName Name(string name, bool attribute)
        {
            int colon = name.IndexOf(':');
            if (colon > 0 && colon < name.Length - 1)
            {
                var prefix = name[..colon];
                var local = XmlConvert.EncodeLocalName(name[(colon + 1)..]);
                if (prefix == "xml") return XNamespace.Xml + local;
                if (prefixes.TryGetValue(prefix, out var ns)) return ns + local;
            }
            // anything XML names can't hold (spaces, a leading digit, an undeclared prefix) is escaped as _xHHHH_ and read back as it was
            var n = name.Length == 0 ? "_" : XmlConvert.EncodeLocalName(name);
            return attribute ? XName.Get(n) : defaultNs + n;
        }
    }

    // ---------- Pretty / Minify (JSON, XML and CSS) ----------

    public static bool CanReformat(string path) => Kinds.Ext(path) is "json" or "xml" or "css";

    public static Task Pretty(Job job) => Reformat(job, pretty: true);
    public static Task Minify(Job job) => Reformat(job, pretty: false);

    static async Task Reformat(Job job, bool pretty)
    {
        for (int i = 0; i < job.Inputs.Count; i++)
        {
            job.Token.ThrowIfCancellationRequested();
            job.Report(i, job.Inputs.Count, 0.1);
            var src = job.Inputs[i];
            var ext = Kinds.Ext(src);
            var bytes = await Task.Run(() => ext switch
            {
                "xml" => ReformatXml(src, pretty),
                "css" => Utf8.GetBytes(ReformatCss(File.ReadAllText(src), pretty)),
                _ => Utf8.GetBytes(ReformatJson(File.ReadAllBytes(src), pretty)),
            }, job.Token);
            var dst = Paths.Beside(src, ext, pretty ? "(pretty)" : "(min)");
            await File.WriteAllBytesAsync(dst, bytes, job.Token);
            job.Outputs.Add(dst);
        }
        job.Summary = Images.SizeSummary(job);
    }

    static byte[] ReformatXml(string path, bool pretty)
    {
        var doc = LoadXml(path, keepComments: true, keepWhitespace: true);
        int deepest = StripLayout(doc);
        if (pretty && deepest > MaxDepth) throw new InvalidDataException(L.En("The XML nests {0:N0} levels deep, too deep to indent (Minify still works)", deepest));
        return SaveXml(doc, pretty);
    }

    /// <summary>
    /// Drops layout whitespace (line breaks and indentation in elements that hold only child elements) and keeps everything
    /// else: mixed content (&lt;b&gt;big&lt;/b&gt; &lt;i&gt;world&lt;/i&gt;) and xml:space="preserve", where an empty
    /// text node also stops XmlWriter from indenting. Returns the deepest nesting level.
    /// </summary>
    static int StripLayout(XDocument doc)
    {
        foreach (var t in doc.Nodes().OfType<XText>().ToList()) t.Remove();
        if (doc.Root == null) return 0;
        int deepest = 0;
        var stack = new Stack<(XElement El, bool Preserve, int Depth)>();
        stack.Push((doc.Root, false, 1));
        while (stack.Count > 0)
        {
            var (el, inherited, depth) = stack.Pop();
            deepest = Math.Max(deepest, depth);
            var space = (string?)el.Attribute(XNamespace.Xml + "space");
            bool preserve = space == "preserve" || space != "default" && inherited;
            if (el.HasElements)
            {
                var texts = el.Nodes().OfType<XText>().ToList();
                // layout is whitespace with a line break; a lone space between elements (<i>x</i> <u>y</u>) is kept
                bool layout = !preserve && texts.All(t => t is not XCData && string.IsNullOrWhiteSpace(t.Value) && t.Value.AsSpan().IndexOfAny('\n', '\r') >= 0);
                if (layout) foreach (var t in texts) t.Remove();
                // XmlWriter only notices mixed content at its first text and would indent before a leading child: say so up front
                else if (el.FirstNode is not XText) el.AddFirst(new XText(""));
            }
            foreach (var child in el.Elements()) stack.Push((child, preserve, depth + 1));
        }
        return deepest;
    }

    /// <summary>Re-lays out JSON token by token: keys, duplicates, escapes and number text stay exactly as written; comments and trailing commas go.</summary>
    static string ReformatJson(byte[] utf8, bool pretty)
    {
        ReadOnlySpan<byte> data = utf8;
        if (data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) data = data[3..];
        var reader = new Utf8JsonReader(data, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 1024 });
        var sb = new StringBuilder(data.Length + data.Length / 4);
        int depth = 0;
        bool comma = false, afterKey = false;

        void Prefix()
        {
            if (afterKey) { afterKey = false; return; }
            if (comma) sb.Append(',');
            if (pretty && depth > 0) sb.Append('\n').Append(' ', depth * 2);
        }

        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject or JsonTokenType.StartArray:
                        Prefix();
                        sb.Append(reader.TokenType == JsonTokenType.StartObject ? '{' : '[');
                        depth++;
                        comma = false;
                        break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                        depth--;
                        if (comma && pretty) sb.Append('\n').Append(' ', depth * 2);   // nothing between the brackets: {} / []
                        sb.Append(reader.TokenType == JsonTokenType.EndObject ? '}' : ']');
                        comma = true;
                        break;
                    case JsonTokenType.PropertyName:
                        Prefix();
                        sb.Append('"').Append(Encoding.UTF8.GetString(reader.ValueSpan)).Append(pretty ? "\": " : "\":");
                        afterKey = true;
                        comma = false;
                        break;
                    default:
                        Prefix();
                        if (reader.TokenType == JsonTokenType.String) sb.Append('"').Append(Encoding.UTF8.GetString(reader.ValueSpan)).Append('"');
                        else sb.Append(Encoding.UTF8.GetString(reader.ValueSpan));
                        comma = true;
                        break;
                }
            }
        }
        catch (JsonException ex) { throw Bad("JSON", ex.LineNumber + 1, ex.BytePositionInLine + 1, FirstSentence(ex.Message)); }
        if (pretty) sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Re-lays out CSS changing nothing but whitespace and comments: strings, url(…), escapes, selectors, values and
    /// at-rules pass through as written. Minify drops comments (keeping /*! … */ licences) and the whitespace around
    /// { } ; , ) and after : and ( — never before ":" (a :hover ≠ a:hover) or "(" (and (…)), and never around + - in calc().
    /// Pretty puts each declaration on its own line, indents blocks and adds a space after "prop:" and commas.
    /// </summary>
    static string ReformatCss(string css, bool pretty)
    {
        var sb = new StringBuilder(css.Length + css.Length / 4);
        int n = css.Length, depth = 0, parens = 0;
        bool space = false;   // whitespace was skipped since the last output
        bool fresh = true;    // at the start of a line (Pretty) or of the output
        bool decl = false, colonDone = false;   // in a "prop: value" statement, before / after its colon

        void TrimEnd()
        {
            int end = sb.Length;
            while (end > 0 && sb[end - 1] is ' ' or '\n') end--;
            sb.Length = end;
        }

        void NewLine()
        {
            TrimEnd();
            if (sb.Length > 0) sb.Append('\n').Append(' ', Math.Min(depth, 32) * 2);   // capped: "{{{{…" mustn't blow up the output
            fresh = true;
            space = false;
        }

        void Put(string s)
        {
            if (space && !fresh && sb.Length > 0 && !(sb[^1] is '{' or '}' or ';' or '(' || !pretty && sb[^1] is ',' or ':')) sb.Append(' ');
            sb.Append(s);
            space = fresh = false;
        }

        // is the statement starting here a declaration (ends with ; or }) rather than a nested rule (has a { first)?
        bool DeclarationAhead(int k)
        {
            for (int p = 0; k < n; k++)
            {
                char c = css[k];
                if (c == '\\') k++;
                else if (c is '"' or '\'') k = StringEnd(k) - 1;
                else if (c == '/' && k + 1 < n && css[k + 1] == '*') k = CommentEnd(k) - 1;
                else if (c == '(') p++;
                else if (c == ')') p = Math.Max(0, p - 1);
                else if (p == 0 && c is ';' or '}') return true;
                else if (p == 0 && c == '{') return false;
            }
            return true;
        }

        int StringEnd(int k)   // index just past the string that starts at k
        {
            char q = css[k];
            for (k++; k < n; k++)
            {
                if (css[k] == '\\') k++;
                else if (css[k] == q) return k + 1;
                else if (css[k] == '\n') return k;   // unterminated: ends at the line break
            }
            return n;
        }

        int CommentEnd(int k)
        {
            int e = css.IndexOf("*/", k + 2, StringComparison.Ordinal);
            return e < 0 ? n : e + 2;
        }

        static bool Word(char c) => char.IsLetterOrDigit(c) || c is '-' or '_' || c > '\u007f';

        for (int i = 0; i < n;)
        {
            char c = css[i];
            if (c == '/' && i + 1 < n && css[i + 1] == '*')
            {
                int end = CommentEnd(i);
                var comment = css[i..end];
                i = end;
                if (!pretty && !comment.StartsWith("/*!", StringComparison.Ordinal))
                {
                    // dropping it must not glue two words into one token: a/**/b
                    if (!space && sb.Length > 0 && Word(sb[^1]) && i < n && (Word(css[i]) || css[i] is '.' or '%')) sb.Append("/**/");   // also 0/**/.5em
                    continue;
                }
                if (pretty && fresh) { sb.Append(comment); NewLine(); }
                else Put(comment);
                continue;
            }
            if (char.IsWhiteSpace(c)) { space = true; i++; continue; }
            if (c == '\\')
            {
                // an escape belongs to its token; a hex escape also owns one whitespace after it (".\31 0" is ".10")
                int j = i + 1;
                if (j < n && char.IsAsciiHexDigit(css[j]))
                {
                    while (j < n && j - i <= 6 && char.IsAsciiHexDigit(css[j])) j++;
                    if (j < n && css[j] == '\r' && j + 1 < n && css[j + 1] == '\n') j += 2;
                    else if (j < n && char.IsWhiteSpace(css[j])) j++;
                }
                else j = Math.Min(n, j + 1);
                Put(css[i..j]);
                i = j;
                continue;
            }
            if (c is '"' or '\'')
            {
                int end = StringEnd(i);
                Put(css[i..end]);
                i = end;
                continue;
            }
            if (c == '(' && !space && sb.Length >= 3 && sb.ToString(sb.Length - 3, 3).Equals("url", StringComparison.OrdinalIgnoreCase)
                && (sb.Length == 3 || !Word(sb[^4])))
            {
                // url(unquoted) is one token, spaces and all; url("…") is an ordinary function with a string
                int k = i + 1;
                while (k < n && char.IsWhiteSpace(css[k])) k++;
                if (k < n && css[k] is not ('"' or '\''))
                {
                    while (k < n && css[k] != ')') k += css[k] == '\\' ? 2 : 1;
                    k = Math.Min(n, k + 1);
                    Put(css[i..k]);
                    i = k;
                    continue;
                }
            }
            i++;
            switch (c)
            {
                case '{':
                    space = false;
                    if (pretty) { TrimEnd(); sb.Append(sb.Length > 0 ? " {" : "{"); }
                    else sb.Append('{');
                    depth++;
                    if (pretty) NewLine();
                    fresh = pretty;
                    decl = DeclarationAhead(i);
                    colonDone = false;
                    break;
                case '}':
                    space = false;
                    depth = Math.Max(0, depth - 1);
                    if (pretty)
                    {
                        TrimEnd();
                        sb.Append('\n').Append(' ', Math.Min(depth, 32) * 2).Append('}');
                        NewLine();
                        if (depth == 0) sb.Append('\n');   // a blank line between top-level rules
                    }
                    else sb.Append('}');
                    decl = depth > 0 && DeclarationAhead(i);
                    colonDone = false;
                    break;
                case ';':
                    space = false;
                    sb.Append(';');
                    fresh = false;
                    if (pretty && parens == 0) NewLine();
                    if (parens == 0) { decl = depth > 0 && DeclarationAhead(i); colonDone = false; }
                    break;
                case ',':
                    space = false;
                    sb.Append(',');
                    fresh = false;
                    space = pretty;   // Pretty: one space after commas
                    break;
                case ':':
                    bool propColon = decl && !colonDone && parens == 0;
                    if (propColon) space = false;   // "margin : 0" → "margin: 0"; in a selector the space would matter
                    Put(":");
                    if (propColon) { colonDone = true; space = pretty; }
                    break;
                case '(':
                    Put("(");
                    parens++;
                    break;
                case ')':
                    space = false;
                    sb.Append(')');
                    fresh = false;
                    parens = Math.Max(0, parens - 1);
                    break;
                default:
                    Put(c.ToString());
                    break;
            }
        }
        if (pretty) { TrimEnd(); sb.Append('\n'); }
        return sb.ToString();
    }
}
