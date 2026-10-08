# Sécurité

## Périmètre

F1 Hue Sync est une application personnelle à utilisateur unique. Le mode bureau écoute uniquement sur `127.0.0.1` et utilise un ticket du lanceur, limité à une minute et à un usage. Le mode serveur exige un mot de passe. Un réseau local partagé exige également un transport chiffré : HTTPS ou tunnel SSH/VPN. Ne pas exposer le pont Hue sur Internet. Voir [les recettes d’hébergement](docs/hosting.md).

Le service possède une autorisation Hue et peut commander la sélection choisie. Le navigateur ne reçoit pas cette clé. La première liaison fait confiance au certificat présenté sur le réseau local, puis mémorise son empreinte. Relier le pont demande son bouton physique. **Oublier ce pont** efface la copie locale ; cela ne révoque pas l’autorisation déjà créée sur le pont. Pour révoquer les anciennes clés, utiliser la gestion des applications autorisées Hue.

Le coffre est chiffré avec AES-GCM. Sa clé est conservée dans le trousseau macOS, protégée par DPAPI sous Windows et placée dans un fichier privé sous Linux. Un accès au compte utilisateur ou au profil Linux complet permet l’accès aux données : ce coffre ne protège pas contre un système déjà compromis. Garder les sauvegardes privées, y compris les archives de récupération et la base qui contient l’authentification serveur.

## Signaler un problème

Utiliser **Security → Advisories → Report a vulnerability** du [dépôt](https://github.com/Oswaaaald/f1_flags_hue/security/advisories/new) si le signalement privé est proposé. Si cette fonction est indisponible, ouvrir une issue demandant un canal privé, sans publier le détail exploitable ni les données du profil. Ne jamais joindre `bridge.enc`, `vault.key`, `config.yml`, cookies, mots de passe ou tickets du lanceur. Le diagnostic exporté est prévu pour le dépannage ; le relire avant tout partage.

Inclure la version, le système, les étapes minimales, l’impact et une reproduction avec un profil de simulation. Ne pas essayer une attaque sur le pont ou le réseau d’une autre personne.

## Versions maintenues

- Seule la dernière preview 2.x publiée reçoit les correctifs ; les anciennes previews ne sont pas des versions LTS.
- Python reste disponible pour migration et diagnostic local. Il ne reçoit plus de nouvelles fonctionnalités et son serveur ne doit pas être exposé au réseau.
- Les builds embarquent .NET : mettre à jour le runtime du système ne corrige pas un ancien paquet autonome. Republier les six exécutables après une mise à jour de sécurité .NET, SQLite ou d’une autre dépendance embarquée.
- Vérifier les avis et les PR de dépendances chaque semaine, et reconstruire une version au moins lors des mises à jour de sécurité pertinentes. Un projet personnel ne garantit pas de délai contractuel de réponse.
- .NET 10 est LTS jusqu’au 14 novembre 2028 selon la [politique Microsoft](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core). Prévoir sa migration avant cette échéance.

## Chaîne de distribution

Les workflows épinglent les actions par commit, les images par digest et les dépendances par fichiers de verrouillage, y compris par architecture. Dependabot propose les mises à jour. Un audit hebdomadaire contrôle les dépendances et l’historique Git ; les paquets publiés incluent un inventaire SBOM, un rapport de vulnérabilités et des attestations GitHub de provenance.

Ces contrôles détectent des problèmes connus ; ils ne prouvent pas l’absence de tout défaut. Une attestation et une somme SHA-256 ne remplacent pas la confiance dans les mainteneurs et les workflows. Les paquets Mac ad hoc et Windows sans certificat commercial peuvent déclencher Gatekeeper ou SmartScreen. Ne pas désactiver globalement ces protections. Voir [la distribution](docs/releases.md).
