#!/usr/bin/env bash
# Builds a self-contained release for one runtime identifier and packages it:
#   win-x64 | win-arm64      -> artifacts/EdenGameLibraryManager-<ver>-win-*.zip (portable)
#   osx-arm64 | osx-x64      -> artifacts/EdenGameLibraryManager-<ver>-osx-*.dmg (on macOS; a .zip elsewhere)
#   linux-x64 | linux-arm64  -> artifacts/EdenGameLibraryManager-<ver>-linux-*.tar.gz
#                               plus an .AppImage when appimagetool is available
#
# Usage: build/publish.sh [rid]          (default: win-x64)
# Environment:
#   VERSION=1.2.3          override the version from Directory.Build.props (used by CI for tags)
#   APPIMAGETOOL=/path     appimagetool binary (otherwise looked up on PATH)
#
# macOS and Linux packages must be built on macOS / Linux respectively: Windows does not record Unix
# execute permissions, and Apple silicon only runs signed binaries (signing needs macOS's codesign).
set -euo pipefail

RID="${1:-win-x64}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/EdenGameLibraryManager.App/EdenGameLibraryManager.App.csproj"
VERSION="${VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -n1)}"
NAME="EdenGameLibraryManager-$VERSION-$RID"
OUT="$ROOT/artifacts/$RID"
PUBLISH="$OUT/publish"
APP_NAME="Eden Game Library Manager"
ICON_PNG="$ROOT/src/EdenGameLibraryManager.App/Assets/app-512.png"

case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
        if [[ "$RID" == osx-* || "$RID" == linux-* ]]; then
            echo "WARNING: building $RID on Windows. The package will lack execute permissions (and macOS signing)." >&2
            echo "         Use the GitHub Actions release workflow, or build on macOS/Linux, for working packages." >&2
        fi
        ;;
esac

echo "==> Publishing $APP_NAME $VERSION for $RID"
rm -rf "$OUT"
mkdir -p "$ROOT/artifacts"
dotnet publish "$PROJECT" -c Release -r "$RID" -o "$PUBLISH" -p:Version="$VERSION" -p:DebugType=embedded

make_zip() { # make_zip <archive> <dir-containing-items> <item>
    local archive="$1" dir="$2" item="$3"
    if command -v ditto >/dev/null 2>&1; then
        (cd "$dir" && ditto -c -k --sequesterRsrc --keepParent "$item" "$archive")
    elif command -v zip >/dev/null 2>&1; then
        (cd "$dir" && zip -qry "$archive" "$item")
    elif command -v python3 >/dev/null 2>&1 || command -v python >/dev/null 2>&1; then
        local py; py="$(command -v python3 || command -v python)"
        (cd "$dir" && "$py" -m zipfile -c "$archive" "$item")
    else
        echo "No zip tool found (zip, ditto or python); the unpacked build is in $dir" >&2
        return 0
    fi
    echo "==> Created $archive"
}

write_checksum() { # write_checksum <file>
    local file="$1" hash
    if command -v sha256sum >/dev/null 2>&1; then hash="$(sha256sum "$file" | cut -d' ' -f1)"
    elif command -v shasum >/dev/null 2>&1; then hash="$(shasum -a 256 "$file" | cut -d' ' -f1)"
    else return 0
    fi
    echo "$hash  $(basename "$file")" > "$file.sha256"
}

case "$RID" in
    win-*)
        STAGE="$OUT/$NAME"
        mkdir -p "$STAGE"
        cp "$PUBLISH/EdenGameLibraryManager.exe" "$STAGE/"
        cp "$ROOT/docs/package/README.txt" "$STAGE/README.txt"
        make_zip "$ROOT/artifacts/$NAME.zip" "$OUT" "$NAME"
        write_checksum "$ROOT/artifacts/$NAME.zip"
        ;;

    osx-*)
        BUNDLE="$OUT/$APP_NAME.app"
        mkdir -p "$BUNDLE/Contents/MacOS" "$BUNDLE/Contents/Resources"
        cp -R "$PUBLISH/." "$BUNDLE/Contents/MacOS/"
        chmod +x "$BUNDLE/Contents/MacOS/EdenGameLibraryManager"

        ICON_ENTRY=""
        if command -v iconutil >/dev/null 2>&1 && command -v sips >/dev/null 2>&1; then
            ICONSET="$OUT/app.iconset"
            mkdir -p "$ICONSET"
            for size in 16 32 128 256 512; do
                sips -z $size $size "$ICON_PNG" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
                double=$((size * 2))
                if [ $double -le 512 ]; then
                    sips -z $double $double "$ICON_PNG" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
                fi
            done
            iconutil -c icns "$ICONSET" -o "$BUNDLE/Contents/Resources/app.icns"
            rm -rf "$ICONSET"
            ICON_ENTRY="<key>CFBundleIconFile</key><string>app.icns</string>"
        else
            echo "iconutil/sips not available (not on macOS): the bundle will use the default icon." >&2
        fi

        cat > "$BUNDLE/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>$APP_NAME</string>
  <key>CFBundleDisplayName</key><string>$APP_NAME</string>
  <key>CFBundleIdentifier</key><string>io.github.edengamelibrarymanager</string>
  <key>CFBundleExecutable</key><string>EdenGameLibraryManager</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  $ICON_ENTRY
