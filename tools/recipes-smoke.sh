#!/usr/bin/env bash
# Recipes, custom commands and "Save results" through the CLI (Linux and macOS CI):  tools/recipes-smoke.sh path/to/convertino
# Saves recipes (and refuses broken ones), runs them, runs commands with hostile file names, checks that only final
# results land and the work folders go. Works in a temporary folder with its own settings and recipes.json
# (CONVERTINO_APPDATA), so the real ones are never touched.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
C="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
W="$(mktemp -d)"
export CONVERTINO_APPDATA="$W/appdata"
TMP="${TMPDIR:-/tmp}"
cd "$W"
cp "$ROOT/packaging/linux/icons/convertino-256.png" photo.png
fail() { echo "FAIL: $*"; exit 1; }
no_work_left() { test -z "$(ls -d "$TMP"/convertino/recipe-* 2>/dev/null)" || fail "a recipe work folder was left in $TMP/convertino"; }

# commands only ever come from recipes.json, written by hand (comments allowed)
mkdir -p "$CONVERTINO_APPDATA"
cat > "$CONVERTINO_APPDATA/recipes.json" <<'JSON'
{
  // your own programs as petals
  "commands": [
    { "name": "Copy", "for": ["png", "webp"], "program": "cp", "args": ["{in}", "{out}"], "output": "{name} (copy).{ext}" },
    { "name": "Fails", "program": "sh", "args": ["-c", "echo boom >&2; exit 3"] },
    { "name": "Slow", "program": "sleep", "args": ["30"], "timeout": 1 },
    { "name": "Missing", "program": "convertino-no-such-program", "args": ["{in}"] },
  ],
}
JSON

echo "== a recipe is checked when it's saved"
"$C" recipe add Web-ready --steps fithd,to-webp,meta | tee add.log
grep -q "for images" add.log
if "$C" recipe add Broken --steps path,to-webp; then fail "a clipboard step in the middle was saved"; fi
if "$C" recipe add Broken --steps to-pdf,square; then fail "Square after PDF was saved"; fi
if "$C" recipe add Broken --steps fithd,crop; then fail "an editor was saved as a step"; fi
if "$C" recipe add Broken --steps no-such-petal; then fail "an unknown petal was saved"; fi
"$C" recipe list | tee list.log
grep -q "Full HD → WEBP → Clean" list.log
grep -q "convertino-no-such-program not found" list.log
"$C" actions photo.png | tee actions.log
grep -q "recipes: recipe-web-ready" actions.log
grep -q "cmd-copy" actions.log

echo "== only the final result lands, and the work folder goes"
"$C" recipe run Web-ready photo.png
test -s "photo (Web-ready).webp"
test "$(ls | grep -c '\.webp$')" -eq 1 || fail "intermediate files next to the original"
no_work_left
"$C" run recipe-web-ready photo.png
test -s "photo (Web-ready) (2).webp"

echo "== a recipe's own results folder"
"$C" recipe add "Insta square" --steps square,to-jpg --out Squares
"$C" recipe run "Insta square" photo.png
test -s "Squares/photo (Insta square).jpg"

echo "== custom commands: arguments go over one by one, never through a shell"
cp photo.png 'x;touch pwned.png'
cp photo.png '$(touch pwned2).png'
cp photo.png ./-rf.png
"$C" run cmd-copy 'x;touch pwned.png' '$(touch pwned2).png' ./-rf.png
test -s 'x;touch pwned (copy).png'
test -s '$(touch pwned2) (copy).png'
test -s ./'-rf (copy).png'
test ! -e pwned && test ! -e pwned.png && test ! -e pwned2 || fail "a file name ran as a command"
if out=$("$C" run cmd-fails photo.png); then fail "a failing command passed"; fi
echo "$out"; echo "$out" | grep -q "boom"
if out=$("$C" run cmd-missing photo.png); then fail "a missing program passed"; fi
echo "$out"; echo "$out" | grep -q "not found"
start=$(date +%s)
if out=$("$C" run cmd-slow photo.png); then fail "the timeout didn't stop it"; fi
echo "$out"; echo "$out" | grep -q "took too long"
test $(( $(date +%s) - start )) -lt 20

echo "== a command as a step, and a step that fails"
"$C" recipe add Copy-webp --steps cmd-copy,to-webp
"$C" recipe run Copy-webp photo.png
test -s "photo (Copy-webp).webp"
"$C" recipe add Boom --steps to-webp,cmd-fails
if out=$("$C" recipe run Boom photo.png); then fail "a recipe with a failing step passed"; fi
echo "$out"; echo "$out" | grep -q "stopped at step 2"
test ! -e "photo (Boom).webp"
no_work_left

echo "== Save results: a subfolder, then a fixed folder"
echo '{ "SaveTo": "subfolder", "SaveSubfolder": "Results" }' > "$CONVERTINO_APPDATA/settings.json"
"$C" convert photo.png --to avif
test -s Results/photo.avif && test ! -e photo.avif
mkdir -p "$W/fixed"
echo "{ \"SaveTo\": \"folder\", \"SaveFolder\": \"$W/fixed\" }" > "$CONVERTINO_APPDATA/settings.json"
"$C" recipe run Web-ready photo.png
test -s "$W/fixed/photo (Web-ready).webp"
rm "$CONVERTINO_APPDATA/settings.json"

echo "== an unreadable recipes.json is kept, not overwritten"
echo '{ "recipes": [ broken' > "$CONVERTINO_APPDATA/recipes.json"
"$C" recipe list | grep -q "can't be read"
"$C" recipe add Again --steps to-webp
ls "$CONVERTINO_APPDATA" | grep -q 'recipes.json.bad-'
"$C" recipe remove Again
if "$C" recipe remove Again; then fail "removed a recipe twice"; fi

echo "recipes smoke: all good"
