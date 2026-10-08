#!/bin/sh
# Deliberate dependency update only; normal builds use --locked-mode.
set -eu
cd "$(dirname "$0")/.."
for runtime in osx-arm64 osx-x64 linux-x64 linux-arm64 win-x64 win-arm64; do
  scripts/dotnet.sh restore apps/host/F1Hue.Host.csproj -p:RuntimeIdentifier="$runtime" -p:SelfContained=true --force-evaluate
  case "$runtime" in win-*) scripts/dotnet.sh restore deploy/windows/F1Hue.Desktop.csproj -p:RuntimeIdentifier="$runtime" -p:SelfContained=true --force-evaluate ;; esac
done
scripts/dotnet.sh restore F1Hue.slnx --force-evaluate
