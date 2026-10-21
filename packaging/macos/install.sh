#!/usr/bin/env bash
# Convertino installer for macOS — per-user, no admin rights needed.
#   ./install.sh            install Convertino.app from this folder (release zip) or build it from source
#   ./install.sh --uninstall
set -euo pipefail

DEST="$HOME/Applications"
APP="$DEST/Convertino.app"
BIN_DIR="$HOME/.local/bin"
SERVICE="$HOME/Library/Services/Convertino.workflow"
AGENT="$HOME/Library/LaunchAgents/io.github.hasanzadee.convertino.plist"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

say() { printf '\033[1;35m✿\033[0m %s\n' "$*"; }

stop_running() {
  pkill -f "Convertino.app/Contents/MacOS/convertino" 2>/dev/null || true
  sleep 0.5
}

refresh_services() { /System/Library/CoreServices/pbs -update >/dev/null 2>&1 || true; }

if [ "${1:-}" = "--uninstall" ]; then
  launchctl bootout "gui/$(id -u)/io.github.hasanzadee.convertino" 2>/dev/null || true
  stop_running
  rm -rf "$APP" "$SERVICE" "$AGENT" "$BIN_DIR/convertino"
  refresh_services
  say "Convertino removed (settings kept in ~/Library/Application Support/Convertino)."
  exit 0
fi

# 1. get the app: a release zip has ./Convertino.app, a source checkout gets built
if [ -d "$HERE/Convertino.app" ]; then
  SRC="$HERE/Convertino.app"
elif [ -f "$HERE/../../src/Convertino.Mac/Convertino.Mac.csproj" ]; then
  command -v dotnet >/dev/null || { echo "Building from source needs the .NET 10 SDK (https://dot.net)."; exit 1; }
  arch="$( [ "$(uname -m)" = arm64 ] && echo arm64 || echo x64 )"
  say "Building Convertino ($arch)…"
  "$HERE/build-app.sh" "$arch" "$HERE/../../dist/macos-$arch" >/dev/null
  SRC="$HERE/../../dist/macos-$arch/Convertino.app"
else
  echo "Can't find Convertino.app next to this script."; exit 1
fi

stop_running
say "Installing to $APP"
mkdir -p "$DEST" "$BIN_DIR"
rm -rf "$APP"
cp -R "$SRC" "$APP"
# downloaded apps are quarantined; Convertino isn't notarized, so lift it for this copy only
xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true
# a copy dragged into /Applications by hand (browser download) would ask "Apple could not verify…"; it's the same app
if [ -d /Applications/Convertino.app ] && [ "/Applications/Convertino.app" != "$APP" ]; then
  xattr -dr com.apple.quarantine /Applications/Convertino.app 2>/dev/null || true
  say "There is another Convertino.app in /Applications (copied by hand); this one runs from $APP. You can delete the other one."
fi
# the managed .dll signatures live in extended attributes; a zip unpacked without them (plain `unzip`) breaks the
# bundle's seal — sign this copy again, ad hoc, like the release build does
if ! codesign --verify --strict "$APP" >/dev/null 2>&1; then
  say "Re-signing Convertino.app (ad hoc)…"
  find "$APP" -name '._*' -delete
  find "$APP/Contents/MacOS" -type f ! -name convertino -print0 | xargs -0 codesign --force --sign - --timestamp=none 2>/dev/null || true
  codesign --force --sign - --timestamp=none "$APP" 2>/dev/null || true
fi

cat > "$BIN_DIR/convertino" <<S
#!/bin/sh
exec "$APP/Contents/MacOS/convertino" "\$@"
S
chmod +x "$BIN_DIR/convertino"

# 2. Finder → right-click → Quick Actions → Convertino (also in the Services menu)
mkdir -p "$SERVICE/Contents"
cat > "$SERVICE/Contents/Info.plist" <<'S'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>NSServices</key>
  <array>
    <dict>
      <key>NSBackgroundColorName</key><string>background</string>
      <key>NSIconName</key><string>NSActionTemplate</string>
      <key>NSMenuItem</key><dict><key>default</key><string>Convertino</string></dict>
      <key>NSMessage</key><string>runWorkflowAsService</string>
      <key>NSRequiredContext</key><dict><key>NSApplicationIdentifier</key><string>com.apple.finder</string></dict>
      <key>NSSendFileTypes</key><array><string>public.item</string></array>
    </dict>
  </array>
