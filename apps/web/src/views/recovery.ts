import type { State } from "../types";
import { button, esc } from "../ui";

export function recovery(state: State) {
  const recovery = state.recovery;
  if (
    !recovery?.pending ||
    state.runner.running ||
    state.runner.stopping ||
    state.initializing
  )
    return "";
  return `<section class="card recovery" aria-labelledby="recovery-title"><h2 id="recovery-title">Récupérer ton ambiance</h2>
    <p>L’état initial est sauvegardé. ${recovery.lightIds.length} lampe(s) restent à vérifier.</p>
    <details><summary>Lampes et ressources concernées</summary><ul>${recovery.lightIds.map((id) => `<li><code>${esc(id)}</code></li>`).join("")}</ul><p>${recovery.resources.map(esc).join(", ")}</p><p>Pont : ${esc(recovery.bridgeId ?? "identité ancienne indisponible")}</p></details>
    <div class="actions">${button("Réessayer l’arrêt", "stop", true)}${button("Restaurer les lampes disponibles", "recover-available")}</div>
    <p class="help">Si une lampe a été retirée, les autres peuvent être restaurées. Aucune nouvelle lampe ne sera ajoutée à la sélection.</p>
    <form data-form="recovery-pair"><label class="field">Relier le même pont<input name="ip" value="${esc(state.hue.ip)}" required></label><p class="help">Appuie d’abord sur son bouton physique. L’identité du pont sera vérifiée avant de remplacer la liaison.</p><div class="actions"><button class="button">Relier pour récupérer</button></div></form>
    <details class="section-gap"><summary>La récupération est définitivement impossible</summary><p>Abandonner arrête les tentatives automatiques. Les lampes peuvent garder leur couleur actuelle ; une pulsation native peut durer encore 15 secondes. Des ressources temporaires peuvent rester sur le pont. Une copie de la sauvegarde sera conservée dans le dossier recovery.</p>
    <form data-form="recovery-abandon"><label class="field">Tape ABANDONNER pour confirmer<input name="confirmation" pattern="ABANDONNER" autocomplete="off" required></label><button class="button danger">Archiver et abandonner la récupération</button></form></details></section>`;
}
