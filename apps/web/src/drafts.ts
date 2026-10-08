type Values = Record<string, string | boolean>;
type Draft = { values: Values; baseline: Values; revision: number };
const tracked = new Set([
  "flag",
  "preferences",
  "offset",
  "selection",
  "pair",
  "clock",
  "replay",
]);
const versioned = new Set(["flag", "preferences", "offset", "selection"]);
export const formKey = (form: HTMLFormElement) =>
  form.dataset.form + ":" + (form.dataset.flag ?? "");
function fields(form: HTMLFormElement) {
  return [
    ...form.querySelectorAll<HTMLInputElement | HTMLSelectElement>(
      "input[name],select[name]",
    ),
  ];
}
function fieldKey(el: HTMLInputElement | HTMLSelectElement) {
  return el.name === "lights" ? `lights:${el.value}` : el.name;
}
function values(form: HTMLFormElement): Values {
  return Object.fromEntries(
    fields(form).map((el) => [
      fieldKey(el),
      el instanceof HTMLInputElement && el.type === "checkbox"
        ? el.checked
        : el.value,
    ]),
  );
}
function fill(form: HTMLFormElement, saved: Values) {
  for (const field of fields(form)) {
    const value = saved[fieldKey(field)];
    if (typeof value === "boolean" && field instanceof HTMLInputElement)
      field.checked = value;
    else if (typeof value === "string" && field.value !== value)
      field.value = value;
  }
  const duration = form.querySelector<HTMLInputElement>('[name="duration"]');
  if (duration)
    duration.disabled =
      form.querySelector<HTMLSelectElement>('[name="durationMode"]')?.value !==
      "fixed";
}
export class Drafts {
  private saved = new Map<string, Draft>();
  private mounted = new WeakMap<HTMLFormElement, Draft>();
  get dirty() {
    return this.saved.size > 0;
  }
  has(form: HTMLFormElement) {
    return this.saved.has(formKey(form));
  }
  revision(form: HTMLFormElement) {
    return this.mounted.get(form)?.revision;
  }
  clear(form: HTMLFormElement) {
    this.saved.delete(formKey(form));
    this.mounted.delete(form);
  }
  mount(root: ParentNode, revision: number) {
    for (const form of root.querySelectorAll<HTMLFormElement>(
      "form[data-form]",
    )) {
      if (!tracked.has(form.dataset.form!)) continue;
      const draft = this.saved.get(formKey(form));
      this.mounted.set(
        form,
        draft ?? { values: values(form), baseline: values(form), revision },
      );
      if (draft) fill(form, draft.values);
    }
    this.warnings(root, revision);
  }
  capture(form: HTMLFormElement) {
    if (!tracked.has(form.dataset.form!)) return;
    const previous = this.mounted.get(form);
    if (!previous) return;
    const draft = { ...previous, values: values(form) };
    if (JSON.stringify(draft.values) === JSON.stringify(draft.baseline))
      this.saved.delete(formKey(form));
    else this.saved.set(formKey(form), draft);
  }
  reconcile(form: HTMLFormElement, fresh: HTMLFormElement, revision: number) {
    if (this.has(form)) return;
    fill(form, values(fresh));
    this.mounted.set(form, {
      values: values(form),
      baseline: values(form),
      revision,
    });
  }
  warnings(root: ParentNode, revision: number) {
    for (const form of root.querySelectorAll<HTMLFormElement>(
      "form[data-form]",
    )) {
      const draft = this.saved.get(formKey(form));
      let notice = form.querySelector<HTMLElement>("[data-draft-notice]");
      if (!draft) {
        notice?.remove();
        continue;
      }
      if (!notice) {
        notice = document.createElement("div");
        notice.dataset.draftNotice = "";
        notice.className = "notice section-gap";
        notice.setAttribute("role", "status");
        form.append(notice);
      }
      const conflicted =
        versioned.has(form.dataset.form!) && draft.revision !== revision;
      notice.replaceChildren(
        document.createTextNode(
          conflicted
            ? "Modifié dans un autre onglet ou une autre action. Ton brouillon est conservé. "
            : "Modifications non enregistrées. ",
        ),
      );
      const button = document.createElement("button");
      button.type = "button";
      button.className = "text-button";
      button.dataset.action = "discard-draft";
      button.textContent = "Recharger les valeurs enregistrées";
      notice.append(button);
    }
  }
}
