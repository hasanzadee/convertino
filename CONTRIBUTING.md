# Contributing to Convertino

Thanks for wanting to make the flower bloom better. Bug reports, new petals, translations of the UI strings, a first report of the macOS build on a real Mac — all welcome.

## Layout

```
src/Convertino.Core      net10.0            petals catalog, converters, editor pixel ops, CLI — no UI
src/Convertino.Windows   net10.0-windows    WPF app (hooks, OLE drop target, flower, toasts, tray panel, editors)
src/Convertino.Linux     net10.0            Avalonia app (X11 input, XDND drop target, flower, toasts, tray, editors)
src/Convertino.Mac       net10.0            macOS app: Quartz input, AppKit window + glass, CoreGraphics PDF, Vision OCR;
                                        reuses Convertino.Linux's Overlay/FlowerView, Ui/ and Editors/ files
packaging/           installers, desktop entry, icons, file-manager integrations, Convertino.app bundling
tools/               dev helpers (x11-sandbox.sh, mac-smoke.sh)
```

The core never touches a UI toolkit. Each app implements `IPlatform` (PDF rendering, OCR, clipboard, office engine, editors, progress cards) and sets `Platform.Current` at startup.

The macOS app compiles the core's sources in instead of referencing `Convertino.Core`, so it can pick Magick.NET per architecture (15.x for Apple silicon, 14.17 — the last line with Intel-Mac natives — for x64). Files under `src/Convertino.Linux/{Overlay/FlowerView.cs,Ui,Editors}` and `Ipc.cs` build into both Avalonia apps: keep them free of X11 and AppKit calls.

## Build

```bash
dotnet build src/Convertino.Core
dotnet build src/Convertino.Linux                 # any OS
dotnet build src/Convertino.Mac                   # any OS (compiles; runs on macOS)
dotnet build src/Convertino.Mac -r osx-x64        # the Intel-Mac flavour
dotnet build src/Convertino.Windows               # Windows only (WPF)
```

Release builds the way CI does:

```bash
dotnet publish src/Convertino.Windows -c Release -r win-x64   --self-contained -o dist/win-x64
dotnet publish src/Convertino.Linux   -c Release -r linux-x64 --self-contained -o dist/linux-x64
packaging/macos/build-app.sh arm64            # on a Mac → dist/macos-arm64/Convertino.app (or x64)
```

## Adding a petal

1. Write the action in `Convertino.Core/Services` as `Task Run(Job job)`: read `job.Inputs`, write outputs with `Paths.Beside(src, ext, suffix)`, add them to `job.Outputs`, report progress with `job.Report(i, n, fraction)`, and optionally set `job.Summary` (shown on the result card).
2. Register it in `Convertino.Core/Catalog.cs` for the right `Kind` — `Fmt(...)` for a format petal, `Tool(...)` for a tool (with an icon key).
3. If it's a new icon key, add a glyph to `Convertino.Windows/Ui/Icons.cs` (Segoe Fluent Icons) and a 24×24 stroke path to `Convertino.Linux/Ui/Icons.cs` (used by macOS too).
4. Check it from the command line: `convertino run <id> some-file`.

Rings look best with 4–9 petals.

## Developer flags

Windows (`Convertino.exe`):

