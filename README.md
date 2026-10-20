<div align="center">

<img src="packaging/linux/icons/convertino-256.png" width="112" alt="Convertino logo" />

# Convertino

**Drag a file. Tap Shift. Pick a petal.**

A radial converter & quick editor for Windows, Linux and macOS. Start dragging any file, tap <kbd>Shift</kbd> (or the key you pick: <kbd>Shift</kbd> twice, <kbd>Ctrl</kbd>, <kbd>Alt</kbd>), and a glass flower blooms under your cursor. Drop the file on a petal and the result lands right next to the original.

[![Build](https://github.com/hasanzadee/convertino/actions/workflows/ci.yml/badge.svg)](https://github.com/hasanzadee/convertino/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/hasanzadee/convertino?color=cba6f7&label=release)](https://github.com/hasanzadee/convertino/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-f5c2e7.svg)](LICENSE)
![Platforms](https://img.shields.io/badge/platforms-Windows%2010%2F11%20%C2%B7%20Linux%20(X11)%20%C2%B7%20macOS%2013%2B-89b4fa)

<img src="docs/demo.gif" width="860" alt="Dragging an image, tapping Shift: the Convertino flower opens, Shift flips to the Tools ring, dropping on WEBP converts it" />

</div>

---

## Why

Converting a file usually means opening an app, finding *Export…*, picking a format, picking a folder. Convertino turns that into one gesture you're already halfway through: you were dragging the file anyway.

- **No windows to open.** The flower appears where your cursor is, and only while you drag.
- **Nothing to configure.** The output goes beside the source (`photo.heic` → `photo.jpg`), never overwriting anything.
- **Offline.** Every conversion, OCR and the AI cut-out run on your machine.
- **Fast.** Most image conversions finish in under 100 ms. Long jobs (video, office documents) show a progress card you can cancel.

## Highlights

| | |
|---|---|
| ✿ **Rings** | **Convert** (formats), **Tools** (editors & actions) and, for images, **More tools** (one tap: Half, Full HD, B&W) and **More formats** (TIFF, ICO, BMP, JPEG XL, QOI…). Tap the key again, press <kbd>Tab</kbd> or turn the mouse wheel mid-drag to flip between them. |
| 🖼 **Images** | PNG · JPG · WEBP · AVIF · GIF · PDF · TIFF · ICO · BMP · JPEG XL · QOI, from anything ImageMagick reads (HEIC, PSD, RAW, SVG…); **Half**, **Full HD** (fit in 1920 × 1080) and **B&W** in one tap |
| 📄 **Documents** | PDF ⇄ DOCX, DOCX/XLSX/PPTX → PDF, slides → PNG, Markdown → HTML/PDF/DOCX (Microsoft Office or LibreOffice) |
| 🧾 **Data** | JSON ⇄ YAML ⇄ TOML ⇄ XML, JSON / CSV ⇄ XLSX (every sheet), CSV / XLSX → Markdown and HTML tables, JSON Lines, Pretty / Minify for JSON, XML and CSS; no office suite needed ([rules](docs/data-formats.md)) |
| 🎞 **Video & audio** | MP4 · WEBM · GIF · MOV · MKV · MP3 · WAV · FLAC · OGG · OPUS · M4A, plus compress, 720p, mute, grab frame, loudness, trim silence |
| 💬 **Subtitles** | SRT ⇄ VTT ⇄ ASS/SSA (and LRC) with exact timings and italics, a video's subtitle track → SRT |
| 🔡 **Encodings** | Any text file (TXT, MD, CSV, JSON, SRT…) → UTF-8 from Windows-1251/1252/1254, KOI8-R, DOS 866 or UTF-16; LF ⇄ CRLF |
| ✂️ **Editors** | **Crop**, **Add BG** (screenshot beautifier), **Annotate** (arrows, text, numbered steps, highlighter), **Redact** (pixelate / blur / black, *find text automatically*), **Adjust** (light, colour, sharpen, vignette, rotate, resize) |
| 🤖 **Cut out** | AI background removal (BiRefNet-lite), fully offline, GPU-accelerated on Windows |
| 🔤 **Text** | Copy text from images and scanned PDFs (Windows OCR / Apple Vision / Tesseract) |
| 🧹 **Clean** | Strip EXIF & GPS — losslessly for JPEGs; strip PDF metadata |
| 🗜 **Compress** | Smart image compression with the savings shown: `1.2 MB → 340 KB (−72%)` |
| 📑 **PDF tools** | Merge, split, rotate, page numbers, cover image, extract every image at original quality, compress (with Ghostscript) |
| 📥 **Watch folders** | New files in a folder convert by themselves into its `Converted` subfolder (panel, or `convertino watch`) |
| 🗃 **Archives** | Extract ZIP · 7Z · RAR · TAR · TAR.GZ / BZ2 / XZ · GZ · BZ2 · XZ into a new folder, refusing `../`, absolute paths and links that point outside it; pack folders as ZIP · 7Z · TAR.GZ |
| 🧰 **File tools** | Split big files into 25 / 50 / 100 MB… parts and join the `.001` back; find duplicates (an HTML report, nothing is deleted); verify a `.sha256` / `SHA256SUMS` (*Matches ✓*); copy MD5 · SHA-1 · SHA-256 · SHA-512 |
| 🧰 **Image tools** | One tap, no questions: **Messenger** (≤ 2560 px JPEG, GPS gone), **Square** (blurred fill), **Rounded** (corners + shadow), **Palette**, **Favicon** set, **Copy** the picture itself, **Read QR** / make a **QR** from a text or link file; for several images **Contact sheet**, **Slideshow** (GIF + WEBP) and **Compare** |
| 📦 **Everything else** | Copy path, data URI |
| 🎨 **Themes** | Catppuccin **Mocha** & **Latte**, **Sunset**, **Graphite** |
| 📊 **Activity** | The tray / menu-bar panel charts your last two weeks: files handled, space saved, day streak, favourite actions |
| ⌨️ **CLI** | `convertino convert IMG_0042.HEIC --to jpg` — the same engine without the UI |

<div align="center">
<img src="docs/themes.png" width="100%" alt="The four themes: Mocha, Sunset (tools ring), Latte (PDF ring), Graphite" />
<sub>Mocha · Sunset (Tools ring) · Latte (PDF ring) · Graphite</sub>
</div>

## Install

### Windows 10 / 11

1. Download **`convertino-win-x64.zip`** from the [latest release](https://github.com/hasanzadee/convertino/releases/latest) and unzip it.
2. Run `install.ps1` (right-click → *Run with PowerShell*, or):

   ```powershell
   powershell -ExecutionPolicy Bypass -File install.ps1
   ```

Convertino installs per-user to `%LOCALAPPDATA%\Programs\Convertino` (no admin rights), adds a Start-menu entry and **Send to → Convertino**, starts in the tray and launches with Windows (toggle it in the tray panel). Click the tray icon for the panel (theme, options, recent files; closes on Esc or a click outside); right-click it for a quick menu with **Quit Convertino**. For video and audio petals install ffmpeg: `winget install Gyan.FFmpeg`; for PDF compression, Ghostscript: `winget install ArtifexSoftware.GhostScript`.

<div align="center">
<img src="docs/panel.png" width="320" alt="The tray panel: two weeks of activity, themes and options" />
</div>

### Linux

1. Download **`convertino-linux-x64.tar.gz`** from the [latest release](https://github.com/hasanzadee/convertino/releases/latest).
2. Unpack and install (per-user, into `~/.local`):

   ```bash
   tar xf convertino-linux-x64.tar.gz && cd convertino && ./install.sh
   ```

The installer adds the `convertino` command, a desktop entry, autostart, and a **Convertino** entry in the right-click menu of Nautilus, Nemo, Dolphin and Caja. Click the tray icon (or **Open panel**, the first item of its menu) for the panel: activity, theme, options, recent files. Optional helpers it will offer to install:

```bash
sudo apt install ffmpeg poppler-utils tesseract-ocr libreoffice ghostscript   # Debian / Ubuntu
sudo dnf install ffmpeg poppler-utils tesseract libreoffice       # Fedora
sudo pacman -S ffmpeg poppler tesseract tesseract-data-eng libreoffice-fresh   # Arch
```

**Copy image** hands the picture to `wl-copy` on Wayland (package `wl-clipboard`) or `xclip` on X11; the card tells you which one is missing.

<img src="docs/linux-tools.png" width="300" align="right" alt="The Tools ring on Linux (Avalonia build) over a file manager" />

> **X11 vs Wayland.** The drag + <kbd>Shift</kbd> gesture needs to see the pointer and keyboard globally, which X11 allows and Wayland (by design) does not. On a Wayland session it works over X11/XWayland apps; everywhere else use **right-click → Convertino** (or `convertino menu <files>`) — same flower, click a petal.

### macOS 13 or later (Apple silicon or Intel)

> **Status: tested on a real Mac** (MacBook M4, macOS 27): conversions, HEIC, video, PDF rendering, Vision OCR, Cut out, the glass flower on Retina and an external display, and dragging from Finder with the trigger key. Every CI run also tests the build on GitHub's macOS runners (Apple silicon and Intel), including the gesture with synthetic input. If something doesn't work on your Mac, please [send a macOS report](https://github.com/hasanzadee/convertino/issues/new?template=macos_report.yml).

1. Download **`convertino-macos-arm64.zip`** (Apple silicon: M1 and later) or **`convertino-macos-x64.zip`** (Intel) from the [latest release](https://github.com/hasanzadee/convertino/releases/latest).
2. Unzip it (double-click it in Finder) and run the installer from Terminal:

   ```bash
   cd ~/Downloads/convertino && ./install.sh
   ```

The installer copies `Convertino.app` to `~/Applications`, adds the `convertino` command (`~/.local/bin`) and a **Quick Actions → Convertino** entry to Finder's right-click menu, and starts Convertino in the menu bar; it opens at login (toggle it in the panel). Click the menu-bar icon for the panel: activity, theme, options, recent files; right-click (or Control-click) it for the menu. `./install.sh --uninstall` removes it again. Optional helpers:

```bash
brew install ffmpeg                  # video & audio petals
brew install ghostscript             # PDF compression
brew install --cask libreoffice      # Word / Excel / PowerPoint petals
```

PDF rendering and OCR use what's built into macOS (CoreGraphics, the Vision framework), so they need nothing extra.

**Permissions.** Convertino watches the mouse buttons and the trigger key by polling their state (no keyboard tap). That works without any permission (seen on a MacBook M4 with macOS 27 and in CI), and no Accessibility or Screen Recording access is needed either: the frosted glass is the system's own blur. If the flower doesn't open on your Mac, macOS may be holding back the Shift key: click the first-start card (or *Input Monitoring…* in the menu you get by right-clicking the menu-bar icon), switch Convertino on in *System Settings → Privacy & Security → Input Monitoring*, then quit and reopen Convertino.

**Gatekeeper.** Convertino isn't signed with an Apple Developer ID or notarized. `install.sh` clears the download quarantine for the copy it installs, so it opens normally. If you move `Convertino.app` somewhere yourself instead, macOS says it *can't verify* the app: click *Done*, open *System Settings → Privacy & Security*, scroll down to *"Convertino" was blocked…* and click **Open Anyway** (once). Or, in Terminal: `xattr -dr com.apple.quarantine /path/to/Convertino.app`.

Intel Macs get every petal except **Cut out**: ONNX Runtime ships no Intel-Mac build.

### AI cut-out (optional, all platforms)

The **Cut out** petal appears once its model is installed (224 MB, one-time, checked against its SHA-256):

```bash
convertino model install
```

## Using it

| Gesture | What happens |
|---|---|
| Drag file(s) · tap <kbd>Shift</kbd> | The flower opens under the cursor on the **Convert** ring |
| <kbd>Shift</kbd> again, <kbd>Tab</kbd> or the mouse wheel | Next ring: **Convert** → **Tools** → **More tools** → **More formats** (images) → Convert; the centre names the ring for a moment. <kbd>Shift</kbd>+<kbd>Tab</kbd> or wheel up goes back, so the rarer formats are one step back from Convert |
| Drop on a petal | Runs it; the result appears beside the source and a card shows the outcome |
| Drop on the centre · drag away · <kbd>Esc</kbd> | Cancel — nothing happens, the flower fades |
| Double-tap <kbd>Shift</kbd> in Explorer / on the desktop *(Windows)* | Same flower for the selected files — click a petal |
| Right-click → **Send to → Convertino** *(Windows)* | Same flower for the selected files |
| Right-click → **Convertino** *(Linux file managers)* | Same flower for the selected files |
| Right-click → **Quick Actions → Convertino** *(macOS Finder)* | Same flower for the selected files |
| Click the result card | Open the file · folder icon reveals it · × cancels a running job |

<kbd>Shift</kbd> is the default. To use another key, pick it under **Open with** in the tray panel or the tray / menu-bar menu: <kbd>Shift</kbd>, <kbd>Shift</kbd> twice, <kbd>Ctrl</kbd> or <kbd>Alt</kbd> (<kbd>⌃ Control</kbd> / <kbd>⌥ Option</kbd> on macOS). The gestures above then use that key.

The wheel flips the rings while the pointer is over the flower; a touchpad swipe or a quick spin counts as one flip. On Windows, Tab and the wheel don't reach the window behind the flower while it's open; <kbd>Alt</kbd>+<kbd>Tab</kbd>, <kbd>Win</kbd>+<kbd>Tab</kbd> and <kbd>Ctrl</kbd>+<kbd>Tab</kbd> stay the system's (unless that modifier is your trigger key). On Linux and macOS Convertino only watches them, so they reach the app behind as well: in Linux click mode, Tab also moves the focus in the file manager. On macOS, Tab during a drag needs Input Monitoring (like <kbd>Esc</kbd>, see *Permissions* above); in the Quick Action's flower it always works.

While dragging over a petal, the caption under the Explorer drag image says what will happen (*Convert to DOCX*, *Crop*…). Convertino always copies — even though <kbd>Shift</kbd> normally means *move*, the source file is never touched.

<div align="center">
<img src="docs/addbg-example.png" width="720" alt="Example output of the Add BG editor: a screenshot on a gradient with a window frame, rounded corners and shadow" />
<br/><sub>Add BG: any screenshot on a gradient, with padding, corners, shadow and an optional window frame</sub>
</div>

## What's on each ring

| Files | Convert ring | Tools ring |
|---|---|---|
| **Image** | PNG · JPG · WEBP · AVIF · PDF · GIF | Cut out · Compress · Clean · Adjust · Annotate · Add BG · Crop · Redact · Text |
| **Several images** | same, as a batch; PDF merges them | Compress · Clean · Cut out · Text · Zip |
| **Images + PDFs** | Merge into one PDF · Zip · Path | |
| **PDF** | DOCX · JPG · TXT · PNG | Text (OCR) · Split · Rotate · Clean · Merge · Zip |
| **Word / RTF / ODT** | PDF · DOCX · TXT · PNG pages · RTF · ODT · HTML | Zip · Path · SHA-256 |
| **Excel / CSV / ODS** | PDF · XLSX · CSV · ODS · HTML · JSON · MD *(CSV ⇄ XLSX, JSON and the HTML / Markdown tables without an office suite)* | Zip · Path · SHA-256 |
| **JSON / YAML / TOML / XML** | JSON · YAML · TOML · XML · CSV · XLSX *(+ JSONL for JSON)* | Pretty · Minify *(JSON, XML)* · Copy · Zip · Path · SHA-256 |
| **CSS** | Pretty · Minify · Copy | Zip · Path · SHA-256 |
| **PowerPoint / ODP** | PDF · PNG slides · PPTX · ODP | Zip · Path · SHA-256 |
| **Markdown** | HTML · PDF · DOCX · Copy | UTF-8 · LF/CRLF · Zip · Path · SHA-256 |
| **Text** | PDF · DOCX · Copy | UTF-8 · LF/CRLF · Zip · Path · SHA-256 · Base64 |
| **Video** | MP4 · WEBM · GIF · MP3 · MOV · MKV · WAV | Compress · 720p · Mute · Frame · Subtitles |
| **Audio** | MP3 · M4A · WAV · FLAC · OGG · OPUS | Loudness · Trim silence · Zip · Path |
| **Subtitles** (SRT / VTT / ASS / SSA / LRC) | SRT · VTT · ASS · TXT | UTF-8 · LF/CRLF · Zip · Path · SHA-256 |
| **Archive** (ZIP, 7Z, RAR, TAR, TAR.GZ…) | Extract · Path · SHA-256 | Split · MD5 · SHA-1 · SHA-512 |
| **Split part** (`.001`) | Join · Path · SHA-256 | |
| **Checksum** (`.sha256`, `.md5`, `SHA256SUMS`…) | Verify · Copy · Path | |
| **File + its checksum** | Verify · Zip · Path | |
| **Folder** | Zip · 7Z · TAR.GZ · Duplicates · Path · Copy | |
| **Anything else** | Zip · Path · SHA-256 · Base64 | Split into 25 / 50 / 100 MB… *(files over 10 MB)* · MD5 · SHA-1 · SHA-512 |
| **Several files** | the ring for their kind, plus Duplicates; mixed kinds: Zip · 7Z · TAR.GZ · Duplicates · Path · Copy | |

Images (one or several) also get **More tools**: Half · Full HD · B&W, and **More formats**: HEIC *(where the codec allows)* · TIFF · ICO · BMP · JXL · QOI. Half, Full HD and B&W keep the source's format and write beside it: `photo (2016x1512).jpg`, `photo (1920x1080).jpg`, `photo (grayscale).jpg`. Full HD only ever shrinks (portrait images fit in 1080 × 1920), and animated GIF / WebP stay animated. No ring shows more than 10 petals; a longer list carries on in a second ring of the same name.

Petals only appear when the engine behind them is available (e.g. office formats need Microsoft Office or LibreOffice).
Plain-text files of any kind (TXT, Markdown, CSV/TSV, JSON, YAML, XML, INI, HTML, scripts, subtitles…) also get **UTF-8** and **LF** or **CRLF** (whichever the file doesn't use) at the front of their tools; files with NUL bytes are treated as binary and never get them.

## Command line

```bash
convertino convert IMG_0042.HEIC --to jpg        # → IMG_0042.jpg
convertino convert slides.pptx --to pdf
convertino convert config.yaml --to json        # JSON · YAML · TOML · XML · CSV · XLSX
convertino run compress *.png                     # any petal id
convertino run ocr scan.pdf                       # text → clipboard
convertino convert movie.srt --to vtt             # also ass, srt, txt
convertino run utf8 old-cyrillic.txt              # → old-cyrillic (UTF-8).txt (any text file)
convertino run lf deploy.sh                       # → deploy (LF).sh; crlf the other way
convertino run subtitles movie.mkv                # first subtitle track → movie.eng.srt
convertino run extract backup.tar.xz              # → backup/
convertino run split-100mb video.mkv              # → video.mkv.001, .002 …  (join: convertino run join video.mkv.001)
convertino run verify ubuntu.iso SHA256SUMS       # Matches ✓ / Doesn't match ✗ (exit code 1)
convertino actions report.pdf                     # what's possible for these files
convertino menu photo.png notes.md                # open the flower for them (used by file-manager menus)
convertino model install | status | remove        # the AI cut-out model
convertino watch ~/Inbox --to pdf                 # convert every new file there, until Ctrl+C
convertino watch add ~/Inbox --to webp            # same, run by the desktop app (also: watch list | remove)
```

On Windows the executable is `Convertino.exe` (in `%LOCALAPPDATA%\Programs\Convertino`); on macOS the installer puts a `convertino` wrapper for `~/Applications/Convertino.app` in `~/.local/bin`.

## Engines

| Job | Windows | Linux | macOS |
|---|---|---|---|
| Images | [Magick.NET](https://github.com/dlemstra/Magick.NET) | Magick.NET | Magick.NET |
| PDF render | Windows.Data.Pdf | poppler `pdftoppm` (or Ghostscript) | CoreGraphics (poppler / Ghostscript fallback) |
| PDF text / merge / split | PdfPig · PDFsharp | PdfPig · PDFsharp | PdfPig · PDFsharp |
| Office documents | Microsoft Office (COM), else LibreOffice | LibreOffice (headless) | LibreOffice (headless) |
| Data (JSON, YAML, TOML, XML, CSV, XLSX) | System.Text.Json · YamlDotNet · Tomlyn · LINQ to XML · MiniExcel | same | same |
| OCR | Windows OCR (installed language packs), else Tesseract | Tesseract | Vision framework, else Tesseract |
| QR codes | ZXing.Net (read) · QRCoder (make) | ZXing.Net · QRCoder | ZXing.Net · QRCoder |
| Picture on the clipboard | WPF clipboard (PNG + bitmap) | `wl-copy` (Wayland) or `xclip` (X11) | NSPasteboard (PNG + TIFF) |
| Video / audio | ffmpeg | ffmpeg | ffmpeg (Homebrew) |
| Subtitles | built in (ffmpeg for a video's track) | built in | built in |
| Cut out | ONNX Runtime + DirectML (GPU), CPU fallback | ONNX Runtime (CPU) | ONNX Runtime (CPU, Apple silicon) |
| UI | WPF, Win32 hooks, OLE drag & drop | Avalonia, X11 polling, XDND | Avalonia, Quartz polling, AppKit drag & drop |

## How it works

- **Seeing the gesture.** On Windows, low-level keyboard/mouse hooks run on their own thread (so the system cursor never waits on the UI). On X11, a thread polls the pointer and keymap — no root, no extensions, and it keeps working while a file manager holds the pointer grab during a drag. On macOS, a thread polls Quartz's mouse-button and modifier state the same way (no event tap, so nothing ever waits on Convertino).
- **Catching the drop.** The flower is a transparent, topmost, non-activating window that *is* a drop target: Explorer's OLE drag (or the file manager's XDND drag, or Finder's drag session) hands it the file list the moment the cursor enters. Hit-testing follows the petal shapes; on Windows and X11 the corners pass drops through to whatever is underneath.
- **The glass.** On Windows and X11 the screen behind the flower is captured just before it appears, downscaled, blurred and saturated on the CPU, then drawn under each petal — real frosted glass without compositor tricks. On macOS the window server does it: a behind-window `NSVisualEffectView`, masked to the petal shapes.
- **The motion.** Every petal, the hub and the hover state are damped springs, so the bloom overshoots slightly and settles like something physical.
- **The work.** Actions run in the background and report progress to a card in the corner. Office conversions work on a private copy, so a document you have open is never touched.

```
src/
  Convertino.Core/      shared engine: catalog of petals, converters, editors' pixel ops, CLI   (net10.0)
  Convertino.Windows/   WPF app: hooks, OLE drop target, flower, toasts, tray panel, editors     (net10.0-windows)
  Convertino.Linux/     Avalonia app: X11 input, XDND drop target, flower, toasts, tray panel, editors (net10.0)
  Convertino.Mac/       macOS app on the same Avalonia UI: Quartz input, AppKit glass, Vision OCR  (net10.0)
packaging/          installers, desktop entry, icons, file-manager integrations
```

## Build from source

You need the [.NET 10 SDK](https://dot.net).

```bash
git clone https://github.com/hasanzadee/convertino && cd convertino
# Windows: build + install
powershell -ExecutionPolicy Bypass -File packaging/windows/install.ps1
# Linux: build + install
./packaging/linux/install.sh
# macOS: build Convertino.app + install
./packaging/macos/install.sh
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for developer flags (render the flower to a PNG, run one action headless, open an editor directly) and a throw-away X11 desktop for testing the Linux build.

## FAQ

**Does it upload anything?** No. Nothing leaves your machine, except a one-time download of the cut-out model if you ask for it and, if you say yes when Convertino asks, a daily check of the latest release on GitHub. That check is one anonymous request, with no ID or usage data, and it only shows a card when a new version is out. Turn it off any time with *Tell me about new versions* in the tray panel or menu, or check by hand with *Check for updates…*.

**Shift-drag already means "move" in Explorer — does Convertino break that?** No. The flower only takes the drop if you release over a petal. Drag away from the flower and it fades after half a second, so a normal Shift-move carries on.

**An app I drop files into uses Shift too (Claude attaches a file on Shift + drop).** Set **Open with** to *Shift twice*. A single Shift then goes to the app, and a quick double-tap during the drag opens the flower. Or pick <kbd>Ctrl</kbd> or <kbd>Alt</kbd>.

**Where are my settings?** `%APPDATA%\Convertino` on Windows, `~/.config/convertino` on Linux, `~/Library/Application Support/Convertino` on macOS (`settings.json`, `convertino.log`, `models/`).

**macOS?** Yes — see [Install](#macos-13-or-later-apple-silicon-or-intel). It's the newest port; it has been tested on a MacBook M4 with macOS 27, and CI runs it on Apple silicon and Intel.

## Credits

Inspired by a macOS "drag + Shift" converter demo that went around social media. Icons on Linux are drawn in the style of [Feather](https://feathericons.com); colours follow [Catppuccin](https://catppuccin.com). The cut-out model is [BiRefNet](https://github.com/ZhengPeng7/BiRefNet) (MIT). Full list in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## License

[MIT](LICENSE)
