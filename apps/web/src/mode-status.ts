import type { State } from "./types";

export function canStop(state: State) {
  return (
    state.runner.running ||
    state.runner.cleanupPending ||
    !!state.recovery?.pending ||
    state.initializing ||
    state.startup.phase === "waiting"
  );
}

export function modeStatus(
  state: State,
  connected: boolean,
  stopping: boolean,
  starting: boolean,
) {
  if (!connected)
    return {
      label: "État inconnu",
      tone: "bad",
      detail:
        "Connexion à l’application perdue. L’état des lampes ne peut pas être confirmé.",
    };
  if (stopping || state.runner.stopping)
    return {
      label: "Arrêt en cours…",
      tone: "",
      detail: "L’application termine les effets et libère les lampes.",
    };
  if (
    state.runner.cleanupPending ||
    (state.recovery?.pending && !state.runner.running)
  )
    return {
      label: "Arrêt incomplet",
      tone: "bad",
      detail:
        "Le direct est inactif. Réessaie l’arrêt pour terminer la récupération des lampes.",
    };
  if (state.initializing || starting)
    return {
      label: "Préparation en cours…",
      tone: "",
      detail: "L’application vérifie le pont et prépare les lampes.",
    };
  if (state.runner.running) {
    if (state.runner.mode === "live")
      return state.feed.connected
        ? {
            label: "Direct actif",
            tone: "good",
            detail:
              "Tes lampes suivent les nouveaux drapeaux F1 avec ton décalage TV.",
          }
        : {
            label: "Direct en attente",
            tone: "",
            detail:
              "Le direct est activé. En attente de la reconnexion au flux F1 pour recevoir les prochains drapeaux.",
          };
    return {
      label:
        state.runner.mode === "replay" ? "Replay en cours" : "Test en cours",
      tone: "good",
      detail:
        "Le direct est inactif. Arrête ce mode pour activer la synchronisation F1.",
    };
  }
  if (state.startup.phase === "waiting")
    return {
      label: "Direct en attente",
      tone: "",
      detail:
        "Le démarrage automatique attend le pont Hue. Stop annule cette attente.",
    };
  return {
    label: "Direct inactif",
    tone: "",
    detail: !state.hue.linked
      ? "Lie ton pont dans Hue, puis active le direct pour synchroniser tes lampes."
      : state.settings.lightIds.length === 0
        ? "Choisis tes lampes dans Hue, puis active le direct pour suivre la F1."
        : "Tes lampes ne suivent pas la F1. Clique sur « Activer le direct » pour commencer.",
  };
}
