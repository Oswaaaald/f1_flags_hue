# F1 Hue Sync

Synchronise vos lampes **Philips Hue** avec les **drapeaux F1** en direct, via le flux de live timing Formula 1 accessible sans compte.
Clignotements réguliers (`alert`), **switch instantané**, fondu propre, **baseline** utilisateur, **offset TV** + **calibration**, et **simulation locale** pour tester hors GP.

> Non affilié à Formula 1®, F1 TV ou Signify/Philips Hue.

---

## État du direct (septembre 2026)

Le programme lit directement les messages `RaceControlMessages` et `TrackStatus` du flux SignalR de live timing de Formula 1. Une connexion sans compte a été vérifiée pendant les essais libres du GP d'Azerbaïdjan le 25 septembre 2026 : le flux a envoyé le drapeau vert de la séance. Les drapeaux rouge et jaune, SC/VSC et drapeau à damier sont décodés à partir des mêmes canaux, mais n'ont pas tous été observés en direct lors de ce contrôle. Ce flux est non documenté et peut changer sans préavis. OpenF1 n'est pas utilisé ; les anciennes clés `source` et `openf1` d'un `config.yml` existant sont ignorées.

Le projet utilise l'API locale Hue v1. Le pont Hue et ce programme doivent être sur le même réseau local. Une connexion au flux F1 ne prouve pas la liaison Hue.

## Démarrage

Installez les dépendances :
```bash
python3 -m venv .venv
.venv/bin/python -m pip install -r requirements.txt
```

Vérifiez le flux sans allumer les lampes :
```bash
make check-live
```

Le flux ne diffuse les drapeaux que pendant une séance. `check-live` indique si la connexion fonctionne et si une séance est en cours.

Configurez le pont et les lampes avec `make setup-wizard`, puis lancez :
```bash
make run-live
```

Si l'adresse IP du pont a changé, mettez `bridge_ip` à jour dans `config.yml`. Pour tester les lampes indépendamment de la F1 : `make quick GAP=5`.

Pour utiliser l'interface web du repo, lancez `make web` et ouvrez [http://localhost:8080](http://localhost:8080). Le fichier `web/static/index.html` dépend du serveur pour ses appels `/api/...`. Par défaut, l'interface n'écoute que sur cet ordinateur ; une exposition au réseau local exige de définir explicitement `F1_HUE_WEB_HOST`.

Sur macOS, si l'adresse du pont s'ouvre dans le navigateur mais que le programme indique « No route to host », lancez `make web` depuis l'application **Terminal**. Si vous utilisez le terminal intégré de VS Code, autorisez **Visual Studio Code** dans Réglages Système → Confidentialité et sécurité → Réseau local, puis relancez le serveur. L'accès au réseau local est accordé séparément à chaque application.

Calibrez votre TV avec les drapeaux F1 :
```bash
make sync-calibrate
```

L'interface web propose aussi une comparaison du **temps restant** de la séance avec le chrono TV. Pendant des essais libres ou qualifications, entrez une valeur à venir du chrono TV (par exemple `12:30`) et cliquez sur « Comparer avec l’API » au moment où la TV affiche cette valeur. Le projet lit `ExtrapolatedClock` du flux F1 et propose un offset, sans le sauvegarder avant « Enregistrer cet offset ». La précision est d'environ une seconde ; l'horloge doit avancer et la séance doit être active. Le calcul n'est pas adapté au compteur de tours d'une course.

Pour une course ou un sprint, armez « Attendre le départ F1 » **avant** le départ, puis cliquez sur « Je vois le départ » quand les feux s'éteignent et que les voitures partent sur la TV. Le repère API est le changement de `SessionStatus` à `Started` ; ce n'est pas une mesure officielle des feux. Si la course a déjà commencé, armez « Attendre le prochain tour API », puis cliquez quand ce nouveau numéro de tour apparaît sur la TV. Dans les deux cas, vérifiez l'offset proposé avant de l'enregistrer. Si Live tourne déjà, redémarrez-le après enregistrement pour appliquer le nouvel offset.

L'offset retarde chaque drapeau à partir de son heure de réception. Ainsi, avec 60 secondes d'offset, un damier reçu à la fin de la séance joue environ 60 secondes plus tard sur les lampes, même si le chrono API est déjà à zéro. Les délais des drapeaux successifs ne s'additionnent pas. Un relevé du GP d'Italie 2025 montre que `SessionStatus: Finished` précède de quelques fractions de seconde le message `CHEQUERED` en essais libres ; le programme accepte désormais ce damier après `Finished`. L'instant exact dépend de la diffusion du flux et des délais réseau.

## Séances enregistrées et effets

Le serveur web partage **une connexion F1** entre le direct, les calibrations et l'écran d'état. Il conserve les drapeaux et les changements de séance ou de tour dans `.data/events.sqlite3` sur ce Mac. Ce journal ne contient pas les paquets bruts de télémétrie. Le fichier est ignoré par Git et réservé à l'utilisateur local.

Le panneau « Drapeaux F1 reçus », à côté de la calibration, affiche les 30 derniers drapeaux du journal avec la date et l'heure de réception sur ce Mac, la séance et, quand Formula 1 l'envoie, l'horodatage UTC du message. Il se rafraîchit toutes les cinq secondes. L'heure F1 n'est pas disponible pour tous les messages, notamment certains changements issus de `TrackStatus`.

Dans l'interface web, la section « Rejouer des drapeaux F1 » propose toujours trois séances passées tirées directement des archives Formula 1 (Bakou, Silverstone et Monza 2025), même si le journal local est vide. Elle affiche aussi les séances enregistrées sur ce Mac dès qu'il y en a. Les archives nécessitent Internet ; les enregistrements locaux fonctionnent hors connexion. Le replay commande les lampes : vérifiez leur sélection avant de cliquer. Le choix ×10 divise par dix le temps entre deux drapeaux (10 minutes deviennent 1 minute) ; la durée des effets Hue reste identique. Les lampes retrouvent leur état à la fin. En ligne de commande :

```bash
.venv/bin/python f1_hue.py replay                        # lister archives et séances locales
.venv/bin/python f1_hue.py replay archive:baku-qualifying-2025 --speed 60
.venv/bin/python f1_hue.py replay CLE_LOCALE --speed 10  # rejouer sur Hue
```

Les effets sont séparés de la lecture du flux. Par défaut, un drapeau `RED` déclenche le pattern `RED`. Vous pouvez changer cette correspondance dans `config.yml` :

```yaml
rules:
  flag_patterns:
    RED: RED
    BLUE: null   # ignorer ce drapeau
```

Les noms à droite doivent exister dans `patterns`. Le nouveau réglage prend effet au prochain démarrage du mode Live. Voir [l'architecture](docs/architecture.md) pour les modules et les limites du flux.

Sources : [client live timing FastF1](https://github.com/theOehrly/Fast-F1/blob/main/fastf1/livetiming/client.py), [API locale Hue](https://developers.meethue.com/support/).
