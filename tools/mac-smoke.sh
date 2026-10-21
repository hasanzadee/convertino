#!/usr/bin/env bash
# Smoke test for the macOS build (used by CI; works on any Mac):
#   tools/mac-smoke.sh dist/Convertino.app          command line: conversions, CoreGraphics PDF, Vision OCR (CUTOUT=1: Cut out)
#   tools/mac-smoke.sh dist/Convertino.app --gui    + editors, pinned flower, "convertino menu" over IPC, the menu-bar panel (theme click,
#                                                 Open panel from the menu, a click on the menu-bar item; right-click and
#                                                 Ctrl-click for the menu), a synthetic drag + Shift
#                                                 + drop; in CI also install.sh and the Finder Quick Action
# Screenshots and the log land in ./mac-smoke/.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
GUI="${2:-}"
D="$APP/Contents/MacOS/convertino"
OUT="$PWD/mac-smoke"
LOG="$HOME/Library/Application Support/Convertino/convertino.log"
rm -rf "$OUT"; mkdir -p "$OUT"
WORK="$(mktemp -d)"
cd "$WORK"

step() { printf '\n\033[1;35m== %s\033[0m\n' "$*"; }
# macOS has no timeout(1); a GUI helper that never returns must not hang CI
limit() { local s="$1"; shift; perl -e 'alarm shift; exec @ARGV' "$s" "$@"; }

step "version"
"$D" --version

step "image conversions"
cp "$ROOT/packaging/linux/icons/convertino-256.png" smoke.png
"$D" convert smoke.png --to webp && test -s smoke.webp
"$D" convert smoke.png --to avif && test -s smoke.avif
"$D" run compress smoke.png
"$D" actions smoke.png

step "live preview: worked out ahead, handed over on drop"
"$D" preview smoke.png --take to-webp | tee preview.log
grep -q "to-avif" preview.log
test -s "smoke (2).webp"

step "archives and file tools"
bash "$ROOT/tools/files-smoke.sh" "$D"

step "recipes and custom commands"
bash "$ROOT/tools/recipes-smoke.sh" "$D"
step "browser drops: Safari / Chrome pasteboards → parse → download → petal (local web server)"
"$D" --browser-drop-selftest "$WORK/webtest" | tail -4
cp "$WORK/webtest/results.txt" "$OUT/browser-drop-selftest.txt"

step "PDF: PDFsharp writes, CoreGraphics renders"
"$D" convert smoke.png --to pdf && test -s smoke.pdf
"$D" convert smoke.pdf --to jpg && test -s smoke.jpg

step "OCR with Vision"
printf 'Convertino reads this page\nwithout Tesseract\n' > note.txt
if cupsfilter -i text/plain -m application/pdf note.txt > note.pdf 2>/dev/null && [ -s note.pdf ]; then
  "$D" run ocr note.pdf | tee ocr-pdf.txt
  grep -qi "convertino" ocr-pdf.txt
  "$D" convert note.pdf --to png && test -s note.png
  "$D" run ocr note.png | tee ocr-png.txt
  grep -qi "tesseract" ocr-png.txt
  cp note.png "$OUT/"
else
  echo "::warning::cupsfilter couldn't make a text PDF; OCR not tested"
fi
cp smoke.jpg "$OUT/pdf-render.jpg"

if [ -n "${CUTOUT:-}" ]; then
  step "Cut out (ONNX Runtime on the CPU)"
  "$D" model install | tail -1
  "$D" run cutout smoke.png
  test -s "smoke (cutout).png"
  cp "smoke (cutout).png" "$OUT/cutout.png"
fi

[ "$GUI" = "--gui" ] || exit 0

shot() { screencapture -x "$OUT/$1.png" 2>/dev/null || echo "::warning::screencapture failed ($1)"; }
running() { pgrep -f "Convertino.app/Contents/MacOS/convertino" >/dev/null; }

