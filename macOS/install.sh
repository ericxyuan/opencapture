#!/bin/bash
set -euo pipefail
port=$(cd "$(dirname "$0")" && pwd -P)
library=$(cd "$port/.." && pwd -P)
applications=${OPENCAPTURE_APPLICATIONS:-/Applications}
destination="$applications/OpenCapture.app"
identifier=local.opencapture.mac
mkdir -p "$applications"
lockdir="$HOME/Library/Caches/OpenCapture"
mkdir -p "$lockdir"
lock="$lockdir/install.lock"
shlock -p "$$" -f "$lock" || { echo "Another OpenCapture install is running." >&2; exit 1; }
trap 'rm "$lock"' EXIT
if [[ -e "$destination" ]]; then
    existing=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$destination/Contents/Info.plist" 2>/dev/null || true)
    [[ "$existing" == "$identifier" ]] || { echo "A different app already occupies $destination." >&2; exit 1; }
fi
stage=$(mktemp -d "$applications/.OpenCapture-install.XXXXXX")
bundle="$stage/OpenCapture.app"
backup="$stage/previous.app"
cleanup() {
    if [[ -d "$backup" && ! -e "$destination" ]]; then mv "$backup" "$destination"; fi
    rm -rf "$stage"
    rm "$lock"
}
trap cleanup EXIT
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
cat "$port/Info.plist" > "$bundle/Contents/Info.plist"
xcrun swiftc -swift-version 5 -target "$(uname -m)-apple-macos15.0" -O -whole-module-optimization -parse-as-library \
    "$port"/Sources/*.swift -o "$bundle/Contents/MacOS/OpenCapture"
iconset="$stage/OpenCapture.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$library/OpenCapture Tray Icon.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "$double" "$double" "$library/OpenCapture Tray Icon.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$bundle/Contents/Resources/OpenCapture.icns"
# A local leaf certificate gives successive versions the same macOS identity.
# Keep its non-exportable private key in the user's keychain.
keychain=$(security default-keychain -d user | sed -E 's/^ *"(.*)"$/\1/')
label="OpenCapture Local Build"
identities=$(security find-identity -p codesigning "$keychain")
signer=$(awk -F '"' -v label="$label" '$2 == label { split($1, a, ") "); print substr(a[2], 1, 40) }' <<< "$identities" | sort -u)
if [[ -z "$signer" ]]; then
    if security find-certificate -c "$label" "$keychain" >/dev/null 2>&1; then
        echo "The OpenCapture signing certificate exists but its private key is unavailable. Unlock its keychain and retry." >&2
        exit 1
    fi
    if [[ -e "$destination" ]] && ! grep -qx 'Signature=adhoc' <<< "$(codesign -dv "$destination" 2>&1)"; then
        echo "Restore the signing identity for the installed OpenCapture before updating it." >&2
        exit 1
    fi
    signer=$(
        set -e
        umask 077
        cd "$stage"
        pass=$(openssl rand -hex 32)
        cat > signing.conf <<EOF
[req]
distinguished_name = dn
prompt = no
x509_extensions = leaf
[dn]
CN = $label
[leaf]
basicConstraints = critical,CA:FALSE
keyUsage = critical,digitalSignature
extendedKeyUsage = critical,codeSigning
subjectKeyIdentifier = hash
EOF
        openssl req -new -x509 -sha256 -days 3650 -newkey rsa:3072 -config signing.conf \
            -keyout signing.key -out signing.crt -passout fd:3 3<<< "$pass" 2>/dev/null
        openssl pkcs12 -export -inkey signing.key -in signing.crt -out signer.p12 \
            -passin fd:3 -passout fd:4 3<<< "$pass" 4<<< "$pass"
        security import signer.p12 -k "$keychain" -x -P "$pass" -T /usr/bin/codesign >&2
        openssl x509 -in signing.crt -noout -fingerprint -sha1 | cut -d= -f2 | tr -d :
    )
fi
[[ "$signer" =~ ^[0-9A-Fa-f]{40}$ ]] || { echo "Expected one OpenCapture signing identity." >&2; exit 1; }
codesign --force --sign "$signer" --keychain "$keychain" --timestamp=none --identifier "$identifier" "$bundle"
codesign --verify --strict "$bundle"
if [[ -e "$destination" ]]; then
    # A previously certificate-signed install must recognize the new build as an update.
    previous_info=$(codesign -dvr - "$destination" 2>&1)
    if ! grep -q '^Signature=adhoc$' <<< "$previous_info"; then
        previous=$(sed -nE 's/^(# )?designated => //p' <<< "$previous_info")
        [[ -n "$previous" ]] || { echo "Could not verify the existing app's signing identity." >&2; exit 1; }
        codesign --verify --strict -R="$previous" "$bundle"
    fi
    # Graceful termination lets an active recording finalize before replacing the app.
    xcrun swift - "$destination" <<'SWIFT'
import AppKit
let target = URL(fileURLWithPath: CommandLine.arguments[1]).standardizedFileURL
let apps = NSRunningApplication.runningApplications(withBundleIdentifier: "local.opencapture.mac")
    .filter { $0.bundleURL?.standardizedFileURL == target }
for app in apps { _ = app.terminate() }
let deadline = Date().addingTimeInterval(25)
while apps.contains(where: { !$0.isTerminated }), Date() < deadline {
    RunLoop.current.run(until: Date().addingTimeInterval(0.1))
}
if apps.contains(where: { !$0.isTerminated }) { fputs("Quit OpenCapture and run the installer again.\n", stderr); exit(1) }
SWIFT
    mv "$destination" "$backup"
fi
mv "$bundle" "$destination"
open -g "$destination" --args --library "$library" "$@"
echo "Installed and launched $destination"
