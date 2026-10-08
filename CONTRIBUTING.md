# Contribuer

## Environnement et vérification

Installer le SDK indiqué dans `global.json`, Node 22 et npm. Pour les tests historiques, utiliser Python 3.12 et `requirements.txt`. Les utilisateurs des paquets n’ont besoin d’aucun de ces outils.

```sh
scripts/build.sh
node tests/audit-api.mjs
F1_HUE_TEST_BUILD=Release node tests/api-smoke.mjs
node tests/desktop-auth.mjs
node scripts/check-contracts.mjs
scripts/dotnet.sh format whitespace F1Hue.slnx --no-restore --verify-no-changes
npm run format:check --prefix apps/web
python -m unittest discover -s tests -q
```

Les tests utilisent des profils temporaires, un pont simulé ou un transport Hue factice. Ne pas pointer une suite de tests vers le profil de l’application ni vers un pont réel. Une commande Hue réelle exige une vérification volontaire, avec possibilité d’arrêter les lampes et conservation de leur ambiance initiale.

Les parcours Chromium/Firefox/WebKit sont dans `apps/web/e2e`. Après `scripts/build.sh`, installer leurs navigateurs avec `npx playwright install` depuis `apps/web`, puis `npm run test:e2e`. La CI Linux vérifie notamment les conflits entre onglets, la calibration, Stop, la sélection partielle, le clavier, les contrastes et une largeur de 320 px. Les captures et traces des échecs sont conservées sept jours.

## Organisation

- `Core` : événements F1, règles, calibration, ordonnanceur, annulation, reprise automatique et chronologie d’exécution ; indépendant du pont et du web.
- `Infrastructure` : flux officiel, SQLite, coffre, transport Hue, pulsations et restauration confirmée.
- `apps/host` : contrats HTTP, sécurité, proxy, SSE, récupération et composition des services.
- `apps/web/src` : vues, client API avec délais, navigation, brouillons et contrôles de calibration.
- `deploy` : lanceurs et installateurs. Un seul service possède un profil ; aucune commande concurrente ne doit dépasser son coordinateur.

Les drapeaux sont reçus puis planifiés avec leur offset. Les règles sont lues au moment de jouer l’événement. Une erreur de journal ne doit pas bloquer un drapeau ; une erreur de sauvegarde de l’ambiance doit empêcher la prise de contrôle. Stop et tout changement de drapeau doivent préempter une ancienne animation sans laisser de renouvellement tardif.

## Contrats et style

Modifier les DTO C# de `ApiContracts.cs`, puis `make contracts`. Ne pas modifier manuellement `types.ts`. `/api/schema` expose les schémas JSON authentifiés ; [la référence API](docs/api.md) associe contrats et routes. Conserver des codes d’erreur stables et traiter les entrées invalides en 400, les conflits en 409 et la limitation d’authentification en 429.

Exécuter `scripts/dotnet.sh format whitespace F1Hue.slnx --no-restore` et `npm run format --prefix apps/web`. Les avertissements C# sont des erreurs de compilation. Ajouter un test qui reproduit le défaut métier ; les variations purement visuelles se vérifient avec les parcours navigateur.

## Dépendances et versions

- Mettre à jour `global.json` et les digests Docker lors des mises à jour SDK/runtime. Tester les versions réellement embarquées via `--runtime-info`.
- Après une mise à jour NuGet ou du SDK, lancer `scripts/lock-runtimes.sh` et versionner tous les `packages*.lock.json`. Les restaurations de publication doivent rester en mode verrouillé.
- `npm ci` utilise le lock npm ; les dépendances Python historiques sont épinglées dans `requirements.txt`.
- Vérifier les PR Dependabot et les résultats **Scheduled security audit**. Ne pas contourner un résultat critique en désactivant le scanner ; documenter toute exception justifiée avec sa date de réexamen.
- `Directory.Build.props`, npm et le numéro de build Mac doivent correspondre ; `scripts/check-release-tag.mjs` le vérifie.
- Tenir `CHANGELOG.md` et le rapport de qualification à jour. Ne pas annoncer comme effectué un essai matériel ou système seulement simulé.

## Qualification avant publication

**Verify** bloque les erreurs de moteur, API, frontend, navigateurs et conteneur. **Build installers** fabrique les six paquets, exécute les services natifs et les essais d’installation disponibles, puis contrôle le SBOM avant toute publication. Le mode manuel sans case de publication, ou une branche `release/nom`, permet de qualifier les artefacts. Les constructions et tests indépendants se déroulent en parallèle ; la publication exige leur réussite à tous.

Compléter ces vérifications par une séance F1 réelle et une matrice matérielle : une lampe et plusieurs lampes, Galaxy statique, Stop pendant une pulsation, lampe absente, redémarrage du pont, perte réseau et veille/réveil. Vérifier macOS Réseau local/Trousseau, Windows SmartScreen/DPAPI, clavier mobile et lecteur d’écran. La CI ne mesure ni le délai optique ni la propagation Zigbee. Voir [les résultats et limites](docs/audit-corrections-2026-10-08.md).
