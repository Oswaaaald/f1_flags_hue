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

### ${download("Télécharger pour Windows — PC Intel ou AMD", `F1Hue-${version}-win-x64-Setup.exe`)}

Ouvre le fichier téléchargé et suis les étapes de l’installation.

Tes réglages et ta liaison Hue sont conservés lors d’une mise à jour.

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

[Guide d’installation](https://github.com/${repository}/blob/${tag}/README.md#utiliser-lapplication) · [Historique des changements](https://github.com/${repository}/commits/${tag})
`);
