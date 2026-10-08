# Contrat HTTP

API locale à utilisateur unique, préfixe `/api`. Tous les chemins ci-dessous exigent une session sauf les points d’entrée d’authentification. Envoyer `Content-Type: application/json` pour un corps et `X-F1Hue-Request: 1` pour toute modification. Le navigateur utilise le cookie HttpOnly, les protections Origin/SameSite restent actives. Les tickets du mode bureau sont obtenus uniquement par le lanceur local.

`GET /api/schema` retourne les schémas JSON des réponses principales et DTO d’entrée. `make contracts` génère les types TypeScript depuis ces mêmes types C#. Cette référence est l’index des routes ; les schémas ne sont pas un document OpenAPI.

| Méthode / route | Entrée / résultat |
| --- | --- |
| GET `/health`, `/ready` | Santé publique minimale ; readiness 503 pendant initialisation, attente de reprise ou récupération incomplète |
| GET `/auth/status` | Mode, configuration requise, session valide |
| POST `/auth/setup`, `/auth/login` | `SetupRequest` / `LoginRequest`, mode serveur seulement |
| POST `/auth/desktop/ticket` | Preuve privée `X-F1Hue-Launcher`, mode bureau seulement |
| POST `/auth/desktop/login` | `DesktopLoginRequest`, ticket à usage unique |
| POST `/auth/logout` | Révoque la session courante |
| GET `/state`, `/events` | `State` / SSE de cet état, 20 connexions SSE maximum, heartbeat 15 s |
| GET `/settings` | `Settings`, ETag de révision |
| PATCH `/settings` | Objet partiel validé : offsetSeconds, brightness, transitionSeconds, restoreOnExit, alertWatchdogSeconds, exitOnChequered, autoLive, effects ; les identifiants Hue ne sont pas modifiables ici |
| POST `/live/start`, `/stop`, `/test/sequence` | Commandes sans corps ; Stop préempte les commandes en attente |
| POST `/test/preview` | `PreviewRequest` : nom exact de drapeau, même s’il est désactivé |
| GET `/replay/scenarios`, POST `/replay/start` | Catalogue / `ReplayRequest`, kind `archive` ou `local`, speed de 0,25 à 100 |
| GET `/journal?session=…` | Jusqu’à 1 000 événements ; session facultative |
| POST `/calibration/arm`, `/calibration/clock` | `CalibrationArmRequest` / `CalibrationClockRequest` |
| POST `/calibration/seen`, `/calibration/cancel` | Mesurer / annuler la mesure |
| POST `/calibration/adjust` | `OffsetAdjustmentRequest` ; delta relatif atomique |
| POST `/hue/discover`, `/hue/pair` | Adresses candidates / `PairRequest` |
| GET `/hue/inventory`, `/hue/diagnostics` | Inventaire / détails des seules cibles sélectionnées |
| POST `/hue/select`, `/hue/scene` | `SelectionRequest` / `SceneRecallRequest` ; mode arrêté exigé |
| POST `/hue/import-selection`, `/hue/unpair` | Migration / oubli local du pont, sans révocation sur le pont |
| POST `/recovery/pair` | `PairRequest`, identité de l’ancien pont exigée |
| POST `/recovery/available` | Restauration des anciennes cibles encore présentes ; conserve les absentes |
| POST `/recovery/abandon` | `RecoveryAbandonRequest`, confirmation `ABANDONNER` ; archive avant abandon |
| GET `/diagnostics`, POST `/backup` | Chronologie technique expurgée / nom du fichier créé dans le profil |
| POST `/simulation/event` | `SimulationRequest` ; disponible uniquement avec `--simulate` |

Les formulaires envoient `If-Match: "REVISION"` pour les réglages et la sélection. Une révision périmée produit 409 `settings_conflict`, sans écrasement. Un client volontairement sans If-Match demande une modification sans contrôle de révision ; les ajustements relatifs d’offset restent atomiques.

Erreurs structurées : `{ "code": "…", "error": "message compréhensible", "traceId": "…" }`. Les formes JSON invalides, champs inconnus et valeurs interdites produisent 400 ; accès refusé 401/403, conflit 409, corps trop grand 413, limitation 429 avec `Retry-After`. Une erreur interne produit 500 sans pile ni secrets dans la réponse. Une réponse perdue après une mutation n’annule pas nécessairement son exécution : relire l’état, puis utiliser Stop si nécessaire.
