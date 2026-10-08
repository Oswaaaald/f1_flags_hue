# Hébergement et exploitation

## Emplacement des données

| Système | Dossier par défaut |
| --- | --- |
| macOS | `~/Library/Application Support/F1Hue` |
| Windows | `%LOCALAPPDATA%\F1Hue` |
| Linux | `~/.local/share/F1Hue` |
| Docker Compose fourni | volume `f1-hue-data`, monté dans `/data` |

Le dossier contient `f1-hue.sqlite3`, `bridge.enc`, le code initial `setup-code.txt` jusqu’à la création du mot de passe, et éventuellement une clé de coffre protégée. Il doit rester privé. Sur Mac, sauvegarder le trousseau avec les données. Sur Windows, la clé DPAPI reste liée au compte Windows : une copie des fichiers vers un autre compte ne suffit pas à déchiffrer le pont. Dans ce cas, relier le pont depuis l’interface.

Sur les applications Mac et Windows, `desktop-launch.key` contient une autorisation privée du lanceur, renouvelée à chaque démarrage et supprimée à l’arrêt. Les sessions de bureau restent en mémoire et expirent après huit heures ou au redémarrage du service. Le menu **Ouvrir F1 Hue Sync** reconnecte automatiquement le navigateur ; une adresse saisie manuellement sans session affiche les instructions d’ouverture. Les réglages, le coffre Hue et un ancien mot de passe serveur sont conservés.

Pour une sauvegarde complète, arrêter le service puis copier son dossier de données. Le bouton **Préférences → Créer une sauvegarde** produit également une copie SQLite cohérente pendant le fonctionnement, dans le sous-dossier `backups` ; elle ne contient pas le coffre Hue externe ni le trousseau. Conserver aussi l’ancien `config.yml` tant que la migration n’a pas été vérifiée. Aucune commande de mise à jour fournie ne supprime ce dossier.

## Paramètres du service

| Option | Utilisation |
| --- | --- |
| `--data CHEMIN` / `F1_HUE_DATA_DIR` | Dossier de données |
| `--listen IP` / `F1_HUE_LISTEN` | Adresse d’écoute, `127.0.0.1` par défaut |
| `--port PORT` / `F1_HUE_PORT` | Port, `8080` par défaut ; lanceurs bureau : `8081` |
| `--desktop` | Connexion automatique des lanceurs Mac/Windows ; exige `--listen 127.0.0.1` et une preuve locale privée |
| `--import config.yml` | Import initial sans écrasement d’une configuration v2 |
| `--health-check` | Vérifie `/health` sans ouvrir la base de données |
| `F1_HUE_HEALTH_URL` | URL de santé personnalisée, notamment avec HTTPS |
| `--check-live` | Vérification du flux officiel, sans lampes |
| `--check-hue` | Lecture de l’inventaire Hue v2, sans commande lumineuse |
| `--backup` / `--restore-backup FICHIER` | Sauvegarde SQLite / restauration hors ligne avec copie préalable ; garder le même `--data` |
| `--runtime-info` | Version du service, du runtime embarqué et de SQLite |
| `F1_HUE_TRUSTED_PROXIES` | IP exactes des reverse proxies autorisés, séparées par des virgules ; vide par défaut |
| `--reset-password` | Révoque les sessions et génère un nouveau code local |
| `--simulate` | Pont simulé ; utiliser un dossier de données séparé |
| `--no-feed` | Désactive la connexion externe F1 pour les vérifications locales |
| `F1_HUE_ALLOWED_HOSTS` | Noms DNS autorisés, séparés par des virgules |
| `F1_HUE_TLS_CERT` | Chemin d’un certificat serveur PFX pour HTTPS |
| `F1_HUE_TLS_PASSWORD` | Mot de passe du PFX, à injecter hors du dépôt |

Les adresses IP littérales et `localhost` sont acceptées comme Host. Les noms DNS personnalisés doivent être ajoutés explicitement. Les en-têtes proxy transmis ne sont pas considérés fiables par défaut. Seuls X-Forwarded-For et X-Forwarded-Proto d’une IP explicitement autorisée sont lus, sur un seul saut. Le proxy doit conserver le Host public. Le mode bureau refuse cette configuration de proxy.

En mode bureau, seul le Host `127.0.0.1` avec le port exact du service est accepté, même si `F1_HUE_ALLOWED_HOSTS` est défini. Le lanceur obtient un ticket utilisable une seule fois pendant une minute. Le navigateur l’échange contre sa session et retire immédiatement le ticket de l’adresse. Les vérifications Host/Origin et des requêtes de modification restent actives. `make web`, Linux et Docker utilisent le mode serveur avec mot de passe.

## Linux distant : accès privé recommandé

