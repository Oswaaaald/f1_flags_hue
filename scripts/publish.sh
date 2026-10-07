#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
target="${1:-osx-arm64}"
case "$target" in osx-arm64|osx-x64|win-x64|win-arm64|linux-x64|linux-arm64) ;; *) echo "Unsupported runtime: $target" >&2; exit 1 ;; esac
if [ "${F1_HUE_SKIP_WEB_BUILD:-0}" != 1 ]; then npm run build --prefix apps/web; fi
mkdir -p "artifacts/$target"
staging=$(mktemp -d "artifacts/$target/.publish.XXXXXX")
trap 'rm -rf "$staging"' EXIT
scripts/dotnet.sh publish apps/host/F1Hue.Host.csproj -c Release -r "$target" --self-contained true -o "$staging/service" -p:UseSharedCompilation=false -m:1
rm -rf "artifacts/$target/service"
mv "$staging/service" "artifacts/$target/service"
if [ "$target" = osx-arm64 ] || [ "$target" = osx-x64 ]; then
  scripts/package-macos.sh "$target"
elif [ "$target" = linux-x64 ] || [ "$target" = linux-arm64 ]; then
  cp deploy/linux/install.sh deploy/linux/uninstall.sh "artifacts/$target/"
  tar -czf "artifacts/f1-hue-$target.tar.gz" -C "artifacts/$target" service install.sh uninstall.sh
else
  scripts/dotnet.sh publish deploy/windows/F1Hue.Desktop.csproj -c Release -r "$target" --self-contained true -o "$staging/desktop" -p:UseSharedCompilation=false -m:1
  cp -R "artifacts/$target/service" "$staging/desktop/service"
  if [ -n "${F1_HUE_RELEASES_URL:-}" ]; then printf '%s\n' "$F1_HUE_RELEASES_URL" > "$staging/desktop/releases-url.txt"; fi
  rm -rf "artifacts/$target/desktop"
  mv "$staging/desktop" "artifacts/$target/desktop"
  node scripts/zip-release.mjs "artifacts/$target/desktop" "artifacts/f1-hue-$target.zip"
fi
