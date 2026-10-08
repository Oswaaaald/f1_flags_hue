#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
npm ci --prefix apps/web
npm run build --prefix apps/web
node scripts/check-web-build.mjs
scripts/dotnet.sh restore F1Hue.slnx --locked-mode
scripts/dotnet.sh build F1Hue.slnx --no-restore -c Release -p:UseSharedCompilation=false -m:1
scripts/dotnet.sh tests/F1Hue.Tests/bin/Release/net10.0/F1Hue.Tests.dll