| Flag | Does |
|---|---|
| `--snapshot out.png [theme] [img\|tools\|more\|formats\|pdf] [hoverIndex]` | render one ring of the flower to a PNG (no drag needed); `img`, `tools`, `more` and `formats` are the four image rings |
| `--paging-selftest dir` | feed the hook callbacks wheel / Tab / Shift data and check the overlay cycles every ring and swallows only what it should; no hooks, no injected input, no window. Writes `results.txt` and a PNG per ring |
| `--panel out.png [theme] [demo] [recipes\|builder] [langs]` | render the tray panel to a PNG (`demo` fills the Activity card with sample data; `recipes` unfolds the Recipes card, `builder` also opens its builder, `langs` unfolds the Language picker) |
| `--clip-selftest dir` | feed synthetic clipboard payloads (PNG, 32/24-bit DIB, CF_HDROP, text, a link, a too-long link, JSON, spreadsheet cells, Office text + picture, password-manager text, empty) through the real reader, offers and text petals, plus a capture crop and the shortcut parsing; renders each bloomed flower (`clip-*.png`, `screenshot.png`) and writes `results.txt`. The system clipboard and the screen are never touched; captured files go to `dir/captures` |
| `--capture-selftest out.png` | render the area-capture overlay (mid-drag on a 1.5× monitor, idle on a 1× one) over a made-up desktop |
| `--run <actionId> files…` | run one action headless; result in the log |
| `--edit <crop\|addbg\|annotate\|redact\|adjust> file` | open an editor directly |
| `--edit-selftest file` | build every editor, simulate an edit, save |
| `--write-icon file.ico` · `--write-pngs dir` | regenerate the app icons |

Linux (`convertino`) environment variables:

| Variable | Does |
|---|---|
| `CONVERTINO_DEBUG=1` | log Shift / drag / XDND events |
| `CONVERTINO_PIN=x,y` | park the flower at a point (test drops onto a pre-mapped window) |
| `CONVERTINO_NOSHAPE=1` | don't apply the circular X input shape |
| `CONVERTINO_EDIT_SELFTEST=/path/img.png` | save every editor once and exit |
| `CONVERTINO_PANEL=1\|demo`, `CONVERTINO_PANEL_RECIPES=open\|builder` | open the panel at start (`demo`: sample activity), with the Recipes card unfolded / its builder open |

macOS (`Convertino.app/Contents/MacOS/convertino`) takes the same `CONVERTINO_DEBUG`, `CONVERTINO_PIN` and `CONVERTINO_EDIT_SELFTEST`, plus:

| Variable | Does |
|---|---|
| `CONVERTINO_PIN_FILE=/path/file` | with `CONVERTINO_PIN`, bloom the parked flower for that file (screenshots) |
| `CONVERTINO_NOVIBRANCY=1` | no native blur under the petals |
| `CONVERTINO_GLASS_MATERIAL=n` | `NSVisualEffectMaterial` for the glass (default 13, HUD window) |

All: `CONVERTINO_CUTOUT_CPU=1` forces the cut-out onto the CPU, `CONVERTINO_LANG=ru` shows the UI in that language whatever the settings say, `CONVERTINO_APPDATA=dir` (or `CONVERTINO_HOME=dir`) keeps settings, stats and the log in `dir` instead of the real ones (tests, screenshots). Logs: `%APPDATA%\Convertino\convertino.log`, `~/.config/convertino/convertino.log`, `~/Library/Application Support/Convertino/convertino.log`.

## Testing the Linux build

Nested or WSLg compositors often don't forward drag & drop between X11 clients. `tools/x11-sandbox.sh` starts a clean X11 desktop on `:99` (Xvfb + openbox + xcompmgr + PCManFM). Then:

```bash
DISPLAY=:99 CONVERTINO_DEBUG=1 ./dist/linux-x64/convertino &
# drag with xdotool: mousedown on a file, move, tap Shift, move to a petal, mouseup
DISPLAY=:99 xdotool mousemove 520 160 mousedown 1 mousemove 700 400 keydown Shift_L keyup Shift_L
DISPLAY=:99 import -window root /tmp/shot.png
```

## Testing the macOS build

`tools/mac-smoke.sh dist/macos-arm64/Convertino.app --gui` runs what CI runs: CLI conversions, CoreGraphics PDF rendering, Vision OCR, the editors' self-test, then the app itself — a pinned flower, `convertino menu` over IPC, and a synthetic drag → Shift → drop on a petal (`tools/mac-drag-test.swift`; the terminal needs Accessibility to post input). Screenshots and the log land in `./mac-smoke/`. `CUTOUT=1` adds Cut out (downloads the model). In CI it also sets a striped wallpaper, runs `install.sh` and the Finder Quick Action through `automator`, and uninstalls again; those steps are skipped on your own Mac. Every CI run uploads the screenshots, plus a zipped `Convertino.app`, as the `macos-arm64` / `macos-x64` artifacts — handy without a Mac.

