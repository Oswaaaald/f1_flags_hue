import type { State, Flag, Inventory, RaceEvent, Scenario } from "./types";
import { flags, labels } from "./flags";
import { $, esc, dot, button } from "./ui";
import { api, viewApi, changeView } from "./api";
import { Drafts } from "./drafts";
import { icons, navNames } from "./navigation";
import {
  updateFreshness as renderFreshness,
  updateCalibration as renderCalibration,
} from "./calibration-controls";
import { syncSelection } from "./selection";
import { recovery } from "./views/recovery";
import { live, logs } from "./views/live";
import { hue } from "./views/hue";
import { tests, scenarioOptions } from "./views/tests";
import { flagSettings, preferences } from "./views/settings";

declare const __UI_BUILD__: string;
const drafts = new Drafts();
let formRevision = -1;
let serviceConnected = true;
let recoveryKey = "";
let stopRequested = false;
let offsetDirty = false;
let offsetSaving = false;
const pending = new WeakSet<HTMLElement>();
let state: State;
let inventory: Inventory | null = null;
let scenarios: Scenario[] = [];
let page = "live";
let tab = "overview";
let stream: EventSource | null = null;
let toastTimer: number | undefined;
let serverDelta = 0;
const launchParameters = new URLSearchParams(location.hash.replace(/^#/, ""));
let setupCode = launchParameters.get("setup") ?? "";
let desktopTicket = launchParameters.get("desktop") ?? "";
if (setupCode || desktopTicket)
  history.replaceState(null, "", location.pathname);
const media = matchMedia("(prefers-color-scheme: dark)");
function theme() {
  const saved = localStorage.getItem("f1hue.theme") ?? "light";
  document.documentElement.dataset.theme =
    saved === "auto" ? (media.matches ? "dark" : "light") : saved;
}
media.addEventListener("change", theme);
theme();
function toast(message: string, error = false) {
  const t = $("#toast");
  t.textContent = message;
  t.classList.toggle("error", error);
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = window.setTimeout(() => (t.hidden = true), error ? 9000 : 4500);
}
async function refresh() {
  state = await api<State>("/state");
  serverDelta = Date.parse(state.serverUtc) - Date.now();
  update();
}
async function showAuth() {
  stream?.close();
  $("#shell").hidden = true;
  $("#auth").hidden = false;
  const auth = await api<{
    mode: "desktop" | "password";
    setupRequired: boolean;
    authenticated: boolean;
  }>("/auth/status");
  let desktopError = "";
  if (auth.mode === "desktop" && desktopTicket) {
    const ticket = desktopTicket;
    desktopTicket = "";
    try {
      await api("/auth/desktop/login", "POST", { ticket });
      await boot();
      return;
    } catch (e) {
      desktopError = (e as Error).message;
    }
  }
  if (auth.authenticated) {
    await boot();
    return;
  }
  if (auth.mode === "desktop") {
    $("#auth").innerHTML =
      `<img src="/icon.svg" alt="" width="45" height="45"><h1>Ouvre F1 Hue Sync.</h1><p>Choisis « Ouvrir F1 Hue Sync » depuis l’icône de drapeau dans la barre des menus ou la zone de notification. L’application te connecte automatiquement.</p>${desktopError ? `<div class="notice error" role="alert">${esc(desktopError)}</div>` : ""}`;
    return;
  }
  $("#auth").innerHTML =
    `<img src="/icon.svg" alt="" width="45" height="45"><h1>${auth.setupRequired ? "Bienvenue chez toi." : "Prêt pour le départ ?"}</h1><p>${auth.setupRequired ? "Protège l’accès à tes lampes avec un mot de passe." : "Connecte-toi pour retrouver tes lampes et ta calibration."}</p><form id="auth-form" data-form="${auth.setupRequired ? "setup" : "login"}">${auth.setupRequired ? `<label class="field">Code de configuration<input name="code" autocomplete="off" value="${esc(setupCode)}" required></label><p class="help section-gap">L’application remplit ce code automatiquement au premier lancement. Sur un serveur, il se trouve dans le fichier setup-code.txt du dossier de données.</p>` : ""}<label class="field">Mot de passe<input name="password" type="password" autocomplete="${auth.setupRequired ? "new-password" : "current-password"}" minlength="${auth.setupRequired ? "12" : "1"}" maxlength="256" required></label><div class="actions"><button class="button primary">${auth.setupRequired ? "Configurer F1 Hue" : "Se connecter"}</button></div><p class="help section-gap">${auth.setupRequired ? "12 caractères minimum. Aucun compte en ligne nécessaire." : ""}</p></form>`;
}
function navigate(next: string, push = true, focus = true) {
  changeView();
  const [nextPage, nextTab] = next.split("/");
  page = navNames[nextPage ?? ""] ? nextPage! : "live";
  if (nextTab)
    tab = ["overview", "calibration", "journal"].includes(nextTab)
      ? nextTab
      : "overview";
  const hash =
    "#" + page + (page === "live" && tab !== "overview" ? "/" + tab : "");
  if (push && location.hash !== hash) history.pushState(null, "", hash);
  $("#nav").innerHTML = Object.entries(navNames)
    .map(
      ([id, name]) =>
        `<a class="nav-link" href="#${id}" data-page="${id}"${id === page ? ' aria-current="page"' : ""}><svg viewBox="0 0 24 24" aria-hidden="true">${icons[id]}</svg>${name}</a>`,
    )
    .join("");
  $("#breadcrumb").textContent = navNames[page]!;
  render();
  if (focus) {
    $("#main").focus();
    window.scrollTo({ top: 0 });
  }
}
window.addEventListener("popstate", () => {
  if (state) {
    tab = "overview";
    navigate(location.hash.slice(1), false);
  }
});
function render() {
  $("#main").innerHTML =
    '<div id="service-warning" class="notice error" role="status" hidden></div><div id="runner-error" class="notice error" role="alert" hidden></div><div id="recovery"></div>' +
    (page === "live"
      ? live(state, tab)
      : page === "hue"
        ? hue(state)
        : page === "tests"
          ? tests(state, scenarios)
          : page === "flags"
            ? flagSettings(state)
            : preferences(state));
  recoveryKey = "";
  drafts.mount($("#main"), state.settings.revision);
  offsetDirty =
    !!document.querySelector<HTMLFormElement>('[data-form="offset"]') &&
    drafts.has($<HTMLFormElement>('[data-form="offset"]'));
  update();
  if (page === "hue") void loadInventory();
  if (page === "tests" && scenarios.length === 0)
    void viewApi<Scenario[]>("/replay/scenarios")
      .then((rows) => {
        scenarios = rows;
        if (page === "tests") {
          $("#replay-scenario").innerHTML = scenarioOptions(state, scenarios);
          drafts.mount($("#main"), state.settings.revision);
        }
      })
      .catch((e) => {
        if (e.name !== "AbortError") toast(e.message, true);
      });
}
async function loadInventory() {
  if (!state.hue.linked) return;
  try {
    inventory = await viewApi<Inventory>("/hue/inventory");
    if (page === "hue") renderInventory();
  } catch (e) {
    if ((e as Error).name === "AbortError") return;
    if (page === "hue")
      $("#inventory").innerHTML =
        `<div class="notice error">${esc((e as Error).message)}</div>`;
  }
}
function renderInventory() {
  if (!inventory) return;
  $("#inventory").innerHTML =
    `<div class="notice" data-hue-lock-notice hidden>Termine l’arrêt des lampes pour modifier cette sélection.</div><form data-form="selection"><p id="selection-summary" class="help" aria-live="polite"></p><h3 class="section-gap">Zones et pièces</h3>${inventory.groups.map((g) => `<label class="check"><input type="checkbox" data-group="${esc(g.id)}"${g.lightIds.length && g.lightIds.every((id) => state.settings.lightIds.includes(id)) ? " checked" : ""}>${esc(g.name)}<small>${g.lightIds.length} lampes</small></label>`).join("") || '<p class="help">Aucune zone sur ce pont.</p>'}<h3 class="section-gap">Lampes</h3>${inventory.lights.map((l) => `<label class="check"><input type="checkbox" name="lights" value="${esc(l.id)}"${state.settings.lightIds.includes(l.id) ? " checked" : ""}${!l.color ? " data-unavailable disabled" : ""}>${esc(l.name)}${!l.color ? "<small>Sans couleur</small>" : ""}</label>`).join("")}<p class="help section-gap">Les clignotements utilisent la pulsation native du pont. Les lampes sélectionnées pulsent ensemble, sans zone Entertainment à configurer.</p><div class="actions"><button class="button primary">Enregistrer la sélection</button></div></form>`;
  drafts.mount($("#inventory"), state.settings.revision);
  syncSelection(inventory);
  updateControls();
}
function updateControls() {
  const locked =
    stopRequested ||
    state.initializing ||
    state.runner.running ||
    state.runner.stopping ||
    state.runner.cleanupPending ||
    !!state.recovery?.pending;
  const disable = (el: HTMLElement, value: boolean) =>
    el.toggleAttribute("disabled", value || pending.has(el));
  disable($("#stop"), stopRequested || state.runner.stopping);
  document
    .querySelectorAll<HTMLElement>(
      "[data-hue-controls] input, [data-hue-controls] button, [data-hue-controls] select",
    )
    .forEach((el) =>
      disable(el, locked || el.hasAttribute("data-unavailable")),
    );
  document
    .querySelectorAll<HTMLElement>("[data-hue-lock-notice]")
    .forEach((el) => (el.hidden = !locked));
  document
    .querySelectorAll<HTMLButtonElement>('[data-action="start"]')
    .forEach((b) => {
      disable(
        b,
        locked || !state.hue.linked || state.settings.lightIds.length === 0,
      );
      b.textContent =
        state.runner.running && state.runner.mode === "live"
          ? "Direct activé"
          : "Activer le direct";
    });
  document
    .querySelectorAll<HTMLElement>(
      '[data-action^="preview:"], [data-action="sequence"], [data-form="replay"] button',
    )
    .forEach((el) =>
      disable(
        el,
        locked || !state.hue.linked || state.settings.lightIds.length === 0,
      ),
    );
}
function update() {
  $("#reload-interface").hidden =
    !state.uiBuild || state.uiBuild === __UI_BUILD__;
  if (!state) return;
  $("#connection").textContent = !serviceConnected
    ? "Service déconnecté"
    : state.feed.connected
      ? "Flux F1 connecté"
      : "Flux F1 hors ligne";
  $("#connection").className = "badge " + (state.feed.connected ? "good" : "");
  updateControls();
  $("#stop").textContent =
    stopRequested || state.runner.stopping
      ? "Arrêt en cours…"
      : state.runner.cleanupPending
        ? "Réessayer l’arrêt"
        : "Stop";
  $("#version").textContent = state.version;
  $("#simulation").textContent = state.simulation
    ? "Mode démonstration · lampes simulées"
    : "";
  const serviceWarning =
    document.querySelector<HTMLElement>("#service-warning");
  if (serviceWarning) {
    const messages = [
      !serviceConnected
        ? "Connexion au service perdue. Les commandes ne sont pas confirmées ; reconnexion en cours…"
        : null,
      state.feed.journalError,
      state.feed.processingError,
      state.startup.phase === "waiting"
        ? `${state.startup.error} Nouvelle tentative automatique ${state.startup.retryAt ? new Date(state.startup.retryAt).toLocaleTimeString("fr-BE") : "bientôt"}. Stop annule cette reprise.`
        : null,
    ].filter(Boolean);
    serviceWarning.hidden = messages.length === 0;
    serviceWarning.textContent = messages.join(" ");
  }
  const recoveryContainer = document.querySelector<HTMLElement>("#recovery");
  const nextRecoveryKey = JSON.stringify([
    state.recovery,
    state.runner.running,
    state.runner.stopping,
    state.initializing,
  ]);
  if (recoveryContainer && nextRecoveryKey !== recoveryKey) {
    recoveryContainer.innerHTML = recovery(state);
    recoveryKey = nextRecoveryKey;
  }
  if (formRevision !== state.settings.revision) {
    const template = document.createElement("template");
    template.innerHTML =
      flagSettings(state) + preferences(state) + live(state, "calibration");
    for (const form of document.querySelectorAll<HTMLFormElement>(
      "#main form[data-form]",
    )) {
      if (pending.has(form)) continue;
      const selector = `form[data-form="${form.dataset.form}"]${form.dataset.flag ? `[data-flag="${form.dataset.flag}"]` : ""}`;
      const fresh = template.content.querySelector<HTMLFormElement>(selector);
      if (fresh) drafts.reconcile(form, fresh, state.settings.revision);
    }
    const selection = document.querySelector<HTMLFormElement>(
      '[data-form="selection"]',
    );
    if (selection && !drafts.has(selection) && !pending.has(selection))
      renderInventory();
    formRevision = state.settings.revision;
  }
  drafts.warnings($("#main"), state.settings.revision);
  const values: Record<string, string> = {
    session: state.feed.sessionName ?? "En attente d’une séance",
    "session-description":
      state.feed.lastError ??
      (state.feed.sessionStatus === "Started"
        ? `Séance en cours${state.feed.currentLap ? ` · Tour ${state.feed.currentLap}${state.feed.totalLaps ? "/" + state.feed.totalLaps : ""}` : ""}`
        : state.feed.sessionStatus === "Aborted"
          ? "Séance interrompue"
          : state.feed.sessionStatus
            ? "Séance terminée · en attente du prochain départ"
            : "Le flux attend les informations de la prochaine séance."),
    effect: state.runner.activeEffect
      ? (labels[state.runner.activeEffect as Flag] ?? state.runner.activeEffect)
      : "Ambiance initiale",
    offset: `${state.settings.offsetSeconds.toLocaleString("fr-BE")} s`,
    targets: `${state.settings.lightIds.length} lampe${state.settings.lightIds.length !== 1 ? "s" : ""}`,
    mode: state.initializing
      ? "Préparation des lampes…"
      : state.runner.stopping
        ? "Arrêt en cours"
        : state.runner.cleanupPending
          ? "Arrêt à réessayer"
          : state.runner.running
            ? ({
                live: "Direct activé",
                preview: "Aperçu",
                sequence: "Séquence",
                replay: "Replay",
              }[state.runner.mode ?? ""] ?? "Actif")
            : "À l’arrêt",
    bridge: state.hue.linked
      ? `${state.hue.name ?? "Pont lié"} · ${state.settings.lightIds.length} lampe${state.settings.lightIds.length !== 1 ? "s" : ""} choisie${state.settings.lightIds.length !== 1 ? "s" : ""}`
      : "Pont à lier",
  };
  document
    .querySelectorAll<HTMLElement>("[data-live]")
    .forEach((el) => (el.textContent = values[el.dataset.live ?? ""] ?? ""));
  const error = document.querySelector<HTMLElement>("#runner-error");
  if (error) {
    error.hidden = !state.runner.error;
    error.textContent = state.runner.error;
  }
  const recent = document.querySelector("#recent-flags");
  if (recent) recent.innerHTML = logs(state.journal, 4);
  const journal = document.querySelector("#journal-list");
  if (journal && !($("#journal-session") as HTMLSelectElement).value)
    journal.innerHTML = logs(state.journal);
  const themeSelect = document.querySelector<HTMLSelectElement>("#theme");
  if (themeSelect)
    themeSelect.value = localStorage.getItem("f1hue.theme") ?? "light";
  updateCalibration();
  updateFreshness();
}
function updateFreshness() {
  renderFreshness(state, serverDelta);
}
function updateCalibration() {
  renderCalibration(state, pending, { offsetDirty, offsetSaving, serverDelta });
}
window.setInterval(() => {
  if (state) updateFreshness();
  if (state && page === "live" && tab === "calibration") updateCalibration();
}, 250);
async function saveOffset(value: number, relative = false) {
  if (offsetSaving) return;
  offsetSaving = true;
  updateCalibration();
  try {
    const saved = await api<State["settings"]>(
      relative ? "/calibration/adjust" : "/settings",
      relative ? "POST" : "PATCH",
      relative ? { deltaSeconds: value } : { offsetSeconds: value },
      relative
        ? {}
        : {
            revision:
              drafts.revision($<HTMLFormElement>('[data-form="offset"]')) ??
              state.settings.revision,
          },
    );
    state.settings = saved;
    const offsetForm = document.querySelector<HTMLFormElement>(
      '[data-form="offset"]',
    );
    if (offsetForm) drafts.clear(offsetForm);
    formRevision = -1;
    offsetDirty = false;
  } finally {
    offsetSaving = false;
    update();
  }
}
async function boot() {
  await refresh();
  $("#auth").hidden = true;
  $("#shell").hidden = false;
  navigate(location.hash.slice(1) || "live", false, false);
  stream?.close();
  stream = new EventSource("/api/events");
  stream.onmessage = (e) => {
    serviceConnected = true;
    state = JSON.parse(e.data) as State;
    serverDelta = Date.parse(state.serverUtc) - Date.now();
    update();
  };
  stream.onerror = () => {
    serviceConnected = false;
    update();
    void api<{ authenticated: boolean }>("/auth/status")
      .then((auth) => {
        if (!auth.authenticated) return showAuth();
      })
      .catch(() => {});
  };
}
async function action(name: string, target: HTMLElement) {
  if (name === "reload-interface") {
    location.reload();
    return;
  }
  if (name === "discard-draft") {
    const form = target.closest("form")!;
    drafts.clear(form);
    formRevision = -1;
    render();
    return;
  }
  if (name === "recover-available") {
    await api("/recovery/available", "POST");
    await refresh();
    return;
  }
  if (name === "backup") {
    const result = await api<{ file: string }>("/backup", "POST");
    toast("Sauvegarde créée dans le dossier backups : " + result.file);
    return;
  }
  if (name === "export-diagnostics") {
    const result = await api("/diagnostics");
    download(result, "f1-hue-diagnostic.json");
    return;
  }
  if (name === "stop") {
    stopRequested = true;
    update();
    try {
      await api("/stop", "POST");
      toast("Mode arrêté.");
    } finally {
      stopRequested = false;
      await refresh();
    }
    return;
  } else if (name === "start") await api("/live/start", "POST");
  else if (name === "calibration" || name === "journal") {
    tab = name === "calibration" ? "calibration" : "journal";
    navigate("live");
    return;
  } else if (name === "logout") {
    await api("/auth/logout", "POST");
    await showAuth();
    return;
  } else if (name.startsWith("preview:"))
    await api("/test/preview", "POST", { flag: name.split(":")[1] });
  else if (name === "sequence") await api("/test/sequence", "POST");
  else if (name.startsWith("arm:"))
    await api("/calibration/arm", "POST", { mode: name.split(":")[1] });
  else if (name === "seen") {
    await api("/calibration/seen", "POST");
    toast("Mesure calculée. Tu peux l’utiliser puis l’enregistrer.");
  } else if (name === "cancel-calibration")
    await api("/calibration/cancel", "POST");
  else if (name.startsWith("adjust-offset:")) {
    if (offsetSaving || offsetDirty) return;
    await saveOffset(Number(name.split(":")[1]), true);
  } else if (name === "use-offset") {
    if (state.calibration.proposedOffset === null)
      throw new Error("Effectue d’abord une mesure.");
    $<HTMLInputElement>('[name="offset"]').value = String(
      state.calibration.proposedOffset,
    );
    offsetDirty = true;
    drafts.capture($<HTMLFormElement>('[data-form="offset"]'));
    drafts.warnings($("#main"), state.settings.revision);
    updateCalibration();
    return;
  } else if (name === "discover") {
    const r = await api<{ addresses: string[] }>("/hue/discover", "POST");
    const discovered = target.closest("form")?.querySelector("#discovered");
    if (!discovered?.isConnected) return;
    discovered.innerHTML = r.addresses.length
      ? r.addresses
          .map(
            (ip) =>
              `<button type="button" class="button" data-action="choose-ip" data-ip="${esc(ip)}">${esc(ip)}</button>`,
          )
          .join(" ")
      : '<p class="help">Aucun pont trouvé. Tu peux saisir son adresse manuellement.</p>';
    return;
  } else if (name === "choose-ip") {
    const ip = $<HTMLInputElement>('[name="ip"]');
    ip.value = target.dataset.ip ?? "";
    drafts.capture(ip.form!);
    return;
  } else if (name === "inventory") {
    await loadInventory();
    return;
  } else if (name === "unpair") {
    if (
      !confirm(
        "Oublier le pont sur cet appareil ? Sa clé restera autorisée sur le pont. Pour la révoquer, supprime F1 Hue Sync dans les applications autorisées de Hue.",
      )
    )
      return;
    await api("/hue/unpair", "POST");
    inventory = null;
    await refresh();
    render();
    return;
  } else if (name === "import-selection") {
    const r = await api<{ mapped: boolean }>("/hue/import-selection", "POST");
    toast(
      r.mapped
        ? "Ancienne sélection importée."
        : "Choisis tes lampes ci-contre : l’ancienne sélection ne peut pas être reprise automatiquement.",
    );
    await refresh();
    renderInventory();
    return;
  } else if (name === "export-journal") {
    const events = await api<RaceEvent[]>(
      "/journal?session=" +
        encodeURIComponent($<HTMLSelectElement>("#journal-session").value),
    );
    download(events, "f1-hue-drapeaux.json");
    return;
  }
  await refresh();
}
function download(value: unknown, filename: string) {
  const url = URL.createObjectURL(
    new Blob([JSON.stringify(value, null, 2)], { type: "application/json" }),
  );
  const link = document.createElement("a");
  link.href = url;
  link.download = filename;
  link.click();
  window.setTimeout(() => URL.revokeObjectURL(url), 1000);
}
document.addEventListener("keydown", (event) => {
  const target = (event.target as Element).closest<HTMLButtonElement>(
    '[role="tab"]',
  );
  if (
    !target ||
    !["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)
  )
    return;
  const tabs = [
    ...document.querySelectorAll<HTMLButtonElement>('[role="tab"]'),
  ];
  const index = tabs.indexOf(target);
  const next =
    event.key === "Home"
      ? 0
      : event.key === "End"
        ? tabs.length - 1
        : (index + (event.key === "ArrowRight" ? 1 : -1) + tabs.length) %
          tabs.length;
  event.preventDefault();
  tabs[next]?.click();
});
window.addEventListener("beforeunload", (event) => {
  if (drafts.dirty) {
    event.preventDefault();
    event.returnValue = "";
  }
});
document.addEventListener("click", (event) => {
  const element = (event.target as Element).closest<HTMLElement>(
    "[data-page],[data-tab],[data-action]",
  );
  if (!element) return;
  event.preventDefault();
  if (element.dataset.page) {
    navigate(element.dataset.page);
    return;
  }
  if (element.dataset.tab) {
    tab = element.dataset.tab;
    navigate("live/" + tab, true, false);
    document.querySelector<HTMLButtonElement>(`[data-tab="${tab}"]`)?.focus();
    return;
  }
  if (element instanceof HTMLButtonElement && element.disabled) return;
  const previouslyDisabled = element.hasAttribute("disabled");
  pending.add(element);
  element.setAttribute("disabled", "");
  void action(element.dataset.action!, element)
    .catch((e) => {
      if (e.name !== "AbortError") toast(e.message, true);
    })
    .finally(() => {
      pending.delete(element);
      if (!previouslyDisabled) element.removeAttribute("disabled");
      update();
    });
});
document.addEventListener("change", (event) => {
  const el = event.target as HTMLInputElement;
  if (el.id === "theme") {
    localStorage.setItem("f1hue.theme", el.value);
    theme();
  }
  if (el.name === "durationMode") {
    const input = el
      .closest("form")!
      .querySelector<HTMLInputElement>('[name="duration"]')!;
    input.disabled = el.value !== "fixed";
  }
  if (el.dataset.group && inventory) {
    const ids =
      inventory.groups.find((g) => g.id === el.dataset.group)?.lightIds ?? [];
    document
      .querySelectorAll<HTMLInputElement>('[name="lights"]')
      .forEach((l) => {
        if (ids.includes(l.value) && !l.disabled) l.checked = el.checked;
      });
  }
  if (el.dataset.group || el.name === "lights") syncSelection(inventory);
  if (el.form && state) {
    drafts.capture(el.form);
    drafts.warnings($("#main"), state.settings.revision);
  }
  if (el.id === "journal-session")
    void viewApi<RaceEvent[]>(
      "/journal?session=" + encodeURIComponent(el.value),
    )
      .then((rows) => {
        const list = document.querySelector("#journal-list");
        if (
          list &&
          document.querySelector<HTMLSelectElement>("#journal-session")
            ?.value === el.value
        )
          list.innerHTML = logs(rows, 1000);
      })
      .catch((e) => {
        if (e.name !== "AbortError") toast(e.message, true);
      });
});
document.addEventListener("input", (event) => {
  const input = event.target;
  if (input instanceof HTMLInputElement && input.form && state) {
    drafts.capture(input.form);
    drafts.warnings($("#main"), state.settings.revision);
  }
  if (input instanceof HTMLInputElement && input.name === "offset") {
    offsetDirty =
      input.value === "" ||
      input.valueAsNumber !== state.settings.offsetSeconds;
    updateCalibration();
  }
});
document.addEventListener("submit", (event) => {
  const form = event.target as HTMLFormElement;
  if (!form.dataset.form) return;
  event.preventDefault();
  if (pending.has(form)) return;
  const data = new FormData(form);
  const kind = form.dataset.form;
  const submit = form.querySelector<HTMLButtonElement>(
    'button:not([type="button"])',
  );
  if (submit?.disabled) return;
  pending.add(form);
  if (submit) {
    pending.add(submit);
    submit.disabled = true;
  }
  const work = async () => {
    if (kind === "login" || kind === "setup") {
      await api("/auth/" + kind, "POST", Object.fromEntries(data));
      setupCode = "";
      await boot();
      return;
    }
    if (kind === "clock") {
      await api("/calibration/clock", "POST", {
        remaining: data.get("remaining"),
      });
      toast("Mesure calculée. Utilise-la puis enregistre le décalage.");
    } else if (kind === "offset") {
      await saveOffset(Number(data.get("offset")));
      toast("Décalage enregistré pour les prochains événements reçus.");
    } else if (kind === "recovery-pair" || kind === "recovery-abandon") {
      await api(
        kind === "recovery-pair" ? "/recovery/pair" : "/recovery/abandon",
        "POST",
        Object.fromEntries(data),
      );
      toast(
        kind === "recovery-pair"
          ? "Pont relié. Tu peux réessayer la récupération."
          : "Sauvegarde archivée. La récupération a été abandonnée.",
      );
    } else if (kind === "pair") {
      await api("/hue/pair", "POST", { ip: data.get("ip") });
      drafts.clear(form);
      formRevision = -1;
      toast("Pont lié. Choisis maintenant tes lampes.");
      await refresh();
      render();
      return;
    } else if (kind === "selection") {
      await api(
        "/hue/select",
        "POST",
        {
          lightIds: data.getAll("lights"),
          groupIds: [],
          entertainmentAreaId: null,
        },
        { revision: drafts.revision(form) },
      );
      toast("Sélection enregistrée.");
    } else if (kind === "replay") {
      const [type, ...key] = String(data.get("scenario")).split(":");
      await api("/replay/start", "POST", {
        kind: type,
        id: key.join(":"),
        speed: Number(data.get("speed")),
      });
      toast("Replay lancé.");
    } else if (kind === "flag") {
      await api(
        "/settings",
        "PATCH",
        {
          effects: {
            [form.dataset.flag!]: {
              enabled: data.has("enabled"),
              durationSeconds:
                data.get("durationMode") === "fixed"
                  ? Number(data.get("duration"))
                  : null,
            },
          },
        },
        { revision: drafts.revision(form) },
      );
      toast("Enregistré · actif au prochain événement.");
    } else if (kind === "preferences") {
      await api(
        "/settings",
        "PATCH",
        {
          brightness: Math.round((Number(data.get("brightness")) * 254) / 100),
          transitionSeconds: Number(data.get("transitionSeconds")),
          alertWatchdogSeconds: Number(data.get("alertWatchdogSeconds")),
          restoreOnExit: data.has("restoreOnExit"),
          exitOnChequered: data.has("exitOnChequered"),
          autoLive: data.has("autoLive"),
        },
        { revision: drafts.revision(form) },
      );
      toast("Préférences enregistrées.");
    }
    drafts.clear(form);
    formRevision = -1;
    await refresh();
  };
  void work()
    .catch((e) => {
      if (e.name !== "AbortError") toast(e.message, true);
    })
    .finally(() => {
      pending.delete(form);
      if (submit) {
        pending.delete(submit);
        submit.disabled = false;
      }
      update();
    });
});
void showAuth().catch((e) => {
  $("#auth").innerHTML =
    `<h1>Service indisponible</h1><p>${esc(e.message)}</p><p>Vérifie que l’application F1 Hue est lancée.</p>`;
});

window.addEventListener("f1hue:unauthorized", () => {
  void showAuth().catch((e) => toast(e.message, true));
});
