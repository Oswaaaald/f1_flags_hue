#!/bin/sh
# Run from an extracted Linux release. Updates retain the previous version.
set -eu
umask 077
source_dir=$(CDPATH='' cd -- "${1:-$(dirname -- "$0")/service}" && pwd)
install_dir="${F1_HUE_INSTALL_ROOT:-$HOME/.local/lib/f1-hue}"
unit_dir="${F1_HUE_UNIT_DIR:-${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user}"
data_dir="${F1_HUE_DATA_DIR:-${XDG_DATA_HOME:-$HOME/.local/share}/F1Hue}"
for value in "$install_dir" "$unit_dir" "$data_dir"; do
  case "$value" in /*) ;; *) echo 'Les chemins d’installation doivent être absolus.' >&2; exit 1 ;; esac
  case "$value" in *'"'*|*'\'*|*'%'*|*'
'*) echo 'Chemin non pris en charge par le fichier de service.' >&2; exit 1 ;; esac
done
[ -x "$source_dir/f1-hue" ] || { echo 'Exécutable f1-hue manquant ou non exécutable.' >&2; exit 1; }
version=$("$source_dir/f1-hue" --version)
case "$version" in ''|*[!a-zA-Z0-9.-]*) echo 'Version du paquet invalide.' >&2; exit 1 ;; esac
# Check the user bus before copying or stopping anything.
load_state=$(systemctl --user show f1-hue.service --property=LoadState --value)
mkdir -p "$install_dir/versions" "$unit_dir" "$data_dir"
chmod 700 "$data_dir"
stage=$(mktemp -d "$install_dir/versions/.stage.XXXXXX")
unit="$unit_dir/f1-hue.service"
previous_unit="$install_dir/previous.service"
previous_link=$(readlink "$install_dir/current" || true)
was_active=0
if systemctl --user is-active --quiet f1-hue.service; then was_active=1; fi
had_unit=0
if [ -f "$unit" ]; then cp -p "$unit" "$previous_unit"; had_unit=1; fi
changed=0
rollback() {
  result=$?
  trap - EXIT HUP INT TERM
  rm -rf "$stage"
  if [ "$result" -ne 0 ] && [ "$changed" -eq 1 ]; then
    echo 'Échec de mise à jour ; restauration de l’installation précédente.' >&2
    systemctl --user stop f1-hue.service || true
    if [ -n "$previous_link" ]; then
      ln -s "$previous_link" "$install_dir/.rollback.$$"
      mv -Tf "$install_dir/.rollback.$$" "$install_dir/current"
    else
      rm -f "$install_dir/current"
    fi
    if [ "$had_unit" -eq 1 ]; then cp -p "$previous_unit" "$unit"; else rm -f "$unit"; fi
    systemctl --user daemon-reload || true
    if [ "$was_active" -eq 1 ]; then systemctl --user start f1-hue.service || true; fi
  fi
  exit "$result"
}
trap rollback EXIT
trap 'exit 1' HUP INT TERM
cp -R "$source_dir/." "$stage/"
[ "$("$stage/f1-hue" --version)" = "$version" ] || { echo 'Le paquet copié est invalide.' >&2; exit 1; }
# A failed stop is a failed update: never overwrite the running application.
if [ "$load_state" != 'not-found' ]; then systemctl --user stop f1-hue.service; fi
changed=1
release="$install_dir/versions/$version-$(date +%Y%m%d%H%M%S)-$$"
mv "$stage" "$release"
ln -s "$release" "$install_dir/.current.$$"
mv -Tf "$install_dir/.current.$$" "$install_dir/current"
unit_stage=$(mktemp "$unit_dir/.f1-hue.XXXXXX")
cat > "$unit_stage" <<UNIT
[Unit]
Description=F1 Hue Sync local service
After=network-online.target
Wants=network-online.target
[Service]
ExecStart="$install_dir/current/f1-hue" --data "$data_dir"
EnvironmentFile=-%h/.config/f1-hue/service.env
Restart=on-failure
RestartSec=5
TimeoutStopSec=45
UMask=0077
NoNewPrivileges=true
[Install]
WantedBy=default.target
UNIT
mv -f "$unit_stage" "$unit"
systemctl --user daemon-reload
systemctl --user enable --now f1-hue.service
sleep 2
systemctl --user is-active --quiet f1-hue.service
# Healthy service startup is distinct from Hue/F1 readiness while offline.
printf '%s\n' "F1 Hue Sync $version : http://localhost:8080" "Code de configuration : $data_dir/setup-code.txt" \
  'L’ancienne version et le fichier previous.service sont conservés pour revenir en arrière.' \
  'Pour fonctionner après déconnexion : loginctl enable-linger "$USER" (une seule fois).'
