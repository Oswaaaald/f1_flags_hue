# Vérification de la preview.6

Dernière vérification locale : 7 octobre 2026.

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

Dans Firefox, les vues Direct, Hue, Tests et Drapeaux ont été ouvertes sur une instance simulée séparée. Un aperçu Safety Car a été lancé puis arrêté ; les boutons sont redevenus disponibles. Le compte, les lampes et les événements de ces tests sont jetables. L’application installée a ensuite été vérifiée en lecture seule sur bureau et en vue adaptative de 402 pixels : Direct, Calibration TV et Drapeaux sont lisibles ; Stop, la navigation et la déconnexion restent accessibles. Ses réglages n’ont pas été modifiés par ces contrôles.

## Portée des résultats

Le flux F1 a fourni une séance terminée. Cela vérifie la connexion, pas la réception d’un nouvel événement pendant une course réelle. Le rendu physique des lampes n’a pas été évalué visuellement pendant cette passe.

Les ZIP Windows sont compilés localement ; les installateurs Inno Setup et les contrôles sur les systèmes natifs sont configurés dans le workflow GitHub. Leur exécution distante doit être verte avant publication. Une compilation croisée ne prouve pas le fonctionnement complet du bureau Windows ou Linux.

Les paquets gratuits utilisent une signature Mac ad hoc et aucun certificat commercial Windows. Aucune notarisation Apple ni mise à jour automatique signée n’est annoncée.

## Reproduire

```sh
scripts/build.sh
F1_HUE_TEST_BUILD=Release node tests/api-smoke.mjs
F1_HUE_TEST_BUILD=Release node tests/api-smoke.mjs --network
.venv/bin/python -m unittest discover -s tests -q
npm run format:check --prefix apps/web
npm audit --prefix apps/web
node scripts/audit-dotnet.mjs
node scripts/check-release.mjs
```

Les tests HTTP créent un compte et un profil temporaires avec un pont simulé. Ils ne doivent pas être dirigés vers une instance réelle.
