import AxeBuilder from "@axe-core/playwright";
import { test, expect } from "./fixtures";

test("Live activity is explicit even when F1 is connected and no effect is playing", async ({ page, service }, info) => {
  await service.open(page);
  await service.post(page, "/hue/select", { lightIds: ["11111111-1111-1111-1111-111111111111"], groupIds: [] });
  await service.post(page, "/simulation/event", { topic: "SessionStatus", payload: { Status: "Started" } });
  await expect(page.locator("#connection")).toHaveText("Flux F1 connecté");
  await expect(page.locator("#mode-status")).toHaveText("Direct inactif");
  await expect(page.locator("[data-mode-detail]")).toContainText("Tes lampes ne suivent pas la F1");
  await expect(page.locator("#stop")).toBeDisabled();
  await page.screenshot({ path: info.outputPath("direct-inactive-desktop.png"), fullPage: true });
  await page.getByRole("button", { name: "Activer le direct", exact: true }).click();
  await expect(page.locator("#mode-status")).toHaveText("Direct actif");
  await expect(page.locator("[data-live=effect]")).toHaveText("Ambiance initiale");
  await expect(page.locator("#stop")).toBeEnabled();
  await page.screenshot({ path: info.outputPath("direct-active-desktop.png"), fullPage: true });
  await page.getByRole("tab", { name: "Calibration TV" }).click();
  await expect(page.locator("[data-mode-title]")).toHaveText("Direct actif");
  await page.locator("#stop").click();
  await expect(page.locator("#mode-status")).toHaveText("Direct inactif");
  await expect(page.locator("#stop")).toBeDisabled();
  await page.getByRole("link", { name: "Tests", exact: true }).click();
  await page.getByRole("button", { name: "Safety car", exact: true }).click();
  await expect(page.locator("#mode-status")).toHaveText("Test en cours");
  await page.locator("#stop").click();
  await expect(page.locator("#mode-status")).toHaveText("Direct inactif");
  await expect(page.locator("#stop")).toBeDisabled();
});

test("Stop remains available while a mode is starting", async ({ page, service }) => {
  await service.open(page);
  await service.post(page, "/hue/select", { lightIds: ["11111111-1111-1111-1111-111111111111"], groupIds: [] });
  let release!: () => void;
  const blocked = new Promise<void>(resolve => release = resolve);
  await page.route("**/api/live/start", async route => { await blocked; await route.abort(); });
  try {
    await page.getByRole("button", { name: "Activer le direct", exact: true }).click();
    await expect(page.locator("#mode-status")).toHaveText("Préparation en cours…");
    await expect(page.locator("#stop")).toBeEnabled();
  } finally { release(); }
  await expect(page.locator("#mode-status")).toHaveText("Direct inactif");
  await expect(page.locator("#stop")).toBeDisabled();
});

test("Hue selection, drafts, native preview and Stop stay coherent", async ({ page, service }) => {
  const errors: string[] = []; page.on("pageerror", e => errors.push(e.message));
  await service.open(page);
  await page.getByRole("link", { name: "Hue", exact: true }).click();
  const selection = page.locator('[data-form="selection"]');
  await selection.locator("[data-group]").check();
  await selection.getByLabel("Ruban TV", { exact: true }).uncheck();
  await expect(selection.locator("[data-group]")).toHaveJSProperty("indeterminate", true);
  await page.getByRole("link", { name: "Drapeaux", exact: true }).click();
  await page.getByRole("link", { name: "Hue", exact: true }).click();
  await expect(selection.getByLabel("Lampe salon", { exact: true })).toBeChecked();
  await expect(selection.getByLabel("Ruban TV", { exact: true })).not.toBeChecked();
  await selection.getByRole("button", { name: "Enregistrer la sélection" }).click();
  await expect(page.locator("#toast")).toContainText("Sélection enregistrée");
  await service.post(page, "/test/preview", { flag: "SC" });
  await expect(selection.getByLabel("Lampe salon", { exact: true })).toBeDisabled();
  await page.locator("#stop").click();
  await expect(selection.getByLabel("Lampe salon", { exact: true })).toBeEnabled();
  expect(errors).toEqual([]);
});

test("Settings conflicts retain drafts and reload current values", async ({ page, context, service }) => {
  await service.open(page);
  await page.getByRole("link", { name: "Préférences", exact: true }).click();
  await page.getByLabel("Luminosité (%)").fill("57.1");
  const other = await context.newPage(); await other.goto(service.url + "/#preferences");
  await other.getByLabel("Luminosité (%)").fill("72.8");
  await other.getByRole("button", { name: "Enregistrer les préférences" }).click();
  await expect(page.locator("[data-draft-notice]")).toContainText("autre onglet");
  await page.getByRole("button", { name: "Enregistrer les préférences" }).click();
  await expect(page.locator("#toast")).toContainText("ont changé");
  await expect(page.getByLabel("Luminosité (%)")).toHaveValue("57.1");
  await page.getByRole("button", { name: "Recharger les valeurs enregistrées" }).click();
  await expect(page.getByLabel("Luminosité (%)")).toHaveValue("72.8");
});

