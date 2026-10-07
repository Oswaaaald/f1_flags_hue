#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
if [ ! -d apps/web/node_modules ]; then npm ci --prefix apps/web; fi
npm run build --prefix apps/web
scripts/dotnet.sh build apps/host/F1Hue.Host.csproj -p:UseSharedCompilation=false -m:1
if [ -f config.yml ]; then
  exec scripts/dotnet.sh apps/host/bin/Debug/net10.0/f1-hue.dll --import "$PWD/config.yml" "$@"
fi
exec scripts/dotnet.sh apps/host/bin/Debug/net10.0/f1-hue.dll "$@"
