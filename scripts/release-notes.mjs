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

Après Stop, les champs Hue et les boutons de lancement redeviennent disponibles dès la fin de l’arrêt, sans changer d’onglet. Les modifications non enregistrées du formulaire sont conservées.

Les couleurs des drapeaux sont envoyées en une seule commande au groupe exact des lampes choisies, avec le fondu configuré. L’application n’ajoute plus de pause entre chaque lampe pour ces changements de couleur.

Les clignotements SC, VSC et damier utilisent la pulsation native du pont, comme dans l’ancienne version Python. La couleur est appliquée immédiatement au démarrage, sans fondu depuis la couleur précédente. Stop arrête le groupe ensemble, et une scène Hue v2 restaure les états individuels en un seul rappel. La connexion HTTPS au pont est réutilisée pour réduire les délais entre commandes.

Stop attend maintenant la confirmation des états des lampes avant de terminer. Une lampe restée sur la couleur du drapeau déclenche une nouvelle tentative groupée ; un échec conserve l’état initial et propose de réessayer l’arrêt.

L’ambiance est mémorisée juste avant le premier drapeau, puis à nouveau après un retour au repos. Une scène choisie dans Hue pendant l’attente du direct est conservée. Stop sans drapeau joué ne change plus les couleurs.

Les applications Mac et Windows te connectent automatiquement à l’interface, sans mot de passe à créer. Utilise leur menu **Ouvrir F1 Hue Sync** pour te reconnecter. Les installations réseau, Linux et Docker gardent la connexion par mot de passe.

Sur Mac, le trousseau peut demander séparément l’accès à **F1Hue.Vault**, qui protège la liaison à ton pont Hue.

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