# CI only: a real Mac keeps its wallpaper and its installed Convertino
if [ -n "${CI:-}" ]; then
step "wallpaper with detail (so the glass blur shows in screenshots)"
python3 - "$WORK/bg.bmp" <<'PY'
import struct, sys
w, h = 1440, 900
rows = []
for y in range(h - 1, -1, -1):          # BMP rows run bottom-up, pixels are BGR
    row = bytearray()
    for x in range(w):
        t = (x + y) / (w + h)
        k = 0.78 if ((x + y) // 22) % 2 else 1.0
        row += bytes((int((240 - 170 * t) * k), int((108 + 40 * t) * k), int((138 + 117 * t) * k)))
    rows.append(bytes(row))
data = b''.join(rows)
head = b'BM' + struct.pack('<IHHI', 54 + len(data), 0, 0, 54) + struct.pack('<IiiHHIIiiII', 40, w, h, 1, 24, 0, len(data), 2835, 2835, 0, 0)
open(sys.argv[1], 'wb').write(head + data)
PY
sips -s format png "$WORK/bg.bmp" --out "$WORK/bg.png" >/dev/null &&
  osascript -e "tell application \"System Events\" to tell every desktop to set picture to \"$WORK/bg.png\"" ||
  echo "::warning::couldn't set the wallpaper"
# GitHub's images ship with Reduce transparency on, which turns every NSVisualEffectView into a flat fill
defaults write com.apple.universalaccess reduceTransparency -bool false 2>/dev/null || true
sleep 2
fi
echo "reduce transparency: $(defaults read com.apple.universalaccess reduceTransparency 2>/dev/null || echo unset)"
system_profiler SPDisplaysDataType 2>/dev/null | grep -E "Chipset|Metal|Resolution" || true

step "editors: save each once (Crop, Redact, Annotate, Adjust, Add BG)"
cp smoke.png edit.png
CONVERTINO_EDIT_SELFTEST="$WORK/edit.png" limit 120 "$D" > /dev/null 2>&1 || true
grep "DEV editors:" "$LOG" | tail -1
if grep "DEV editors:" "$LOG" | tail -1 | grep -q "FAILED"; then echo "an editor failed to save"; exit 1; fi
grep -q "DEV editors:" "$LOG" || { echo "editor self-test didn't run"; exit 1; }

step "app: pinned flower"
CONVERTINO_DEBUG=1 CONVERTINO_PIN=420,360 CONVERTINO_PIN_FILE="$WORK/smoke.png" "$D" > "$OUT/app-stdout.txt" 2>&1 &
sleep 8
running || { echo "app exited:"; cat "$OUT/app-stdout.txt"; cat "$LOG"; exit 1; }
shot 1-pinned

# the name Activity Monitor and the Force Quit list show (LaunchServices display name), not "Avalonia"
asn=$(lsappinfo find pid="$(pgrep -f "Convertino.app/Contents/MacOS/convertino" | head -1)" 2>/dev/null || true)
lsname=$([ -n "$asn" ] && lsappinfo info -only name "$asn" 2>/dev/null || true)
echo "LaunchServices name: ${lsname:-?}"
if [ -z "$lsname" ]; then echo "::warning::lsappinfo couldn't read the app name"
elif ! printf '%s' "$lsname" | grep -q '"Convertino"'; then echo "the process shows up as $lsname, expected Convertino"; exit 1; fi
wid=$(grep -o "overlay window=[0-9]*" "$LOG" | tail -1 | grep -o "[0-9]*$" || true)
[ -n "$wid" ] && screencapture -x -o -l "$wid" "$OUT/1-pinned-window.png" 2>/dev/null || true
grep "overlay window=" "$LOG" | tail -1 || true

step "app: convertino menu (IPC into the running app)"
limit 30 "$D" menu "$WORK/smoke.png"
sleep 2
shot 2-menu
grep -q "menu: " "$LOG"
pkill -f "Convertino.app/Contents/MacOS/convertino" || true
sleep 1

step "app: menu-bar panel (demo activity), pick a theme in it"
SETTINGS="$HOME/Library/Application Support/Convertino/settings.json"
from=$(($(wc -l < "$LOG") + 1))
CONVERTINO_DEBUG=1 CONVERTINO_PANEL=demo "$D" >> "$OUT/app-stdout.txt" 2>&1 &
sleep 6
shot 2b-panel
pwid=$(grep -o "panel window=[0-9]*" "$LOG" | tail -1 | grep -o "[0-9]*$" || true)
if [ -n "$pwid" ]; then
  screencapture -x -o -l "$pwid" "$OUT/2b-panel-window.png" 2>/dev/null || true
  grep "panel window=" "$LOG" | tail -1
else
  echo "::warning::the panel didn't open (no 'panel window=' in the log)"
fi
# the panel logs where its swatches are once it has slid in, and again whenever the window moves, resizes or
# restyles, so the last line is where they are now. Click Latte, then Graphite, the way a person would. Each pick
# also refills the menu-bar menu, so the app has to survive both (the swatches stay put while the panel restyles).
swiftc -O "$ROOT/tools/mac-click.swift" -o "$WORK/click"
swatch() { tail -n +"$from" "$LOG" | grep -o "$1=-*[0-9]*,-*[0-9]*" | tail -1 | cut -d= -f2 || true; }
for theme in Latte Graphite; do
  for _ in 1 2 3 4 5 6 7 8 9 10; do at=$(swatch "$theme"); [ -n "$at" ] && break; sleep 1; done
  [ -n "$at" ] || { echo "the panel logged no swatch positions"; grep "panel" "$LOG" | tail -5; exit 1; }
  tail -n +"$from" "$LOG" | grep "panel at" | tail -1 || true
  limit 20 "$WORK/click" "${at%,*}" "${at#*,}"
  sleep 1.5
  shot "2c-panel-$(echo "$theme" | tr 'A-Z' 'a-z')"
  if ! running || tail -n +"$from" "$LOG" | grep -q "ERR fatal"; then
    echo "the app died after picking $theme in the panel"; tail -n +"$from" "$LOG" | grep -A12 "ERR" | head -30; exit 1
  fi
  if grep -q "panel theme $theme" "$LOG" && grep -q "\"Theme\": \"$theme\"" "$SETTINGS"; then
    echo "$theme picked in the panel: restyled, saved, app still running"
  else
    echo "clicking the $theme swatch didn't switch the theme"; grep -E "panel|button" "$LOG" | tail -10; exit 1
  fi
done
[ -n "$pwid" ] && screencapture -x -o -l "$pwid" "$OUT/2c-panel-graphite-window.png" 2>/dev/null || true
pkill -f "Convertino.app/Contents/MacOS/convertino" || true
sleep 1
sed -i '' 's/"Theme": "Graphite"/"Theme": "Mocha"/' "$SETTINGS"

step "app: Open panel from the menu-bar menu"
CONVERTINO_DEBUG=1 "$D" >> "$OUT/app-stdout.txt" 2>&1 &
sleep 6
pid=$(pgrep -f "Convertino.app/Contents/MacOS/convertino" | head -1 || true)
before=$(grep -c "panel window=" "$LOG" || true)
# the status item lives in the process's last menu bar; System Events can open its menu and click an item
items=$(limit 40 osascript \
  -e "tell application \"System Events\" to tell (first process whose unix id is $pid)" \
  -e "click menu bar item 1 of last menu bar" \
  -e "delay 1" \
  -e "set names to name of menu items of menu 1 of menu bar item 1 of last menu bar" \
  -e "click menu item \"Open panel\" of menu 1 of menu bar item 1 of last menu bar" \
  -e "return names" -e "end tell" 2>&1) || true
echo "menu: $items"
sleep 2
shot 2d-panel-from-menu
after=$(grep -c "panel window=" "$LOG" || true)
case "$items" in "Open panel"*) echo "Open panel is the first item" ;; *) echo "::warning::Open panel isn't the first menu item" ;; esac
if [ "$after" -gt "$before" ]; then echo "Open panel opened the panel"
else echo "::warning::Open panel from the menu didn't open the panel (System Events: $items)"; fi
pkill -f "Convertino.app/Contents/MacOS/convertino" || true
sleep 1

