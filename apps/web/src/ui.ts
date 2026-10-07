import type { Flag } from "./types";
export const $ = <T extends HTMLElement = HTMLElement>(selector: string): T => {
  const e = document.querySelector<T>(selector);
  if (!e) throw new Error(`Élément absent : ${selector}`);
  return e;
};
export const esc = (s: unknown) =>
  String(s ?? "").replace(
    /[&<>"']/g,
    (c) =>
      ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[
        c
      ]!,
  );
export const dot = (f: Flag) =>
  `<span class="flag-dot" data-flag="${f}" aria-hidden="true"></span>`;
export const button = (
  label: string,
  action: string,
  primary = false,
  disabled = false,
) =>
  `<button type="button" class="button${primary ? " primary" : ""}" data-action="${action}"${disabled ? " disabled" : ""}>${label}</button>`;
export function heading(title: string, subtitle: string) {
  return `<div class="page-heading"><div><h1>${title}</h1><p>${subtitle}</p></div></div>`;
}
