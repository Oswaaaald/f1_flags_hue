import type { State, Flag, RaceEvent, Scenario } from "../types";
import { flags, labels, descriptions } from "../flags";
import { esc, dot, button, heading } from "../ui";
export function scenarioOptions(state: State, scenarios: Scenario[]) {
  return `<optgroup label="Archives officielles F1">${scenarios.map((s) => `<option value="archive:${esc(s.id)}">${esc(s.name)}</option>`).join("")}</optgroup>${state.sessions.length ? `<optgroup label="Séances enregistrées chez toi">${state.sessions.map((s) => `<option value="local:${esc(s.key)}">${esc(s.name)} · ${s.flags} drapeaux</option>`).join("")}</optgroup>` : ""}`;
}
export function tests(state: State, scenarios: Scenario[]) {
  return (
    heading(
      "Avant le prochain départ.",
      "Vérifie une couleur, joue une séquence ou revis une séance.",
    ) +
    `<div class="grid"><section class="card wide"><div class="card-heading"><div><h2>Aperçu d’un drapeau</h2><p class="help">L’aperçu individuel fonctionne même si ce drapeau est désactivé dans le direct.</p></div></div><div class="preview-buttons">${flags.map((f) => `<button class="button" data-action="preview:${f}"${state.runner.running ? " disabled" : ""}>${dot(f)}${labels[f]}</button>`).join("")}</div></section><section class="card"><h2>Séquence automatique</h2><p>Passe d’un événement activé au suivant, toutes les quatre secondes au maximum.</p><div class="actions">${button("Lancer la séquence", "sequence", true, state.runner.running)}</div></section><section class="card"><h2>Replay d’une séance</h2><p>Retrouve les mêmes règles et durées que pendant le direct.</p><form data-form="replay"><label class="field">Séance<select id="replay-scenario" name="scenario">${scenarios.length ? scenarioOptions(state, scenarios) : "<option>Chargement des archives…</option>"}</select></label><label class="field">Vitesse de lecture<select name="speed"><option value="1">Temps réel · 1 min = 1 min</option><option value="5">Accéléré ×5 · 1 min = 12 s</option><option value="10" selected>Accéléré ×10 · 1 min = 6 s</option><option value="30">Accéléré ×30 · 1 min = 2 s</option><option value="60">Accéléré ×60 · 1 min = 1 s</option></select></label><div class="actions"><button class="button primary"${state.runner.running ? " disabled" : ""}>Lancer le replay</button></div></form>${state.sessions.length ? "" : '<p class="help section-gap">Tes propres séances apparaîtront ici après réception de leurs premiers drapeaux.</p>'}</section></div>`
  );
}