step "app: click the menu-bar item (panel), right-click / Ctrl-click it (menu)"
CONVERTINO_DEBUG=1 "$D" >> "$OUT/app-stdout.txt" 2>&1 &
sleep 6
pid=$(pgrep -f "Convertino.app/Contents/MacOS/convertino" | head -1 || true)
# System Events knows where the item is: points, top-left origin, like CGEvent
item=$(limit 30 osascript \
  -e "tell application \"System Events\" to tell (first process whose unix id is $pid)" \
  -e "set {x, y} to position of menu bar item 1 of last menu bar" \
  -e "set {w, h} to size of menu bar item 1 of last menu bar" \
  -e "return (x as text) & \" \" & (y as text) & \" \" & (w as text) & \" \" & (h as text)" -e "end tell" 2>&1) || true
echo "menu-bar item (x y w h): $item"
read -r ix iy iw ih _ <<< "$item"
centre=$(awk -v x="$ix" -v y="$iy" -v w="$iw" -v h="$ih" 'BEGIN { if (w > 0 && h > 0) printf "%d %d", x + w / 2, y + h / 2 }')
[ -n "$centre" ] || { echo "System Events gave no position for the menu-bar item"; exit 1; }
read -r cx cy <<< "$centre"
count() { grep -c "$1" "$LOG" || true; }
visible() { grep -o "panel visible=[A-Za-z]*" "$LOG" | tail -1 | cut -d= -f2; }
fail() { echo "$1"; grep -E "panel|status item|tray menu|button" "$LOG" | tail -12; exit 1; }
before=$(count "panel window=")
limit 20 "$WORK/click" "$cx" "$cy"
sleep 2
shot 2e-panel-from-click
[ "$(count "panel window=")" -gt "$before" ] || fail "a click on the menu-bar item didn't open the panel"
[ "$(visible)" = True ] || fail "the panel opened from the menu-bar item closed again by itself"
echo "a click on the menu-bar item opened the panel (still open 2 s later)"
# a second click closes it again
limit 20 "$WORK/click" "$cx" "$cy"
sleep 1.5
[ "$(visible)" = False ] || fail "a second click on the menu-bar item didn't close the panel"
echo "a second click closed it"
for how in right ctrl; do
  # the menu takes the panel's place
  limit 20 "$WORK/click" "$cx" "$cy"
  sleep 1.5
  opened=$(count "tray menu opening")
  limit 20 "$WORK/click" "$cx" "$cy" "$how"
  sleep 1.5
  shot "2f-menu-from-$how-click"
  limit 10 osascript -e 'tell application "System Events" to key code 53' || true   # Esc closes the menu
  sleep 1
  if [ "$(count "tray menu opening")" -gt "$opened" ]; then echo "$how-click opened the menu"
  else echo "::warning::$how-click on the menu-bar item didn't open the menu"; fi
  [ "$(visible)" = False ] && echo "and closed the panel" || echo "::warning::the panel stayed open under the $how-click menu"