Un serveur doit pouvoir joindre l’IP privée du pont Hue. Un VPS exige une route VPN vers le LAN domestique (par exemple un routeur de sous-réseau WireGuard ou Tailscale). Le tunnel du navigateur au serveur et la route du serveur au pont sont deux connexions distinctes. Ne pas ouvrir les ports du pont sur Internet.

Pour consulter un service Linux qui écoute sur loopback, depuis son ordinateur :

```sh
ssh -N -L 8080:127.0.0.1:8080 utilisateur@serveur
```

Ouvrir ensuite `http://127.0.0.1:8080`. Le service reste protégé par son mot de passe. Lire le code initial dans `~/.local/share/F1Hue/setup-code.txt` sur le serveur. Avec Docker, utiliser `docker compose exec f1-hue cat /data/setup-code.txt`.

## Téléphone et proxy HTTPS

Pour un accès LAN partagé, placer un proxy HTTPS devant le service et conserver l’écoute du backend sur `127.0.0.1`. Exemple de variables pour un proxy situé sur la même machine :

```ini
F1_HUE_ALLOWED_HOSTS=f1.example.net
F1_HUE_TRUSTED_PROXIES=127.0.0.1
```

Exemple Caddy, avec un nom et un certificat reconnus par les appareils :

```caddyfile
f1.example.net {
    reverse_proxy 127.0.0.1:8080
}
```

Conserver le Host d’origine. Restreindre l’accès au réseau privé dans le pare-feu ; le service n’est pas une plateforme publique multi-utilisateur. Caddy relaie le schéma HTTPS et le serveur crée alors un cookie Secure. Un proxy non déclaré ne peut pas changer cette perception. Ne pas ajouter tout un réseau aux proxies de confiance. Avec un proxy dans un conteneur, déclarer son IP réelle et limiter le backend à ce réseau ; ne pas utiliser une liste globale arbitraire.

L’alternative HTTPS directe utilise `F1_HUE_TLS_CERT` et `F1_HUE_TLS_PASSWORD`. Aucun certificat n’est approuvé automatiquement. HTTP entre deux appareils transmettrait mot de passe et session sans chiffrement.

Compose publie uniquement `127.0.0.1:8080:8080`. Le conteneur fonctionne sans root, sans capacités Linux, avec un système de fichiers en lecture seule hors volume et `/tmp`. La découverte mDNS ne traverse pas nécessairement Docker/VPN : saisir l’IP privée du pont reste possible. Garder le volume `/data` lors d’une recréation ; `docker compose down -v` supprimerait les données.

## Service Linux sans interface graphique

Les paquets Linux sont destinés à un OS **64 bits glibc**, x64 ou ARM64. Ubuntu 24.04 est la cible CI de référence ; vérifier les [distributions et dépendances .NET 10](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md) pour une autre distribution. Ils ne sont pas des paquets Alpine/musl ni Raspberry Pi OS 32 bits. Le paquet est autonome pour .NET, pas pour les bibliothèques système (glibc, OpenSSL, ICU).

`install.sh` installe un service **utilisateur**, copie chaque version dans un nouveau dossier, vérifie le binaire et bascule le lien `current` après l’arrêt réussi du service précédent. En cas d’échec de démarrage immédiat, il remet l’ancienne unité et l’ancien lien. L’échec d’arrêt annule la mise à jour. Les données restent à part. La disponibilité Hue/F1 n’est pas exigée pour installer hors séance ou hors réseau.

Personnaliser les variables dans `~/.config/f1-hue/service.env`, ou utiliser `systemctl --user edit f1-hue` pour un drop-in. Ne pas éditer l’unité générée. Après un changement :

```sh
systemctl --user daemon-reload
systemctl --user restart f1-hue
journalctl --user -u f1-hue --since today
```

Pour démarrer au boot et rester actif après la déconnexion, un administrateur peut autoriser une fois `loginctl enable-linger NOM_UTILISATEUR`. Cela maintient les services de cet utilisateur sans session ouverte ; choisir un compte dédié si nécessaire. Sans linger, la durée de vie dépend de la session utilisateur. L’installateur exige un gestionnaire `systemd --user` joignable.

L’unité applique `UMask=0077` et `NoNewPrivileges=true`. Elle n’impose pas `PrivateTmp`/`ProtectSystem`, dont l’usage dans les services utilisateur dépend des espaces de noms de la distribution. Le conteneur est l’option fournie pour un système de fichiers en lecture seule. Un administrateur peut ajouter et vérifier ses protections avec `systemd-analyze --user security f1-hue.service`.

## macOS et le pont

Autoriser **Réseau local** pour l’application qui lance le service. Une autorisation donnée à Terminal n’autorise pas automatiquement iTerm, Codex ou un autre exécutable. Le lanceur Mac inclut une description de cet accès et du service Bonjour Hue.

