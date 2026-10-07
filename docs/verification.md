# Vérification du projet

Dernière vérification locale : 7 octobre 2026.

## Preview.7 : connexion automatique des applications de bureau

Les applications Mac et Windows utilisent maintenant une preuve privée du lanceur pour ouvrir une session locale. Aucun mot de passe n’est demandé dans leur interface. Les installations serveur, Linux et Docker conservent l’authentification par mot de passe. Le coffre Hue et un éventuel ancien mot de passe serveur restent conservés.

| Contrôle local | Résultat |
| --- | --- |
| Compilation .NET avec avertissements traités comme erreurs | Réussie, aucun avertissement |
| Contrôles C# | 127 réussis, dont 9 sur les tickets du lanceur |
| Tests HTTP existants | 20 réussis |
| Tests HTTP de connexion automatique | 17 réussis |
| TypeScript strict, esbuild et formatage | Réussis |
| Lanceurs Swift et Windows | Compilés ; lanceur Windows compilé depuis Mac |
| Service inclus dans le paquet Mac ARM64 | 20 contrôles HTTP et 17 contrôles de connexion automatique réussis |
| Contrôle du paquet et signature locale Mac | Réussis |
| Application dans Applications | Preview.7 installée ; réglages, mot de passe serveur et coffre Hue conservés par comparaison d’empreintes |

Sur une instance simulée séparée, le navigateur a été ouvert par ticket, puis déconnecté. L’adresse ne conserve pas le ticket et l’écran de déconnexion propose de rouvrir l’application, sans formulaire de mot de passe. L’ouverture du lanceur réellement installé a ensuite affiché directement le tableau de bord dans Firefox avec la version preview.7, la sélection Hue et la calibration existantes.

Les tests vérifient notamment le refus des clients anonymes, des tickets expirés ou réutilisés, des mauvaises origines et des Host non canoniques ; les permissions du fichier privé, l’expiration des sessions de bureau, l’invalidation au redémarrage, la déconnexion et la conservation du mode serveur. Aucun test d’effet n’a commandé le pont réel pendant cette passe. La compilation du lanceur Windows depuis Mac ne constitue pas une vérification de son installation interactive.

## Preview.6 : audit précédent

## Corrections issues de l’audit

- Stop annule les opérations lentes et les commandes reçues précédemment. Une requête de connexion incomplète ne bloque plus la commande d’arrêt.
- La récupération au lancement est sérialisée avec les effets et ne commence qu’après l’ouverture du port HTTP.
- Un verrou système empêche deux instances de partager le même profil et de restaurer les lampes l’une de l’autre.
- Les drapeaux de secteur ne remplacent plus SC, VSC ou rouge. La fin d’une neutralisation reste un événement global. Le replay lit aussi TrackStatus.
- La déduplication prend en compte la séance. Les heures F1 sans suffixe sont interprétées en UTC.
- Le délai de téléchargement des archives couvre les en-têtes et le corps. Stop annule un téléchargement sans attendre ce délai.
- Une capture qui échoue après un changement de sélection ne réutilise jamais les lampes du mode précédent.
- Le lanceur Windows conserve la propriété du service si son arrêt dépasse le délai. Les lanceurs Mac et Windows retentent un démarrage après un crash, au maximum trois fois par minute.
- Les ressources web portent une empreinte dans leur URL pour éviter de mélanger une ancienne feuille CSS avec une interface mise à jour. La déconnexion reste disponible sur mobile.
- Le moteur Entertainment expérimental et BouncyCastle ont été supprimés. La pulsation native v1 et la récupération des anciennes zones restent prises en charge.
- Werkzeug a été corrigé ; signalrcore et sa dépendance MessagePack ont été remplacés par un transport JSON utilisant websocket-client. L’interface Python refuse les clients LAN. Docker publie son port sur la boucle locale par défaut.

## Contrôles exécutés localement

