import { readFileSync } from "node:fs";

const version = readFileSync(
  new URL("../Directory.Build.props", import.meta.url),
  "utf8",
).match(/<Version>([^<]+)<\/Version>/)[1];
const repository = process.env.GITHUB_REPOSITORY || "Oswaaaald/f1_flags_hue";
const tag = `v${version}`;
const release = `https://github.com/${repository}/releases/download/${tag}`;
const download = (label, file) => `[${label}](${release}/${file})`;

process.stdout.write(`## Télécharger l’application

Une seule version : **${tag}**. Choisis ton appareil.

### ${download("Télécharger pour Mac — puce Apple (M1, M2, M3…)", "f1-hue-osx-arm64.zip")}

Dézippe le fichier, déplace **F1 Hue Sync.app** dans **Applications**, puis ouvre l’application.

Si macOS bloque la première ouverture : **Réglages Système → Confidentialité et sécurité → Ouvrir quand même**. Le paquet utilise une signature locale gratuite. [Procédure Apple](https://support.apple.com/fr-fr/102445).

### ${download("Télécharger pour Windows — PC Intel ou AMD", `F1Hue-${version}-win-x64-Setup.exe`)}

Ouvre le fichier téléchargé et suis les étapes de l’installation.

Tes réglages et ta liaison Hue sont conservés lors d’une mise à jour.

## Ce qui change

- Correctif d’allumage : une lampe éteinte reçoit directement la couleur du drapeau, sans fondu depuis sa couleur mémorisée. L’allumage reste groupé et les changements de drapeau suivants respectent le fondu configuré.
- Stop et fermeture de l’application réactifs, restauration des lampes confirmée et parcours de récupération en cas de pont ou lampe indisponible.
- Direct automatique qui reprend quand le réseau revient, fraîcheur des données F1 visible, journal et diagnostic plus précis.
- Sélection Hue cohérente, brouillons conservés, conflits entre onglets détectés, luminosité en pourcentage et navigation mobile/clavier améliorée.
- Sauvegardes et migrations protégées, hébergement HTTPS derrière un proxy explicite, installation Linux avec retour arrière.
- Dépendances verrouillées, tests sur plusieurs systèmes et navigateurs, inventaire des composants et attestations de provenance.

La calibration fine **±0,1 / 0,5 / 1 / 5 secondes** reste disponible. Les pulsations natives SC/VSC/damier sont conservées, avec annulation groupée et restauration Hue v2. Arrête une scène Hue dynamique avant de lancer la synchronisation : sa progression ne peut pas être restaurée fidèlement. Couleurs fixes, températures et gradients compatibles restent pris en charge.

Les applications Mac et Windows ouvrent une session automatiquement ; Linux et Docker utilisent un mot de passe. Sur Mac, le trousseau peut demander séparément l’accès à **F1Hue.Vault** après la mise à jour.

[Changements détaillés](https://github.com/${repository}/blob/${tag}/CHANGELOG.md) · [Correctifs et limites de vérification](https://github.com/${repository}/blob/${tag}/docs/audit-corrections-2026-10-08.md)

<details>
<summary>Autres appareils et installation avancée</summary>

| Appareil | Télécharger |
| --- | --- |
| Mac avec un processeur Intel | ${download("Application Mac Intel", "f1-hue-osx-x64.zip")} |
| Windows avec un processeur ARM | ${download("Installateur Windows ARM", `F1Hue-${version}-win-arm64-Setup.exe`)} |
| Linux — PC Intel ou AMD | ${download("Paquet Linux", "f1-hue-linux-x64.tar.gz")} |
| Raspberry Pi ou Linux ARM 64 bits | ${download("Paquet Linux ARM", "f1-hue-linux-arm64.tar.gz")} |
| Windows portable — Intel ou AMD | ${download("ZIP sans installateur", "f1-hue-win-x64.zip")} |
| Windows portable — ARM | ${download("ZIP sans installateur", "f1-hue-win-arm64.zip")} |
| Docker | ${download("Archive de l’image", "f1-hue-image.tar")} |

${download("SHA256SUMS", "SHA256SUMS")} permet de vérifier les fichiers téléchargés.

</details>

[Guide d’installation](https://github.com/${repository}/blob/${tag}/README.md#utiliser-lapplication) · [Historique des changements](https://github.com/${repository}/blob/${tag}/CHANGELOG.md)
`);
