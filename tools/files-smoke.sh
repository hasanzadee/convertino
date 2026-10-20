#!/usr/bin/env bash
# Archives and file tools through the CLI (Linux and macOS CI):  tools/files-smoke.sh path/to/convertino
# Packs a folder as TAR.GZ and 7Z and extracts both, refuses a "../" entry, splits and joins a file,
# finds duplicates and verifies checksums. Works in a temporary folder.
set -euo pipefail

C="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
W="$(mktemp -d)"
cd "$W"
sha() { if command -v sha256sum >/dev/null; then sha256sum "$@"; else shasum -a 256 "$@"; fi; }

mkdir -p "src/sub/Документы"
head -c 3000000 /dev/urandom > src/sub/random.bin
echo "salam dünya" > "src/sub/Документы/ağac.txt"
cp src/sub/random.bin "src/copy of random.bin"

echo "== pack and extract"
"$C" convert src --to tar.gz
"$C" convert src --to 7z
mv src orig
"$C" run extract src.tar.gz
cmp orig/sub/random.bin src/sub/random.bin
"$C" run extract src.7z
cmp "orig/sub/Документы/ağac.txt" "src (2)/sub/Документы/ağac.txt"

echo "== a ../ entry is refused"
mkdir -p evil/inner && echo pwned > evil/escaped.txt
(cd evil/inner && tar -cPf ../../evil.tar ../escaped.txt)   # -P keeps the "../" (GNU tar and bsdtar)
tar -tPf evil.tar | grep -q '^\.\./escaped.txt$'
rm -rf evil
"$C" run extract evil.tar | tee evil.log
grep -q "refused" evil.log
test ! -e escaped.txt

echo "== split and join"
head -c 30000000 /dev/urandom > big.bin
"$C" run split-25mb big.bin
test -s big.bin.002
test -s big.bin.sha256
mv big.bin big.orig
"$C" run join big.bin.001
cmp big.bin big.orig
rm big.bin
mv big.bin.002 part2.away
if "$C" run join big.bin.001; then echo "joined with a part missing"; exit 1; fi
test ! -e big.bin
mv part2.away big.bin.002
cp big.orig big.bin

echo "== duplicates"
"$C" run dupes orig | tee dupes.log
grep -q "1 duplicate group" dupes.log
test -s "orig duplicates.html"

echo "== checksums"
sha big.bin > big.bin.sha256
"$C" run verify big.bin big.bin.sha256 | tee ok.log
grep -q "Matches" ok.log
echo "0000000000000000000000000000000000000000000000000000000000000000  big.bin" > bad.sha256
if "$C" run verify big.bin bad.sha256; then echo "a wrong checksum passed"; exit 1; fi

cd / && rm -rf "$W"
echo "files smoke: ok"