What CI can't show is a real Finder drag with Shift (its synthetic drag starts from a test window). The glass does show: GitHub's macOS images ship with *Reduce transparency* on, which draws every `NSVisualEffectView` flat, so the script switches it off in CI. If you have one, `CONVERTINO_DEBUG=1 ~/Applications/Convertino.app/Contents/MacOS/convertino` logs every Shift / drag / drop step.

## Translating

The UI is translated gettext style: the English text in the code is the key. Strings go through `L.T("Drop on a petal")`, templates through `L.T("Convert to {0}", label)`, counts through `L.N(n, "{0} file", "{0} files")`; the petal factories in `Catalog.cs` (`Tool`, `Fmt`, `Pack`) translate their labels, verbs and hints themselves, so hand them the English as a literal (`L.T("Save {0} to Downloads", n)` when it needs a value — never `$"…"` or `+`). Exception messages stay English for the log: `throw new IOException(L.En("Part {0} of {1} is missing", n, name))`; the card shows them translated. A text without a translation shows in English, so a new string never breaks anything.

Translations live in `src/Convertino.Core/Lang/<code>.json` (embedded into the app):

```json
{
  "@name": "Deutsch",
  "Drop on a petal": "Auf ein Blütenblatt ziehen",
  "Convert to {0}": "In {0} konvertieren",
  "{0} file": ["{0} Datei", "{0} Dateien"]
}
```

- `@name` is the language's own name, shown in the **Language** picker; the file name is its code (`de`, `pt-BR`, `zh-Hans`), which also picks the culture for dates and numbers.
- Keep every `{0}`, `{1}` of the English; you may move them. `{0:l}` lowercases the first letter of the text put there ("Couldn't {0:l}" + "Convert to PNG"); drop the `:l` where your language shouldn't.
- A plural entry (the singular English of an `L.N` call is its key) lists the language's forms in CLDR order: one string for Japanese, Chinese, Turkish and Azerbaijani, `[one, other]` for most, `[one, few, many]` for Russian and Ukrainian. A new language with other rules needs a line in `L.Forms` / `L.Plural`.
- Keep it short: tool petals show one word in capitals, and the hub has room for about 20 characters of the hover verb.

To add a language, copy an existing file, translate every value and run

```bash
convertino lang-check        # inside the checkout (Convertino.exe lang-check on Windows; dotnet run --project src/Convertino.Linux -- lang-check on Linux)
```

It lists what each language is missing and exits with 1 on malformed JSON, keys whose English is gone from the code, placeholders that differ from the English, a counted (`L.N`) entry without all of its plural forms, or text that can never match a key (an interpolated or `+`-joined string handed to `L.T` / `L.N` / `L.En` or a petal factory). Missing strings, and factory arguments that come from variables, are only reported. To see the result: `CONVERTINO_LANG=de` (any platform) forces a language without touching your settings, and `Convertino.exe --panel out.png Mocha demo langs` / `--snapshot out.png Mocha tools 2` render the panel and the flower (Linux / macOS: `CONVERTINO_PANEL=langs`).

## Style

- Match the surrounding code: file-scoped namespaces, `var`, small static service classes, comments only where the *why* isn't obvious.
- Keep the core UI-free and cross-platform.
- No telemetry, no network calls except explicit user actions (model download, *Check for updates…*) and, if the user said yes on the one-time card, a daily check of the latest release on GitHub.

## Pull requests

Describe what changed and how you checked it (CLI run, screenshot of the flower, both platforms if you touched the core). CI builds Windows, Linux and macOS (Apple silicon and Intel) on every PR.
