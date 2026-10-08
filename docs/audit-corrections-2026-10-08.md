# Correctifs de l’audit du 7 octobre 2026

Ce suivi concerne le code de la preview.14. Le [rapport initial](audit-2026-10-07.md) et ses preuves décrivent l’ancienne version ; ils sont conservés comme historique. Les changements ci-dessous répondent à ses 27 constats. Les résultats de la CI et les limites matérielles restent distincts de l’implémentation.

## Couverture

| Constat | Correctif | Vérification |
| --- | --- | --- |
| A01 | Nettoyage déclenché dès l’arrêt ; SSE liés à l’arrêt du processus | HTTP : SC actif + 20 SSE, extinction du service en environ 0,3 s en simulation |
| A02 | Livraison des drapeaux avant la persistance facultative, erreur de journal visible | Échec SQLite forcé, RED reçu, reprise du journal |
| A03 | Restauration d’une seule lampe retentée et confirmée par deux lectures | HTTP accepté mais état incorrect, conservation puis récupération |
| A04 | Reprise AutoLive avec délai progressif ; Stop annule l’intention | Pont indisponible puis disponible, annulation manuelle |
| A05 | Carte de récupération, même pont, lampes disponibles, abandon archivé, ressources supprimées recréées | Tests de reliaison, scène supprimée, cible absente et archive conservée |
| A06 | Schémas validés, migration transactionnelle avec sauvegarde, CLI de restauration hors ligne | Format futur refusé sans modification, migration interrompue, retour explicite à une sauvegarde |
| A07 | Proxy par IP explicitement approuvée, schéma transmis contrôlé, Host conservé | Requête HTTPS même origine, cookie Secure ; faux proxy refusé |
| A08 | Historique du snapshot affiné uniquement pour la neutralisation courante ; buffer de souscription ordonné | SC_ENDING/VSC_ENDING et delta reçu pendant Subscribe |
| A09 | Fraîcheur, attente hors séance, erreurs de traitement et événements différés affichés | Parcours de direct simulé, API d’état |
| A10 | Groupe exact découvert pendant la préparation ; inventaire réutilisé à la capture | Premier événement mesuré à 7 échanges sur le pont factice ; ambiance capturée au dernier moment |
| A11 | Budget partagé d’une commande groupée par seconde ; Stop prioritaire | Horodatages des pulsations et annulation d’un renouvellement en attente |
| A12 | Capacités de restauration vérifiées avant toute modification ; limites expliquées | Gradients/effets compatibles conservés ; palette dynamique et effets avancés non restituables refusés |
| A13 | Identité des groupes de secours persistée avant création, nettoyage limité aux ressources reconnues | Changements de sélection et réponse de création perdue |
| A14 | Projection SQL des séances, caches bornés, sérialisation SSE partagée | Journal de 100 000 lignes, comptage/pruning/caches ; limite de 20 connexions atomique |
| A15 | Chronologie corrélée réception → commande → restauration, health/readiness, export, rotation des logs | Diagnostic HTTP sans secret et jalons d’exécution dans les tests moteur |
| A16 | Action renommée « Oublier ce pont », conséquence de non-révocation explicitée | Libellé et confirmation UI, guide sécurité |
| A17 | DTO stricts, schéma JSON, types TS générés, erreurs/codes et 429 | Formes invalides, champs inconnus, conflits, limite d’authentification et contrat généré |
| A18 | Verrous par RID, images/actions épinglées, Dependabot, scan hebdomadaire, SBOM/attestation | Gates des workflows et contrôle du runtime réellement embarqué |
| A19 | États de groupes dérivés des lampes, état partiel et résumé exact | Parcours navigateur de sélection et conservation au changement de page |
| A20 | Brouillons par formulaire, révision If-Match, refus des écrasements concurrents | Deux onglets, erreur 409, brouillon conservé puis rechargement |
| A21 | Contraste, en-tête mobile, historique, focus et clavier des onglets | Axe, 320 px, thème sombre, texte agrandi et navigation clavier en CI |
| A22 | Délais GET/mutations, annulation des lectures à la navigation, état hors connexion | Réponse d’inventaire tardive après changement de vue |
| A23 | Arrêt/import asynchrones des lanceurs ; état d’attente visible | Compilation Swift/Windows, essais lifecycle natifs en CI |
| A24 | Installation Linux avec staging, lien atomique, arrêt obligatoire, rollback et drop-ins conservés | Transaction simulée puis service utilisateur systemd réel en CI |
| A25 | Parcours navigateur, Docker exécuté, service Linux et paquets natifs, installateur Windows | Workflows étendus ; qualification manuelle matérielle explicitée ci-dessous |
| A26 | Séparation contrats/sécurité/SSE/proxy/récupération, modules UI, formatage et contrôles de version | Compilation sans avertissement, formatage, types générés, métadonnées de version et empreinte des sources web ; proposition de rechargement après mise à jour |
| A27 | Documentation unifiée, support Python limité et dépendances figées, scénario Monza corrigé | Guides de maintenance, exploitation, sécurité et changelog |

