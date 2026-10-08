import type { State, Flag, RaceEvent, Scenario } from "../types";
import { flags, labels, descriptions } from "../flags";
import { esc, dot, button, heading } from "../ui";
export function hue(state: State) {
  return (
    heading(
      "Tes lampes, ton ambiance.",
      "Lie ton pont, puis choisis les lampes qui suivent la course.",
    ) +
    `<div class="grid" data-hue-controls><section class="card"><div class="card-heading"><h2><span class="number">1</span> Le pont Hue</h2><span class="badge ${state.hue.linked ? "good" : ""}">${state.hue.linked ? "Lié" : "À configurer"}</span></div><p>${state.hue.linked ? esc(state.hue.name) : "Le pont et cet appareil doivent être sur le même réseau local."}</p><form data-form="pair"><label class="field">Adresse du pont<input name="ip" placeholder="192.168.1.20" value="${esc(state.hue.ip === "simulation" ? "192.168.1.10" : state.hue.ip)}" required></label><div class="actions">${button("Rechercher un pont", "discover", false)}<button class="button primary" >Lier le pont</button></div><div id="discovered" class="section-gap"></div><div class="notice">Appuie sur le bouton physique du pont, puis sur « Lier le pont » dans les 30 secondes.</div></form>${state.hue.linked ? `<div class="actions">${button("Importer mon ancienne sélection", "import-selection", false)}${button("Oublier ce pont", "unpair", false)}</div>` : ""}</section><section class="card"><div class="card-heading"><h2><span class="number">2</span> Les lampes de la course</h2>${button("Actualiser", "inventory", false)}</div><div id="inventory"><div class="empty">${state.hue.linked ? "Chargement des lampes…" : "Lie le pont pour retrouver ses zones et ses lampes."}</div></div></section></div><p class="help section-gap">La restauration conserve les couleurs fixes, la température, la luminosité et les dégradés. Arrête les animations de scène et les effets Hue avancés avant de lancer le direct : leur progression ne peut pas être restaurée fidèlement.</p>`
  );
}
