# Changelog

## Unreleased

- **Image tools**, one tap each, no questions asked, results saved beside the source:
  - **Messenger**: long side at most 2560 px, JPEG quality 85, turned upright from EXIF, colours baked into sRGB, every tag removed including the GPS location (the card says so).
  - **Square**: a 1:1 canvas with the photo centred over a blurred, enlarged copy of itself.
  - **Rounded**: rounded corners and a soft drop shadow on a transparent PNG, for screenshots in docs and READMEs. A window capture with a transparent margin is trimmed first, so the shadow follows the window.
  - **Palette**: the 5–8 dominant colours as HEX on the clipboard, one per line, plus a swatch strip PNG. A flat logo gives just its own colours, not the blends along its edges.
  - **Favicon**: a folder with `favicon.ico` (16/32/48), `apple-touch-icon.png` (180, on white since iOS paints transparency black), `icon-192.png`, `icon-512.png`, `site.webmanifest` and `snippet.html` with the `<link>` tags. SVG logos are rendered sharp at every size.
  - **Copy** (image): puts the picture itself on the clipboard, not the file, so it pastes straight into chats and documents. Windows offers PNG (keeps transparency) plus a plain bitmap, macOS PNG plus TIFF; Linux hands it to `wl-copy` (Wayland) or `xclip` (X11). Pictures longer than 4096 px are scaled down to that first, and the card says so.
  - **Read QR**: copies the text of the QR code(s) in an image, also small, inverted (dark mode) or photographed ones; Data Matrix and Aztec codes too. **QR** on a `.txt`, `.url` or `.webloc` makes a QR code PNG of its text or link (UTF-8, so non-Latin text scans correctly).
  - For several images: **Contact sheet** (one JPG grid of thumbnails with file names, up to 400 images), **Slideshow** (a GIF and an animated WEBP of up to 60 pictures, sorted by name, 1 s per frame, letterboxed to one size) and, for exactly two of the same proportions, **Compare** (the changes in red and boxed, and *N% of pixels differ* on the card). When files are left out, the card says *first 60 of N*.
- 1-bit images (QR codes, line-art scans) reached the editors and tools bit-packed; they're read as normal 8-bit pixels now.
- Windows: when the clipboard can't be opened (another app holds it, or the screen is locked) the card now says so instead of claiming the text was copied.

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
