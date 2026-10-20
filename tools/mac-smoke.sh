#!/usr/bin/env bash
# Smoke test for the macOS build (used by CI; works on any Mac):
#   tools/mac-smoke.sh dist/Convertino.app          command line: conversions, CoreGraphics PDF, Vision OCR (CUTOUT=1: Cut out
#                                                 on Core ML and on the CPU, compared)
#   tools/mac-smoke.sh dist/Convertino.app --gui    + editors, pinned flower, "convertino menu" over IPC, a synthetic drag + Shift
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
  step "Cut out: Core ML (compile, then from the cache + timed against the CPU once) vs the CPU"
  "$D" model install | tail -1
  # the test picture: the app icon (a known subject) over a full-size desktop picture, or over nothing
  swiftc -O "$ROOT/tools/mac-cutout-diff.swift" -o "$WORK/cutdiff"
  bg="$(find "/System/Library/Desktop Pictures" -maxdepth 1 -type f \( -iname '*.heic' -o -iname '*.jpg' -o -iname '*.png' \) 2>/dev/null | sort | head -1 || true)"
  if [ -n "$bg" ] && sips -s format png "$bg" --out scene-bg.png >/dev/null 2>&1; then echo "background: $bg"
  else echo "background: none found, plain white"; cp smoke.png scene-bg.png; fi
  "$WORK/cutdiff" --scene scene-bg.png "$ROOT/packaging/linux/icons/convertino-512.png" cut.png
  cp cut.png "$OUT/cutout-input.png"
  MODELS="$HOME/Library/Application Support/Convertino/models"
  rm -rf "$MODELS"/coreml-*
  n0=0; [ -f "$LOG" ] && n0=$(wc -l < "$LOG")
  cutout() { "$D" run cutout cut.png; mv "cut (cutout).png" "$OUT/cutout-$1.png"; }
  CONVERTINO_CUTOUT_COREML=1 cutout coreml-cold        # first load: compiles the Core ML programs
  du -sh "$MODELS"/coreml-* || true                    # ORT keeps each partition's ML package and compiled model
  echo "partitions: $(ls -d "$MODELS"/coreml-*/*/*/ 2>/dev/null | wc -l)"
  du -sch "$MODELS"/coreml-*/*/*/model/compiled_model.mlmodelc 2>/dev/null | tail -1 | sed 's/total/of that compiled models/' || true
  cutout coreml                                        # from the cache, then timed against the CPU once
  CONVERTINO_CUTOUT_CPU=1 cutout cpu
  cutout auto                                          # whichever won on this Mac
  echo "choice: $(cat "$MODELS"/coreml-*/provider.txt 2>/dev/null || echo none)"
  tail -n +"$((n0 + 1))" "$LOG" | grep "cutout:" | grep -v "onnxruntime: Some nodes\|onnxruntime: Rerunning" | tee "$OUT/cutout-log.txt"
  [ "$(grep -c "inference on CoreML" "$OUT/cutout-log.txt")" -ge 2 ] || { echo "Cut out didn't run on Core ML"; exit 1; }
  if grep -qE "Core ML unavailable|retrying on CPU" "$OUT/cutout-log.txt"; then echo "Core ML failed and the CPU took over"; exit 1; fi
  grep -q "program from the cache" "$OUT/cutout-log.txt" || echo "::warning::the second Core ML load didn't use the compiled-model cache"
  grep -q "from now on" "$OUT/cutout-log.txt" || { echo "Core ML wasn't timed against the CPU"; exit 1; }
  "$WORK/cutdiff" "$OUT/cutout-coreml.png" "$OUT/cutout-cpu.png" "$OUT/cutout-compare.png" "Core ML" "CPU"
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
if [ -s "$WORK/drag.jpg" ]; then
  echo "drop on the first petal converted drag.png → drag.jpg"
else
  echo "the synthetic drag didn't produce drag.jpg"
  grep -E "shift|drag|drop|show|mac " "$LOG" | tail -30 || true
  exit 1
fi
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
