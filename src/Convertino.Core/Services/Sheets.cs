using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MiniExcelLibs;
using MiniExcelLibs.OpenXml;

namespace Convertino.Services;

/// <summary>CSV and XLSX without an office suite (MiniExcel reads and writes XLSX), and the bridge between sheets and JSON trees.</summary>
public static partial class Sheets
{
    /// <summary>
    /// One sheet as rows of cells. Cells are strings when read from CSV (<see cref="Typed"/> false: numbers are
    /// recognised on the way out), and string, double, bool or DateTime from XLSX, or <see cref="Num"/> from JSON.
    /// </summary>
    public sealed record Grid(string Name, List<object?[]> Rows, bool Typed);

    /// <summary>A JSON number kept as its source text, so 1.50 or a 20-digit id isn't rounded on the way through.</summary>
    public readonly record struct Num(string Raw);

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static bool IsSheet(string path) => Kinds.Ext(path) is "csv" or "xlsx" or "xlsm";

    public static List<Grid> Read(string path) => Kinds.Ext(path) == "csv" ? [ReadCsv(path)] : ReadXlsx(path);

    /// <summary>Writes one XLSX (a sheet per grid) or CSV files (one per non-empty sheet: "book - Sheet2.csv"). Returns a line for the result card.</summary>
    public static string Write(List<Grid> grids, string src, string to, List<string> outputs)
    {
        if (grids.Count == 0) grids = [new Grid(Path.GetFileNameWithoutExtension(src), [], true)];
        if (to is "md" or "html")
        {
            // one file; with several sheets, one table per non-empty sheet under its name
            var tables = grids.Where(g => g.Rows.Count > 0).ToList();
            var dst = Paths.Beside(src, to);
            File.WriteAllText(dst, to == "md" ? MarkdownTables(tables) : HtmlTables(tables, Path.GetFileNameWithoutExtension(src)), new UTF8Encoding(false));
            outputs.Add(dst);
            return Note(tables.Count > 0 ? tables : grids);
        }
        if (to == "xlsx")
        {
            var dst = Paths.Beside(src, "xlsx");
            WriteXlsx(grids, dst);
            outputs.Add(dst);
            return Note(grids);
        }
        var sheets = grids.Where(g => g.Rows.Count > 0).ToList();
        if (sheets.Count == 0) sheets = [new Grid(Path.GetFileNameWithoutExtension(src), [], true)];
        foreach (var g in sheets)
        {
            var dst = sheets.Count == 1 ? Paths.Beside(src, "csv") : Paths.Beside(src, "csv", "- " + g.Name);
            WriteCsv(g, dst);
            outputs.Add(dst);
        }
        return Note(sheets);
    }

    static string Note(List<Grid> grids)
    {
        int rows = grids.Sum(g => Math.Max(0, g.Rows.Count - 1));
        var r = rows == 1 ? "1 row" : $"{rows:N0} rows";
        return grids.Count == 1 ? r : $"{grids.Count} sheets · {r}";
    }

    // ---------- CSV ----------

    static Grid ReadCsv(string path)
    {
        var text = ReadText(path);
        return new Grid(Path.GetFileNameWithoutExtension(path), ParseCsv(text, Delimiter(text)), Typed: false);
    }