## Vérifications locales effectuées

- 207 contrôles C# réussis, dont le comptage et la rétention du journal de 100 000 événements. 1 000 lectures du cache combiné réglages/journal/séances ont pris environ 0,2 ms sur ce Mac ; cela exclut la sérialisation HTTP et le coût d’une invalidation.
- 27 contrôles API, 17 contrôles d’authentification bureau, 14 contrôles HTTP d’audit et 77 tests Python réussis.
- TypeScript, contrats générés, compilation du lanceur Windows et vérification de types du lanceur Swift réussis.
- Runtime local : .NET 10.0.12, SQLite 3.53.3, ARM64.
- Paquet Mac autonome reconstruit avec un cache NuGet vide et signature locale vérifiée. La restauration des dépendances demande explicitement les runtimes autonomes sur les six cibles ; elle reste verrouillée.
- Connexion réelle au flux officiel F1 depuis le paquet final réussie le 8 octobre. Le snapshot reçu concernait une séance terminée ; cet essai ne valide pas la réception d’un nouveau drapeau pendant une séance en cours.

## Vérifications dans GitHub Actions et le navigateur

La [qualification complète du commit 5a3a4b3](https://github.com/Oswaaaald/f1_flags_hue/actions/runs/37814623828) est réussie : vérifications macOS/Windows/Linux, six paquets natifs, image Docker multiarchitecture et inventaire de sécurité. Cette branche de qualification ne publie aucune release.

- Les 18 parcours Chromium/Firefox/WebKit passent, avec Axe, affichage à 320 px et absence de débordement des préférences avec texte agrandi à 200 %. Le test renforcé a permis de corriger une différence de largeur de grille dans Firefox/WebKit.
- Chaque service empaqueté x64/ARM64 passe les 27 contrôles API, les 17 contrôles d’authentification bureau et les 14 contrôles HTTP d’audit.
- Les deux applications Mac sont réellement lancées puis quittées pendant un effet simulé avec une connexion SSE ouverte.
- Les deux installateurs Windows sont installés, mis à jour et désinstallés. Le tray démarre son service et le profil utilisateur est conservé après désinstallation.
- Les deux paquets Linux sont installés puis mis à jour avec un vrai service utilisateur systemd ; les données et le drop-in sont conservés. Les échecs de transaction sont également testés avec un gestionnaire simulé.
- Le conteneur fonctionne effectivement sans root, avec système de fichiers en lecture seule et volume persistant ; arrêt et redémarrage conservent ses réglages.
- Le scan de secrets dans l’historique, les audits NuGet/npm/Python, le scan du conteneur et celui de l’inventaire des six paquets passent. Cela reflète les bases de vulnérabilités à la date du contrôle, sans garantie pour les vulnérabilités inconnues ou publiées ensuite.

La [publication de la preview.14](https://github.com/Oswaaaald/f1_flags_hue/actions/runs/37816676735) a également réussi tous ces contrôles et publié les attestations et fichiers téléchargeables. Un contrôle séparé de `main` a exposé une limite de temps trop stricte dans le test unitaire de Stop sous Windows. Ce test utilise désormais une horloge contrôlée : le renouvellement reste bloqué jusqu’à son annulation, et Stop doit réussir sans avancer cette horloge. La vérification ne dépend plus de la vitesse du disque ou de la charge du runner.

Contrôle manuel dans Chrome sur ordinateur : sélection de zone partielle, résumé d’une seule lampe, sauvegarde, verrouillage pendant un Safety Car simulé, puis déverrouillage dans la même vue après Stop. Les cartes de préférences et de sauvegarde sont lisibles ; aucune erreur JavaScript n’est apparue pendant ce parcours. Le mobile est couvert par les navigateurs automatisés, sans contrôle manuel sur téléphone. Tous ces essais utilisent des profils temporaires sans pont réel.

## Qualification restante et limites

Une installation propre sur une machine utilisateur, les dialogues Gatekeeper/SmartScreen/Trousseau, Windows fermeture de session, veille/réveil, Safari iOS/Chrome Android et lecteur d’écran restent des essais manuels. Le test Windows lance réellement le tray et arrête son service via son marqueur privé ; il ne clique pas le menu natif. Les simulations ne prouvent pas la simultanéité physique Zigbee. Aucun test de cette passe n’a modifié l’ambiance du pont de l’utilisateur.

Le flux officiel F1 reste un service externe sans garantie d’API publique. Tester une séance réelle après un changement de protocole. Ces correctifs et leurs tests réduisent les défauts identifiés ; ils ne justifient pas une promesse de logiciel sans bugs.
