#!/bin/sh
set -eu
systemctl --user disable --now f1-hue.service
rm -f "$HOME/.config/systemd/user/f1-hue.service"
systemctl --user daemon-reload
printf '%s\n' 'Service arrêté et désactivé. Les données personnelles et les fichiers de l’application sont conservés.'
