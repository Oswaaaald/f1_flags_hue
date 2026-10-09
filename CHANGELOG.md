# Historique

## 2.0.0-preview.16 — direct sans rejeu d’un ancien drapeau

- Activer le direct attend désormais les nouveaux événements F1. Le dernier drapeau mémorisé n’est plus recréé avec une nouvelle heure de réception et un nouveau délai TV.
- Les états initiaux reçus à la connexion ou à la reconnexion alimentent l’affichage sans déclencher un effet ni l’arrêt automatique au damier.
- Les nouveaux drapeaux conservent leur délai TV calculé depuis leur réception réelle. L’interface explique l’attente du prochain événement.

## 2.0.0-preview.15 — couleur au premier allumage

- Lorsqu’une lampe sélectionnée est éteinte, le premier drapeau fixe applique simultanément allumage, luminosité et couleur avec une transition à zéro, pour éviter le fondu depuis sa couleur mémorisée.
- Les sélections de plusieurs lampes, y compris mixtes allumées/éteintes, restent commandées en groupe. Les drapeaux suivants retrouvent la transition configurée.
- La restauration conserve l’état initial éteint ; le prochain test applique à nouveau l’allumage direct. Les pulsations natives gardent leur commande initiale sans transition.

## 2.0.0-preview.14 — correctifs de l’audit

### Fonctionnement

- Arrêt immédiat du moteur avant la fermeture des connexions web ; menus natifs disponibles pendant l’attente.
- Restauration confirmée même avec une seule lampe, récupération d’une scène temporaire supprimée, reliaison du même pont et récupération partielle des anciennes cibles.
- Une erreur d’écriture du journal n’empêche plus le passage d’un drapeau aux lampes.
- Reprise progressive du direct automatique après un démarrage sans réseau, annulable avec Stop.
- Snapshot F1 conservant l’annonce de fin de Safety Car/VSC et ordre correct des messages reçus pendant la souscription.
- Préparation Hue réduite avant le premier drapeau, budget des commandes groupées, nettoyage des groupes créés et refus explicite des animations Hue non restaurables.

### Interface et exploitation

- Sélection de zone vide/partielle/complète cohérente avec les lampes ; brouillons conservés et conflits entre onglets détectés.
- Historique de navigation, onglets au clavier, meilleur contraste, mise en page à 320 px et requêtes annulées en quittant une vue.
- Fraîcheur F1, file des événements différés, attente réseau et erreurs du journal visibles ; diagnostic technique exportable.
- Sauvegarde SQLite cohérente, migrations sauvegardées, refus des schémas futurs et restauration hors ligne.
- Proxy HTTPS explicitement approuvé, contrats API stricts, erreurs structurées et types navigateur générés.
- Journal indexé et état SSE partagé ; rotation des logs des lanceurs pendant leur fonctionnement.
- Installation Linux par versions avec bascule atomique et retour arrière si le démarrage échoue.

### Distribution et maintenance

- Actions, images, SDK et dépendances épinglés ; verrouillage NuGet propre à chaque architecture.
- Scans périodiques, mises à jour proposées automatiquement, SBOM et attestations pour les publications.
- Tests navigateur sur trois moteurs, lifecycle Docker/systemd/installateur Windows et lanceur macOS dans la CI.
- Guides de sécurité, contribution, API, hébergement distant, sauvegarde et récupération ; documentation Python clarifiée.

Les essais matériels, la perception optique des fondus, les dialogues système et les téléphones réels restent une qualification distincte des tests simulés. Voir [la couverture des correctifs](docs/audit-corrections-2026-10-08.md).
