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
| `--snapshot out.png [theme] [img\|tools\|pdf] [hoverIndex]` | render the flower to a PNG (no drag needed) |
| `--panel out.png [theme] [demo]` | render the tray panel to a PNG (`demo` fills the Activity card with sample data) |
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

macOS (`Convertino.app/Contents/MacOS/convertino`) takes the same `CONVERTINO_DEBUG`, `CONVERTINO_PIN` and `CONVERTINO_EDIT_SELFTEST`, plus:

| Variable | Does |
|---|---|
| `CONVERTINO_PIN_FILE=/path/file` | with `CONVERTINO_PIN`, bloom the parked flower for that file (screenshots) |
| `CONVERTINO_NOVIBRANCY=1` | no native blur under the petals |
| `CONVERTINO_GLASS_MATERIAL=n` | `NSVisualEffectMaterial` for the glass (default 13, HUD window) |

All: `CONVERTINO_CUTOUT_CPU=1` forces the cut-out onto the CPU. Logs: `%APPDATA%\Convertino\convertino.log`, `~/.config/convertino/convertino.log`, `~/Library/Application Support/Convertino/convertino.log`.

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

## Style

- Match the surrounding code: file-scoped namespaces, `var`, small static service classes, comments only where the *why* isn't obvious.
- Keep the core UI-free and cross-platform.
- No telemetry, no network calls except explicit user actions (model download, *Check for updates…*) and, if the user said yes on the one-time card, a daily check of the latest release on GitHub.

## Pull requests

Describe what changed and how you checked it (CLI run, screenshot of the flower, both platforms if you touched the core). CI builds Windows, Linux and macOS (Apple silicon and Intel) on every PR.