</dict>
</plist>
PLIST

        if command -v codesign >/dev/null 2>&1; then
            # Ad-hoc signature: required for Apple silicon. Use a Developer ID + notarization to avoid
            # the first-launch Gatekeeper prompt.
            codesign --force --deep --sign - "$BUNDLE"
            codesign --verify --deep --strict "$BUNDLE"
        fi

        if command -v hdiutil >/dev/null 2>&1; then
            DMG_STAGE="$OUT/dmg"
            mkdir -p "$DMG_STAGE"
            cp -R "$BUNDLE" "$DMG_STAGE/"
            cp "$ROOT/docs/package/README.txt" "$DMG_STAGE/README.txt"
            ln -s /Applications "$DMG_STAGE/Applications"
            rm -f "$ROOT/artifacts/$NAME.dmg"
            # hdiutil occasionally fails with "Resource busy" on CI machines; retry a few times.
            for attempt in 1 2 3; do
                if hdiutil create -volname "$APP_NAME" -srcfolder "$DMG_STAGE" -fs HFS+ -format UDZO -ov "$ROOT/artifacts/$NAME.dmg"; then
                    break
                fi
                [ "$attempt" -eq 3 ] && exit 1
                sleep 5
            done
            echo "==> Created $ROOT/artifacts/$NAME.dmg"
            write_checksum "$ROOT/artifacts/$NAME.dmg"
        else
            make_zip "$ROOT/artifacts/$NAME.zip" "$OUT" "$APP_NAME.app"
            write_checksum "$ROOT/artifacts/$NAME.zip"
        fi
        ;;

    linux-*)
        STAGE="$OUT/$NAME"
        mkdir -p "$STAGE"
        cp "$PUBLISH/EdenGameLibraryManager" "$STAGE/"
        chmod +x "$STAGE/EdenGameLibraryManager"
        cp "$ROOT/src/EdenGameLibraryManager.App/Assets/app.png" "$STAGE/eden-game-library-manager.png"
        cp "$ROOT/docs/package/README.txt" "$STAGE/README.txt"
        cat > "$STAGE/eden-game-library-manager.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=$APP_NAME
Comment=Browse and download games from your AeroFoil server
Exec=EdenGameLibraryManager
Icon=eden-game-library-manager
Categories=Game;
Terminal=false
DESKTOP
        cat > "$STAGE/install.sh" <<'INSTALL'
#!/usr/bin/env sh
# Installs for the current user only (no root needed).
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$HOME/.local/bin" "$HOME/.local/share/applications" "$HOME/.local/share/icons/hicolor/256x256/apps"
cp "$DIR/EdenGameLibraryManager" "$HOME/.local/bin/"
chmod +x "$HOME/.local/bin/EdenGameLibraryManager"
cp "$DIR/eden-game-library-manager.png" "$HOME/.local/share/icons/hicolor/256x256/apps/"
sed "s|^Exec=.*|Exec=$HOME/.local/bin/EdenGameLibraryManager|" "$DIR/eden-game-library-manager.desktop" \
    > "$HOME/.local/share/applications/eden-game-library-manager.desktop"
echo "Installed. Launch 'Eden Game Library Manager' from your application menu."
INSTALL
        chmod +x "$STAGE/install.sh"
        tar -C "$OUT" -czf "$ROOT/artifacts/$NAME.tar.gz" "$NAME"
        echo "==> Created $ROOT/artifacts/$NAME.tar.gz"
        write_checksum "$ROOT/artifacts/$NAME.tar.gz"

        TOOL="${APPIMAGETOOL:-$(command -v appimagetool || true)}"
        if [ -n "$TOOL" ]; then
            case "$RID" in
                linux-x64) APPIMAGE_ARCH=x86_64 ;;
                linux-arm64) APPIMAGE_ARCH=aarch64 ;;
            esac
            APPDIR="$OUT/AppDir"
            mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/icons/hicolor/256x256/apps" "$APPDIR/usr/share/applications"
            cp "$STAGE/EdenGameLibraryManager" "$APPDIR/usr/bin/"
            cp "$STAGE/eden-game-library-manager.png" "$APPDIR/eden-game-library-manager.png"
            cp "$STAGE/eden-game-library-manager.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/"
            cp "$STAGE/eden-game-library-manager.desktop" "$APPDIR/eden-game-library-manager.desktop"
            cp "$STAGE/eden-game-library-manager.desktop" "$APPDIR/usr/share/applications/"
            cat > "$APPDIR/AppRun" <<'APPRUN'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/EdenGameLibraryManager" "$@"
APPRUN
            chmod +x "$APPDIR/AppRun"
            rm -f "$ROOT/artifacts/$NAME.AppImage"
            ARCH="$APPIMAGE_ARCH" APPIMAGE_EXTRACT_AND_RUN=1 "$TOOL" "$APPDIR" "$ROOT/artifacts/$NAME.AppImage"
            chmod +x "$ROOT/artifacts/$NAME.AppImage"
            echo "==> Created $ROOT/artifacts/$NAME.AppImage"
            write_checksum "$ROOT/artifacts/$NAME.AppImage"
        else
            echo "appimagetool not found: skipping the AppImage (set APPIMAGETOOL or add it to PATH)." >&2
        fi
        ;;

    *)
        echo "Unsupported runtime identifier: $RID" >&2
        exit 1
        ;;
esac
