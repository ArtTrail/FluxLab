#!/bin/bash
# FluxLab macOS build (osx-arm64 or osx-x64). Runs entirely over SSH -- no local Terminal step
# (DMG via dmgbuild, not create-dmg, so no Finder/Automation permission needed).
# Adapted from VariLab/TransitLab's proven publish_mac.sh.
#
# Usage:  ./build_mac.sh osx-arm64     (or osx-x64)
# Pulls the FluxLab source fresh from GitHub each run, so the exe is FluxLab.exe/FluxLab.

set -e
ARCH="${1:?usage: build_mac.sh osx-arm64|osx-x64}"

# Common .NET / homebrew locations (SSH non-login shell has a bare PATH). The last entry is the
# pip --user console-scripts dir (where `dmgbuild` lands), resolved dynamically per Python version.
export PATH="$PATH:/usr/local/share/dotnet:$HOME/.dotnet:/opt/homebrew/bin:$(python3 -m site --user-base 2>/dev/null)/bin"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO="$SCRIPT_DIR/repo"
VERSION="2.2.2"
APP_NAME="FluxLab"
# Source lives in the per-version folder (per-version subfolders started at v2.2.2; v2.2.0..v2.2.1
# shared the "Version 2.2.0" folder). Update SRCDIR on each new version.
SRCDIR="Version 2.2.2"

echo ""
echo "FluxLab v$VERSION -- macOS build ($ARCH)"
echo "----------------------------------------"

# ── Source: clone or update from GitHub ──────────────────────────────────────
if [ -d "$REPO/.git" ]; then
    echo "Updating source..."
    git -C "$REPO" fetch --quiet origin
    git -C "$REPO" reset --hard --quiet origin/master
else
    echo "Cloning source..."
    rm -rf "$REPO"
    git clone --quiet --depth 1 https://github.com/ArtTrail/FluxLab.git "$REPO"
fi

PROJECT="$REPO/$SRCDIR/SimpleFitsViewer/SimpleFitsViewer.csproj"
PUBLISH_OUT="$REPO/$SRCDIR/SimpleFitsViewer/bin/Release/net8.0/$ARCH/publish"
ICON_SRC="$REPO/$SRCDIR/SimpleFitsViewer/Assets/FluxLab_logo.png"
LICENSE_SRC="$REPO/LICENSE"
DIST="$SCRIPT_DIR/out/$ARCH"
APP_BUNDLE="$DIST/$APP_NAME.app"

if ! command -v dotnet &> /dev/null; then echo "ERROR: .NET SDK not found on PATH."; exit 1; fi
echo "dotnet: $(command -v dotnet)  ($(dotnet --version))"
if ! command -v dmgbuild &> /dev/null; then echo "Installing dmgbuild..."; pip3 install dmgbuild; fi

# ── Build ────────────────────────────────────────────────────────────────────
echo "Building..."
dotnet publish "$PROJECT" -c Release -r "$ARCH" --self-contained true --verbosity minimal

# ── Assemble .app bundle ─────────────────────────────────────────────────────
echo "Assembling .app bundle..."
rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS" "$APP_BUNDLE/Contents/Resources"
cp -R "$PUBLISH_OUT/"* "$APP_BUNDLE/Contents/MacOS/"

cat > "$APP_BUNDLE/Contents/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>FluxLab</string>
    <key>CFBundleDisplayName</key><string>FluxLab</string>
    <key>CFBundleIdentifier</key><string>com.arttrail.fluxlab</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleExecutable</key><string>FluxLab</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
    <key>NSPrincipalClass</key><string>NSApplication</string>
</dict>
</plist>
EOF

# ── Icon (from the 1024px master PNG) ────────────────────────────────────────
echo "Converting icon..."
ICONSET="$DIST/AppIcon.iconset"
rm -rf "$ICONSET"; mkdir -p "$ICONSET"
for size in 16 32 64 128 256 512; do
    sips -s format png -z $size $size "$ICON_SRC" --out "$ICONSET/icon_${size}x${size}.png" &> /dev/null
    double=$((size * 2))
    sips -s format png -z $double $double "$ICON_SRC" --out "$ICONSET/icon_${size}x${size}@2x.png" &> /dev/null
done
iconutil -c icns "$ICONSET" -o "$APP_BUNDLE/Contents/Resources/AppIcon.icns"
rm -rf "$ICONSET"

# ── Ad-hoc sign ──────────────────────────────────────────────────────────────
echo "Signing (ad-hoc)..."
find "$APP_BUNDLE" -name "*.dylib" -exec codesign --force --sign - {} \;
codesign --force --deep --sign - "$APP_BUNDLE"

# ── DMG ──────────────────────────────────────────────────────────────────────
echo "Creating DMG..."
DMG_OUT="$DIST/FluxLab-v$VERSION-$ARCH.dmg"
STAGING="$(mktemp -d)"
rm -f "$DMG_OUT"
cp -R "$APP_BUNDLE" "$STAGING/"
cp "$SCRIPT_DIR/README.txt" "$STAGING/README.txt"
cp "$LICENSE_SRC" "$STAGING/LICENSE"
dmgbuild -s "$SCRIPT_DIR/dmg_settings.py" \
    -D app="$STAGING/$APP_NAME.app" \
    -D readme="$STAGING/README.txt" \
    -D license="$STAGING/LICENSE" \
    "FluxLab v$VERSION" "$DMG_OUT"
rm -rf "$STAGING"

echo ""
echo "Done! Output: $DMG_OUT"
