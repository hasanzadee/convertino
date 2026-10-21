#!/usr/bin/env bash
# "convertino clip" against a real X11 clipboard (used by CI; needs DISPLAY and xclip):
#   tools/clip-smoke.sh path/to/convertino
# Text, a link, spreadsheet cells, a PNG picture and copied files (text/uri-list) go through the same reader the app uses.
set -euo pipefail
C="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
W="$(mktemp -d)"; cd "$W"
XPID=
# xclip stays in the foreground (-quiet) and serves the selection until the next one replaces it
put() { [ -n "$XPID" ] && kill "$XPID" 2>/dev/null || true; xclip -quiet -selection clipboard -t "$1" -i "$2" >/dev/null 2>&1 & XPID=$!; sleep 0.4; }
last() { tail -1 "$1"; }
magic() { head -c 3 "$1" | od -An -tx1 | tr -d ' \n'; }   # 89504e = PNG, ffd8ff = JPEG

printf 'Meeting notes\n- ship 1.4\n' > text.txt
put UTF8_STRING text.txt
"$C" clip --out out | tee o1; cmp text.txt "$(last o1)"
"$C" clip --to md --out out | tee o2; test -s "$(last o2)"
case "$(last o2)" in */Clipboard\ ????-??-??\ ??.??.??*.md) ;; *) echo "unexpected name: $(last o2)"; exit 1 ;; esac

printf 'https://github.com/hasanzadee/convertino' > link.txt
put UTF8_STRING link.txt
"$C" clip --to qr --out out | tee o3; [ "$(magic "$(last o3)")" = 89504e ]
"$C" run readqr "$(last o3)" | grep -q 'github.com/hasanzadee/convertino'

printf 'Name\tQty\nPears, ripe\t5\n' > cells.txt
put UTF8_STRING cells.txt
"$C" clip --to csv --out out | tee o4; grep -q '"Pears, ripe",5' "$(last o4)"

cp "$ROOT/packaging/linux/icons/convertino-256.png" pic.png
put image/png pic.png
"$C" clip --out out | tee o5; cmp pic.png "$(last o5)"
"$C" clip --to jpg --out out | tee o6; [ "$(magic "$(last o6)")" = ffd8ff ]

cp pic.png copied.png
printf 'file://%s/copied.png\n' "$PWD" > uris.txt
put text/uri-list uris.txt
"$C" clip | tee o7; grep -qx "$PWD/copied.png" o7
"$C" clip --to webp | tee o8; test -s copied.webp

kill "$XPID" 2>/dev/null || true
echo "clip smoke: OK"