Si `--check-hue` répond « No route to host » alors que le navigateur accède au pont, vérifier l’autorisation de l’application dans Réglages Système → Confidentialité et sécurité → Réseau local. Vérifier aussi le réseau Wi-Fi, l’isolation des clients et l’adresse du pont.

La liaison utilise le bouton physique et HTTPS. L’import d’une ancienne clé établit l’empreinte du certificat à la première connexion. Une empreinte modifiée n’est jamais acceptée silencieusement avec la clé existante.

## Mettre à jour

Arrêter l’application avec son menu, remplacer l’application par le nouveau paquet puis la relancer. Sur Windows, l’installateur met à jour les fichiers de l’application et conserve les données. Pour Docker : `docker compose up -d --build` après mise à jour du dépôt. Sauvegarder les données avant une migration majeure.

La distribution standard utilise une signature Mac ad hoc gratuite et des exécutables Windows sans certificat payant. Le workflow manuel génère les artefacts ; un tag de version publie une release après les tests sur les systèmes cibles. Aucun téléchargement automatique n’est exécuté par le service.

## Revenir à une sauvegarde

1. Arrêter le mode actif, attendre la restauration, puis quitter l’application ou arrêter son service. Une restauration de base ne rappelle pas à elle seule une scène lumineuse.
2. Conserver une copie complète du profil actuel, de son coffre et de la sauvegarde compatible. Garder le même chemin sur Mac pour retrouver l’entrée du trousseau.
3. Avec le binaire de la version visée :

```sh
./f1-hue --data /chemin/profil --restore-backup /chemin/sauvegarde.sqlite3
```

Cette commande exclusive vérifie l’intégrité et le format de la sauvegarde, crée une copie de sécurité du profil actuel, puis remplace sa base. Une application ancienne refuse un schéma plus récent au démarrage ; elle ne le renumérote pas. Cette restauration explicite permet de revenir à une sauvegarde compatible, y compris après un retour de version. Recréer la liaison Hue si le coffre ou son compte système a changé. Les copies de base contiennent aussi les paramètres d’accès serveur.

Les sauvegardes et anciennes versions ne sont pas effacées automatiquement. Les retirer manuellement après validation de la mise à jour, en gardant au moins un ensemble cohérent récupérable.

## Récupération des lampes

Si Stop échoue, l’ambiance initiale et les ressources de restauration sont conservées ; aucun nouveau mode ne peut l’écraser. La carte de récupération permet de réessayer, de relier **le même pont**, de restaurer les anciennes lampes encore présentes, ou d’archiver puis d’abandonner explicitement. Une lampe absente conserve sa sauvegarde après une récupération partielle. Un autre pont ne peut pas recevoir cette ancienne ambiance par erreur.

L’abandon demande `ABANDONNER`, archive les informations dans `recovery/` et cesse les tentatives automatiques. Il peut laisser des couleurs ou ressources temporaires sur le pont. Les pulsations natives expirent sans renouvellement, mais une remise en état manuelle dans Hue peut être nécessaire. Une ressource temporaire supprimée est recréée ; une ressource modifiée par une autre application n’est pas utilisée aveuglément.

## Diagnostic

- Bureau : menu **Ouvrir les journaux**.
- Docker : `docker compose logs --tail=100 f1-hue`.
- Linux : `journalctl --user -u f1-hue`.
- Santé du processus : `GET /health` ; disponibilité : `GET /ready` (503 pendant initialisation, attente réseau ou récupération). l’état complet et le journal exigent une session.
- F1 : hors séance, une connexion réussie peut retourner une séance terminée. Cela valide le transport, pas la réception d’un nouveau drapeau en course.
- Pulsations : elles sont exécutées nativement par le pont (API v1). Aucun espace Entertainment n’est requis. Stop annule les renouvellements puis la pulsation de chaque lampe sélectionnée.
- Profil occupé : une seule instance peut ouvrir un dossier de données. Arrêter l’application de bureau avant `make web`. `--check-live` reste utilisable sans ouvrir ce profil.
- Après un crash : le lanceur retente jusqu’à trois démarrages par minute ; une boucle d’échecs reste visible dans les journaux. La récupération du précédent effet finit avant le lancement d’un nouveau mode.

Le diagnostic exportable expose réception, planification, commande appliquée, préemption, restauration confirmée et erreur, avec identifiants de corrélation. Il distingue le délai TV du temps de traitement, sans mesurer la lumière physique. La connexion F1 et la fraîcheur du dernier message sont affichées séparément. Les journaux des lanceurs tournent à 2 Mo, avec trois archives ; les limites journald/Docker relèvent de leur configuration.
