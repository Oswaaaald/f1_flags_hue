import type { State, Flag, Inventory, RaceEvent, Scenario } from "./types";
import { flags, labels } from "./flags";
import { $, esc, dot, button } from "./ui";
import { api } from "./api";
import { live, logs } from "./views/live";
import { hue } from "./views/hue";
import { tests, scenarioOptions } from "./views/tests";
import { flagSettings, preferences } from "./views/settings";

let stopRequested = false;
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
const icons: Record<string, string> = {
  live: '<path d="M4 12h3l3-7 4 14 3-7h3"/>',
  hue: '<path d="M9 18h6m-5 3h4M8 14a6 6 0 1 1 8 0l-1 3H9z"/>',
  tests: '<path d="M8 4v16l12-8z"/>',
  flags: '<path d="M5 21V3m0 1h14l-3 5 3 5H5"/>',
  preferences:
    '<path d="M4 7h16M4 17h16"/><circle cx="9" cy="7" r="3"/><circle cx="15" cy="17" r="3"/>',
};
const navNames: Record<string, string> = {
  live: "Direct",
  hue: "Hue",
  tests: "Tests",
  flags: "Drapeaux",
  preferences: "Préférences",
};
function navigate(next: string) {
  page = navNames[next] ? next : "live";
  history.replaceState(null, "", "#" + page);
  $("#nav").innerHTML = Object.entries(navNames)
    .map(
      ([id, name]) =>
        `<a class="nav-link" href="#${id}" data-page="${id}"${id === page ? ' aria-current="page"' : ""}><svg viewBox="0 0 24 24" aria-hidden="true">${icons[id]}</svg>${name}</a>`,
    )
    .join("");
  $("#breadcrumb").textContent = navNames[page]!;
  render();
}
function render() {
  $("#main").innerHTML =
    '<div id="runner-error" class="notice error" role="alert" hidden></div>' +
    (page === "live"
      ? live(state, tab)
      : page === "hue"
        ? hue(state)
        : page === "tests"
          ? tests(state, scenarios)
          : page === "flags"
            ? flagSettings(state)
            : preferences(state));
  update();
  if (page === "hue") void loadInventory();
  if (page === "tests" && scenarios.length === 0)
    void api<Scenario[]>("/replay/scenarios")
      .then((rows) => {
        scenarios = rows;
        if (page === "tests")
          $("#replay-scenario").innerHTML = scenarioOptions(state, scenarios);
      })
      .catch((e) => toast(e.message, true));
}
async function loadInventory() {
  if (!state.hue.linked) return;
  try {
    inventory = await api<Inventory>("/hue/inventory");
    if (page === "hue") renderInventory();
  } catch (e) {
    if (page === "hue")
      $("#inventory").innerHTML =
        `<div class="notice error">${esc((e as Error).message)}</div>`;
  }
}
function renderInventory() {
  if (!inventory) return;
  const locked =
    state.runner.running || state.runner.cleanupPending ? " disabled" : "";
  $("#inventory").innerHTML =
    `${locked ? '<div class="notice">Termine l’arrêt des lampes pour modifier cette sélection.</div>' : ""}<form data-form="selection"><h3 class="section-gap">Zones et pièces</h3>${inventory.groups.map((g) => `<label class="check"><input type="checkbox" data-group="${esc(g.id)}"${g.lightIds.length && g.lightIds.every((id) => state.settings.lightIds.includes(id)) ? " checked" : ""}${locked}>${esc(g.name)}<small>${g.lightIds.length} lampes</small></label>`).join("") || '<p class="help">Aucune zone sur ce pont.</p>'}<h3 class="section-gap">Lampes</h3>${inventory.lights.map((l) => `<label class="check"><input type="checkbox" name="lights" value="${esc(l.id)}"${state.settings.lightIds.includes(l.id) ? " checked" : ""}${!l.color ? " disabled" : locked}>${esc(l.name)}${!l.color ? "<small>Sans couleur</small>" : ""}</label>`).join("")}<p class="help section-gap">Les clignotements utilisent la pulsation native du pont. Les lampes sélectionnées pulsent ensemble, sans zone Entertainment à configurer.</p><div class="actions"><button class="button primary"${locked}>Enregistrer la sélection</button></div></form>`;
}
function update() {
  if (!state) return;
  $("#connection").textContent = state.feed.connected
    ? "Flux F1 connecté"
    : "Flux F1 hors ligne";
  $("#connection").className = "badge " + (state.feed.connected ? "good" : "");
  $("#stop").toggleAttribute(
    "disabled",
    stopRequested || state.runner.stopping,
  );
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
      ? labels[state.runner.activeEffect]
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
  document
    .querySelectorAll<HTMLButtonElement>('[data-action="start"]')
    .forEach((b) => {
      b.disabled =
        state.runner.running ||
        state.runner.cleanupPending ||
        !state.hue.linked ||
        state.settings.lightIds.length === 0;
      b.textContent =
        state.runner.running && state.runner.mode === "live"
          ? "Direct activé"
          : "Activer le direct";
    });
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
  document
    .querySelectorAll<HTMLButtonElement>(
      '[data-action^="preview:"],[data-action="sequence"]',
    )
    .forEach(
      (b) =>
        (b.disabled =
          state.initializing ||
          state.runner.running ||
          state.runner.cleanupPending),
    );
  document
    .querySelectorAll<HTMLButtonElement>('[data-form="replay"] button')
    .forEach(
      (b) =>
        (b.disabled =
          state.initializing ||
          state.runner.running ||
          state.runner.cleanupPending),
    );
  const themeSelect = document.querySelector<HTMLSelectElement>("#theme");
  if (themeSelect)
    themeSelect.value = localStorage.getItem("f1hue.theme") ?? "light";
  updateCalibration();
}
function updateCalibration() {
  const status = document.querySelector("#calibration-status");
  if (status) {
    const c = state.calibration;
    status.textContent = c.waiting
      ? "En attente du prochain repère sur le flux F1…"
      : c.reference
        ? `Repère reçu : ${c.mode === "start" ? "départ de la course" : c.mode === "lap" ? "tour " + c.reference.value : (labels[c.reference.value as Flag] ?? c.reference.value)}. Clique quand tu le vois à la TV.`
        : "Choisis un repère pour démarrer.";
    document.querySelector<HTMLButtonElement>(
      '[data-action="seen"]',
    )!.disabled = !c.reference || c.proposedOffset !== null;
    $("#proposed-offset").textContent =
      c.proposedOffset !== null
        ? `${c.proposedOffset} secondes`
        : "Aucune mesure";
  }
  const clock = document.querySelector("#session-clock");
  if (clock) {
    const c = state.feed.clock;
    let seconds: number | null = null;
    if (
      c?.remaining &&
      c.utc &&
      c.extrapolating &&
      state.feed.connected &&
      state.feed.sessionStatus === "Started"
    ) {
      const parts = c.remaining.split(":").map(Number);
      seconds =
        parts.reduce((a, b) => a * 60 + b, 0) -
        (Date.now() + serverDelta - Date.parse(c.utc)) / 1000;
      if (!Number.isFinite(seconds) || seconds < 0) seconds = null;
    }
    clock.textContent =
      seconds === null
        ? "—:—"
        : `${Math.floor(seconds / 60)
            .toString()
            .padStart(2, "0")}:${Math.floor(seconds % 60)
            .toString()
            .padStart(2, "0")}`;
    $("#clock-note").textContent =
      seconds === null
        ? "Le chrono est arrêté ou indisponible hors séance."
        : "Temps restant estimé à partir du chrono F1.";
  }
}
window.setInterval(() => {
  if (state && page === "live" && tab === "calibration") updateCalibration();
}, 250);
async function boot() {
  await refresh();
  $("#auth").hidden = true;
  $("#shell").hidden = false;
  navigate(location.hash.slice(1) || "live");
  stream?.close();
  stream = new EventSource("/api/events");
  stream.onmessage = (e) => {
    state = JSON.parse(e.data) as State;
    serverDelta = Date.parse(state.serverUtc) - Date.now();
    update();
  };
  stream.onerror = () => {
    $("#connection").textContent = "Reconnexion au service…";
    void api<{ authenticated: boolean }>("/auth/status")
      .then((auth) => {
        if (!auth.authenticated) return showAuth();
      })
      .catch(() => {});
  };
}
async function action(name: string, target: HTMLElement) {
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
  else if (name === "use-offset") {
    if (state.calibration.proposedOffset === null)
      throw new Error("Effectue d’abord une mesure.");
    $<HTMLInputElement>('[name="offset"]').value = String(
      state.calibration.proposedOffset,
    );
    return;
  } else if (name === "discover") {
    const r = await api<{ addresses: string[] }>("/hue/discover", "POST");
    $("#discovered").innerHTML = r.addresses.length
      ? r.addresses
          .map(
            (ip) =>
              `<button type="button" class="button" data-action="choose-ip" data-ip="${esc(ip)}">${esc(ip)}</button>`,
          )
          .join(" ")
      : '<p class="help">Aucun pont trouvé. Tu peux saisir son adresse manuellement.</p>';
    return;
  } else if (name === "choose-ip") {
    $<HTMLInputElement>('[name="ip"]').value = target.dataset.ip ?? "";
    return;
  } else if (name === "inventory") {
    await loadInventory();
    return;
  } else if (name === "unpair") {
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
    const url = URL.createObjectURL(
      new Blob([JSON.stringify(events, null, 2)], { type: "application/json" }),
    );
    const link = document.createElement("a");
    link.href = url;
    link.download = "f1-hue-drapeaux.json";
    link.click();
    window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    return;
  }
  await refresh();
}
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
    render();
    return;
  }
  if (element instanceof HTMLButtonElement && element.disabled) return;
  const previouslyDisabled = element.hasAttribute("disabled");
  element.setAttribute("disabled", "");
  void action(element.dataset.action!, element)
    .catch((e) => toast(e.message, true))
    .finally(() => {
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
  if (el.id === "journal-session")
    void api<RaceEvent[]>("/journal?session=" + encodeURIComponent(el.value))
      .then((rows) => ($("#journal-list").innerHTML = logs(rows, 1000)))
      .catch((e) => toast(e.message, true));
});
document.addEventListener("submit", (event) => {
  const form = event.target as HTMLFormElement;
  if (!form.dataset.form) return;
  event.preventDefault();
  const data = new FormData(form);
  const kind = form.dataset.form;
  const submit = form.querySelector<HTMLButtonElement>(
    'button:not([type="button"])',
  );
  if (submit) submit.disabled = true;
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
      await api("/settings", "PATCH", {
        offsetSeconds: Number(data.get("offset")),
      });
      toast("Décalage enregistré pour les prochains événements reçus.");
    } else if (kind === "pair") {
      await api("/hue/pair", "POST", { ip: data.get("ip") });
      toast("Pont lié. Choisis maintenant tes lampes.");
      await refresh();
      render();
      return;
    } else if (kind === "selection") {
      await api("/hue/select", "POST", {
        lightIds: data.getAll("lights"),
        groupIds: [],
        entertainmentAreaId: null,
      });
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
      await api("/settings", "PATCH", {
        effects: {
          [form.dataset.flag!]: {
            enabled: data.has("enabled"),
            durationSeconds:
              data.get("durationMode") === "fixed"
                ? Number(data.get("duration"))
                : null,
          },
        },
      });
      toast("Enregistré · actif au prochain événement.");
    } else if (kind === "preferences") {
      await api("/settings", "PATCH", {
        brightness: Number(data.get("brightness")),
        transitionSeconds: Number(data.get("transitionSeconds")),
        alertWatchdogSeconds: Number(data.get("alertWatchdogSeconds")),
        restoreOnExit: data.has("restoreOnExit"),
        exitOnChequered: data.has("exitOnChequered"),
        autoLive: data.has("autoLive"),
      });
      toast("Préférences enregistrées.");
    }
    await refresh();
  };
  void work()
    .catch((e) => toast(e.message, true))
    .finally(() => {
      if (submit) submit.disabled = false;
    });
});
void showAuth().catch((e) => {
  $("#auth").innerHTML =
    `<h1>Service indisponible</h1><p>${esc(e.message)}</p><p>Vérifie que l’application F1 Hue est lancée.</p>`;
});

window.addEventListener("f1hue:unauthorized", () => {
  void showAuth().catch((e) => toast(e.message, true));
});
