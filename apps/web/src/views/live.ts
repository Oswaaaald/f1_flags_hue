import type { State, Flag, RaceEvent, Scenario } from "../types";
import { flags, labels, descriptions } from "../flags";
import { esc, dot, button, heading } from "../ui";
export function live(state: State, tab: string) {
  return (
    heading(
      "La course, dans ton salon.",
      "Les événements de la piste, au rythme de ta diffusion TV.",
    ) +
    `<div class="tabs" role="tablist" aria-label="Vues du direct">${[
      ["overview", "Vue d’ensemble"],
      ["calibration", "Calibration TV"],
      ["journal", "Journal des drapeaux"],
    ]
      .map(
        ([id, name]) =>
          `<button class="tab" role="tab" data-tab="${id}" aria-selected="${tab === id}">${name}</button>`,
      )
      .join("")}</div>` +
    (tab === "calibration"
      ? calibrationView(state)
      : tab === "journal"
        ? journalView(state)
        : `<div class="stack"><section class="card live-hero"><div class="split"><div><div class="eyebrow">Sur la piste</div><h2 data-live="session">En attente d’une séance</h2><p data-live="session-description"></p></div><div class="hero-symbol" aria-hidden="true">⚑</div></div><div class="stats"><div class="stat"><div class="stat-label">Effet dans le salon</div><div class="stat-value" data-live="effect">—</div></div><div class="stat"><div class="stat-label">Décalage TV</div><div class="stat-value" data-live="offset">—</div></div><div class="stat"><div class="stat-label">Lampes sélectionnées</div><div class="stat-value" data-live="targets">—</div></div></div><div class="actions">${button("Activer le direct", "start", true)}${button("Régler la calibration", "calibration")}</div></section><div class="grid"><section class="card"><div class="card-heading"><h2>Tout est prêt ?</h2><span class="badge" data-live="mode"></span></div><div class="toggle"><span><strong>Ton pont Hue</strong><small data-live="bridge"></small></span><a class="button" href="#hue" data-page="hue">Configurer</a></div><div class="toggle"><span><strong>Le rythme de ta TV</strong><small>Le décalage s’applique à chaque nouvel événement.</small></span><button class="button" data-action="calibration">Ajuster</button></div><p class="help section-gap">Le service continue à fonctionner quand tu fermes cette page. Le bouton Stop reste accessible en haut de l’écran.</p></section><section class="card"><div class="card-heading"><h2>Derniers drapeaux</h2><button class="text-button" data-action="journal">Tout voir</button></div><div id="recent-flags"></div></section></div></div>`)
  );
}
function calibrationView(state: State) {
  return `<div class="grid"><section class="card"><div class="eyebrow">Essais & qualifications</div><h2 class="section-gap">Compare le temps restant</h2><p>Lis le chrono sur ta TV et saisis-le ci-dessous.</p><div class="clock" id="session-clock">—:—</div><div class="help" id="clock-note">Chrono reçu de la séance.</div><form data-form="clock"><label class="field">Temps restant sur ta TV<input name="remaining" placeholder="12:34" inputmode="numeric" pattern="[0-9]{1,3}:[0-5][0-9](:[0-5][0-9])?" required></label><div class="actions"><button class="button primary">Comparer maintenant</button></div></form></section><section class="card"><div class="eyebrow">Course & événements</div><h2 class="section-gap">Clique quand tu le vois à la TV</h2><p>Arme une mesure avant l’événement, puis clique au moment où il apparaît à l’écran.</p><div class="method-list">${button("Départ de la course", "arm:start")}${button("Prochain drapeau", "arm:flag")}${button("Prochain tour", "arm:lap")}</div><div class="notice" id="calibration-status">Choisis un repère pour démarrer.</div><div class="actions">${button("Je le vois maintenant", "seen", true, true)}${button("Annuler", "cancel-calibration")}</div><p class="help section-gap">Au départ, clique quand les voitures s’élancent. Le repère fourni par la F1 correspond au statut de départ et peut être légèrement différent de l’extinction des feux.</p></section><section class="card wide"><div class="row spread"><div><h2>Décalage de la diffusion</h2><p class="help">Les événements attendent ce délai avant d’arriver aux lampes.</p></div><span class="badge" data-live="offset"></span></div><form data-form="offset"><div class="grid"><label class="field">Décalage en secondes<input name="offset" type="number" min="0" max="3600" step="0.1" value="${state.settings.offsetSeconds}" required></label><div class="field"><span>Mesure proposée</span><strong id="proposed-offset">Aucune mesure</strong><button type="button" class="text-button" data-action="use-offset">Utiliser cette mesure</button></div></div><div class="actions"><button class="button primary">Enregistrer le décalage</button></div></form></section></div>`;
}
function journalView(state: State) {
  return `<section class="card"><div class="card-heading"><div><h2>Journal des drapeaux</h2><p class="help">Heure de réception locale et heure de la source F1, lorsqu’elle est fournie.</p></div>${button("Exporter JSON", "export-journal")}</div><label class="field">Séance<select id="journal-session"><option value="">Événements récents</option>${state.sessions.map((s) => `<option value="${esc(s.key)}">${esc(s.name)} · ${new Date(s.at).toLocaleDateString("fr-BE")}</option>`).join("")}</select></label><div class="section-gap" id="journal-list"></div></section>`;
}
export function logs(events: RaceEvent[], limit = 60) {
  const rows = events.filter((e) => e.kind === "flag").slice(0, limit);
  return rows.length
    ? rows
        .map(
          (e) =>
            `<div class="log-entry"><time datetime="${esc(e.receivedAt)}" title="${esc(new Date(e.receivedAt).toLocaleString("fr-BE"))}">${new Date(e.receivedAt).toLocaleTimeString("fr-BE")}</time><span class="flag-title">${flags.includes(e.value as Flag) ? dot(e.value as Flag) : ""}${esc(labels[e.value as Flag] ?? e.value)}</span><small title="Horodatage F1">${e.sourceUtc ? new Date(e.sourceUtc).toLocaleTimeString("fr-BE") : "—"}</small></div>`,
        )
        .join("")
    : '<div class="empty"><strong>La piste est calme.</strong>Les prochains drapeaux reçus apparaîtront ici avec leur heure.</div>';
}
