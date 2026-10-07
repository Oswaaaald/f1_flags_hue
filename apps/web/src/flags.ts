import type { Flag } from "./types";
export const flags: Flag[] = [
  "GREEN",
  "YELLOW",
  "RED",
  "SC",
  "SC_ENDING",
  "VSC",
  "VSC_ENDING",
  "BLUE",
  "CHEQUERED",
];
export const labels: Record<Flag, string> = {
  GREEN: "Drapeau vert",
  YELLOW: "Drapeau jaune",
  RED: "Drapeau rouge",
  SC: "Safety car",
  SC_ENDING: "Fin de safety car",
  VSC: "Safety car virtuelle",
  VSC_ENDING: "Fin de VSC",
  BLUE: "Drapeau bleu",
  CHEQUERED: "Drapeau à damier",
};
export const descriptions: Record<Flag, string> = {
  GREEN: "La piste est libre.",
  YELLOW: "Danger sur la piste.",
  RED: "La séance est interrompue.",
  SC: "La voiture de sécurité est déployée.",
  SC_ENDING: "La course va reprendre.",
  VSC: "La vitesse est limitée sur la piste.",
  VSC_ENDING: "La neutralisation va se terminer.",
  BLUE: "Un pilote plus rapide approche.",
  CHEQUERED: "La séance est terminée.",
};
