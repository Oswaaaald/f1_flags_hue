# Architecture du direct

```text
Flux Formula 1 (SignalR)
        │
        ▼
F1SourceFormula1Live ──► LiveEvent ──► LiveTimingService ──► calibrations / état web
                                           │
                                           ├──► EventJournal (SQLite local)
                                           │
                                           └──► EffectRules + ordonnanceur TV ──► LightEngine ──► Hue
EventJournal ──► replay_events ────────────────────────────────────────┘
Archive F1 ──► historical_replay ──► replay_events ─────────────────────┘
```

## Responsabilités

- `f1_sources.py` est le seul adaptateur du flux Formula 1. Il gère SignalR, les snapshots, les mises à jour et la reconnexion. Il publie des `LiveEvent` sans connaître Hue ou Flask.
- `live_events.py` définit l'événement commun : type, valeur, séance, heure de réception, heure F1 éventuelle et métadonnées limitées.
- `live_service.py` ouvre une connexion par processus, tient l'état courant de la séance et distribue les événements aux consommateurs. Le web garde ce service actif même lorsque le mode Hue est arrêté, afin que les calibrations et l'état restent disponibles.
- `event_journal.py` enregistre localement les événements dérivés utiles au diagnostic et au replay. Les données brutes du flux F1 ne sont pas copiées dans le dépôt. Le journal SQLite est dans `.data/`, ignoré par Git.
- `historical_replay.py` propose un petit catalogue fixe de séances passées et lit leurs messages de contrôle directement dans les archives de `livetiming.formula1.com`. Les archives sont téléchargées à la demande et les drapeaux dérivés sont gardés en mémoire pour la durée du processus web.
- `effects.py` associe les drapeaux aux patterns configurés et applique l'offset TV à partir de l'heure de réception de chaque événement. L'effet dépend d'une interface `play(pattern_name)`, pas directement de l'API Hue.
- `hue_targets.py` prépare les lampes sélectionnées. Un groupe Hue synchronisé est utilisé si possible ; sinon seules les lampes sélectionnées sont ciblées.
- `web/server.py` et `f1_hue.py` pilotent le cycle de vie de Hue, sans décoder eux-mêmes le protocole F1.

## Limites actuelles

Le flux appartient à Formula 1 et n'est pas une API publique documentée. Son schéma ou son accès peuvent changer. Une connexion web et une commande CLI lancées simultanément constituent deux processus, donc deux connexions ; le partage vaut à l'intérieur de chaque processus. Le replay reproduit les écarts entre drapeaux à la vitesse choisie, tandis que les durées des patterns Hue restent celles de `config.yml`.
