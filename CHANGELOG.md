# Changelog

## Unreleased

- Subtitles: SRT, WebVTT, ASS and SSA files (and LRC lyrics) convert into each other and to plain text. Timestamps stay to the millisecond (ASS itself counts hundredths); line breaks, italics, bold and top-of-screen cues ({\an8}, `line:0`) carry over, and ASS styles, positions and drawings are dropped. Each conversion is written as UTF-8 and reads legacy encodings correctly.
- **UTF-8** tool for every plain-text file (TXT, Markdown, CSV/TSV, JSON, YAML, XML, INI, HTML, scripts, subtitles…): re-encodes Windows-1251/1252/1254, KOI8-R, DOS 866, the system code page and UTF-16/32 as UTF-8 (BOMs are honoured; otherwise the encoding is detected) and fixes mixed line endings. It writes `notes (UTF-8).txt` and says *Already UTF-8* when there's nothing to fix; a UTF-8 CSV without a BOM gets one so that Excel reads it as UTF-8. **Copy contents** uses the same detection, so legacy text pastes correctly.
- **LF** / **CRLF** tools for the same files: the petal offers the line ending the file doesn't use (both when it mixes them) and writes `run (LF).sh`; the encoding is left exactly as it was, UTF-16 included.
- Files with NUL bytes (anything but UTF-16/32) count as binary: the text tools aren't offered for them and refuse to touch them.
- **Subtitles** tool for videos (with ffmpeg): saves the first text subtitle track beside the video as SRT (`movie.eng.srt`), with ASS styles dropped. It says so clearly when the video has no subtitle track, or only picture-based ones (PGS, VobSub).

## 1.2.0 — 2026-10-20

Thanks again to [@AliverdiSultanli](https://github.com/AliverdiSultanli) for the trigger keys, the update check and the macOS Quit fix, all tested on his Mac.

- Choose the key that opens the flower: **Open with** → Shift (default), Shift twice, Ctrl or Alt (Control / Option on macOS), in the tray panel and the tray / menu-bar menu. Hints and the Explorer drag caption follow the key. *Shift twice* leaves a single Shift to apps that use it on drop, such as Claude, which attaches a file on Shift + drop (#6).
- Know when a new version is out: **Check for updates…** in the menus and **Updates** in the panel footer, plus an optional daily check. It's off until you answer a one-time *Notify about updates?* card; it only reads the latest release from GitHub, sends nothing else, and never downloads or installs anything (#11).
- macOS: **Quit Convertino** froze the app until it was killed (#8).
- macOS: Activity Monitor and Force Quit listed the app as *Avalonia*; it's *Convertino* now (#10).
- Activity card on Linux and macOS: a new panel opens from the tray icon (Linux) or **Open panel** in the tray / menu-bar menu, with the same counting-up numbers, a bar per day that shows that day's numbers on hover, and your most used actions; until you've converted something it shows how to start. It closes on Esc, a click outside or ✕. `CONVERTINO_PANEL=1` (or `=demo`) opens it at start, for screenshots.
- The Linux and macOS panel now has everything the Windows one has: the four theme swatches (switching restyles everything at once and is remembered), the Enabled / Notifications / Tell me about new versions / Start at login (Open at login on macOS) switches, the **Open with** key picker (Shift, Shift ×2, Ctrl/Control, Alt/Option), your recent files with thumbnails, and Log / Updates / Quit. **Open panel** is now the first item of the tray / menu-bar menu, and the menu follows what you change in the panel (it is refreshed in place; the tray icon is no longer re-created on a theme change).
- macOS: a click on the menu-bar icon opens the panel straight away, like the tray icon on Windows; right-click or Control-click it for the menu. Avalonia's status item can only open its menu, so Convertino takes the plain click itself.

## 1.1.1 — 2026-10-20

Fixes from [@AliverdiSultanli](https://github.com/AliverdiSultanli), who also ran the first test of Convertino on a real Mac (M4, macOS 27). Thank you!

- Zip: a folder passed with a trailing slash (`photos/`, shell tab completion on Linux and macOS) was zipped into itself as `.zip` and its entries lost the folder prefix; a folder with dots in its name (`release.v2`) came out as `release.zip`. Both fixed (#2).
- Compress: images with real transparency (ICO, TGA, PSD, SVG, …) came back as JPEG on white; they now stay PNG with their alpha. Images with an alpha channel that is fully opaque go to the smaller JPEG (#3).
- Settings: `settings.json` is written to a temporary file and swapped in, behind one lock, so a crash or a second process can never see half a file and reset your theme, toggles and recent list (#4).

## 1.1.0 — 2026-10-20

- Activity card in the Windows tray panel: files handled, space saved and your day streak for the last two weeks (the numbers count up as the panel opens), a bar per day that shows that day's numbers on hover, and your most used actions. It takes the place of the how-to card once you've converted something.
- Every finished job is tallied per day in `stats.json` next to the settings (all platforms; nothing leaves your machine).
- `Convertino.exe --panel out.png [theme] [demo]` renders the panel to a PNG (used for the README).

## 1.0.1 — 2026-10-20

- Windows tray panel: it opened only once — changing the theme or a toggle crashed the rebuild, and every later click on the tray icon did nothing. Fixed.
- Windows tray panel: it grew taller on every reopen until the header went off screen. It now keeps its size, stays inside the work area and scrolls when the screen is short.
- Windows tray panel: closes on Esc, a click anywhere outside it, or the new ✕ button, even when Windows didn't give it focus.
- Windows tray icon: right-click menu with Open panel, Enabled, Theme, Notifications, Start with Windows, Open log and Quit Convertino.

## 1.0.0 — 2026-10-19

First public release.

- Drag any file and tap Shift: the radial menu blooms under the cursor (Windows OLE drag & drop, Linux XDND, macOS Finder drops).
- Convert and Tools rings, flip with Shift; drag captions in Explorer; drops always copy, never move.
- Images, PDF, Word/Excel/PowerPoint (Microsoft Office or LibreOffice), Markdown, text, video, audio, ZIP, folders.
- Editors: Crop, Add BG, Annotate, Redact (with automatic text detection), Adjust.
- Cut out: offline AI background removal (BiRefNet-lite; DirectML GPU on Windows, CPU on Linux and Apple silicon).
- OCR to clipboard (Windows OCR / Vision / Tesseract), lossless metadata cleaning, smart compression.
- Double-tap Shift in Explorer and Send to → Convertino (Windows); right-click → Convertino in Nautilus, Nemo, Dolphin, Caja (Linux); Finder Quick Action (macOS).
- macOS app (13 Ventura or later, Apple silicon and Intel): system blur glass, CoreGraphics PDF rendering, Vision OCR, menu-bar icon, LaunchAgent. Ad-hoc signed, not notarized. **Beta:** the drag + Shift gesture is verified in CI with synthetic input, not yet on real hardware.
- Command line: `convertino convert | run | actions | menu | model`.
- Themes: Catppuccin Mocha & Latte, Sunset, Graphite.
