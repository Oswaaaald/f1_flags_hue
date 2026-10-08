#!/bin/bash
# Runs on disposable Linux CI runners, never against a developer's service.
set -euo pipefail
[[ "${CI:-}" == true ]] || { echo 'This lifecycle test requires a disposable CI runner.' >&2; exit 1; }
service=$(realpath "$1")
export XDG_RUNTIME_DIR="/run/user/$(id -u)"
sudo loginctl enable-linger "$(id -un)"
sudo systemctl start "user@$(id -u).service"
profile=$(mktemp -d)
unit="$HOME/.config/systemd/user/f1-hue.service"
cleanup() {
  systemctl --user disable --now f1-hue.service || true
  rm -f "$unit"
  rm -rf "$profile" "$HOME/.config/systemd/user/f1-hue.service.d"
  systemctl --user daemon-reload || true
}
trap cleanup EXIT
mkdir -p "$HOME/.config/systemd/user/f1-hue.service.d"
cat > "$HOME/.config/systemd/user/f1-hue.service.d/ci.conf" <<UNIT
[Service]
Environment=F1_HUE_SIMULATE=1
Environment=F1_HUE_PORT=18381
UNIT
F1_HUE_DATA_DIR="$profile" sh deploy/linux/install.sh "$service"
curl --fail --retry 10 --retry-connrefused --retry-delay 1 http://127.0.0.1:18381/health
systemctl --user restart f1-hue.service
F1_HUE_DATA_DIR="$profile" sh deploy/linux/install.sh "$service"
curl --fail --retry 10 --retry-connrefused --retry-delay 1 http://127.0.0.1:18381/health
test -s "$profile/setup-code.txt"
test -s "$HOME/.local/lib/f1-hue/previous.service"
echo 'PASS User service installs, restarts and updates with persistent data and retained override settings'
