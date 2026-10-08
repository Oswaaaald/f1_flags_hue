#!/bin/bash
# Disposable native runner only: owns the test's menu application and profile.
set -euo pipefail
[[ "${CI:-}" == true ]] || { echo 'Requires a disposable CI runner.' >&2; exit 1; }
bundle="$1"
profile="$HOME/Library/Application Support/F1Hue"
[[ ! -e "$profile/f1-hue.sqlite3" ]] || { echo 'Refusing an existing desktop profile.' >&2; exit 1; }
launcher_pid=''
cleanup() {
  if [[ -n "$launcher_pid" ]] && kill -0 "$launcher_pid" 2>/dev/null; then
    osascript -e 'tell application id "local.f1hue.desktop" to quit' || true
    kill "$launcher_pid" 2>/dev/null || true
  fi
}
trap cleanup EXIT
F1_HUE_SIMULATE=1 "$bundle/Contents/MacOS/F1Hue" --background &
launcher_pid=$!
curl --fail --retry 30 --retry-connrefused --retry-delay 1 http://127.0.0.1:8081/health
F1_HUE_LAUNCHER_PROFILE="$profile" node tests/launcher-service.mjs
osascript -e 'tell application id "local.f1hue.desktop" to quit'
for attempt in {1..50}; do
  if ! kill -0 "$launcher_pid" 2>/dev/null; then break; fi
  sleep 0.1
done
if kill -0 "$launcher_pid" 2>/dev/null; then echo 'Menu application failed to quit promptly.' >&2; exit 1; fi
wait "$launcher_pid"
if curl --silent --fail http://127.0.0.1:8081/health; then echo 'Service survived Quit.' >&2; exit 1; fi
test ! -e "$profile/desktop-launch.key"
test -s "$profile/f1-hue.sqlite3"
echo 'PASS Actual macOS menu app launches its packaged service and quits while an effect and SSE are active'
