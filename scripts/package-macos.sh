#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
target="${1:-osx-arm64}"
case "$target" in osx-arm64) arch=arm64 ;; osx-x64) arch=x86_64 ;; *) exit 1 ;; esac
bundle="artifacts/$target/F1 Hue Sync.app"
rm -rf "$bundle"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources/service"
cp deploy/macos/Info.plist "$bundle/Contents/Info.plist"
cp -R "artifacts/$target/service/." "$bundle/Contents/Resources/service/"
if [ -n "${F1_HUE_RELEASES_URL:-}" ]; then /usr/libexec/PlistBuddy -c "Add :F1HueReleasesURL string $F1_HUE_RELEASES_URL" "$bundle/Contents/Info.plist"; fi
swiftc deploy/macos/Launcher.swift deploy/macos/RotatingLog.swift -O -target "$arch-apple-macosx15.0" -module-cache-path "$PWD/.tools/swift-cache" -o "$bundle/Contents/MacOS/F1Hue"
identity="${APPLE_SIGNING_IDENTITY:--}"
find "$bundle/Contents/Resources/service" -type f \( -name '*.dylib' -o -name 'f1-hue' -o -name 'createdump' \) -exec codesign --force --options runtime --entitlements deploy/macos/entitlements.plist --sign "$identity" '{}' \;
codesign --force --options runtime --entitlements deploy/macos/entitlements.plist --sign "$identity" "$bundle"
codesign --verify --deep --strict "$bundle"
ditto -c -k --sequesterRsrc --keepParent "$bundle" "artifacts/f1-hue-$target.zip"
if [ -n "${APPLE_NOTARY_PROFILE:-}" ]; then
  xcrun notarytool submit "artifacts/f1-hue-$target.zip" --keychain-profile "$APPLE_NOTARY_PROFILE" --wait
  xcrun stapler staple "$bundle"
  ditto -c -k --sequesterRsrc --keepParent "$bundle" "artifacts/f1-hue-$target.zip"
fi
echo "Application prête : $bundle"
