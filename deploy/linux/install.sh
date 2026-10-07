#!/bin/sh
# Run from an extracted linux-x64/linux-arm64 release. No root account required.
set -eu
source_dir="${1:-$(CDPATH='' cd -- "$(dirname -- "$0")/service" && pwd)}"
install_dir="$HOME/.local/lib/f1-hue"
unit_dir="$HOME/.config/systemd/user"
mkdir -p "$install_dir" "$unit_dir"
systemctl --user stop f1-hue.service 2>/dev/null || true
cp -R "$source_dir/." "$install_dir/"
chmod +x "$install_dir/f1-hue"
cat > "$unit_dir/f1-hue.service" <<EOF
[Unit]
Description=F1 Hue Sync local service
After=network-online.target
Wants=network-online.target
[Service]
ExecStart="$install_dir/f1-hue"
Restart=on-failure
RestartSec=5
TimeoutStopSec=45
UMask=0077
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ReadWritePaths=%h/.local/share/F1Hue
[Install]
WantedBy=default.target
EOF
mkdir -p "$HOME/.local/share/F1Hue"
chmod 700 "$HOME/.local/share/F1Hue"
systemctl --user daemon-reload
systemctl --user enable --now f1-hue.service
printf '%s\n' 'F1 Hue Sync : http://localhost:8080' 'Code de configuration : ~/.local/share/F1Hue/setup-code.txt'
