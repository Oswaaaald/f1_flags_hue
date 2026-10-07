#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_CLI_HOME="$PWD/.tools/dotnet-home"
export NUGET_PACKAGES="$PWD/.tools/nuget"
if [ -x .tools/dotnet/dotnet ]; then exec .tools/dotnet/dotnet "$@"; fi
exec dotnet "$@"
