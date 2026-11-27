#!/bin/bash
set -euo pipefail
src=$(cd "$(dirname "$0")" && pwd)
out=${1:?Provide an output ZIP path}
mkdir -p "$(dirname "$out")"
out="$(cd "$(dirname "$out")" && pwd)/$(basename "$out")"
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
app="$stage/OpenCapture.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$stage/icon.iconset"
cp "$src/Info.plist" "$app/Contents/Info.plist"
for arch in arm64 x86_64; do
    xcrun swiftc -swift-version 5 -target "$arch-apple-macos15.0" -module-cache-path "$stage/modules-$arch" -O -whole-module-optimization -parse-as-library "$src"/Sources/*.swift -o "$stage/$arch"
done
lipo -create "$stage/arm64" "$stage/x86_64" -output "$app/Contents/MacOS/OpenCapture"
lipo "$app/Contents/MacOS/OpenCapture" -verify_arch arm64 x86_64
for size in 16 32 128 256 512; do
    for scale in 1 2; do
        suffix=""; if [[ $scale == 2 ]]; then suffix="@2x"; fi
        pixels=$((size * scale))
        sips -z "$pixels" "$pixels" "$src/../OpenCapture Tray Icon.png" --out "$stage/icon.iconset/icon_${size}x${size}$suffix.png" >/dev/null
    done
done
iconutil -c icns "$stage/icon.iconset" -o "$app/Contents/Resources/OpenCapture.icns"
codesign --force --sign - --timestamp=none "$app"
codesign --verify --strict "$app"
(cd "$stage" && ditto -c -k --keepParent --norsrc OpenCapture.app release.zip)
unzip -tq "$stage/release.zip"
mv "$stage/release.zip" "$out"
