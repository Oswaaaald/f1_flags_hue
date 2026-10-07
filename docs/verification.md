# Vérification du projet

Dernière vérification locale : 7 octobre 2026.

## Preview.9 : pulsations et restauration groupées

Le démarrage d’une pulsation applique la couleur et `alert=lselect` (ou `select` pour le bleu) dans une seule commande avec `transitiontime=0`, pour éviter un fondu depuis la couleur précédente pendant la première pulsation. Stop utilise une commande au groupe exact, également après un redémarrage. Si le groupe a changé, l’annulation reste limitée aux lampes mémorisées. Les requêtes réutilisent la connexion HTTPS avec le même certificat vérifié.

Une scène v2 temporaire restaure ensemble les états individuels des lampes choisies, puis son nettoyage supprime les ressources appartenant au mode. La restauration n’est pas rejouée après la fin d’un effet fixe. Un groupe élargi, une scène modifiée, une réponse de création perdue et les erreurs de rappel sont couverts par les tests de récupération.

- Compilation .NET : réussie sans avertissement ni erreur. TypeScript strict et esbuild : réussis.
- 157 contrôles C# réussis, dont 18 nouveaux contrôles de restauration, zones et récupération. Les contrôles existants de pulsation, de couleur et de Stop ont été adaptés aux commandes groupées.
- 20 contrôles HTTP et 17 contrôles de connexion automatique réussis sur des profils et ponts simulés.

Ces tests vérifient les commandes envoyées et le traitement des réponses d’un pont simulé. Ils ne valident pas visuellement la pulsation sur les ampoules ni un écart physique nul sur le réseau Zigbee.

Le pont réel a ensuite révélé deux contraintes qui manquaient au simulateur : une zone v2 contient des services `light`, et une action de scène ne peut combiner `effects.effect` avec couleur, température ou dégradé, même pour `no_effect`. Le format des zones, l’inventaire des zones et la normalisation des états sauvegardés ont été corrigés ; le simulateur refuse désormais ces combinaisons. Les anciennes restaurations en attente sont normalisées avant récupération.

L’application dans Applications a été mise à jour et redémarrée. `/health` répond `ready: true` avec preview.9. La récupération des trois lampes s’est terminée ; les aperçus SC et GREEN ont ensuite été lancés sur le pont réel et arrêtés sans erreur ni marqueur de récupération restant. La restauration est acceptée par le pont, les réglages sont identiques et le direct a été relancé dans son mode initial. Ces essais ne constituent pas une observation visuelle du rendu physique.

## Preview.8 : changements de couleur groupés

Les couleurs fixes de plusieurs lampes sont envoyées par une seule commande au groupe Hue contenant exactement la sélection. La boucle de commandes individuelles et sa pause de 100 ms ont été supprimées. Le groupe est réutilisé ou créé sans modifier une pièce existante ; sa composition est vérifiée avant chaque commande. Le même adaptateur est utilisé par le direct, les aperçus, la séquence et le replay.

- Compilation .NET : réussie, aucun avertissement ni erreur. TypeScript strict et esbuild : réussis.
- 139 contrôles C# réussis, dont 12 nouveaux contrôles sur les couleurs groupées : cinq couleurs fixes, luminosité et fondu, réutilisation et création du groupe exact, groupe modifié, erreurs du pont, durée fixe, restauration et sélection d’une lampe unique.
- 20 contrôles HTTP existants et 17 contrôles de connexion automatique réussis, également sur le service inclus dans le paquet Mac ARM64.
- Paquet Mac ARM64 construit ; vérification de version, d’absence de fichiers privés et de signature locale réussie.

Les tests utilisent un pont simulé et contrôlent les requêtes effectivement envoyées. Ils ne mesurent pas le décalage physique entre les ampoules ni le temps de propagation Zigbee. La restauration conserve les états individuels des lampes choisies.

Les workflows du commit `50857b3` ont terminé avec succès le 7 octobre 2026 : [Verify sur main](https://github.com/Oswaaaald/f1_flags_hue/actions/runs/37667579465), [Verify sur le tag](https://github.com/Oswaaaald/f1_flags_hue/actions/runs/37667579590) et [Build installers](https://github.com/Oswaaaald/f1_flags_hue/actions/runs/37667580006). Les six paquets natifs et l’image Docker sont construits ; la [release preview.8](https://github.com/Oswaaaald/f1_flags_hue/releases/tag/v2.0.0-preview.8) contient les dix fichiers attendus, tous téléversés.

L’application preview.8 a remplacé celle d’Applications puis a redémarré : `/health` indique la version attendue et `ready: true`. Les empreintes des réglages, du mot de passe serveur et de `bridge.enc` sont identiques avant et après la mise à jour. L’ancienne application est conservée en sauvegarde temporaire.

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

Les tests vérifient notamment le refus des clients anonymes, des tickets expirés ou réutilisés, des mauvaises origines et des Host non canoniques ; les permissions du fichier privé, le cookie de session de huit heures, l’invalidation au redémarrage, la déconnexion et la conservation du mode serveur. Aucun test d’effet n’a commandé le pont réel pendant cette passe. La compilation du lanceur Windows depuis Mac ne constitue pas une vérification de son installation interactive.

### GitHub Actions et publication de la preview.7

Les workflows du commit `04346e8` ont terminé avec succès le 7 octobre 2026 :

- [Verify sur main](https://github.com/Oswaaaald/f1_flags_hue/actions/runs/37662334919) et [Verify sur le tag](https://github.com/Oswaaaald/f1_flags_hue/actions/runs/37662334315) : contrôles réussis sur Mac, Windows et Linux, dont 127 contrôles C#, 20 contrôles HTTP existants et 17 contrôles de connexion automatique par système.
- [Build installers](https://github.com/Oswaaaald/f1_flags_hue/actions/runs/37662334799) : six paquets Mac ARM64/Intel, Windows x64/ARM64 et Linux x64/ARM64 construits sur leurs systèmes natifs. Chaque service empaqueté passe les 20 contrôles HTTP existants, les 17 contrôles de connexion automatique et la vérification de version et d’absence de fichiers privés. Les deux installateurs Windows et l’image Docker multiarchitecture sont construits avec succès.
- [Release v2.0.0-preview.7](https://github.com/Oswaaaald/f1_flags_hue/releases/tag/v2.0.0-preview.7) : publiée avec les dix fichiers attendus, dont les installateurs, les archives portables, l’image Docker et les sommes SHA-256. Les fichiers sont tous marqués comme téléversés.

Ces contrôles ne vérifient pas l’installation interactive Windows, la notarisation Apple ni le rendu physique des lampes.

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
