# Reproductions de l’audit du 7 octobre 2026

Ces sondes documentent les comportements de la preview.13. Elles utilisent uniquement des profils SQLite temporaires et un transport Hue simulé. Elles ne lisent pas le coffre de l’utilisateur et ne commandent pas ses lampes.

Exécuter ces reproductions sur la preview.13 : les nouveaux garde-fous peuvent interrompre volontairement ces anciennes sondes. Les tests de non-régression actuels sont intégrés à `tests/`, et le [suivi des correctifs](../../audit-corrections-2026-10-08.md) décrit leurs résultats.

Les observations sont dans [evidence.json](evidence.json). Le [rapport](../../audit-2026-10-07.md) précise les impacts, limites et corrections recommandées. Ces programmes impriment des observations : ce ne sont pas encore des tests de non-régression intégrés à la CI.

Depuis la racine du dépôt, avec le SDK .NET 10 et les dépendances du projet installés :

```sh
scripts/dotnet.sh run --project docs/audits/2026-10-07/Probe.csproj -c Release
```

Cette sonde couvre snapshot Safety Car, version du schéma SQLite, erreur d’écriture du journal, restauration d’une seule lampe et coût des lectures avec 100 000 événements synthétiques. Les profils sont supprimés à la fin.

Pour vérifier aussi les trois archives F1 en lecture seule, avec un accès Internet :

```sh
scripts/dotnet.sh run --project docs/audits/2026-10-07/Probe.csproj -c Release -- --archives
```

Cette option contacte uniquement les chemins officiels prédéfinis dans `ReplayCatalog` ; elle n’ouvre aucun profil et ne pilote aucune lampe.

Pour les observations HTTP, compiler d’abord le frontend et le service :

```sh
npm run build --prefix apps/web
scripts/dotnet.sh build apps/host/F1Hue.Host.csproj -c Release
node docs/audits/2026-10-07/http.mjs
```

Le script démarre son propre service simulé sur `127.0.0.1:18371`. Ce port doit être libre ; `F1_HUE_AUDIT_PORT` permet de le changer. Il crée un compte jetable, teste le proxy HTTPS et douze entrées invalides, puis demande l’arrêt pendant un Safety Car simulé avec un client SSE connecté. La sonde peut durer environ 40 secondes. Elle termine uniquement son propre processus et supprime son profil.

Le mot de passe fixe du script sert exclusivement au compte local temporaire. Ne pas le réutiliser dans une installation réelle. Le port 8081 de l’application installée n’est pas utilisé.

Les tailles et durées de `evidence.json` sont celles de la machine et des données de cet audit. Elles ne constituent pas des objectifs de performance universels.
