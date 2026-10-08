import type { State, Flag, RaceEvent, Scenario } from "../types";
import { flags, labels, descriptions } from "../flags";
import { esc, dot, button, heading } from "../ui";
export function flagSettings(state: State) {
  return (
    heading(
      "Garde les événements qui comptent.",
      "Les changements s’appliquent aux prochains drapeaux joués, même pendant le direct.",
    ) +
    `<div class="flag-grid">${flags
      .map((f) => {
        const effect = state.settings.effects[f];
        return `<form class="card flag-card" data-form="flag" data-flag="${f}"><div class="card-heading"><h2 class="flag-title">${dot(f)}${labels[f]}</h2><input class="switch" type="checkbox" name="enabled" aria-label="Activer ${labels[f]}"${effect.enabled ? " checked" : ""}></div><p class="help">${descriptions[f]}</p><label class="field">Durée<select name="durationMode"><option value="until"${effect.durationSeconds === null ? " selected" : ""}>Jusqu’au prochain événement</option><option value="fixed"${effect.durationSeconds !== null ? " selected" : ""}>Durée fixe</option></select></label><label class="field compact">Secondes<input name="duration" type="number" min="0.1" max="3600" step="0.1" value="${effect.durationSeconds ?? 10}"${effect.durationSeconds === null ? " disabled" : ""}></label><button class="button">Enregistrer</button></form>`;
      })
      .join(
        "",
      )}</div><p class="help section-gap">Une durée fixe restaure ensuite l’état initial des lampes. Un drapeau désactivé termine l’effet précédent sans afficher sa couleur.</p>`
  );
}
function toggle(
  name: string,
  title: string,
  description: string,
  value: boolean,
) {
  return `<label class="toggle"><span><strong>${title}</strong><small>${description}</small></span><input class="switch" name="${name}" type="checkbox"${value ? " checked" : ""}></label>`;
}
export function preferences(state: State) {
  const s = state.settings;
  return (
    heading(
      "À ton rythme.",
      "L’ambiance, le comportement du service et le confort de lecture.",
    ) +
    `<div class="grid"><form class="card" data-form="preferences"><h2>Ambiance et fonctionnement</h2><div class="grid"><label class="field">Luminosité (%)<input name="brightness" type="number" min="0.4" max="100" step="0.1" value="${Math.round((s.brightness / 254) * 1000) / 10}" required></label><label class="field">Transition en secondes<input name="transitionSeconds" type="number" min="0" max="10" step="0.1" value="${s.transitionSeconds}" required><small>Durée du changement de couleur. Le rythme des pulsations natives est fixé par les lampes.</small></label></div><label class="field">Limite des clignotements prolongés (secondes)<input name="alertWatchdogSeconds" type="number" min="30" max="3600" value="${s.alertWatchdogSeconds}" required><small>Utilisée quand le clignotement attend le prochain événement.</small></label><div class="section-gap">${toggle("restoreOnExit", "Restaurer les lampes à l’arrêt", "Retrouver l’ambiance qui précédait le direct ou le test.", s.restoreOnExit)}${toggle("exitOnChequered", "Arrêter le direct au damier", "Après le décalage TV et la durée du dernier effet.", s.exitOnChequered)}${toggle("autoLive", "Activer le direct au lancement", "Le service attend ensuite les prochains événements F1.", s.autoLive)}</div><div class="actions"><button class="button primary">Enregistrer les préférences</button></div></form><div class="stack"><section class="card"><h2>Apparence</h2><label class="field">Thème<select id="theme"><option value="light">Clair</option><option value="dark">Sombre</option><option value="auto">Automatique</option></select><small>Ce choix reste propre à ce navigateur.</small></label></section><section class="card"><h2>Sauvegarde et diagnostic</h2><p>La sauvegarde est créée dans le dossier de données du service. Le diagnostic ne contient pas la clé Hue ni le mot de passe.</p><div class="actions">${button("Créer une sauvegarde", "backup")}${button("Exporter le diagnostic", "export-diagnostics")}</div></section><section class="card"><h2>Toujours prêt</h2><p>L’application de bureau peut démarrer à l’ouverture de ta session. Sur un serveur domestique, le service redémarre automatiquement.</p><p class="help section-gap">Le menu de l’application donne accès à l’interface, au démarrage automatique et à l’arrêt du service. Fermer cet onglet laisse les lampes synchronisées.</p></section></div></div>`
  );
}
