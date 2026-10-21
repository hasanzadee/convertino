#!/usr/bin/env bash
# One-line install / update of the latest Convertino on macOS:
#   curl -fsSL https://raw.githubusercontent.com/hasanzadee/convertino/main/packaging/macos/get.sh | bash
# Downloads the release for this Mac's chip with curl (so macOS doesn't mark it as "downloaded from the internet"
# and never shows "Apple could not verify Convertino"), then runs the release's own install.sh.
set -euo pipefail

case "$(uname -m)" in
  arm64) arch=arm64 ;;
  x86_64) arch=x64 ;;
  *) echo "Unsupported Mac: $(uname -m)" >&2; exit 1 ;;
esac

work="$(mktemp -d "${TMPDIR:-/tmp}/convertino.XXXXXX")"
trap 'rm -rf "$work"' EXIT

url="https://github.com/hasanzadee/convertino/releases/latest/download/convertino-macos-$arch.zip"
echo "Downloading $url"
curl -fL --progress-bar -o "$work/convertino.zip" "$url"
ditto -x -k "$work/convertino.zip" "$work"
bash "$work/convertino/install.sh"
