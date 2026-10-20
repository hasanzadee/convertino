# Data formats

Convertino converts **JSON**, **JSON Lines** (`.jsonl`, `.ndjson`), **YAML** (`.yaml`, `.yml`), **TOML** and **XML** into one another, and to and from **CSV** and **XLSX**. CSV and XLSX also become **Markdown** and **HTML** tables, and JSON, XML and **CSS** get **Pretty** and **Minify**. Nothing needs Microsoft Office or LibreOffice, so these petals are there on every platform. The output goes beside the source like every other conversion (`config.yaml` → `config.json`).

| Files | Convert ring | Tools ring |
|---|---|---|
| JSON | YAML · TOML · XML · CSV · XLSX · JSONL | Pretty · Minify · Copy · Zip · Path · SHA-256 |
| JSON Lines | JSON · YAML · TOML · XML · CSV · XLSX | Copy · Zip · Path · SHA-256 |
| YAML / TOML | the other tree formats · CSV · XLSX | Copy · Zip · Path · SHA-256 |
| XML | JSON · YAML · TOML · CSV · XLSX | Pretty · Minify · Copy · Zip · Path · SHA-256 |
| CSV / XLSX | XLSX or CSV · HTML table · JSON · Markdown table (plus PDF and ODS with an office suite) | Zip · Path · SHA-256 |
| CSS | Pretty · Minify · Copy | Zip · Path · SHA-256 |

```bash
convertino convert config.yaml --to toml
convertino convert orders.json --to xlsx
convertino convert book.xlsx --to csv          # one CSV per sheet
convertino run pretty response.json            # → response (pretty).json
convertino run minify feed.xml                 # → feed (min).xml
convertino run minify site.css                 # → site (min).css
convertino convert sales.xlsx --to md          # → sales.md, one table per sheet
```

Everything goes through one in-memory JSON tree, so any format converts to any other with the same rules. They are listed below.

## JSON and JSON Lines