test("Calibration, history, journal and replay work from the keyboard", async ({ page, service }) => {
  await service.open(page);
  await page.getByRole("tab", { name: "Vue d’ensemble" }).focus();
  await page.keyboard.press("ArrowRight");
  await expect(page).toHaveURL(/#live\/calibration$/);
  await expect(page.getByRole("tab", { name: "Calibration TV" })).toBeFocused();
  await page.getByRole("button", { name: "Retarder les effets de 0,1 seconde", exact: true }).click();
  await expect(page.locator('[data-live="offset"]').first()).toHaveText("0,1 s");
  await service.post(page, "/simulation/event", { topic: "SessionInfo", payload: { Key: 123, Name: "Course test", Type: "Race" } });
  await service.post(page, "/simulation/event", { topic: "SessionStatus", payload: { Status: "Started" } });
  await page.getByRole("button", { name: "Prochain tour", exact: true }).click();
  await expect(page.locator("#calibration-status")).toContainText("En attente du prochain repère");
  await service.post(page, "/simulation/event", { topic: "LapCount", payload: { CurrentLap: 2, TotalLaps: 50 } });
  await page.getByRole("button", { name: "Je le vois maintenant", exact: true }).click();
  await expect(page.locator("#offset-measurement")).toBeVisible();
  await service.post(page, "/simulation/event", { topic: "TrackStatus", payload: { Status: "5" } });
  await page.getByRole("tab", { name: "Journal des drapeaux" }).click();
  await expect(page.locator("#journal-list")).toContainText("Drapeau rouge");
  await page.goBack(); await expect(page).toHaveURL(/#live\/calibration$/);
  await page.goForward(); await expect(page).toHaveURL(/#live\/journal$/);
  await service.post(page, "/hue/select", { lightIds: ["11111111-1111-1111-1111-111111111111"], groupIds: [] });
  await page.getByRole("link", { name: "Tests", exact: true }).click();
  await page.locator('[name="scenario"]').selectOption("local:123");
  await page.getByRole("button", { name: "Lancer le replay", exact: true }).click();
  await expect(page.locator("#toast")).toContainText("Replay lancé");
  await page.locator("#stop").click();
});

test("Every view stays accessible at 320px and with enlarged text", async ({ page, service }, info) => {
  await page.setViewportSize({ width: 320, height: 740 });
  await service.open(page);
  for (const name of ["Direct", "Hue", "Tests", "Drapeaux", "Préférences"]) {
    await page.getByRole("link", { name, exact: true }).click();
    await expect(page.locator("#stop")).toBeInViewport();
    if (name === "Direct") await page.screenshot({ path: info.outputPath("direct-inactive-320px.png"), fullPage: true });
    if (name === "Hue") await expect(page.locator('[data-form="selection"]')).toBeVisible();
    const result = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa"]).analyze();
    expect(result.violations, JSON.stringify(result.violations, null, 2)).toEqual([]);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBeTruthy();
  }
  await page.getByRole("combobox", { name: /^Thème/ }).selectOption("dark");
  expect((await new AxeBuilder({ page }).withTags(["wcag2aa"]).analyze()).violations).toEqual([]);
  const zoomCss = await page.evaluate(() => {
    const elements = [...document.querySelectorAll<HTMLElement>("body, body *")];
    const sizes = elements.map(el => parseFloat(getComputedStyle(el).fontSize) * 2);
    return elements.map((el, i) => { el.dataset.textZoom = String(i); return `[data-text-zoom="${i}"] { font-size: ${sizes[i]}px !important; }`; }).join("\n");
  });
  await page.route("**/test-text-zoom.css", route => route.fulfill({ contentType: "text/css", body: zoomCss }));
  await page.addStyleTag({ url: service.url + "/test-text-zoom.css" });
  await expect(page.locator("#stop")).toBeInViewport();
  await page.screenshot({ path: info.outputPath("preferences-320px-large-text.png"), fullPage: true });
  const overflow = await page.evaluate(() => [...document.querySelectorAll<HTMLElement>("body *")]
    .filter(el => el.getBoundingClientRect().right > innerWidth + 1)
    .map(el => `${el.tagName}.${el.className}: ${el.textContent?.trim().slice(0, 60)}`));
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), overflow.join("\n")).toBeTruthy();
});

test("Leaving a view cancels its read and does not produce stale DOM errors", async ({ page, service }) => {
  const errors: string[] = []; page.on("pageerror", e => errors.push(e.message));
  await service.open(page);
  let entered: () => void = () => {};
  const pending = new Promise<void>(resolve => entered = resolve);
  await page.route("**/api/hue/inventory", async route => { entered(); await new Promise(r => setTimeout(r, 500)); await route.abort().catch(() => {}); });
  await page.getByRole("link", { name: "Hue", exact: true }).click(); await pending;
  await page.getByRole("link", { name: "Préférences", exact: true }).click();
  await expect(page.getByRole("heading", { name: "À ton rythme." })).toBeVisible();
  expect(errors).toEqual([]);
});


test("Recovery guidance is shown only after active control ends", async ({ page, service }) => {
  await service.open(page);
  const response = await page.request.get(service.url + "/api/state");
  const state = await response.json();
  state.recovery = { pending: true, bridgeId: "test-bridge", lightIds: ["11111111-1111-1111-1111-111111111111"], resources: ["pending_restore"] };
  state.runner.running = true;
  state.runner.mode = "live";
  await page.route("**/api/state", route => route.fulfill({ json: state }));
  await page.route("**/api/events", route => route.fulfill({ contentType: "text/event-stream", body: `data: ${JSON.stringify(state)}\n\n` }));
  await page.reload();
  await expect(page.locator("#shell")).toBeVisible();
  await expect(page.getByRole("heading", { name: "Récupérer ton ambiance" })).toHaveCount(0);
  state.runner.running = false;
  state.runner.cleanupPending = true;
  await page.reload();
  await expect(page.getByRole("heading", { name: "Récupérer ton ambiance" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Restaurer les lampes disponibles" })).toBeEnabled();
  // This fixture closes SSE after its single state. The global status must
  // report the lost connection while keeping the known recovery actionable.
  await expect(page.locator("#mode-status")).toHaveText("État inconnu");
  await expect(page.locator("#stop")).toBeEnabled();
  await expect(page.locator("#stop")).toHaveText("Réessayer l’arrêt");
});