</dict>
</plist>
S
# the shell step; "convertino menu" starts the app first if it isn't running
script="exec \"$APP/Contents/MacOS/convertino\" menu \"\$@\""
cat > "$SERVICE/Contents/document.wflow" <<S
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>AMApplicationBuild</key><string>523</string>
  <key>AMApplicationVersion</key><string>2.10</string>
  <key>AMDocumentVersion</key><string>2</string>
  <key>actions</key>
  <array>
    <dict>
      <key>action</key>
      <dict>
        <key>AMAccepts</key>
        <dict>
          <key>Container</key><string>List</string>
          <key>Optional</key><true/>
          <key>Types</key><array><string>com.apple.cocoa.string</string></array>
        </dict>
        <key>AMActionVersion</key><string>2.0.3</string>
        <key>AMApplication</key><array><string>Automator</string></array>
        <key>AMParameterProperties</key>
        <dict>
          <key>COMMAND_STRING</key><dict/>
          <key>CheckedForUserDefaultShell</key><dict/>
          <key>inputMethod</key><dict/>
          <key>shell</key><dict/>
          <key>source</key><dict/>
        </dict>
        <key>AMProvides</key>
        <dict>
          <key>Container</key><string>List</string>
          <key>Types</key><array><string>com.apple.cocoa.string</string></array>
        </dict>
        <key>ActionBundlePath</key><string>/System/Library/Automator/Run Shell Script.action</string>
        <key>ActionName</key><string>Run Shell Script</string>
        <key>ActionParameters</key>
        <dict>
          <key>COMMAND_STRING</key><string>$script</string>
          <key>CheckedForUserDefaultShell</key><true/>
          <key>inputMethod</key><integer>1</integer>
          <key>shell</key><string>/bin/bash</string>
          <key>source</key><string></string>
        </dict>
        <key>BundleIdentifier</key><string>com.apple.RunShellScript</string>
        <key>CFBundleVersion</key><string>2.0.3</string>
        <key>CanShowSelectedItemsWhenRun</key><false/>
        <key>CanShowWhenRun</key><true/>
        <key>Category</key><array><string>AMCategoryUtilities</string></array>
        <key>Class Name</key><string>RunShellScriptAction</string>
        <key>InputUUID</key><string>6A1E4D2C-9B7F-4E1A-8C3D-2F5B7A9C1E01</string>
        <key>Keywords</key><array><string>Shell</string><string>Script</string><string>Command</string><string>Run</string><string>Unix</string></array>
        <key>OutputUUID</key><string>6A1E4D2C-9B7F-4E1A-8C3D-2F5B7A9C1E02</string>
        <key>UUID</key><string>6A1E4D2C-9B7F-4E1A-8C3D-2F5B7A9C1E03</string>
        <key>UnlocalizedApplications</key><array><string>Automator</string></array>
        <key>arguments</key>
        <dict>
          <key>0</key><dict><key>default value</key><integer>0</integer><key>name</key><string>inputMethod</string><key>required</key><string>0</string><key>type</key><string>0</string><key>uuid</key><string>0</string></dict>
          <key>1</key><dict><key>default value</key><false/><key>name</key><string>CheckedForUserDefaultShell</string><key>required</key><string>0</string><key>type</key><string>0</string><key>uuid</key><string>1</string></dict>
          <key>2</key><dict><key>default value</key><string></string><key>name</key><string>source</string><key>required</key><string>0</string><key>type</key><string>0</string><key>uuid</key><string>2</string></dict>
          <key>3</key><dict><key>default value</key><string></string><key>name</key><string>COMMAND_STRING</string><key>required</key><string>0</string><key>type</key><string>0</string><key>uuid</key><string>3</string></dict>
          <key>4</key><dict><key>default value</key><string>/bin/sh</string><key>name</key><string>shell</string><key>required</key><string>0</string><key>type</key><string>0</string><key>uuid</key><string>4</string></dict>
        </dict>
        <key>isViewVisible</key><integer>1</integer>
        <key>location</key><string>309.000000:253.000000</string>
        <key>nibPath</key><string>/System/Library/Automator/Run Shell Script.action/Contents/Resources/Base.lproj/main.nib</string>
      </dict>
      <key>isViewVisible</key><integer>1</integer>
    </dict>
  </array>
  <key>connectors</key><dict/>
  <key>workflowMetaData</key>
  <dict>
    <key>applicationBundleID</key><string>com.apple.finder</string>
    <key>applicationBundleIDsByPath</key><dict><key>/System/Library/CoreServices/Finder.app</key><string>com.apple.finder</string></dict>
    <key>applicationPath</key><string>/System/Library/CoreServices/Finder.app</string>
    <key>applicationPaths</key><array><string>/System/Library/CoreServices/Finder.app</string></array>
    <key>inputTypeIdentifier</key><string>com.apple.Automator.fileSystemObject</string>
    <key>outputTypeIdentifier</key><string>com.apple.Automator.nothing</string>
    <key>presentationMode</key><integer>15</integer>
    <key>processesInput</key><false/>
    <key>serviceApplicationBundleID</key><string>com.apple.finder</string>
    <key>serviceApplicationPath</key><string>/System/Library/CoreServices/Finder.app</string>
    <key>serviceInputTypeIdentifier</key><string>com.apple.Automator.fileSystemObject</string>
    <key>serviceOutputTypeIdentifier</key><string>com.apple.Automator.nothing</string>
    <key>serviceProcessesInput</key><false/>
    <key>systemImageName</key><string>NSActionTemplate</string>
    <key>useAutomaticInputType</key><false/>
    <key>workflowTypeIdentifier</key><string>com.apple.Automator.servicesMenu</string>
  </dict>
</dict>
</plist>
S
refresh_services

# 3. optional helpers
missing=""
for t in ffmpeg tesseract; do
  command -v "$t" >/dev/null || [ -x "/opt/homebrew/bin/$t" ] || [ -x "/usr/local/bin/$t" ] || missing="$missing $t"
done
if [ -n "$missing" ]; then say "Optional extras not found:$missing (video/audio petals; OCR fallback)"; echo "   brew install$missing"; fi
if [ ! -d /Applications/LibreOffice.app ] && [ ! -d "$HOME/Applications/LibreOffice.app" ]; then
  say "Office petals need LibreOffice:"; echo "   brew install --cask libreoffice"
fi

case ":$PATH:" in *":$BIN_DIR:"*) ;; *) say "Add $BIN_DIR to your PATH to use the 'convertino' command (echo 'export PATH=\"\$HOME/.local/bin:\$PATH\"' >> ~/.zshrc).";; esac

# 4. start it; on first run it adds itself to login items (a LaunchAgent) and asks for Input Monitoring
open "$APP"
say "Convertino is in the menu bar — drag any file in Finder and tap Shift."
say "If nothing happens, allow Convertino in System Settings → Privacy & Security → Input Monitoring, then restart it."