- Comments (`//`, `/* */`) and trailing commas are accepted when reading. A key that appears twice keeps its last value, like `JSON.parse`.
- A `.json` file that actually holds one value per line (NDJSON) is read as a list.
- Output is indented with two spaces. Non-ASCII text, emoji included, is written as is, not as `\u` escapes.
- Numbers keep the way they were written (`1.50` stays `1.50`, a 20-digit id isn't rounded).
- JSON Lines: one compact value per line. Reading gives a list; writing a list gives one line per item.

**Pretty** and **Minify** re-lay out the file token by token: keys, duplicate keys, escapes and number spelling are kept exactly. Only comments and trailing commas are dropped.

## YAML

- YAML is read with the **YAML 1.2 core schema**: `yes`, `no`, `on` and `off` stay text, and `2024-05-17` stays text. `0x1F`, `0o17` and `.5` are numbers.
- Anchors and aliases are expanded, and merge keys (`<<: *defaults`) are applied (keys stated in the mapping win).
- A file with several documents (`---`) becomes a list of documents.
- Tags: `!!str`, `!!int` and so on are honoured. Custom tags (`!Ref`, `!!binary` …) keep the value as text.
- When writing, text that a YAML 1.1 reader (PyYAML, js-yaml, Ruby) would take for something else is quoted: `"yes"`, `"NO"`, `"00123"`, `"1.10"`, `"2024-03-01"`, `"12:30"`. Multi-line text is written as a literal block (`|`, `|-` or `|+`). Lists under a key are indented.

## TOML

- A TOML document is always a table. A list at the top goes under `items` (`[[items]]`), and a single value under `value`.
- TOML has no `null`, so keys whose value is null are left out (and nulls inside lists are skipped).
- Lists of objects become arrays of tables (`[[products]]`). Objects inside other lists are written inline (`{ k = "v" }`).
- Dates and times become ISO 8601 text in JSON and YAML (`1979-05-27T07:32:00Z`). Going the other way, text that is exactly a TOML date or time becomes a TOML date again, so dates survive TOML → JSON → TOML.
- Integers are 64-bit. A larger integer is written as text so its digits aren't lost. Floats stay floats (`1.0`, not `1`). `inf` and `nan` become text in JSON.

## XML

These rules follow the common "Badgerfish-lite" convention (as in Newtonsoft.Json and xml2js):

| XML | JSON |
|---|---|
| `<catalog>…</catalog>` | `{"catalog": …}`: the root element is the single top-level key |
| `<book id="bk101">` | `"@id": "bk101"`: attributes get an `@` prefix |
| `<price currency="AZN">44.95</price>` | `{"@currency": "AZN", "#text": 44.95}`: text next to attributes or child elements goes in `#text` |
| `<tag>a</tag><tag>b</tag>` | `"tag": ["a", "b"]`: repeated elements become a list |
| `<note/>` / `<note></note>` | `null` / `""` |
| `<dc:title>` with `xmlns:dc="…"` | `"dc:title"`, plus `"@xmlns:dc": "…"`, so namespaces come back on the way to XML |
| `<![CDATA[…]]>` | plain text |

- XML has no types, so values are typed the way CSV cells are: `true` and `false` become booleans, and plain numbers become numbers when they have no leading zeros and at most 15 digits. `0012345678`, `+994…` and long ids stay text.
- Going to XML, a JSON object with a single key that isn't a list becomes the root element. Anything else is wrapped in `<root>`, and list items without a name become `<item>`. Names that XML can't hold (spaces, a leading digit) are escaped as `_x0020_` and unescaped when read back.
- What XML can't express: the order of differently named siblings (they are grouped by name), a list with one item (it reads back as a single value), an empty list (it disappears), and comments and processing instructions (dropped when converting; **Pretty** and **Minify** keep them).
- DOCTYPEs with internal entities are fine. External entities are never fetched, and entity expansion is capped, so "billion laughs" files are refused.

## CSV and XLSX

**Reading CSV.** The CSV can be UTF-8 (with or without a BOM) or UTF-16. A file that isn't valid UTF-8 is read in the system's ANSI code page, like Excel's legacy "CSV (comma delimited)". The separator (comma, semicolon, tab or pipe) is detected from the first record. Quoted fields may contain separators, `""` quotes and line breaks.

**Writing CSV.** Output is RFC 4180: comma-separated, CRLF line endings, UTF-8 with a BOM so Excel opens it as UTF-8. Fields are quoted only when they need it.

**XLSX** is read and written with MiniExcel, without an office suite.

- **Several sheets.**
  - XLSX → CSV writes one file per non-empty sheet: `book - Orders.csv`, `book - Customers.csv` (just `book.csv` when there is one sheet).
  - XLSX → JSON gives `{"Orders": […], "Customers": […]}` when there are several sheets, and a plain list for one.
  - JSON → XLSX does the reverse: an object whose values are all lists becomes one sheet per key.
  - CSV → XLSX makes one sheet named after the file.
- **Header row.** The first row names the keys. Empty names become the column letter (`C`), and repeated names get `_2`, `_3`. In XLSX output a clean first row is styled as a header, frozen, and given filters.
- **Typing CSV text for XLSX.**
  - Plain numbers become numbers when Excel can hold them exactly (at most 15 significant digits). `007`, `+1` and 16-digit ids stay text. XLSX keeps the number, not its spelling: `49.90` comes back as `49.9`.
  - `true` and `false` become booleans, and `yyyy-mm-dd` becomes a date. Everything else stays text, formulas too (`=1+2` is stored as text and never evaluated).
- **Typing CSV text for JSON.** Text is typed per column: a column becomes numbers (or booleans) only when every filled cell in it is one. A zip-code column holding `01234` and `10001` therefore stays text throughout. Empty cells become `null`.
- **XLSX cell values out.** Numbers are written as Excel shows them (15 significant digits). Dates become ISO 8601 (`2024-02-01`, or `2024-02-03 14:30:00` in CSV and `2024-02-03T14:30:00` in JSON). Booleans become `true` / `false`. Formula cells give their last calculated value.
- **JSON → CSV / XLSX.**
  - The JSON should be a list of objects, but wrappers such as `{"data": {"users": […]}}` are looked through.
  - An object that holds lists of objects next to other fields gives one table per such list, without the other fields. Examples: an API response `{"total": 2, "results": […]}`, or an XML root with attributes and repeated children.
  - Any other object becomes a single row.
  - Columns are the union of all keys, in order of first appearance. Nested objects flatten to dotted columns (`supplier.address.city`). Lists inside a row are kept as JSON text (`["kitchen","gift"]`).
  - CSV / XLSX → JSON gives flat objects. Dotted column names are not turned back into nested objects.

**With Microsoft Office or LibreOffice present**, CSV ⇄ XLSX, → JSON and the HTML and Markdown tables still use the engine above, because it keeps UTF-8 and every sheet. Excel itself reads a CSV without a BOM in the ANSI code page and saves only the active sheet to CSV. The office suite still handles PDF and ODS, and conversions from `.xls` and `.ods`.

## Markdown and HTML tables

CSV and XLSX also convert to a **Markdown** table (`.md`) or an **HTML** page (`.html`), both UTF-8.

- **Several sheets.** An XLSX with several sheets gives one file with one table per non-empty sheet, each under its sheet name (`## Orders` in Markdown, `<h2>` in HTML). A single sheet or a CSV gives just the table.
- The first row is the header. Columns where every filled cell is a number are right-aligned (`---:` in Markdown).
- **Markdown** uses GitHub-style pipe tables, padded so they also read well as plain text.
  - Markdown punctuation in cells is backslash-escaped: `\|`, `\*`, `\_`, `` \` ``, `\[`, `\]`, `\<`, `\>`, `\~`, `\&` and `\\`. A cell such as `Pipe | test` or `<b>` therefore shows exactly as written.
  - Line breaks inside a cell become `<br>`.
- **HTML** is a complete page with a small built-in stylesheet: borders, a sticky header and dark mode.
  - Only `& < > " '` are escaped. Non-ASCII text and emoji stay as UTF-8 characters, not entities.
  - Line breaks inside a cell become `<br>`.

## CSS: Pretty and Minify

Both tools change **only whitespace and comments**. Selectors, values, strings, `url(…)`, escapes and at-rules pass through exactly as written. I verified this in Chromium: the original, the pretty and the minified file give the same rules and the same computed styles.

- **Minify** removes comments, but keeps `/*! … */` licence comments. It also removes the whitespace around `{ } ; , )` and after `:` and `(`.
  - It never removes whitespace before `:`: `a :hover` (any hovered element inside `a`) is not `a:hover`. The one exception is a declaration's own colon (`margin : 0` → `margin:0`).
  - It never removes whitespace before `(` (`and (min-width…)`), or around `+ - > ~` (`calc(1px + 2px)` needs it).
  - When removing a comment would glue two words into one token, as in `a/**/b`, it leaves `/**/`.
- **Pretty** puts each declaration on its own line, indents nested blocks (`@media`, `@supports`, CSS nesting), separates top-level rules with a blank line, and adds a space after `prop:` and after commas. Comments stay where they were.

