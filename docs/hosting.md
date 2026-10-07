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

Pour sauvegarder, arrêter le service puis copier son dossier de données. Conserver aussi l’ancien `config.yml` tant que la migration n’a pas été vérifiée. Aucune commande de mise à jour fournie ne supprime ce dossier.

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
| `--reset-password` | Révoque les sessions et génère un nouveau code local |
| `--simulate` | Pont simulé ; utiliser un dossier de données séparé |
| `--no-feed` | Désactive la connexion externe F1 pour les vérifications locales |
| `F1_HUE_ALLOWED_HOSTS` | Noms DNS autorisés, séparés par des virgules |
| `F1_HUE_TLS_CERT` | Chemin d’un certificat serveur PFX pour HTTPS |
| `F1_HUE_TLS_PASSWORD` | Mot de passe du PFX, à injecter hors du dépôt |

Les adresses IP littérales et `localhost` sont acceptées comme Host. Les noms DNS personnalisés doivent être ajoutés explicitement. Les en-têtes proxy transmis ne sont pas considérés fiables par défaut.

En mode bureau, seul le Host `127.0.0.1` avec le port exact du service est accepté, même si `F1_HUE_ALLOWED_HOSTS` est défini. Le lanceur obtient un ticket utilisable une seule fois pendant une minute. Le navigateur l’échange contre sa session et retire immédiatement le ticket de l’adresse. Les vérifications Host/Origin et des requêtes de modification restent actives. `make web`, Linux et Docker utilisent le mode serveur avec mot de passe.

## Accès depuis un téléphone

Le navigateur ne commande jamais directement le pont. Il communique avec le service installé chez soi. Pour un accès sur le réseau domestique, écouter sur `0.0.0.0`, autoriser le pare-feu local et utiliser l’adresse du serveur. L’authentification est obligatoire, même en réseau local.

HTTP local ne chiffre pas le mot de passe ni la session sur le réseau. Pour le LAN partagé, configurer HTTPS avec `F1_HUE_TLS_CERT` et un certificat reconnu par les appareils. Aucun certificat auto-signé n’est installé ou approuvé automatiquement. Un reverse proxy peut aussi terminer TLS, mais il faut préserver une origine cohérente ; ne pas désactiver les vérifications Host/Origin pour le contourner. Une instance HTTPS directe est la configuration la plus simple pour cette preview.

Docker Compose limite désormais le port à `127.0.0.1:8080:8080`. Pour accéder depuis un autre appareil, configurer HTTPS dans le conteneur, monter le PFX en lecture seule et remplacer explicitement cette liaison par l’adresse LAN du serveur. Mettre aussi `F1_HUE_HEALTH_URL` à une URL HTTPS validée pour le contrôle de santé. Le conteneur fonctionne sans root, sans capacités Linux, avec un système de fichiers en lecture seule hors volume et `/tmp`.

## macOS et le pont

Autoriser **Réseau local** pour l’application qui lance le service. Une autorisation donnée à Terminal n’autorise pas automatiquement iTerm, Codex ou un autre exécutable. Le lanceur Mac inclut une description de cet accès et du service Bonjour Hue.

Si `--check-hue` répond « No route to host » alors que le navigateur accède au pont, vérifier l’autorisation de l’application dans Réglages Système → Confidentialité et sécurité → Réseau local. Vérifier aussi le réseau Wi-Fi, l’isolation des clients et l’adresse du pont.

La liaison utilise le bouton physique et HTTPS. L’import d’une ancienne clé établit l’empreinte du certificat à la première connexion. Une empreinte modifiée n’est jamais acceptée silencieusement avec la clé existante.

## Mettre à jour

Arrêter l’application avec son menu, remplacer l’application par le nouveau paquet puis la relancer. Sur Windows, l’installateur met à jour les fichiers de l’application et conserve les données. Pour Docker : `docker compose up -d --build` après mise à jour du dépôt. Sauvegarder les données avant une migration majeure.

La distribution standard utilise une signature Mac ad hoc gratuite et des exécutables Windows sans certificat payant. Le workflow manuel génère les artefacts ; un tag de version publie une release après les tests sur les systèmes cibles. Aucun téléchargement automatique n’est exécuté par le service.

## Diagnostic

- Bureau : menu **Ouvrir les journaux**.
- Docker : `docker compose logs --tail=100 f1-hue`.
- Linux : `journalctl --user -u f1-hue`.
- Santé du service : `GET /health` ; l’état complet et le journal exigent une session.
- F1 : hors séance, une connexion réussie peut retourner une séance terminée. Cela valide le transport, pas la réception d’un nouveau drapeau en course.
- Pulsations : elles sont exécutées nativement par le pont (API v1). Aucun espace Entertainment n’est requis. Stop annule les renouvellements puis la pulsation de chaque lampe sélectionnée.
- Profil occupé : une seule instance peut ouvrir un dossier de données. Arrêter l’application de bureau avant `make web`. `--check-live` reste utilisable sans ouvrir ce profil.
- Après un crash : le lanceur retente jusqu’à trois démarrages par minute ; une boucle d’échecs reste visible dans les journaux. La récupération du précédent effet finit avant le lancement d’un nouveau mode.
