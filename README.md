# F1 Hue Sync

Synchroniser ses lampes Philips Hue avec les drapeaux F1, en tenant compte du retard de sa TV. Le service fonctionne chez soi, en arrière-plan. Aucun compte cloud propre au projet, aucun abonnement à une API de timing.

La version **2.0 preview** utilise C#/.NET 10, une interface TypeScript intégrée et SQLite. La version Python est conservée pour migration et diagnostic local ; les nouvelles fonctionnalités concernent la version 2.

## Utiliser l’application

**[Télécharger l’application](https://github.com/Oswaaaald/f1_flags_hue/releases)** : les liens **Mac** et **Windows** sont en tête de la page. Les autres appareils sont dans **Autres appareils et installation avancée**.

### macOS

macOS 15 ou ultérieur, conformément aux [systèmes pris en charge par .NET 10](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md).

Ouvrir **F1 Hue Sync.app** depuis le paquet `f1-hue-osx-arm64.zip` (Apple Silicon) ou `f1-hue-osx-x64.zip` (Intel). Déplacer l’application dans Applications avant d’activer son démarrage automatique.

Une icône de drapeau apparaît dans la barre des menus. Elle permet d’ouvrir l’interface, de consulter les journaux, d’importer un ancien `config.yml`, d’activer le lancement à l’ouverture de session et de quitter proprement. L’interface de bureau se trouve à **http://127.0.0.1:8081**. Fermer l’onglet laisse le service actif ; mettre le Mac en veille suspend la synchronisation.

L’application ouvre l’interface et te connecte automatiquement, sans mot de passe à créer. Puis lier le pont, choisir les lampes, calibrer la TV et activer le direct. L’option **Préférences → Activer le direct au lancement** permet ensuite de démarrer automatiquement la synchronisation. Pour rouvrir l’interface après une déconnexion ou un redémarrage, utiliser **Ouvrir F1 Hue Sync** dans le menu de l’application.

Le trousseau macOS protège séparément la clé de la liaison Hue. Il peut demander une autorisation d’accès à **F1Hue.Vault**, notamment après une mise à jour ; cette fenêtre utilise le mot de passe du trousseau, généralement celui de la session Mac. Le mot de passe de l’ancienne interface reste conservé pour une utilisation ultérieure en mode serveur.

Les paquets Mac sont signés localement (signature ad hoc gratuite), sans abonnement Apple Developer. À la première ouverture, macOS peut demander une autorisation : après avoir tenté d’ouvrir l’application, aller dans **Réglages Système → Confidentialité et sécurité → Ouvrir quand même**. Voir la [procédure Apple](https://support.apple.com/fr-fr/102445). Les mises à jour conservent tes données.

### Windows

Exécuter `F1Hue-VERSION-win-x64-Setup.exe` (ou la variante ARM64), ou extraire le ZIP et ouvrir `F1Hue.exe`. L’application ouvre l’interface avec une connexion automatique et reste dans la zone de notification. Le menu offre les mêmes fonctions que sur Mac, notamment le lancement à l’ouverture de session. Aucun Python, Node ou .NET à installer pour utiliser un paquet autonome.

### NAS, Raspberry Pi ou mini-PC avec Docker

Depuis le dépôt :

```sh
docker compose up -d --build
docker compose exec f1-hue cat /data/setup-code.txt
```

Ouvrir `http://localhost:8080` sur le serveur, saisir le code affiché et créer son mot de passe. Le volume `f1-hue-data` conserve les réglages, le journal et la liaison du pont. Le service redémarre avec Docker. Les images prennent en charge `linux/amd64` et `linux/arm64` (système 64 bits requis).

Le conteneur et le pont doivent pouvoir communiquer sur le réseau local. La découverte mDNS traverse rarement un réseau Docker ; la découverte Hue ou la saisie manuelle de l’adresse restent disponibles. Ne pas rediriger ce port sur Internet. Pour un accès réseau chiffré, voir [l’hébergement et la sécurité](docs/hosting.md).

### Linux sans Docker

Extraire le paquet correspondant puis exécuter `./install.sh`. Le service utilisateur systemd est installé sous `~/.local/lib/f1-hue` et démarre à la connexion. Pour démarrer avant la connexion, l’administrateur peut activer le maintien des services de cet utilisateur (`loginctl enable-linger NOM_UTILISATEUR`).

## Fonctionnalités

- **Direct** : état du flux, séance, effet actif, lampes ciblées, Stop et journal horodaté.
- **Calibration** : temps restant d’essais/qualifications ; clic au départ, au prochain drapeau ou au prochain tour pour la course. Ajustement manuel avec des pas de ±0,1 / 0,5 / 1 / 5 secondes, enregistrés à chaque clic, ou saisie d’une valeur précise. Réduire le délai avance les effets ; l’augmenter les retarde. Le nouveau délai s’applique aux prochains événements reçus, sans redémarrer le direct ; les événements déjà en attente gardent leur délai.
- **Hue** : découverte, liaison au bouton physique, choix précis de zones ou de lampes couleur. HTTPS avec empreinte du certificat du pont mémorisée lors du premier contact.
- **Tests** : aperçu individuel, séquence des événements activés, replay des séances reçues et de trois archives officielles F1.
- **Drapeaux** : neuf événements, activation individuelle et durée fixe ou jusqu’au suivant. Le bleu est désactivé par défaut.
- **Préférences** : luminosité, transition, restauration, limite de clignotement, arrêt au damier, démarrage du direct et thèmes clair/sombre/automatique.
- **Pulsation native Hue** : même animation que dans la version Python, calculée par les lampes. Aucune zone Entertainment nécessaire ; les lampes choisies sont synchronisées dans un groupe exact.

Les règles de drapeaux sont relues au moment de jouer chaque événement, **après** le décalage TV. L’effet déjà actif conserve sa durée initiale. Un événement désactivé termine l’effet précédent et restaure les lampes. La sélection reste verrouillée pendant un mode actif.

Les clignotements utilisent `alert=lselect` (SC, VSC, damier) et `alert=select` répété pour le bleu. Le réglage **Transition** concerne les changements de couleur ; le rythme natif est fixé par les lampes. L’application renouvelle la pulsation pour les effets prolongés et envoie `alert=none` aux seules lampes choisies avant un autre drapeau ou Stop. Une annulation échouée reste réessayable et est reprise au prochain démarrage. La restauration restitue couleurs, température, intensité, gradients et effets natifs compatibles. Une scène dynamique ou un effet avancé dont les paramètres ne sont pas restaurables est refusé avant de modifier les lampes : arrêter d’abord son animation dans Hue.

L’ambiance est mémorisée juste avant le premier drapeau joué, puis renouvelée après un retour au repos. Tu peux donc choisir une scène dans Hue pendant l’attente du direct. Stop sans drapeau joué laisse les lampes intactes. Lors d’une restauration, l’application attend que le pont confirme les états des lampes et retente le rappel groupé si nécessaire.

## Données F1

Une seule connexion au flux officiel :

- SignalR : `https://livetiming.formula1.com/signalrcore`
- Topics : `RaceControlMessages`, `TrackStatus`, `SessionInfo`, `SessionStatus`, `ExtrapolatedClock`, `LapCount`.
- Replays historiques : `https://livetiming.formula1.com/static/` avec un catalogue de chemins fixes.

Le projet n’utilise ni OpenF1 ni une API commerciale intermédiaire. Cet accès au flux est non documenté comme API publique : sa disponibilité et son format peuvent évoluer. Projet indépendant, non affilié à Formula 1 ou à Philips Hue.

## Migrer la version Python

L’application Mac détecte `~/f1_flags_hue/config.yml` au premier lancement. Le menu **Importer un config.yml…** permet de sélectionner un autre fichier. Un import n’écrase jamais une configuration v2 déjà enregistrée. Le fichier source reste intact.

Le décalage `sync.offset_seconds`, la clé du pont, les préférences et les réglages des drapeaux sont importés. Les anciennes références aux lampes et groupes sont converties en identifiants Hue v2 quand le pont est joignable. Une ancienne sélection « toutes les lampes », vide ou introuvable demande un choix explicite. Le journal Python reste dans `.data/events.sqlite3` ; le journal v2 commence à la première réception du nouveau service.

Pour importer en ligne de commande, avec le binaire de la release :

```sh
./f1-hue --import /chemin/config.yml
```

Les anciens réglages spécifiques au moteur v1 (`blink_method`, répétitions d’alertes, timings d’extinction) sont convertis quand un équivalent existe ; ils ne constituent pas le moteur v2. Le fichier original sert de sauvegarde.

## Développer

Prérequis : SDK défini dans `global.json`, Node 22 et npm. Aucun de ces outils n’est nécessaire sur les appareils qui utilisent les paquets publiés.

```sh
scripts/build.sh                  # interface + service + tests C#
F1_HUE_TEST_BUILD=Release node tests/api-smoke.mjs # routes avec pont simulé et données temporaires
make web                          # nouvelle version, avec import initial de config.yml
make web-legacy                   # ancienne interface Python
make check-live                    # client .NET, aucune commande Hue
```

Le wrapper `scripts/dotnet.sh` utilise `.tools/dotnet` s’il existe, sinon le SDK installé. L’interface est embarquée dans le service ; aucune étape npm n’est demandée à l’utilisateur final.

```sh
scripts/publish.sh osx-arm64      # application et ZIP Mac
scripts/publish.sh linux-arm64    # service autonome Raspberry Pi 64 bits
# Windows : deploy/windows/build.ps1 -Runtime win-x64
```

Les workflows GitHub vérifient le moteur et les routes sur Mac, Windows et Linux. **Build installers** fabrique les six variantes, teste chaque service empaqueté sur son système et produit les installateurs Windows. Un lancement manuel conserve les artefacts ; cocher **Publier la release après validation des paquets** depuis `main`, ou pousser un tag `vVERSION` correspondant à `Directory.Build.props`, publie une release avec les sommes SHA-256 après validation de tous les jobs. Aucune signature payante n’est requise. Voir [la distribution](docs/releases.md).

Voir aussi [les changements](CHANGELOG.md), [le guide de contribution](CONTRIBUTING.md), [la sécurité](SECURITY.md) et [les correctifs de l’audit](docs/audit-corrections-2026-10-08.md).

Voir [l’architecture](docs/architecture.md), [l’hébergement](docs/hosting.md), [le rapport de vérification](docs/verification.md) et [l’ancienne documentation Python](docs/legacy-python.md).
