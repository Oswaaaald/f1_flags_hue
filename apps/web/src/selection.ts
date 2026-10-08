import type { Inventory } from "./types";

export function syncSelection(inventory: Inventory | null) {
  if (!inventory) return;
  const selected = new Set(
    [
      ...document.querySelectorAll<HTMLInputElement>('[name="lights"]:checked'),
    ].map((l) => l.value),
  );
  for (const input of document.querySelectorAll<HTMLInputElement>(
    "[data-group]",
  )) {
    const ids =
      inventory.groups.find((group) => group.id === input.dataset.group)
        ?.lightIds ?? [];
    const count = ids.filter((id) => selected.has(id)).length;
    input.checked = ids.length > 0 && count === ids.length;
    input.indeterminate = count > 0 && count < ids.length;
  }
  const summary = document.querySelector("#selection-summary");
  if (summary)
    summary.textContent = selected.size
      ? `${selected.size} lampe${selected.size > 1 ? "s" : ""} : ${inventory.lights
          .filter((l) => selected.has(l.id))
          .map((l) => l.name)
          .join(", ")}`
      : "Aucune lampe sélectionnée.";
}
