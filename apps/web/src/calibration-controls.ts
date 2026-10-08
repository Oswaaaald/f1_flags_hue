import type { State, Flag } from "./types";
import { $ } from "./ui";
import { labels } from "./flags";

export function updateFreshness(state: State, serverDelta: number) {
  const el = document.querySelector("#feed-freshness");
  if (!el || !state) return;
  const age = state.feed.lastDataAt
    ? Math.max(
        0,
        Math.floor(
          (Date.now() + serverDelta - Date.parse(state.feed.lastDataAt)) / 1000,
        ),
      )
    : null;
  el.textContent =
    age === null
      ? "Aucune donnée F1 reçue."
      : `Dernière donnée reçue il y a ${age} s.${state.feed.sessionStatus === "Started" && age > 90 ? " Le flux est silencieux pendant la séance : vérifie sa connexion." : state.feed.sessionStatus !== "Started" ? " Le silence entre les séances est normal." : ""}`;
  const queue = document.querySelector("#pending-events");
  if (queue)
    queue.textContent = state.runner.nextEffectAt
      ? `Prochain effet dans ${Math.max(0, (Date.parse(state.runner.nextEffectAt) - Date.now() - serverDelta) / 1000).toFixed(1)} s · ${state.runner.queuedEvents} autre(s) événement(s) en attente.`
      : "Aucun événement en attente de l’offset TV.";
}
export function updateCalibration(
  state: State,
  pending: WeakSet<HTMLElement>,
  {
    offsetDirty,
    offsetSaving,
    serverDelta,
  }: { offsetDirty: boolean; offsetSaving: boolean; serverDelta: number },
) {
  const offsetInput =
    document.querySelector<HTMLInputElement>('[name="offset"]');
  if (offsetInput) {
    if (!offsetDirty && !offsetSaving)
      offsetInput.value = String(state.settings.offsetSeconds);
    offsetInput.disabled = offsetSaving;
    document
      .querySelectorAll<HTMLButtonElement>('[data-action^="adjust-offset:"]')
      .forEach((b) => {
        const delta = Number(b.dataset.action!.split(":")[1]);
        b.disabled =
          offsetSaving ||
          offsetDirty ||
          (delta < 0 && state.settings.offsetSeconds <= 0) ||
          (delta > 0 && state.settings.offsetSeconds >= 3600);
      });
    const save = document.querySelector<HTMLButtonElement>(
      '[data-form="offset"] button[type="submit"]',
    );
    if (save) save.disabled = offsetSaving;
    const use = document.querySelector<HTMLButtonElement>(
      '[data-action="use-offset"]',
    );
    if (use)
      use.disabled = offsetSaving || state.calibration.proposedOffset === null;
    $("#offset-feedback").textContent = offsetSaving
      ? "Enregistrement…"
      : offsetDirty
        ? "Valeur modifiée : enregistre-la pour utiliser les boutons d’ajustement."
        : "Chaque clic est enregistré, même pendant le direct. Limites : 0 à 3 600 secondes.";
  }
  const status = document.querySelector("#calibration-status");
  if (status) {
    const c = state.calibration;
    status.textContent = c.waiting
      ? "En attente du prochain repère sur le flux F1…"
      : c.reference
        ? `Repère reçu : ${c.mode === "start" ? "départ de la course" : c.mode === "lap" ? "tour " + c.reference.value : (labels[c.reference.value as Flag] ?? c.reference.value)}. Clique quand tu le vois à la TV.`
        : "Choisis un repère pour démarrer.";
    const seen = document.querySelector<HTMLButtonElement>(
      '[data-action="seen"]',
    )!;
    seen.disabled =
      !c.reference || c.proposedOffset !== null || pending.has(seen);
    $("#offset-measurement").hidden = c.proposedOffset === null;
    $("#proposed-offset").textContent =
      c.proposedOffset !== null ? `${c.proposedOffset} secondes` : "";
  }
  const clock = document.querySelector("#session-clock");
  if (clock) {
    const c = state.feed.clock;
    let seconds: number | null = null;
    if (
      c?.remaining &&
      c.utc &&
      c.extrapolating &&
      state.feed.connected &&
      state.feed.sessionStatus === "Started"
    ) {
      const parts = c.remaining.split(":").map(Number);
      seconds =
        parts.reduce((a, b) => a * 60 + b, 0) -
        (Date.now() + serverDelta - Date.parse(c.utc)) / 1000;
      if (!Number.isFinite(seconds) || seconds < 0) seconds = null;
    }
    clock.textContent =
      seconds === null
        ? "—:—"
        : `${Math.floor(seconds / 60)
            .toString()
            .padStart(2, "0")}:${Math.floor(seconds % 60)
            .toString()
            .padStart(2, "0")}`;
    $("#clock-note").textContent =
      seconds === null
        ? "Le chrono est arrêté ou indisponible hors séance."
        : "Temps restant estimé à partir du chrono F1.";
  }
}
