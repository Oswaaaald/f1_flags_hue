# Distribuer F1 Hue Sync gratuitement

Les paquets de bureau sont autonomes : les utilisateurs n’installent ni Python, ni Node, ni .NET. La signature locale Mac est gratuite. Les exécutables Windows sont distribués sans certificat commercial. Les paquets ne sont pas notarisés par Apple ni certifiés par Microsoft.

## Installer

- **Mac Apple Silicon** : `f1-hue-osx-arm64.zip`.
- **Mac Intel** : `f1-hue-osx-x64.zip`.
- **Windows x64 / ARM64** : installateur `F1Hue-VERSION-win-ARCH-Setup.exe`, ou ZIP portable contenant `F1Hue.exe` et son dossier `service`.
- **Linux x64 / ARM64** : archive `.tar.gz`, puis `./install.sh`.

Sur Mac, déplacer le `.app` dans Applications. Si macOS bloque sa première ouverture, suivre la [procédure officielle « Ouvrir quand même »](https://support.apple.com/fr-fr/102445) dans Confidentialité et sécurité. Sur Windows, une alerte de réputation peut apparaître ; vérifier la provenance du téléchargement et sa somme SHA-256 avant d’autoriser son exécution.

`SHA256SUMS` permet de vérifier que le fichier téléchargé correspond à celui de la release. Ce contrôle ne remplace pas un certificat d’éditeur.

## Fabriquer une version

1. Mettre à jour `Directory.Build.props`, le numéro de build Mac et les métadonnées npm.
2. Lancer `scripts/build.sh`, les tests HTTP et la suite Python.
3. Pousser le code dans le dépôt.
4. Lancer **Actions → Build installers → Run workflow** pour obtenir les artefacts sans publier.
5. Pour publier, créer et pousser un tag `vVERSION` correspondant au code. Le workflow vérifie les tests, construit les six cibles, exécute les contrôles HTTP sur les services empaquetés, puis crée la release et ses sommes de contrôle. Un échec bloque la publication.

Les versions contenant un suffixe comme `-preview.6` sont publiées comme préversions. Aucun secret Apple ou Windows n’est nécessaire au workflow standard. Les options de signature commerciale restent facultatives dans les scripts locaux.

Les [runners standards GitHub Actions](https://docs.github.com/en/actions/reference/runners/github-hosted-runners) sont gratuits pour les dépôts publics. Un dépôt privé possède des limites distinctes : la publication de ce projet ne nécessite pas de souscrire un service payant.

## Vérification locale

```sh
scripts/publish.sh osx-arm64
scripts/publish.sh osx-x64
scripts/publish.sh win-x64
scripts/publish.sh win-arm64
scripts/publish.sh linux-x64
scripts/publish.sh linux-arm64
node scripts/check-release.mjs
```

Le script Unix produit les ZIP portables Windows. Les installateurs `.exe` sont fabriqués sur Windows avec `deploy/windows/build.ps1` et Inno Setup 6. Les tests de compilation croisée ne prouvent pas le fonctionnement de l’interface de bureau sur un autre système ; le workflow utilise des machines natives pour tester les services.