done
grep -E "status item|tray menu|panel visible" "$LOG" | tail -12 || true
pkill -f "Convertino.app/Contents/MacOS/convertino" || true
sleep 1

step "app: pinned flower without the native glass (comparison)"
CONVERTINO_DEBUG=1 CONVERTINO_NOVIBRANCY=1 CONVERTINO_PIN=420,360 CONVERTINO_PIN_FILE="$WORK/smoke.png" "$D" >> "$OUT/app-stdout.txt" 2>&1 &
sleep 6
shot 1b-pinned-novibrancy
grep "overlay window=" "$LOG" | tail -1 || true
pkill -f "Convertino.app/Contents/MacOS/convertino" || true
sleep 1

step "app: drag a file, tap Shift, drop on a petal (synthetic input)"
CONVERTINO_DEBUG=1 "$D" >> "$OUT/app-stdout.txt" 2>&1 &
sleep 6
cp smoke.png drag.png
swiftc -O "$ROOT/tools/mac-drag-test.swift" -o "$WORK/dragtest"
limit 60 "$WORK/dragtest" "$WORK/drag.png" "$OUT/4-drag-over-petal.png" || echo "::warning::dragtest exited with $?"
sleep 2
shot 5-after-drop
grep -E "INF ring [0-9]" "$LOG" | tail -8 || true
if grep -q "(wheel)" "$LOG"; then echo "the wheel flipped the rings during the drag"
else echo "::warning::the wheel didn't flip the rings during the synthetic drag"; fi
if [ -s "$WORK/drag.jpg" ]; then
  echo "drop on the first petal converted drag.png → drag.jpg"
