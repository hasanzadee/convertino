# Changelog

## Unreleased

- Images get two more rings after Tools: **More tools**, with **Half**, **Full HD** (shrinks to fit 1920 × 1080, or 1080 × 1920 for portrait; never enlarges) and **B&W** in one tap, and **More formats**: **HEIC** (where the codec allows), **TIFF**, **ICO**, **BMP**, **JPEG XL** and **QOI**. Half, Full HD and B&W keep the source's format (PNG for SVG/PSD/TGA, JPG for camera RAW), keep animated GIF / WebP animated, and write beside it as `photo (2016x1512).jpg` / `photo (grayscale).jpg`. The Convert ring now holds the everyday formats: PNG · JPG · WEBP · AVIF · PDF · **GIF**. `.qoi` files are recognised as images.
- Flip the rings with **Tab** / **Shift+Tab** and the **mouse wheel** (down = next, up = previous) as well as the trigger key, on all three platforms, mid-drag and in click mode. The rings wrap around (Convert → Tools → More tools → More formats → Convert, so wheel up from Convert reaches the rarer formats), the centre names the ring for a moment after each flip, and a touchpad swipe or a fast spin counts as one flip. No ring shows more than 10 petals: a longer list carries on in a second ring. The trigger key now flips when it comes back up, so Shift+Tab goes back instead of also counting the Shift. Windows: Tab and the wheel over the flower don't reach the window behind it. Linux: the wheel is read through XInput2 raw events, so it works while the file manager holds the drag. macOS: Tab during a drag needs Input Monitoring (like Esc); the flower's own window always sees it in click mode.
- `convertino actions` lists the new rings (`more:`, `formats:`); `Convertino.exe --snapshot out.png <theme> more|formats` renders them, and `Convertino.exe --paging-selftest <dir>` runs the wheel / Tab / Shift paging through the real hook callbacks and overlay without touching the desktop.
- PDF tools ring: **Compress** (re-distilled with Ghostscript when it's installed; the result is kept only if it's smaller), **Images** (every embedded picture at its original quality, duplicates skipped), **Numbers** ("3 / 12" at the bottom of each page, upright on rotated pages, no font files needed) and **Cover** (the first page as PNG).
- Watch folders: pick a folder in the panel ("Watch a folder…") and what new files become (PDF, JPG, PNG, WEBP, Compress, Clean); everything that lands there is converted into its `Converted` subfolder, with a result card. Partial downloads (`.part`, `.crdownload`, …) are waited out. Also from the command line: `convertino watch <folder> --to pdf` in the foreground, or `convertino watch add | list | remove` for the desktop app, which picks up changes immediately.
- Fixed: images → PDF failed with "Unsupported image format" for JPEGs that don't start with a JFIF/Exif segment (Windows' own wallpapers, many camera exports).
- Subtitles: SRT, WebVTT, ASS and SSA files (and LRC lyrics) convert into each other and to plain text. Timestamps stay to the millisecond (ASS itself counts hundredths); line breaks, italics, bold and top-of-screen cues ({\an8}, `line:0`) carry over, and ASS styles, positions and drawings are dropped. Each conversion is written as UTF-8 and reads legacy encodings correctly.
- **UTF-8** tool for every plain-text file (TXT, Markdown, CSV/TSV, JSON, YAML, XML, INI, HTML, scripts, subtitles…): re-encodes Windows-1251/1252/1254, KOI8-R, DOS 866, the system code page and UTF-16/32 as UTF-8 (BOMs are honoured; otherwise the encoding is detected) and fixes mixed line endings. It writes `notes (UTF-8).txt` and says *Already UTF-8* when there's nothing to fix; a UTF-8 CSV without a BOM gets one so that Excel reads it as UTF-8. **Copy contents** uses the same detection, so legacy text pastes correctly.
- **LF** / **CRLF** tools for the same files: the petal offers the line ending the file doesn't use (both when it mixes them) and writes `run (LF).sh`; the encoding is left exactly as it was, UTF-16 included.
- Files with NUL bytes (anything but UTF-16/32) count as binary: the text tools aren't offered for them and refuse to touch them.
- **Subtitles** tool for videos (with ffmpeg): saves the first text subtitle track beside the video as SRT (`movie.eng.srt`), with ASS styles dropped. It says so clearly when the video has no subtitle track, or only picture-based ones (PGS, VobSub).
- **Archives**: Extract now opens 7Z, RAR (RAR4, RAR5, solid and multi-volume `.part1.rar`), TAR, TAR.GZ / TGZ, TAR.BZ2, TAR.XZ and single GZ / BZ2 / XZ files, besides ZIP, with progress and cancel. Archives unpack into a new folder beside them (`foo.zip` holding just `foo/` gives `foo`, not `foo/foo`); a single compressed file (`notes.txt.gz`) lands beside it as `notes.txt`. Entries that would land outside the folder (`../`, absolute paths, drive letters) are refused and counted on the card, symbolic links only go in when they stay inside it (on Windows they need Developer Mode, otherwise they are skipped), and names Windows can't store (`con.txt`, `what?.txt`) get a safe name. Password-protected archives say so.
- **Pack as 7Z or TAR.GZ**: folders (and several files) get **7Z** (LZMA2, the smallest, slower) and **TAR.GZ** (keeps links and Unix permissions) next to **Zip**.
- **Split / Join**: big files (over 10 MB) get split petals on the Tools ring with sizes that fit them (25 MB · 50 MB · 100 MB …, 4 GB for FAT32), writing `name.ext.001`, `.002` … beside it. Drop the `.001` and **Join** puts the original back together after checking that no part is missing or cut short.
- **Duplicates**: drop a folder (or several files) on **Duplicates** to find identical files (same size, then same SHA-256). The card shows *N duplicate groups · X MB* and an HTML report beside the folder lists every group and the space it wastes. Nothing is ever deleted; hidden / system files, links and online-only OneDrive files are left out.
- **Checksums**: drop a file together with its `.sha256` / `.sha1` / `.md5` / `.sha512` or a `SHA256SUMS`-style list and **Verify** shows *Matches ✓* or *Doesn't match ✗* (a list on its own checks the files beside it, like `sha256sum -c`; GNU, BSD and bare-hash formats). Archives and other files get **MD5**, **SHA-1** and **SHA-512** on the Tools ring next to the SHA-256 petal. `convertino run verify` exits with 1 on a mismatch.

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