| Contrôle | Résultat |
| --- | --- |
| Compilation .NET, avertissements traités comme erreurs | Réussie |
| TypeScript strict, esbuild et formatage | Réussis |
| Contrôles C# | 118 réussis |
| Tests HTTP sur un service simulé autonome | 20 réussis |
| Téléchargement et démarrage d’une archive officielle F1 | Réussis, 21e contrôle HTTP |
| Tests de l’ancienne version Python et de son transport JSON | 77 réussis |
| Audit NuGet avec dépendances transitives | Aucun avis de vulnérabilité remonté |
| Audit npm | Aucun avis de vulnérabilité remonté |
| Audit OSV du graphe Python du projet | 14 paquets examinés, aucun avis remonté |
| Flux officiel avec le moteur .NET et le transport Python | Connexion et snapshot reçus |
| Service Mac empaqueté | 20 contrôles HTTP réussis |
| Docker isolé | 19 contrôles HTTP réussis |
| Paquets Mac ARM64/Intel, Windows x64/ARM64, Linux x64/ARM64 | Construits avec la même version ; sommes SHA-256 générées |
| Signature locale des applications Mac | `codesign --verify --deep --strict` réussi |
| Application installée dans Applications | Preview.6, réglages conservés par comparaison d’empreintes avant/après |

Les 13 contrôles consacrés au moteur Entertainment supprimé ont été retirés. Les autres contrôles existants ont été conservés et complétés par les reproductions de l’audit.

Les tests Python de restauration et de remplacement attendent désormais des événements explicites. Le calcul de l’espacement utilise une horloge contrôlée pour éviter de mesurer l’ordonnanceur du runner. Le contrôle des permissions POSIX est exécuté sur Mac/Linux et ignoré sous Windows, où `chmod` n’expose pas ces bits. Les autres tests restent exécutés sur les trois systèmes. Git conserve les fins de ligne LF sur tous les systèmes pour le contrôle Prettier.

Dans Firefox, les vues Direct, Hue, Tests et Drapeaux ont été ouvertes sur une instance simulée séparée. Un aperçu Safety Car a été lancé puis arrêté ; les boutons sont redevenus disponibles. Le compte, les lampes et les événements de ces tests sont jetables. L’application installée a ensuite été vérifiée en lecture seule sur bureau et en vue adaptative de 402 pixels : Direct, Calibration TV et Drapeaux sont lisibles ; Stop, la navigation et la déconnexion restent accessibles. Ses réglages n’ont pas été modifiés par ces contrôles.

## Vérification GitHub Actions

Les workflows ont été exécutés le 7 octobre 2026 sur le commit `de14c40` :

- [Verify](https://github.com/Oswaaaald/f1_flags_hue/actions/runs/37640063442) : réussi sur Windows, Mac et Linux, avec 118 contrôles C# et 20 contrôles HTTP par système. Les 77 tests Python passent sur Mac/Linux ; Windows en exécute 76 et ignore uniquement le contrôle des permissions POSIX.
- [Build installers](https://github.com/Oswaaaald/f1_flags_hue/actions/runs/37640082650) : les six paquets Mac ARM64/Intel, Windows x64/ARM64 et Linux x64/ARM64 sont construits sur leurs systèmes natifs. Chaque service empaqueté passe les 20 contrôles HTTP et le contrôle de version et d’absence de fichiers privés.
- Les installateurs Inno Setup Windows x64 et ARM64 et les ZIP portables sont générés. L’image Docker OCI est construite pour `linux/amd64` et `linux/arm64`. Les fichiers sont disponibles dans les artifacts de cette exécution.

Les problèmes relevés pendant ces exécutions ont été corrigés : fins de ligne LF pour Prettier sous Windows, tests Python indépendants de l’ordonnanceur et de la précision de l’horloge Windows, contrôle POSIX réservé aux systèmes compatibles, apostrophe dans le script PowerShell.

## Portée des résultats

Le flux F1 a fourni une séance terminée. Cela vérifie la connexion, pas la réception d’un nouvel événement pendant une course réelle. Le rendu physique des lampes n’a pas été évalué visuellement pendant cette passe.

Cette exécution manuelle n’a pas publié de release. La compilation des installateurs et les tests du service ne vérifient pas l’installation interactive ni le rendu du lanceur Windows ou Linux.

Les paquets gratuits utilisent une signature Mac ad hoc et aucun certificat commercial Windows. Aucune notarisation Apple ni mise à jour automatique signée n’est annoncée.

## Reproduire

```sh
scripts/build.sh
F1_HUE_TEST_BUILD=Release node tests/api-smoke.mjs
F1_HUE_TEST_BUILD=Release node tests/desktop-auth.mjs
F1_HUE_TEST_BUILD=Release node tests/api-smoke.mjs --network
.venv/bin/python -m unittest discover -s tests -q
npm run format:check --prefix apps/web
npm audit --prefix apps/web
node scripts/audit-dotnet.mjs
node scripts/check-release.mjs
```

Les tests HTTP créent un compte et un profil temporaires avec un pont simulé. Ils ne doivent pas être dirigés vers une instance réelle.