else
  echo "the synthetic drag didn't produce drag.jpg"
  grep -E "shift|drag|drop|show|mac " "$LOG" | tail -30 || true
  exit 1
fi
pkill -f "Convertino.app/Contents/MacOS/convertino" || true
sleep 1

step "app: drag a picture out of a web page (file promise + link), tap Shift, drop on JPG"
CONVERTINO_DEBUG=1 "$D" >> "$OUT/app-stdout.txt" 2>&1 &
sleep 6
DL="$HOME/Downloads"
mkdir -p "$DL"
rm -f "$DL"/cvt-promise*
cp smoke.png cvt-promise.png
swiftc -O "$ROOT/tools/mac-web-drag.swift" -o "$WORK/webdrag"
# the link points nowhere: a result in Downloads can only have come through the promise
limit 60 "$WORK/webdrag" "$WORK/cvt-promise.png" "https://example.invalid/cvt-promise.png" 1 6 "$OUT/7-web-drag-over-petal.png" || echo "::warning::webdrag exited with $?"
sleep 4
shot 8-after-web-drop
ls -l "$DL"/cvt-promise* 2>/dev/null || true
if [ -s "$DL/cvt-promise.png" ] && [ -s "$DL/cvt-promise.jpg" ]; then
  echo "the promised picture arrived in Downloads and was converted there"
  xattr -p com.apple.quarantine "$DL/cvt-promise.jpg" || echo "::warning::no quarantine mark on the result"
else
  echo "::warning::the file-promise drag didn't land in Downloads"
fi
grep -E "dragenter|drop |file promise|browser|job " "$LOG" | tail -12 || true
rm -f "$DL"/cvt-promise*
pkill -f "Convertino.app/Contents/MacOS/convertino" || true
sleep 1

if [ -n "${CI:-}" ]; then
step "install.sh + Finder Quick Action"
STAGE="$(mktemp -d)"
cp -R "$APP" "$STAGE/"
xattr -cr "$STAGE/Convertino.app"   # as if unpacked with plain unzip: install.sh has to re-sign it
cp "$ROOT/packaging/macos/install.sh" "$STAGE/"
# show the first-run card again (the installed app is launched by macOS, so it checks Input Monitoring for itself)
sed -i '' 's/"FirstRunDone": true/"FirstRunDone": false/' "$HOME/Library/Application Support/Convertino/settings.json" || true
"$STAGE/install.sh"
sleep 6
running
test -f "$HOME/Library/LaunchAgents/io.github.hasanzadee.convertino.plist"
codesign --verify --strict "$HOME/Applications/Convertino.app"
plutil -lint "$HOME/Library/Services/Convertino.workflow/Contents/document.wflow" "$HOME/Library/Services/Convertino.workflow/Contents/Info.plist"
before=$(grep -c "menu: " "$LOG" || true)
limit 60 automator -i "$WORK/smoke.png" "$HOME/Library/Services/Convertino.workflow" || echo "::warning::automator returned $?"
sleep 3
shot 3-quick-action
after=$(grep -c "menu: " "$LOG" || true)
[ "$after" -gt "$before" ] || { echo "Quick Action didn't reach the app"; exit 1; }
pkill -f "Convertino.app/Contents/MacOS/convertino" || true   # close the click-mode flower
sleep 1
open "$HOME/Applications/Convertino.app"
sleep 5

step "installed app (launched by macOS, $(grep -o 'input-monitoring=[A-Za-z]*' "$LOG" | tail -1)): drag + Shift + drop"
cp smoke.png drag2.png
limit 60 "$WORK/dragtest" "$WORK/drag2.png" "$OUT/6-installed-drag-over-petal.png" || echo "::warning::dragtest exited with $?"
sleep 2
if [ -s "$WORK/drag2.jpg" ]; then echo "drop on the first petal converted drag2.png → drag2.jpg"
else echo "::warning::the installed app didn't take the synthetic drop"; fi
"$STAGE/install.sh" --uninstall
test ! -e "$HOME/Applications/Convertino.app"
else
  echo "install.sh / Quick Action steps run in CI only (they would replace your installed Convertino)"
fi

cp "$LOG" "$OUT/convertino.log"
step "done"