    /// <summary>UTF-8 (or UTF-16 with a BOM); a file that isn't valid UTF-8 is read in the system's ANSI code page, like Excel's legacy "CSV (comma delimited)".</summary>
    static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            using var r = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            return r.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            int cp = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            return Encoding.GetEncoding(cp is 0 or 65001 ? 1252 : cp).GetString(bytes);
        }
    }

    /// <summary>The separator used in the first record: comma, semicolon (Excel in many locales), tab or pipe.</summary>
    static char Delimiter(string text)
    {
        var counts = new Dictionary<char, int> { [','] = 0, [';'] = 0, ['\t'] = 0, ['|'] = 0 };
        bool quoted = false;
        foreach (var c in text.AsSpan(0, Math.Min(text.Length, 65536)))
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && c is '\n' or '\r') break;
            else if (!quoted && counts.ContainsKey(c)) counts[c]++;
        }
        var best = counts.MaxBy(kv => kv.Value);
        return best.Value > 0 ? best.Key : ',';
    }

    /// <summary>RFC 4180, leniently: quoted fields may hold separators, quotes ("") and line breaks; CRLF, LF or CR end a record.</summary>
    static List<object?[]> ParseCsv(string text, char sep)
    {
        var rows = new List<object?[]>();
        var row = new List<object?>();
        var field = new StringBuilder();
        bool quoted = false, pending = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"' && field.Length == 0) { quoted = true; pending = true; }
            else if (c == sep) { row.Add(field.ToString()); field.Clear(); pending = true; }
            else if (c is '\r' or '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row.ToArray());
                row.Clear();
                pending = false;
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else { field.Append(c); pending = true; }
        }
        if (pending || field.Length > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }
        return rows;
    }

    /// <summary>Comma-separated, CRLF, UTF-8 with a BOM (so Excel opens it as UTF-8, not in the ANSI code page).</summary>
    static void WriteCsv(Grid g, string dst)
    {
        var sb = new StringBuilder();
        foreach (var row in g.Rows)
        {
            for (int c = 0; c < row.Length; c++)
            {
                if (c > 0) sb.Append(',');
                var s = Text(row[c]);
                if (s.AsSpan().IndexOfAny("\",\r\n") >= 0 || s.Length > 0 && (s[0] == ' ' || s[^1] == ' '))
                    sb.Append('"').Append(s.Replace("\"", "\"\"")).Append('"');
                else sb.Append(s);
            }
            sb.Append("\r\n");
        }
        File.WriteAllText(dst, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>A cell as CSV text: numbers as Excel shows them (15 significant digits), dates as ISO 8601, booleans as true / false.</summary>
    public static string Text(object? v) => v switch
    {
        null => "",
        string s => s,
        Num n => n.Raw,
        bool b => b ? "true" : "false",
        double d => Number(d),
        DateTime t => Stamp(t, ' '),
        IFormattable f => f.ToString(null, Inv),
        _ => v.ToString() ?? "",
    };

    static string Number(double d) =>
        d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(Inv) : d.ToString("G15", Inv);

    static string Stamp(DateTime t, char sep) =>
        t.TimeOfDay == TimeSpan.Zero ? t.ToString("yyyy-MM-dd", Inv)
        : t.ToString(t.Millisecond == 0 ? $"yyyy-MM-dd'{sep}'HH:mm:ss" : $"yyyy-MM-dd'{sep}'HH:mm:ss.fff", Inv);

    // ---------- Markdown / HTML tables ----------

    /// <summary>GitHub-flavoured pipe tables: the first row is the header, number columns are right-aligned, cells are escaped.</summary>
    static string MarkdownTables(List<Grid> tables)
    {
        var sb = new StringBuilder();
        foreach (var g in tables)
        {
            if (tables.Count > 1) sb.Append("## ").Append(MdCell(g.Name)).Append("\n\n");
            int width = Math.Max(1, g.Rows.Max(r => r.Length));
            var cells = g.Rows.Select(r => Enumerable.Range(0, width).Select(c => MdCell(Text(c < r.Length ? r[c] : null))).ToArray()).ToList();
            var right = Enumerable.Range(0, width).Select(c => NumberColumn(g, c)).ToArray();
            // columns are padded for reading as plain text, up to 40 characters
            var w = Enumerable.Range(0, width).Select(c => Math.Clamp(cells.Max(r => r[c].Length), 3, 40)).ToArray();
            for (int r = 0; r < cells.Count; r++)
            {
                sb.Append('|');
                for (int c = 0; c < width; c++)
                {
                    var s = cells[r][c];
                    var pad = new string(' ', Math.Max(0, w[c] - s.Length));
                    sb.Append(' ').Append(right[c] ? pad + s : s + pad).Append(" |");
                }
                sb.Append('\n');
                if (r > 0) continue;
                sb.Append('|');
                for (int c = 0; c < width; c++) sb.Append(' ').Append(right[c] ? new string('-', w[c] - 1) + ":" : new string('-', w[c])).Append(" |");
                sb.Append('\n');
            }
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>A cell for a pipe table: Markdown punctuation is backslash-escaped (| included), line breaks become &lt;br&gt;.</summary>
    static string MdCell(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '\\' or '|' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '~' or '&') sb.Append('\\').Append(c);
            else if (c == '\r') { sb.Append("<br>"); if (i + 1 < s.Length && s[i + 1] == '\n') i++; }
            else if (c == '\n') sb.Append("<br>");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    const string TableCss = """
        body{font-family:system-ui,'Segoe UI',Roboto,sans-serif;margin:32px;color:#1f2328;background:#fff}
        h2{font-size:1.15em;margin:28px 0 10px}
        table{border-collapse:collapse;font-size:14px}
        th,td{border:1px solid #d1d9e0;padding:6px 12px;text-align:left;vertical-align:top}
        th{background:#f6f8fa;position:sticky;top:0}
        tbody tr:nth-child(2n) td{background:#f9fafb}
        .n{text-align:right;font-variant-numeric:tabular-nums}
        @media (prefers-color-scheme:dark){body{color:#e6edf3;background:#0d1117}th,td{border-color:#30363d}th{background:#161b22}tbody tr:nth-child(2n) td{background:#11161d}}
        """;

    /// <summary>A UTF-8 page with one &lt;table&gt; per sheet (a heading each when there are several); text is HTML-escaped.</summary>
    static string HtmlTables(List<Grid> tables, string title)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>")
          .Append(Html(title, breaks: false)).Append("</title>\n<style>\n").Append(TableCss).Append("\n</style>\n</head>\n<body>\n");
        foreach (var g in tables)
        {
            if (tables.Count > 1) sb.Append("<h2>").Append(Html(g.Name, breaks: false)).Append("</h2>\n");
            int width = Math.Max(1, g.Rows.Max(r => r.Length));
            var right = Enumerable.Range(0, width).Select(c => NumberColumn(g, c)).ToArray();
            sb.Append("<table>\n");
            for (int r = 0; r < g.Rows.Count; r++)
            {
                var tag = r == 0 ? "th" : "td";
                if (r == 0) sb.Append("<thead>\n");
                sb.Append("<tr>");
                for (int c = 0; c < width; c++)
                    sb.Append('<').Append(tag).Append(right[c] ? " class=\"n\">" : ">")
                      .Append(Html(Text(c < g.Rows[r].Length ? g.Rows[r][c] : null), breaks: true)).Append("</").Append(tag).Append('>');
                sb.Append("</tr>\n");
                if (r == 0) sb.Append("</thead>\n<tbody>\n");
            }
            sb.Append("</tbody>\n</table>\n");
        }
        return sb.Append("</body>\n</html>\n").ToString();
    }

    /// <summary>&amp; &lt; &gt; &quot; &#39; escaped, everything else (Unicode, emoji) as it is.</summary>
    static string Html(string s, bool breaks)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            switch (s[i])
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                case '\r' when breaks: sb.Append("<br>"); if (i + 1 < s.Length && s[i + 1] == '\n') i++; break;
                case '\n' when breaks: sb.Append("<br>"); break;
                default: sb.Append(s[i]); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Every filled cell below the header is a number (and there is at least one).</summary>
    static bool NumberColumn(Grid g, int c)
    {
        bool any = false;
        foreach (var row in g.Rows.Skip(1))
        {
            var v = c < row.Length ? row[c] : null;
            if (Blank(v)) continue;
            if (!(v is double or Num || v is string s && Numeric().IsMatch(s))) return false;
            any = true;
        }
        return any;
    }

    // ---------- XLSX (MiniExcel) ----------

    static List<Grid> ReadXlsx(string path)
    {
        var grids = new List<Grid>();
        foreach (var name in MiniExcel.GetSheetNames(path))
        {
            var rows = new List<object?[]>();
            foreach (IDictionary<string, object?> r in MiniExcel.Query(path, useHeaderRow: false, sheetName: name))
                rows.Add(r.Values.ToArray());
            while (rows.Count > 0 && rows[^1].All(Blank)) rows.RemoveAt(rows.Count - 1);
            grids.Add(new Grid(name, rows, Typed: true));
        }
        return grids;
    }

    static void WriteXlsx(List<Grid> grids, string dst)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < grids.Count; i++)
        {
            var g = grids[i];
            // a clean first row (non-empty, unique names, as wide as the data) becomes a styled, frozen header with filters
            bool header = g.Rows.Count > 1 && IsHeader(g);
            int width = Math.Max(1, g.Rows.Count == 0 ? 0 : g.Rows.Max(r => r.Length));
            var keys = header ? g.Rows[0].Select(Text).ToArray() : Enumerable.Range(0, width).Select(c => "c" + c).ToArray();
            var rows = new List<IDictionary<string, object?>>();
            for (int r = header ? 1 : 0; r < g.Rows.Count; r++)
            {
                var d = new Dictionary<string, object?>(width);
                for (int c = 0; c < width; c++)
                {
                    var v = c < g.Rows[r].Length ? g.Rows[r][c] : null;
                    d[keys[c]] = r == 0 ? (v is string ? v : Cell(v, g.Typed)) : Cell(v, g.Typed);   // header text stays text
                }
                rows.Add(d);
            }
            if (rows.Count == 0) rows.Add(new Dictionary<string, object?> { [keys[0]] = null });
            // the default table style also keeps the stylesheet readable by openpyxl / pandas (its "None" style isn't)
            var config = new OpenXmlConfiguration
            {
                FastMode = true,
                EnableAutoWidth = true,
                TrimColumnNames = false,
                AutoFilter = header,
                FreezeRowCount = header ? 1 : 0,
                TableStyles = TableStyles.Default,
            };
            var name = SheetName(g.Name, used);
            if (i == 0) MiniExcel.SaveAs(dst, rows, printHeader: header, sheetName: name, configuration: config);
            else MiniExcel.Insert(dst, rows, sheetName: name, configuration: config, printHeader: header);
        }
    }

    static bool IsHeader(Grid g)
    {
        var names = g.Rows[0].Select(Text).ToList();
        return names.All(n => n.Trim().Length > 0) && names.Distinct().Count() == names.Count && g.Rows.All(r => r.Length <= names.Count);
    }

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}$")] private static partial Regex IsoDate();
    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][-+]?[0-9]+)?$")] private static partial Regex Numeric();

    /// <summary>
    /// What goes into an XLSX cell. Text from CSV becomes a number when it is a plain number Excel can hold exactly
    /// (42, 3.14, 49.90 — not 007, +1 or a 16-digit id), true / false a boolean; a yyyy-mm-dd date (from CSV or JSON) becomes
    /// a date; everything else stays text.
    /// </summary>
    static object? Cell(object? v, bool typed)
    {
        switch (v)
        {
            case string s when !typed:
                if (s.Length == 0) return null;
                if (s is "true" or "false") return s == "true";
                return AsNumber(s) ?? AsDate(s) ?? (object)s;
            case string s:
                return AsDate(s) ?? (object)s;
            case Num n:
                return AsNumber(n.Raw) ?? (object)n.Raw;
            default:
                return v;
        }
    }

    // at most 15 significant digits: what Excel keeps and shows
    static object? AsNumber(string s) =>
        s.Length <= 32 && Numeric().IsMatch(s) && double.TryParse(s, NumberStyles.Float, Inv, out var d)
        && double.TryParse(Number(d), NumberStyles.Float, Inv, out var back) && back == d ? d : null;

    static object? AsDate(string s) =>
        IsoDate().IsMatch(s) && DateTime.TryParseExact(s, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var t) ? t : null;

    /// <summary>Excel's rules: at most 31 characters, none of : \ / ? * [ ], not "History", unique.</summary>
    static string SheetName(string name, HashSet<string> used)
    {
        var s = new string(name.Select(c => c is ':' or '\\' or '/' or '?' or '*' or '[' or ']' ? '_' : c).ToArray()).Trim().Trim('\'');
        if (s.Length == 0) s = "Sheet";
        if (s.Equals("History", StringComparison.OrdinalIgnoreCase)) s += "_";
        if (s.Length > 31) s = s[..31];
        var unique = s;
        for (int i = 2; !used.Add(unique); i++)
        {
            var tail = $" ({i})";
            unique = (s.Length + tail.Length > 31 ? s[..(31 - tail.Length)] : s) + tail;
        }
        return unique;
    }

    static bool Blank(object? v) => v is null || v is string { Length: 0 };

    // ---------- sheets ⇄ JSON ----------

    /// <summary>
    /// Rows of objects for CSV / XLSX. A list becomes one sheet. An object whose values are all lists becomes one sheet
    /// per key (the shape XLSX → JSON writes for several sheets); an object holding lists of objects next to other fields
    /// (an API response with "results", an XML root with attributes) gives a sheet per such list; any other object is one row.
    /// Wrappers like {"data": {"users": […]}} are looked through. Nested objects flatten to "a.b" columns, lists inside a row stay JSON text.
    /// </summary>
    public static List<Grid> FromTree(JsonNode? root, string name)
    {
        var node = root;
        while (node is JsonObject { Count: 1 } one && one.First().Value is JsonObject inner) node = inner;
        if (node is JsonObject o && o.Count > 0)
        {
            if (o.All(kv => kv.Value is JsonArray))
                return o.Select(kv => Table(kv.Key, (JsonArray)kv.Value!)).ToList();
            var records = o.Where(kv => kv.Value is JsonArray { Count: > 0 } a && a.All(x => x is JsonObject)).ToList();
            if (records.Count > 0)
                return records.Select(kv => Table(kv.Key, (JsonArray)kv.Value!)).ToList();
        }
        return [Table(name, node is JsonArray list ? list : new[] { node })];
    }

    static Grid Table(string name, IEnumerable<JsonNode?> items)
    {
        var columns = new List<string>();
        var seen = new HashSet<string>();
        var records = new List<Dictionary<string, object?>>();
        foreach (var item in items)
        {
            var rec = new Dictionary<string, object?>();
            if (item is JsonObject o) Flatten(o, "", rec);
            else rec["value"] = TableCell(item);
            foreach (var k in rec.Keys)
                if (seen.Add(k)) columns.Add(k);
            records.Add(rec);
        }
        var rows = new List<object?[]>();
        if (records.Count > 0)
        {
            rows.Add(columns.ToArray<object?>());
            foreach (var rec in records) rows.Add(columns.Select(c => rec.GetValueOrDefault(c)).ToArray());
        }
        return new Grid(name, rows, Typed: true);
    }

    static void Flatten(JsonObject o, string prefix, Dictionary<string, object?> rec)
    {
        foreach (var (k, v) in o)
        {
            if (v is JsonObject { Count: > 0 } inner) Flatten(inner, prefix + k + ".", rec);
            else rec[prefix + k] = TableCell(v);
        }
    }

    static object? TableCell(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.Number => new Num(v.ToJsonString()),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        },
        _ => Data.Json(n, indent: false),
    };

    /// <summary>The first row names the keys. One sheet → a list of objects; several → {"Sheet1": […], "Sheet2": […]}.</summary>
    public static JsonNode ToTree(List<Grid> grids)
    {
        if (grids.Count == 1) return Records(grids[0]);
        var o = new JsonObject();
        foreach (var g in grids) o[g.Name] = Records(g);
        return o;
    }

    static JsonArray Records(Grid g)
    {
        var list = new JsonArray();
        if (g.Rows.Count == 0) return list;
        int width = g.Rows.Max(r => r.Length);
        object? At(object?[] row, int c) => c < row.Length ? row[c] : null;
        // columns with neither a name nor any value (stray trailing separators) are dropped
        var cols = Enumerable.Range(0, width).Where(c => !Blank(At(g.Rows[0], c)) || g.Rows.Skip(1).Any(r => !Blank(At(r, c)))).ToList();
        var names = new List<string>();
        foreach (var c in cols)
        {
            var n = Text(At(g.Rows[0], c));
            if (n.Length == 0) n = Column(c);
            var unique = n;
            for (int i = 2; names.Contains(unique); i++) unique = $"{n}_{i}";
            names.Add(unique);
        }
        // CSV text is typed per column: numbers or true / false only when every filled cell of the column is one,
        // so a zip-code column with 01234 and 10001 stays text throughout
        var infer = cols.Select(c => g.Rows.Skip(1).All(r => At(r, c) is not string { Length: > 0 } s || Data.Infer(s).GetValueKind() != JsonValueKind.String)).ToList();
        foreach (var row in g.Rows.Skip(1))
        {
            if (row.All(Blank)) continue;
            var o = new JsonObject();
            for (int i = 0; i < cols.Count; i++) o[names[i]] = TreeCell(At(row, cols[i]), g.Typed, infer[i]);
            list.Add(o);
        }
        return list;
    }

    static JsonNode? TreeCell(object? v, bool typed, bool infer) => v switch
    {
        null => null,
        string { Length: 0 } when !typed => null,
        string s when !typed => infer ? Data.Infer(s) : s,
        string s => s,
        bool b => b,
        double d => Data.NumberNode(Number(d)),
        Num n => Data.NumberNode(n.Raw),
        DateTime t => Stamp(t, 'T'),
        _ => Text(v),
    };

    /// <summary>0 → "A", 26 → "AA".</summary>
    static string Column(int i)
    {
        var s = "";
        for (i++; i > 0; i = (i - 1) / 26) s = (char)('A' + (i - 1) % 26) + s;
        return s;
    }
}
