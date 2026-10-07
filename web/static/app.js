// --- DOM refs ---
const bridgeStatus = document.getElementById("bridgeStatus");
const bridgeHint   = document.getElementById("bridgeHint");
const bridgesPanel = document.getElementById("bridgesPanel");
const gridBridges  = document.getElementById("gridBridges");
const refreshBridges = document.getElementById("refreshBridges");

const linkedBridgePanel = document.getElementById("linkedBridgePanel");
const linkedBridgeCard  = document.getElementById("linkedBridgeCard");

const stMode   = document.getElementById("stMode");
const stFlag   = document.getElementById("stFlag");
const stStarted= document.getElementById("stStarted");
const stBase   = document.getElementById("stBase");
const stOff    = document.getElementById("stOff");
const logEl    = document.getElementById("log");
const flagHistory = document.getElementById("flagHistory");
const flagHistoryStatus = document.getElementById("flagHistoryStatus");
const flagHistoryRefresh = document.getElementById("flagHistoryRefresh");
const stFeed = document.getElementById("stFeed");
const stSession = document.getElementById("stSession");
const stLap = document.getElementById("stLap");
const stFeedTime = document.getElementById("stFeedTime");
const replaySession = document.getElementById("replaySession");
const replaySpeed = document.getElementById("replaySpeed");
const replayRefresh = document.getElementById("replayRefresh");
const replayStart = document.getElementById("replayStart");
const replayResult = document.getElementById("replayResult");
const replayDescription = document.getElementById("replayDescription");
const replaySpeedHelp = document.getElementById("replaySpeedHelp");

// Selection UI
const segBtns = document.querySelectorAll(".seg-btn");
const gridSingle = document.getElementById("gridSingle");
const gridMulti  = document.getElementById("gridMulti");
const gridLights = document.getElementById("gridLights");
const toolbarSingle = document.getElementById("toolbar-single");
const toolbarMulti  = document.getElementById("toolbar-multi");
const toolbarLights = document.getElementById("toolbar-lights");
const panelAll = document.getElementById("panelAll");

const searchSingle = document.getElementById("searchSingle");
const searchMulti  = document.getElementById("searchMulti");
const multiCount   = document.getElementById("multiCount");
const multiClear   = document.getElementById("multiClear");

const searchLights = document.getElementById("searchLights");
const onlyOn       = document.getElementById("onlyOn");
const lightsCount  = document.getElementById("lightsCount");
const lightsClear  = document.getElementById("lightsClear");

const saveSelBtn   = document.getElementById("saveSelection");
const selPreview   = document.getElementById("selPreview");
const saveStatus   = document.getElementById("saveStatus");

// Controls
const startLive = document.getElementById("startLive");
const startTest = document.getElementById("startTest");
const stopBtn   = document.getElementById("stopBtn");
const gapInput  = document.getElementById("gapInput");
const runStatus = document.getElementById("runStatus");

// Sync / Offset
const offsetBadge = document.getElementById("offsetBadge");
const syncQuality = document.getElementById("syncQuality");
const offMinus025 = document.getElementById("offMinus025");
const offMinus010 = document.getElementById("offMinus010");
const offReset    = document.getElementById("offReset");
const offPlus010  = document.getElementById("offPlus010");
const offPlus025  = document.getElementById("offPlus025");
const offInput    = document.getElementById("offInput");
const offSetBtn   = document.getElementById("offSetBtn");

// Calibration
const calStart  = document.getElementById("calStart");
const calStop   = document.getElementById("calStop");
const markSeen  = document.getElementById("markSeen");
const calState  = document.getElementById("calState");
const tvClockInput = document.getElementById("tvClockInput");
const clockCompare = document.getElementById("clockCompare");
const clockApply = document.getElementById("clockApply");
const clockResult = document.getElementById("clockResult");
const activeEffect = document.getElementById("activeEffect");
const previewStart = document.getElementById("previewStart");
const toast = document.createElement("div");
toast.className = "toast";
toast.setAttribute("role", "status");
toast.setAttribute("aria-live", "polite");
document.body.appendChild(toast);
let toastTimer;
function showToast(message, error=false){
  toast.textContent = message;
  toast.classList.toggle("error", error);
  toast.classList.add("visible");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(()=>toast.classList.remove("visible"), 5000);
}

// --- state ---
let INVENTORY = { groups: {}, lights: {} };
let MODE = "single"; // single | multi | lights | all
let selection = { group_id: null, group_ids: [], light_ids: [] };
let confCache = null;
let isRunning = false;
let starting = false;
let stopping = false;
let bridgesCache = [];
const linkingState = new Map(); // ip -> {inflight:boolean, tries:number}

// --- utils ---
function addLog(msg, cls){
  const div = document.createElement("div");
  div.className = "line" + (cls?` ${cls}`:"");
  div.textContent = msg;
  logEl.appendChild(div);
  logEl.scrollTop = logEl.scrollHeight;
  if (cls === "err") showToast(msg, true);
}
function fmtTime(ts){
  if (!ts) return "—";
  const d = new Date(ts*1000);
  return d.toLocaleString();
}
function fmtReceivedTime(ts){
  const date = new Date(ts * 1000);
  if (!Number.isFinite(date.getTime())) return "heure inconnue";
  return `${date.toLocaleDateString("fr-BE")} ${date.toLocaleTimeString("fr-BE", {hour12:false})}.${String(date.getMilliseconds()).padStart(3, "0")}`;
}
let flagHistorySignature = null;
let flagHistoryLoading = false;
async function refreshFlagHistory(){
  if (flagHistoryLoading) return;
  flagHistoryLoading = true;
  flagHistoryRefresh.disabled = true;
  try{
    const response = await fetch("/api/journal/flags?limit=30", {cache:"no-store"});
    if (!response.ok) throw new Error(response.status === 404 ? "Redémarre make web pour charger le nouvel historique." : "Lecture du journal impossible.");
    const data = await response.json();
    const signature = data.flags.map(item => item.id).join(",");
    if (signature !== flagHistorySignature){
      flagHistory.replaceChildren();
      for (const item of data.flags){
        const entry = document.createElement("div");
        entry.className = "flag-entry";
        const title = document.createElement("div");
        title.className = "flag-entry-title";
        const flag = document.createElement("strong");
        flag.textContent = item.flag;
        const received = document.createElement("span");
        received.className = "flag-entry-time";
        received.textContent = `Réception : ${fmtReceivedTime(item.received_at)}`;
        title.append(flag, received);
        const session = document.createElement("div");
        session.className = "flag-entry-meta";
        session.textContent = item.session_name || (item.session_key ? `Séance ${item.session_key}` : "Séance inconnue");
        entry.append(title, session);
        if (item.source_utc){
          const source = document.createElement("div");
          source.className = "flag-entry-meta";
          source.textContent = `Heure F1 : ${item.source_utc.replace("T", " ").replace(/Z$/, "")} UTC`;
          entry.appendChild(source);
        }
        flagHistory.appendChild(entry);
      }
      flagHistory.scrollTop = 0;
      flagHistorySignature = signature;
    }
    flagHistoryStatus.textContent = data.flags.length ? `${data.flags.length} derniers drapeaux reçus sur ce Mac.` : "Aucun drapeau reçu depuis le démarrage du journal local.";
  }catch(error){
    flagHistoryStatus.textContent = String(error.message || error);
  }finally{
    flagHistoryLoading = false;
    flagHistoryRefresh.disabled = false;
  }
}
flagHistoryRefresh.onclick = refreshFlagHistory;
refreshFlagHistory();
setInterval(refreshFlagHistory, 5000);
function updateFeedStatus(feed){
  if (!feed) return;
  stFeed.textContent = feed.connected ? "connecté" : (feed.last_error ? `déconnecté (${feed.last_error})` : "connexion…");
  document.getElementById("feedDot").className = `status-dot ${feed.connected ? "online" : (feed.last_error ? "error" : "")}`;
  stSession.textContent = [feed.session_name, feed.session_status].filter(Boolean).join(" — ") || "—";
  stLap.textContent = feed.current_lap == null ? "—" : `${feed.current_lap}/${feed.total_laps || "?"}`;
  stFeedTime.textContent = fmtTime(feed.last_data_at);
}
function setBadgeOffset(val){
  offsetBadge.textContent = (val!=null && !isNaN(val)) ? `${Number(val).toFixed(2)} s` : "—";
  stOff.textContent = offsetBadge.textContent;
}
function updateSelPreview(sel){
  const directTargets = document.getElementById("directTargets");
  const selectedGroups = (sel.group_ids || []).map(id=>INVENTORY.groups?.[String(id)]?.name || `Zone ${id}`);
  const selectedLights = (sel.light_ids || []).map(id=>INVENTORY.lights?.[String(id)]?.name || `Lampe ${id}`);
  directTargets.textContent = sel.group_id === 0 ? "Toutes les lampes" :
    sel.group_id != null ? (INVENTORY.groups?.[String(sel.group_id)]?.name || `Zone ${sel.group_id}`) :
    selectedGroups.length ? selectedGroups.join(", ") :
    selectedLights.length ? selectedLights.join(", ") : "Aucune sélection";
  selPreview.textContent = `Sélection : ${directTargets.textContent}`;
}
function saveMsgOk(){
  saveStatus.textContent = "✓ Enregistré";
  saveStatus.classList.add("show");
  setTimeout(()=> saveStatus.classList.remove("show"), 1600);
}
function saveMsgClear(){
  saveStatus.textContent = "";
  saveStatus.classList.remove("show");
}
function clearGrid(el){ while(el.firstChild) el.removeChild(el.firstChild); }

// --- BRIDGE LINKED CARD ---
// Tente de récupérer les infos du bridge lié (name/id) via la discovery si possible
async function findBridgeMetaByIp(ip){
  if (!ip) return null;
  // 1) si on a déjà une cache de bridges, on tente
  let hit = (bridgesCache||[]).find(b => (b && (b.ip === ip)));
  if (hit) return hit;
  // 2) relance une découverte
  try{
    const r = await fetch("/api/bridge/discover_all");
    const j = await r.json();
    bridgesCache = (j && j.bridges) || [];
    hit = (bridgesCache||[]).find(b => (b && (b.ip === ip)));
    return hit || { ip };
  }catch(_){
    return { ip };
  }
}

function renderLinkedBridgeCard(meta){
  clearGrid(linkedBridgeCard);

  const card = document.createElement("div");
  card.className = "bridge-card";

  const title = document.createElement("div");
  title.className = "title";
  title.textContent = meta?.name || `Hue Bridge ${meta?.ip || ""}`;

  const metaEl = document.createElement("div");
  metaEl.className = "meta";
  const idPart = meta?.id ? `#${meta.id} — ` : "";
  metaEl.textContent = `${idPart}${meta?.ip || "—"}`;

  const row = document.createElement("div");
  row.className = "row";

  const unlinkBtnInCard = document.createElement("button");
  unlinkBtnInCard.className = "btn danger";
  unlinkBtnInCard.textContent = "Délier";
  unlinkBtnInCard.onclick = onUnlinkBridge;

  row.appendChild(unlinkBtnInCard);

  card.appendChild(title);
  card.appendChild(metaEl);
  card.appendChild(row);

  linkedBridgeCard.appendChild(card);
}

function showLinkedBridgeUI(show){
  if (show){
    linkedBridgePanel.classList.remove("hidden");
    bridgesPanel.classList.add("hidden");
  }else{
    linkedBridgePanel.classList.add("hidden");
    bridgesPanel.classList.remove("hidden");
  }
}

// --- bridge link status ---
async function setBridgeStatus(connected){
  document.getElementById("bridgeDot").className = `status-dot ${connected ? "online" : "error"}`;
  if (connected){
    bridgeStatus.textContent = "Connecté";
    bridgeStatus.classList.remove("not-connected");
    bridgeStatus.classList.add("connected");
    if (bridgeHint) bridgeHint.style.display = "none";

    // essaie d'afficher une jolie carte avec nom/id/ip
    const ip = (confCache && confCache.bridge_ip) || null;
    const meta = await findBridgeMetaByIp(ip);
    renderLinkedBridgeCard(meta || { ip });
    showLinkedBridgeUI(true);
  }else{
    bridgeStatus.textContent = "Non connecté";
    bridgeStatus.classList.remove("connected");
    bridgeStatus.classList.add("not-connected");
    if (bridgeHint) bridgeHint.style.display = "";

    showLinkedBridgeUI(false);
  }
}
// --- run status UI ---
function setRunStatus(text, cls){
  runStatus.textContent = text;
  runStatus.className = `run-status ${cls||"idle"}`;
}
function updateRunUI({running, mode, pendingStart=false, pendingStop=false, gap=null}){
  isRunning = !!running;
  starting = !!pendingStart;
  stopping = !!pendingStop;

  if (pendingStart){
    startLive.disabled = true;
    startTest.disabled = true;
    replayStart.disabled = true;
    previewStart.disabled = true;
    stopBtn.disabled = true;
    setRunStatus("Démarrage…", "pending");
    return;
  }
  if (pendingStop){
    startLive.disabled = true;
    startTest.disabled = true;
    replayStart.disabled = true;
    previewStart.disabled = true;
    stopBtn.disabled = true;
    setRunStatus("Arrêt…", "stopping");
    return;
  }

  if (isRunning){
    startLive.disabled = true;
    startTest.disabled = true;
    replayStart.disabled = true;
    previewStart.disabled = true;
    stopBtn.disabled = false;
    if (mode === "live"){
      setRunStatus("Direct en cours", "live");
    } else if (mode === "test"){
      setRunStatus(`Test en cours${gap ? " ("+gap+" s)" : ""}`, "test");
    } else {
      setRunStatus(mode === "preview" ? "Aperçu en cours" : mode === "replay" ? "Replay en cours" : "Démarrage…", "live");
    }
  }else{
    startLive.disabled = false;
    startTest.disabled = false;
    replayStart.disabled = !replaySession.value;
    previewStart.disabled = false;
    stopBtn.disabled = true;
    setRunStatus("À l’arrêt", "idle");
  }
}

// --- inventory load ---
async function loadInventory(){
  if (!confCache?.username || !confCache?.bridge_ip){
    INVENTORY = {groups:{}, lights:{}};
    await setBridgeStatus(false);
    return false;
  }
  try{
    const gr = await fetch("/api/hue/groups");
    const groups = await gr.json();
    const lr = await fetch("/api/hue/lights");
    const lights = await lr.json();
    if (!gr.ok || !lr.ok || groups.ok === false || lights.ok === false){
      throw new Error(groups.error || lights.error || "Pont Hue inaccessible");
    }
    INVENTORY.groups = groups || {};
    INVENTORY.lights = lights || {};
    updateSelPreview(selection);
    if (confCache && confCache.bridge_ip && confCache.username) await setBridgeStatus(true);
    return true;
  }catch(e){
    INVENTORY = { groups: {}, lights: {} };
    setBridgeStatus(false);
    addLog(`Inventaire Hue indisponible — ${e}`, "err");
    return false;
  }
}

// --- bridge discovery grid ---
function renderBridges(){
  clearGrid(gridBridges);
  if (!Array.isArray(bridgesCache) || bridgesCache.length === 0){
    const empty = document.createElement("div");
    empty.className = "muted";
    empty.textContent = "Aucun bridge détecté pour l’instant.";
    gridBridges.appendChild(empty);
    return;
  }
  bridgesCache.forEach(b=>{
    const card = document.createElement("div");
    card.className = "bridge-card clickable";
    card.setAttribute("role", "button");
    card.tabIndex = 0;
    const title = document.createElement("div");
    title.className = "title";
    title.textContent = b.name || `Hue Bridge ${b.ip}`;
    const meta = document.createElement("div");
    meta.className = "meta";
    meta.textContent = `${b.id ? `#${b.id} — ` : ""}${b.ip}`;
    const msg = document.createElement("div");
    msg.className = "bridge-link-msg muted";
    msg.textContent = "Cliquer la carte puis appuyer sur le bouton du bridge.";

    card.onclick = ()=> startLinkFlow(b.ip, msg, card);
    card.onkeydown = event=>{
      if (event.key === "Enter" || event.key === " ") { event.preventDefault(); card.click(); }
    };

    card.appendChild(title);
    card.appendChild(meta);
    card.appendChild(msg);
    gridBridges.appendChild(card);
  });
}

async function discoverBridges(){
  try{
    const r = await fetch("/api/bridge/discover_all");
    const j = await r.json();
    bridgesCache = (j && j.bridges) || [];
    renderBridges();
  }catch(e){
    bridgesCache = [];
    renderBridges();
    document.getElementById("bridgeFeedback").textContent = "Recherche réseau indisponible. Vous pouvez saisir l’adresse IP locale du pont ci-dessous.";
  }
}

document.getElementById("manualBridgeLink").addEventListener("click", async ()=>{
  const ip = document.getElementById("manualBridgeIp").value.trim();
  const feedback = document.getElementById("bridgeFeedback");
  if (!ip){
    feedback.textContent = "Saisissez l’adresse IP locale du pont Hue.";
    feedback.className = "bridge-feedback error";
    return;
  }
  feedback.className = "bridge-feedback";
  await startLinkFlow(ip, feedback, document.getElementById("manualBridgePanel"));
});

// --- auto link flow with gentle retry (anti rate-limit) ---
async function tryLinkOnce(ip){
  const r = await fetch("/api/bridge/link", {
    method:"POST",
    headers:{'Content-Type':'application/json'},
    body: JSON.stringify({bridge_ip: ip})
  });
  return r.json();
}
async function startLinkFlow(ip, msgEl, cardEl){
  let st = linkingState.get(ip) || { inflight:false, tries:0 };
  if (st.inflight) return; // déjà en cours
  st.inflight = true; st.tries = 0;
  linkingState.set(ip, st);

  cardEl.classList.add("busy");
  const MAX_S = 30;
  const STEP_MS = 1200;

  let ok = false;
  let lastErr = null;

  const updateMsg = (t)=> {
    const sLeft = Math.max(0, Math.ceil((MAX_S*1000 - t)/1000));
    msgEl.textContent = `En attente du bouton du bridge… (${sLeft}s)`;
  };

  const t0 = performance.now();
  while ((performance.now() - t0) < MAX_S*1000){
    st.tries++;
    updateMsg(performance.now() - t0);
    try{
      const j = await tryLinkOnce(ip);
      if (j && j.ok){
        ok = true;

        // recharge la config pour connaître bridge_ip, offset, etc.
        try{
          confCache = await (await fetch("/api/config")).json();
        }catch(_){}

        await setBridgeStatus(true);
        addLog(`Bridge lié: ${ip}`, "");
        await loadInventory();
        setMode(MODE);
        msgEl.textContent = "✓ Lié !";
        break;
      }else{
        lastErr = (j && j.error) || "Erreur de liaison";
        if (!/link button|bouton|press/i.test(lastErr)) break;
      }
    }catch(e){
      lastErr = String(e);
    }
    await new Promise(res=> setTimeout(res, STEP_MS));
  }

  if (!ok){
    msgEl.textContent = lastErr ? `Échec: ${lastErr}` : "Échec: délai dépassé";
    addLog(msgEl.textContent, "err");
  }

  cardEl.classList.remove("busy");
  st.inflight = false;
  linkingState.set(ip, st);
}

// --- rendering lights/groups ---
function renderSingle(filter = "") {
  clearGrid(gridSingle);

  const current = (selection && selection.group_id !== undefined) ? selection.group_id : null;

  const rows = [];
  const groups = INVENTORY.groups || {};
  Object.keys(groups).forEach(id => {
    const g = groups[id];
    if (!g || !g.type) return;
    if (g.type !== "Room" && g.type !== "Zone") return;
    const name = g.name || "(sans nom)";
    if (filter && !name.toLowerCase().includes(filter.toLowerCase())) return;
    rows.push({ id: parseInt(id, 10), name, type: g.type, n: (g.lights || []).length });
  });

  rows.sort((a, b) => a.name.localeCompare(b.name));

  rows.forEach(r => {
    const card = document.createElement("div");
    const isSelected = (current !== null && current !== undefined && Number(current) === r.id);
    card.className = "card-item" + (isSelected ? " selected" : "");
    card.setAttribute("role", "button");
    card.setAttribute("aria-pressed", String(isSelected));
    card.tabIndex = 0;

    const title = document.createElement("div");
    title.className = "title";
    title.textContent = r.name;

    const meta = document.createElement("div");
    meta.className = "meta";
    meta.textContent = `${r.type} — ${r.n} lampes`;

    card.appendChild(title);
    card.appendChild(meta);

    card.onclick = () => {
      selection = { group_id: r.id, group_ids: [], light_ids: [] };
      saveSelBtn.disabled = false;
      saveMsgClear();
      updateSelPreview(selection);
      document.querySelectorAll("#gridSingle .card-item").forEach(el => el.classList.remove("selected"));
      card.classList.add("selected");
      card.setAttribute("aria-pressed", "true");
    };
    card.onkeydown = event=>{
      if (event.key === "Enter" || event.key === " ") { event.preventDefault(); card.click(); }
    };

    gridSingle.appendChild(card);
  });
}


function renderMulti(filter=""){
  clearGrid(gridMulti);
  let selected = new Set(selection.group_ids||[]);
  const rows = [];
  Object.keys(INVENTORY.groups||{}).forEach(id=>{
    const g = INVENTORY.groups[id];
    if (!g || !g.type) return;
    if (g.type!=="Room" && g.type!=="Zone") return;
    const name = g.name || "(sans nom)";
    if (filter && !name.toLowerCase().includes(filter.toLowerCase())) return;
    rows.push({id: parseInt(id,10), name, type:g.type, n:(g.lights||[]).length});
  });
  rows.sort((a,b)=> a.name.localeCompare(b.name));
  rows.forEach(r=>{
    const card = document.createElement("div");
    card.className = "card-item" + (selected.has(r.id)?" selected":"");
    card.setAttribute("role", "button");
    card.setAttribute("aria-pressed", String(selected.has(r.id)));
    card.tabIndex = 0;
    const title = document.createElement("div");
    title.className = "title";
    title.textContent = `${r.name}`;
    const meta = document.createElement("div");
    meta.className = "meta";
    meta.textContent = `${r.type} — ${r.n} lampes`;
    card.appendChild(title); card.appendChild(meta);
    card.onclick = ()=>{
      if (selected.has(r.id)) selected.delete(r.id); else selected.add(r.id);
      selection = { group_id: null, group_ids: Array.from(selected).sort((a,b)=>a-b), light_ids: [] };
      multiCount.textContent = `${selected.size} sélection`;
      saveSelBtn.disabled = selected.size===0;
      saveMsgClear();
      updateSelPreview(selection);
      card.classList.toggle("selected");
      card.setAttribute("aria-pressed", String(selected.has(r.id)));
    };
    card.onkeydown = event=>{
      if (event.key === "Enter" || event.key === " ") { event.preventDefault(); card.click(); }
    };
    gridMulti.appendChild(card);
  });
  multiCount.textContent = `${selected.size} sélection`;
}

function renderLights(filter="", onlyOnChecked=false){
  clearGrid(gridLights);
  const rows = [];
  Object.keys(INVENTORY.lights||{}).forEach(id=>{
    const L = INVENTORY.lights[id] || {};
    const name = L.name || "(sans nom)";
    const prod = L.productname || "";
    const isOn = !!(L.state && L.state.on);
    if (onlyOnChecked && !isOn) return;
    if (filter && !(name.toLowerCase().includes(filter.toLowerCase()) || prod.toLowerCase().includes(filter.toLowerCase()))) return;
    rows.push({id: parseInt(id,10), name, prod, on:isOn});
  });
  rows.sort((a,b)=> a.id-b.id);

  const selected = new Set(selection.light_ids||[]);
  rows.forEach(r=>{
    const card = document.createElement("div");
    card.className = "card-item" + (selected.has(r.id)?" selected":"");
    card.setAttribute("role", "button");
    card.setAttribute("aria-pressed", String(selected.has(r.id)));
    card.tabIndex = 0;
    const title = document.createElement("div");
    title.className = "title";
    title.textContent = `${r.name}`;
    const meta = document.createElement("div");
    meta.className = "meta";
    meta.textContent = `${r.prod} — [${r.on?'on':'off'}] (id ${r.id})`;
    card.appendChild(title); card.appendChild(meta);
    card.onclick = ()=>{
      if (selected.has(r.id)) selected.delete(r.id); else selected.add(r.id);
      selection = { group_id: null, group_ids: [], light_ids: Array.from(selected).sort((a,b)=>a-b) };
      lightsCount.textContent = `${selected.size} sélection`;
      saveSelBtn.disabled = selected.size===0;
      saveMsgClear();
      updateSelPreview(selection);
      card.classList.toggle("selected");
      card.setAttribute("aria-pressed", String(selected.has(r.id)));
    };
    card.onkeydown = event=>{
      if (event.key === "Enter" || event.key === " ") { event.preventDefault(); card.click(); }
    };
    gridLights.appendChild(card);
  });
  lightsCount.textContent = `${selected.size} sélection`;
}

function setMode(mode){
  MODE = mode;
  segBtns.forEach(b=> b.classList.toggle("active", b.dataset.mode===mode));

  toolbarSingle.classList.toggle("show", mode==="single");
  toolbarMulti.classList.toggle("show", mode==="multi");
  toolbarLights.classList.toggle("show", mode==="lights");

  gridSingle.classList.toggle("show", mode==="single");
  gridMulti.classList.toggle("show", mode==="multi");
  gridLights.classList.toggle("show", mode==="lights");
  panelAll.classList.toggle("show", mode==="all");

  if (mode==="single") renderSingle(searchSingle.value||"");
  if (mode==="multi")  renderMulti(searchMulti.value||"");
  if (mode==="lights") renderLights(searchLights.value||"", !!onlyOn.checked);

  if (mode==="all"){
    selection = { group_id: 0, group_ids: [], light_ids: [] };
    updateSelPreview(selection);
    saveMsgClear();
    saveSelBtn.disabled = false;
  }
}

// --- init ---
(async()=>{
  try{
    confCache = await (await fetch("/api/config")).json();
    setBadgeOffset((confCache.sync && confCache.sync.offset_seconds) || 0);

    // Si déjà lié : affiche la carte liée
    const connected = !!(confCache.username && confCache.bridge_ip);
    await setBridgeStatus(connected);

    if (!connected){
      await discoverBridges();
    }

    const sel = await (await fetch("/api/setup/selection")).json();
    selection = {
      group_id: sel.group_id ?? null,
      group_ids: sel.group_ids || [],
      light_ids: sel.light_ids || [],
    };
    updateSelPreview(selection);

    const st = await (await fetch("/api/status")).json();
    updateRunUI({running: st.running, mode: st.mode, gap: st.gap});
    setActiveEffect(st.active_pattern);
    stMode.textContent    = st.mode || (st.running ? "running" : "idle");
    stFlag.textContent    = st.last_flag || "—";
    stStarted.textContent = st.started_at ? fmtTime(st.started_at) : "—";
  }catch(e){}

  await loadInventory();
  setMode("single");
})();

// --- Bridges refresh ---
if (refreshBridges){
  refreshBridges.onclick = async ()=>{
    refreshBridges.disabled = true;
    await discoverBridges();
    refreshBridges.disabled = false;
  };
}

// --- segmented UI events ---
segBtns.forEach(btn=>{
  btn.addEventListener("click", ()=>{
    setMode(btn.dataset.mode);
  });
});

searchSingle.addEventListener("input", ()=> renderSingle(searchSingle.value||""));
searchMulti.addEventListener("input",  ()=> renderMulti(searchMulti.value||""));
multiClear.addEventListener("click", ()=>{
  selection.group_ids = [];
  renderMulti(searchMulti.value||"");
  saveSelBtn.disabled = true;
  saveMsgClear();
  updateSelPreview(selection);
});
searchLights.addEventListener("input", ()=> renderLights(searchLights.value||"", !!onlyOn.checked));
onlyOn.addEventListener("change",     ()=> renderLights(searchLights.value||"", !!onlyOn.checked));
lightsClear.addEventListener("click", ()=>{
  selection.light_ids = [];
  renderLights(searchLights.value||"", !!onlyOn.checked);
  saveSelBtn.disabled = true;
  saveMsgClear();
  updateSelPreview(selection);
});

// --- save selection ---
saveSelBtn.addEventListener("click", async ()=>{
  let payload = null;
  if (selection.group_id===0){
    payload = { group_id: 0 };
  } else if (selection.group_id !== null){
    payload = { group_id: selection.group_id };
  } else if ((selection.group_ids||[]).length){
    payload = { group_ids: selection.group_ids };
  } else if ((selection.light_ids||[]).length){
    payload = { light_ids: selection.light_ids };
  } else {
    addLog("Rien à enregistrer.", "err");
    return;
  }

  saveMsgClear();

  try{
    const r = await fetch("/api/setup/targets", {
      method:"POST",
      headers:{'Content-Type':'application/json'},
      body: JSON.stringify(payload)
    });
    const j = await r.json();
    if (!j.ok){
      addLog(j.error||"Erreur d’enregistrement de la sélection", "err");
      return;
    }
    selection = j.selection;
    updateSelPreview(selection);
    addLog("Sélection enregistrée ✅", "");
    saveMsgOk();
  }catch(e){
    addLog("Erreur réseau pendant l’enregistrement de la sélection", "err");
  }
});

// --- unlink (bouton intégré dans la carte liée) ---
async function onUnlinkBridge(){
  const btn = this; // bouton dans la carte
  btn.disabled = true;
  try{
    const j = await (await fetch("/api/bridge/unlink", {method:"POST"})).json();
    if (!j.ok){
      addLog(j.error || "Erreur lors du déliage", "err");
      return;
    }
    // reset UI
    await setBridgeStatus(false);

    INVENTORY = {groups:{}, lights:{}};
    clearGrid(gridSingle); clearGrid(gridMulti); clearGrid(gridLights);
    selection = { group_id: null, group_ids: [], light_ids: [] };
    updateSelPreview(selection);
    saveSelBtn.disabled = true;

    updateRunUI({running:false});
    if (confCache){ confCache.bridge_ip = null; confCache.username = null; }
    stMode.textContent = "idle";
    stFlag.textContent = "—";
    stStarted.textContent = "—";
    stBase.textContent = "—";
    setBadgeOffset(getCurrentOffset());

    await discoverBridges();
    addLog("Bridge délié. Choisissez un bridge dans la liste pour relier.", "");
  }catch(e){
    addLog("Erreur réseau pendant le déliage.", "err");
  }finally{
    btn.disabled = false;
  }
}

// --- start/stop ---
let replayCatalog = {historical: [], local: []};
function updateReplayDescription(){
  const [kind, id] = replaySession.value.split(":", 2);
  if (kind === "history"){
    const scenario = replayCatalog.historical.find(item => item.id === id);
    replayDescription.textContent = scenario ? `${scenario.description}. Source : archive Formula 1.` : "";
  }else if (kind === "local"){
    replayDescription.textContent = "Enregistrement sur ce Mac, disponible hors connexion.";
  }else{
    replayDescription.textContent = "";
  }
  replayStart.disabled = isRunning || !replaySession.value;
}
function updateReplaySpeedHelp(){
  const speed = Number(replaySpeed.value);
  const seconds = 600 / speed;
  const duration = seconds >= 60 ? `${seconds / 60} min` : `${seconds} s`;
  replaySpeedHelp.textContent = `Exemple : 10 min entre deux drapeaux deviennent ${duration} à ×${speed}. La durée de chaque effet lumineux reste identique.`;
}
async function refreshReplaySessions(){
  replayRefresh.disabled = true;
  try{
    const previous = replaySession.value;
    const response = await fetch("/api/replay/scenarios");
    if (!response.ok) throw new Error("Liste des séances indisponible");
    replayCatalog = await response.json();
    replaySession.replaceChildren();
    const historicalGroup = document.createElement("optgroup");
    historicalGroup.label = "Séances passées · archive Formula 1";
    for (const scenario of replayCatalog.historical){
      const option = document.createElement("option");
      option.value = `history:${scenario.id}`;
      option.textContent = scenario.label;
      historicalGroup.appendChild(option);
    }
    replaySession.appendChild(historicalGroup);
    const localGroup = document.createElement("optgroup");
    localGroup.label = "Séances enregistrées sur ce Mac";
    for (const session of replayCatalog.local){
      if (!session.flag_count) continue;
      const option = document.createElement("option");
      option.value = `local:${session.session_key}`;
      option.textContent = `${session.session_name || "Séance"} — ${session.flag_count} drapeaux (${new Date(session.started_at * 1000).toLocaleDateString()})`;
      localGroup.appendChild(option);
    }
    if (localGroup.children.length) replaySession.appendChild(localGroup);
    if (previous && [...replaySession.options].some(option => option.value === previous)) replaySession.value = previous;
    updateReplayDescription();
    replayResult.textContent = localGroup.children.length
      ? "Choisis une séance et une vitesse de replay."
      : "Aucun enregistrement local pour l’instant ; les séances passées sont disponibles ci-dessus.";
    replayResult.className = "bridge-feedback";
  }catch(error){
    replayResult.textContent = String(error.message || error);
    replayResult.className = "bridge-feedback error";
  }finally{
    replayRefresh.disabled = false;
  }
}
replayRefresh.onclick = refreshReplaySessions;
replaySession.onchange = updateReplayDescription;
replaySpeed.onchange = updateReplaySpeedHelp;
updateReplaySpeedHelp();
replayStart.onclick = async ()=>{
  if (isRunning || !replaySession.value) return;
  replayStart.disabled = true;
  const speed = Number(replaySpeed.value);
  const [kind, id] = replaySession.value.split(":", 2);
  try{
    const response = await fetch("/api/replay/start", {
      method: "POST", headers: {"Content-Type": "application/json"},
      body: JSON.stringify(kind === "history" ? {scenario_id: id, speed} : {session_key: id, speed})
    });
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || "Replay impossible");
    updateRunUI({running:false, pendingStart:true});
    replayResult.textContent = `Replay démarré : temps entre les drapeaux accéléré ×${speed}.`;
    replayResult.className = "bridge-feedback success";
  }catch(error){
    replayResult.textContent = String(error.message || error);
    replayResult.className = "bridge-feedback error";
    replayStart.disabled = false;
  }
};
refreshReplaySessions();

startLive.onclick = async ()=>{
  if (starting || isRunning) return;
  updateRunUI({running:false, pendingStart:true});
  try{
    const response = await fetch("/api/start", {method:"POST", headers:{'Content-Type':'application/json'}, body: JSON.stringify({mode:"live"})});
    if (!response.ok) throw new Error((await response.json()).error || "Démarrage impossible");
  }catch(e){
    addLog(`Démarrage live impossible : ${e.message || e}`, "err");
    updateRunUI({running:false}); // revert
  }
};
startTest.onclick = async ()=>{
  if (starting || isRunning) return;
  const gap = parseFloat(gapInput.value || "1.0");
  updateRunUI({running:false, pendingStart:true});
  try{
    const response = await fetch("/api/start", {method:"POST", headers:{'Content-Type':'application/json'}, body: JSON.stringify({mode:"test", gap})});
    if (!response.ok) throw new Error((await response.json()).error || "Démarrage impossible");
  }catch(e){
    addLog(`Démarrage test impossible : ${e.message || e}`, "err");
    updateRunUI({running:false});
  }
};

stopBtn.onclick = async ()=>{
  if (stopping || !isRunning) return;
  updateRunUI({running:true, pendingStop:true});
  try{
    const response = await fetch("/api/stop", {method:"POST"});
    const data = await response.json();
    if (!response.ok || !data.ok) throw new Error(data.error || "Arrêt impossible");
    if (data.actually_stopped) updateRunUI({running:false});
    else {
      stopping = false;
      showToast("Arrêt demandé ; les lampes seront restaurées dès que le pont répond.");
      refreshFeedStatus();
    }
  }catch(e){
    addLog(`Erreur pendant l’arrêt : ${e.message || e}`, "err");
    updateRunUI({running:true});
  }
};

// --- offset quick buttons ---
function getCurrentOffset(){
  const v = (confCache && confCache.sync && confCache.sync.offset_seconds) || 0;
  return Number(v) || 0;
}
async function setOffset(val){
  try{
    const r = await fetch("/api/config", {method:"POST", headers:{'Content-Type':'application/json'}, body: JSON.stringify({sync: {offset_seconds: Number(val)}})});
    if (!r.ok) throw new Error((await r.json()).error || "Enregistrement impossible");
    confCache = confCache || {};
    confCache.sync = confCache.sync || {};
    confCache.sync.offset_seconds = Number(val);
    setBadgeOffset(val);
    addLog(`Offset enregistré: ${Number(val).toFixed(2)}s`, "");
    return true;
  }catch(e){
    addLog(`Offset non enregistré — ${e}`, "err");
    return false;
  }
}
offMinus025.onclick = ()=> setOffset(Math.max(0, getCurrentOffset() - 0.25));
offMinus010.onclick = ()=> setOffset(Math.max(0, getCurrentOffset() - 0.10));
offPlus010.onclick  = ()=> setOffset(getCurrentOffset() + 0.10);
offPlus025.onclick  = ()=> setOffset(getCurrentOffset() + 0.25);
offReset.onclick    = ()=> setOffset(0);
offSetBtn.onclick   = ()=>{
  const v = parseFloat(offInput.value || "0");
  if (isNaN(v) || v<0) { alert("Valeur invalide"); return; }
  setOffset(v);
};

// --- calibration (auto) ---
let calWaiting = false;
let calLastEventTs = null;
let calStream = null;

calStart.onclick = () => {
  if (calStream) calStream.close();
  calWaiting = true;
  calLastEventTs = null;
  calState.textContent = "État: attente message API…";
  addLog("Calibration: en attente du prochain message API (FLAG/SC/VSC…)", "");
  calStream = new EventSource("/api/sync/flag-stream");
  calStream.onmessage = event=>{
    const data = JSON.parse(event.data);
    if (data.type === "flag"){
      calLastEventTs = performance.now();
      calState.textContent = `État: ${data.flag} reçu — clique quand tu le vois à la TV`;
      addLog(`Calibration : ${data.flag} reçu de la F1. Clique quand tu le vois à la TV.`, "flag");
      calStream.close();
      calStream = null;
    }else if (data.type === "error"){
      calWaiting = false;
      calState.textContent = `État: ${data.message}`;
      addLog(`Calibration : ${data.message}`, "err");
      calStream.close();
      calStream = null;
    }
  };
  calStream.onerror = ()=>{
    if (!calStream) return;
    calWaiting = false;
    calState.textContent = "État: connexion F1 interrompue";
    addLog("Calibration : connexion F1 interrompue.", "err");
    calStream.close();
    calStream = null;
  };
};

calStop.onclick = () => {
  if (calStream){ calStream.close(); calStream = null; }
  calWaiting = false;
  calLastEventTs = null;
  calState.textContent = "État: idle";
  addLog("Calibration: annulée.", "");
};

markSeen.onclick = async () => {
  if (!calWaiting){
    addLog("Astuce: lance d'abord 'Attendre prochain message'.", "");
    return;
  }
  calWaiting = false;
  if (calStream){ calStream.close(); calStream = null; }

  if (calLastEventTs === null){
    calState.textContent = "État: idle";
    addLog("Calibration: aucun message reçu — offset non modifié.", "err");
    return;
  }

  const now = performance.now();
  const offsetSec = Math.max(0, (now - calLastEventTs) / 1000);
  if (await setOffset(offsetSec)){
    calState.textContent = "État: calibré (auto)";
    addLog(`Calibration auto: offset=${offsetSec.toFixed(2)}s`, "");
  }else{
    calState.textContent = "État: erreur d’enregistrement";
  }
};

// --- calibration par comparaison des chronos ---
let clockCandidate = null;
tvClockInput.addEventListener("input", ()=>{
  clockCandidate = null;
  clockApply.disabled = true;
  clockResult.textContent = "";
});

function formatSessionTime(seconds){
  const total = Math.max(0, Math.floor(seconds));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor(total / 60) % 60;
  const secs = String(total % 60).padStart(2, "0");
  return hours ? `${hours}:${String(minutes).padStart(2, "0")}:${secs}` : `${Math.floor(total / 60)}:${secs}`;
}

clockCompare.onclick = async ()=>{
  clockCandidate = null;
  clockApply.disabled = true;
  clockCompare.disabled = true;
  clockResult.className = "bridge-feedback";
  clockResult.textContent = "Lecture du chrono F1…";
  try{
    const r = await fetch("/api/sync/compare-clock", {
      method:"POST",
      headers:{'Content-Type':'application/json'},
      body: JSON.stringify({tv_remaining: tvClockInput.value.trim()})
    });
    if (r.status === 404) throw new Error("Redémarre make web pour activer la comparaison des chronos.");
    const result = await r.json();
    if (!r.ok) throw new Error(result.error || "Comparaison impossible");
    const estimate = result.estimated_offset_seconds;
    const session = result.session_name ? ` (${result.session_name})` : "";
    clockResult.textContent = `Au clic : TV ${result.tv_remaining}, API ${formatSessionTime(result.api_remaining_seconds)}${session}. Retard estimé : ${estimate} s (± 1 s).` +
      (result.can_apply ? " Vérifie la séance, puis enregistre si la valeur te convient." : " Valeur non applicable : les drapeaux ne peuvent être avancés avant leur réception.");
    clockResult.className = `bridge-feedback ${result.can_apply ? "success" : "error"}`;
    if (result.can_apply){
      clockCandidate = estimate;
      clockApply.disabled = false;
    }
  }catch(e){
    clockResult.textContent = String(e.message || e);
    clockResult.className = "bridge-feedback error";
  }finally{
    clockCompare.disabled = false;
  }
};

clockApply.onclick = async ()=>{
  if (clockCandidate === null) return;
  clockApply.disabled = true;
  if (await setOffset(clockCandidate)){
    clockResult.textContent += isRunning ? " Offset enregistré : redémarre Live pour l’appliquer." : " Offset enregistré.";
  }else{
    clockResult.textContent = "Enregistrement impossible. Réessaie.";
    clockResult.className = "bridge-feedback error";
    clockApply.disabled = false;
  }
};

function setupEventCalibration(kind, url, markerType, markerLabel){
  const watch = document.getElementById(`${kind}Watch`) || document.getElementById(`${kind}Start`);
  const cancel = document.getElementById(`${kind}Cancel`);
  const seen = document.getElementById(`${kind}Seen`);
  const apply = document.getElementById(`${kind}Apply`);
  const result = document.getElementById(`${kind}Result`);
  let stream = null;
  let receivedAt = null;
  let candidate = null;
  let label = markerLabel;

  function closeStream(){
    if (stream){ stream.close(); stream = null; }
  }
  function reset(){
    closeStream();
    receivedAt = null;
    candidate = null;
    watch.disabled = false;
    cancel.disabled = true;
    seen.disabled = true;
    apply.disabled = true;
  }
  watch.onclick = ()=>{
    reset();
    watch.disabled = true;
    cancel.disabled = false;
    result.className = "bridge-feedback";
    result.textContent = "Connexion au flux F1…";
    stream = new EventSource(url);
    stream.onmessage = event=>{
      const data = JSON.parse(event.data);
      if (data.type === "ready"){
        const lap = data.current_lap ? ` (tour ${data.current_lap}/${data.total_laps || "?"})` : "";
        result.textContent = `En attente du signal F1${lap}…`;
      }else if (data.type === markerType){
        receivedAt = performance.now();
        label = data.lap ? `tour ${data.lap}/${data.total_laps || "?"}` : markerLabel;
        result.textContent = `Signal API reçu : ${label}. Clique au moment où tu le vois à la TV.`;
        seen.disabled = false;
        closeStream();
      }else if (data.type === "error"){
        reset();
        result.textContent = data.message;
        result.className = "bridge-feedback error";
      }
    };
    stream.onerror = ()=>{
      if (!stream) return;
      reset();
      result.textContent = "Connexion F1 interrompue. Vérifie que make web est relancé et que la séance est active.";
      result.className = "bridge-feedback error";
    };
  };
  cancel.onclick = ()=>{
    reset();
    result.textContent = "Attente annulée.";
  };
  seen.onclick = ()=>{
    if (receivedAt === null) return;
    candidate = Math.round((performance.now() - receivedAt) / 10) / 100;
    seen.disabled = true;
    apply.disabled = false;
    cancel.disabled = true;
    watch.disabled = false;
    result.textContent = `${label} : retard TV estimé à ${candidate.toFixed(2)} s. Vérifie la valeur avant de l’enregistrer.`;
    result.className = "bridge-feedback success";
  };
  apply.onclick = async ()=>{
    if (candidate === null) return;
    apply.disabled = true;
    if (await setOffset(candidate)){
      result.textContent += isRunning ? " Offset enregistré : redémarre Live pour l’appliquer." : " Offset enregistré.";
    }else{
      apply.disabled = false;
      result.textContent = "Enregistrement impossible. Réessaie.";
      result.className = "bridge-feedback error";
    }
  };
}

setupEventCalibration("start", "/api/sync/start-stream", "start", "départ F1");
setupEventCalibration("lap", "/api/sync/lap-stream", "lap", "nouveau tour");

// --- SSE (temps réel) ---
async function refreshFeedStatus(){
  try{
    const status = await (await fetch("/api/status")).json();
    updateFeedStatus(status.feed);
    if (!starting && !stopping){
      updateRunUI({running:status.running,mode:status.mode,gap:status.gap});
      setActiveEffect(status.active_pattern);
      stMode.textContent = status.mode || "à l’arrêt";
      stFlag.textContent = status.last_flag || "—";
      stStarted.textContent = status.started_at ? fmtTime(status.started_at) : "—";
    }
  }catch(_){
    stFeed.textContent = "serveur inaccessible";
  }
}
setInterval(refreshFeedStatus, 5000);
try{
  const ev = new EventSource("/api/events");

  ev.onopen = async ()=>{
    try{
      const st = await (await fetch("/api/status")).json();
      updateRunUI({running: st.running, mode: st.mode, gap: st.gap});
      setActiveEffect(st.active_pattern);
      stMode.textContent    = st.mode || (st.running ? "running" : "idle");
      stFlag.textContent    = st.last_flag || "—";
      stStarted.textContent = st.started_at ? fmtTime(st.started_at) : "—";
      updateFeedStatus(st.feed);
    }catch(e){}
  };

  ev.onmessage = (e)=>{
    const data = JSON.parse(e.data);
    if (data.type === "running"){
      updateRunUI({running:true, mode:data.mode, gap:data.gap});
      stMode.textContent = data.mode || "running";
      stStarted.textContent = fmtTime(Math.floor(Date.now()/1000));
      addLog(`RUNNING: ${data.mode}`, "");
    } else if (data.type === "flag"){
      stFlag.textContent = data.flag;
      addLog(`FLAG: ${data.flag}`, "flag");
    } else if (data.type === "flag_skipped"){
      stFlag.textContent = data.flag;
      addLog(`${data.flag} masqué : état Hue restauré`, "");
    } else if (data.type === "effect"){
      setActiveEffect(data.pattern);
    } else if (data.type === "baseline"){
      stBase.textContent = `captured ${data.captured}`;
      addLog(`Baseline capturée (${data.captured} lampes)`, "");
    } else if (data.type === "baseline_restored"){
      stBase.textContent = `restored ${data.count} (tt=${data.tt})`;
      addLog(`Baseline restaurée (${data.count}, tt=${data.tt})`, "");
    } else if (data.type === "sync"){
      stOff.textContent = `${data.offset_seconds.toFixed(2)}s`;
      setBadgeOffset(data.offset_seconds);
      addLog(`Sync offset: ${data.offset_seconds}s`, "");
    } else if (data.type === "stopped"){
      updateRunUI({running:false});
      setActiveEffect(null);
      stMode.textContent = "idle";
      addLog(`STOPPED`, "");
    } else if (data.type === "error"){
      addLog(`ERROR: ${data.message}`, "err");
      starting = false;
      refreshFeedStatus();
    } else if (data.type === "sync_group"){
      addLog(`LightGroup sync id=${data.group_id} (lampes=${data.size})`, "");
    } else if (data.type === "status"){
      updateRunUI({running: !!data.mode, mode: data.mode, gap: data.gap});
      stMode.textContent    = data.mode || "idle";
      stFlag.textContent    = data.last_flag || "—";
      stStarted.textContent = data.started_at ? fmtTime(data.started_at) : "—";
      setActiveEffect(data.active_pattern);
    }
  };
}catch(e){
  addLog("SSE indisponible", "err");
}

function setActiveEffect(pattern){
  activeEffect.textContent = pattern ? (FLAG_LABELS[pattern] || pattern) : "Aucun effet actif";
  activeEffect.classList.toggle("has-effect", !!pattern);
  activeEffect.style.setProperty("--active-color", pattern ? (FLAG_COLORS[pattern] || "#999") : "transparent");
  const pill = document.getElementById("liveStatePill");
  pill.textContent = pattern ? "Effet actif" : "En attente";
  pill.classList.toggle("active", !!pattern);
  document.getElementById("liveHelper").textContent = pattern
    ? "L’effet prendra fin selon sa durée ou au prochain événement F1."
    : "Les lampes conservent leur état normal entre les effets.";
}

// Navigation locale : les URLs restent rechargeables grâce aux routes Flask.
const PAGES = new Set(["direct", "hue", "tests", "flags", "preferences"]);
function showPage(page){
  const selected = PAGES.has(page) ? page : "direct";
  document.querySelectorAll("[data-page-panel]").forEach(panel=>{
    panel.hidden = panel.dataset.pagePanel !== selected;
  });
  document.querySelectorAll(".main-nav [data-page]").forEach(link=>{
    const active = link.dataset.page === selected;
    link.classList.toggle("active", active);
    if (active) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  });
  window.scrollTo({top:0, behavior:"auto"});
}
document.querySelectorAll("a[data-page]").forEach(link=>link.addEventListener("click", event=>{
  event.preventDefault();
  history.pushState({}, "", link.getAttribute("href"));
  showPage(link.dataset.page);
}));
window.addEventListener("popstate", ()=>showPage(location.pathname.slice(1)));
showPage(location.pathname.slice(1));

function showDirectTab(name){
  document.querySelectorAll("[data-direct-tab]").forEach(tab=>{
    const active = tab.dataset.directTab === name;
    tab.classList.toggle("active", active);
    tab.setAttribute("aria-selected", String(active));
    tab.tabIndex = active ? 0 : -1;
  });
  document.querySelectorAll("[data-direct-panel]").forEach(panel=>{
    panel.hidden = panel.dataset.directPanel !== name;
  });
}
document.querySelectorAll("[data-direct-tab]").forEach(tab=>tab.addEventListener("click", ()=>showDirectTab(tab.dataset.directTab)));
showDirectTab("live");

const themeChoice = document.getElementById("themeChoice");
const storedTheme = localStorage.getItem("f1-hue-theme");
themeChoice.value = ["light","dark","system"].includes(storedTheme) ? storedTheme : "light";
document.documentElement.dataset.theme = themeChoice.value;
themeChoice.addEventListener("change", ()=>{
  document.documentElement.dataset.theme = themeChoice.value;
  localStorage.setItem("f1-hue-theme", themeChoice.value);
});

const FLAG_LABELS = {GREEN:"Drapeau vert",YELLOW:"Drapeau jaune",RED:"Drapeau rouge",SC:"Safety Car",SC_ENDING:"Fin Safety Car",VSC:"Virtual Safety Car",VSC_ENDING:"Fin VSC",BLUE:"Drapeau bleu",CHEQUERED:"Drapeau à damier"};
const FLAG_DESCRIPTIONS = {GREEN:"Piste dégagée",YELLOW:"Danger sur la piste",RED:"Séance interrompue",SC:"Voiture de sécurité",SC_ENDING:"Retour prochainement à la course",VSC:"Voiture de sécurité virtuelle",VSC_ENDING:"Fin de la VSC",BLUE:"Laisser passer",CHEQUERED:"Fin de séance"};
const FLAG_COLORS = {GREEN:"#58b778",YELLOW:"#e5ba42",RED:"#d95c62",SC:"#e5ba42",SC_ENDING:"#e5ba42",VSC:"#e5ba42",VSC_ENDING:"#e5ba42",BLUE:"#5a9fdf",CHEQUERED:"conic-gradient(#fff 25%,#222 0 50%,#fff 0 75%,#222 0) 0 0 / 12px 12px"};
let settingsCache = null;
async function patchSettings(body){
  const response = await fetch("/api/settings", {method:"PATCH",headers:{"Content-Type":"application/json"},body:JSON.stringify(body)});
  const data = await response.json();
  if (!response.ok) throw new Error(data.error || "Enregistrement impossible");
  settingsCache = data;
  return data;
}
async function loadSettings(){
  const response = await fetch("/api/settings", {cache:"no-store"});
  const data = await response.json();
  if (!response.ok) throw new Error(data.error || "Réglages indisponibles");
  settingsCache = data;
  renderEffects();
  renderPreferences();
}
function renderEffects(){
  const grid = document.getElementById("effectsGrid");
  grid.replaceChildren();
  for (const [flag, effect] of Object.entries(settingsCache.effects)){
    const card = document.createElement("article");
    card.className = "effect-card";
    const header = document.createElement("div");
    header.className = "effect-card-header";
    const swatch = document.createElement("span");
    swatch.className = "effect-swatch";
    swatch.style.background = FLAG_COLORS[flag] || "#999";
    const titleWrap = document.createElement("div");
    const title = document.createElement("h2");
    title.textContent = FLAG_LABELS[flag] || flag;
    const subtitle = document.createElement("small");
    subtitle.textContent = FLAG_DESCRIPTIONS[flag] || "";
    titleWrap.append(title,subtitle);
    const enabled = document.createElement("input");
    enabled.type = "checkbox";
    enabled.className = "switch-input";
    enabled.checked = effect.enabled;
    enabled.setAttribute("aria-label", `Afficher ${FLAG_LABELS[flag] || flag}`);
    header.append(swatch,titleWrap,enabled);
    const label = document.createElement("label");
    label.textContent = "Durée de l’effet (secondes)";
    const durationRow = document.createElement("div");
    durationRow.className = "effect-duration";
    const mode = document.createElement("select");
    mode.className = "input";
    mode.innerHTML = '<option value="fixed">Durée fixe</option><option value="until">Jusqu’au prochain événement</option>';
    mode.value = effect.duration_seconds == null ? "until" : "fixed";
    mode.setAttribute("aria-label", `Mode de durée ${FLAG_LABELS[flag] || flag}`);
    const seconds = document.createElement("input");
    seconds.className = "input";
    seconds.type = "number";
    seconds.min = "0.1";
    seconds.max = "3600";
    seconds.step = "0.1";
    seconds.value = effect.duration_seconds ?? "10";
    seconds.disabled = mode.value === "until";
    seconds.setAttribute("aria-label", `Durée en secondes ${FLAG_LABELS[flag] || flag}`);
    durationRow.append(mode,seconds);
    const feedback = document.createElement("div");
    feedback.className = "bridge-feedback";
    feedback.setAttribute("role", "status");
    card.append(header,label,durationRow,feedback);
    grid.appendChild(card);
    async function save(change){
      feedback.textContent = "Enregistrement…";
      feedback.className = "bridge-feedback";
      enabled.disabled = mode.disabled = seconds.disabled = true;
      try{
        const saved = await patchSettings({effects:{[flag]:change}});
        const savedEffect = saved.effects[flag];
        if ("duration_seconds" in change){
          feedback.textContent = savedEffect.duration_seconds == null
            ? "Durée « jusqu’au prochain événement » enregistrée."
            : `Durée fixe de ${savedEffect.duration_seconds.toLocaleString("fr-BE")} s enregistrée.`;
        }else{
          feedback.textContent = savedEffect.enabled
            ? "Drapeau activé et enregistré."
            : "Drapeau désactivé et enregistré.";
        }
        feedback.className = "bridge-feedback success";
      }catch(error){
        feedback.textContent = error.message || String(error);
        feedback.className = "bridge-feedback error";
        const saved = settingsCache.effects[flag];
        enabled.checked = saved.enabled;
        mode.value = saved.duration_seconds == null ? "until" : "fixed";
        seconds.value = saved.duration_seconds ?? "10";
        showToast(feedback.textContent,true);
      }finally{
        enabled.disabled = mode.disabled = false;
        seconds.disabled = mode.value === "until";
      }
    }
    enabled.addEventListener("change", ()=>save({enabled:enabled.checked}));
    mode.addEventListener("change", ()=>{
      seconds.disabled = mode.value === "until";
      const value = mode.value === "until" ? null : Number(seconds.value || 10);
      save({duration_seconds:value});
    });
    seconds.addEventListener("change", ()=>{
      const value = Number(seconds.value);
      if (!Number.isFinite(value) || value < 0.1 || value > 3600){
        feedback.textContent = "La durée doit être entre 0,1 et 3600 s.";
        feedback.className = "bridge-feedback error";
        return;
      }
      save({duration_seconds:value});
    });
  }
}
function renderPreferences(){
  const p = settingsCache.preferences;
  document.getElementById("prefBrightness").value = p.brightness;
  document.getElementById("prefTransition").value = p.transition_seconds;
  document.getElementById("prefWatchdog").value = p.alert_watchdog_seconds;
  document.getElementById("prefRestore").checked = p.restore_on_exit;
  document.getElementById("prefExitChequered").checked = p.exit_on_chequered;
}
document.getElementById("preferencesForm").addEventListener("submit", async event=>{
  event.preventDefault();
  const feedback = document.getElementById("preferencesFeedback");
  const preferences = {
    brightness:Number(document.getElementById("prefBrightness").value),
    transition_seconds:Number(document.getElementById("prefTransition").value),
    alert_watchdog_seconds:Number(document.getElementById("prefWatchdog").value),
    restore_on_exit:document.getElementById("prefRestore").checked,
    exit_on_chequered:document.getElementById("prefExitChequered").checked,
  };
  feedback.textContent = "Enregistrement…";
  try{
    await patchSettings({preferences});
    feedback.textContent = "Préférences enregistrées.";
    feedback.className = "bridge-feedback success";
    showToast("Préférences enregistrées");
  }catch(error){
    feedback.textContent = error.message || String(error);
    feedback.className = "bridge-feedback error";
    renderPreferences();
    showToast(feedback.textContent,true);
  }
});
loadSettings().catch(error=>showToast(`Réglages indisponibles : ${error.message || error}`,true));

previewStart.addEventListener("click", async ()=>{
  if (isRunning || starting) return;
  const feedback = document.getElementById("previewFeedback");
  previewStart.disabled = true;
  feedback.textContent = "Démarrage de l’aperçu…";
  try{
    const response = await fetch("/api/test/preview",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({flag:document.getElementById("previewFlag").value})});
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || "Aperçu impossible");
    updateRunUI({running:false,pendingStart:true});
    feedback.textContent = "Aperçu en cours sur les lampes sélectionnées.";
    feedback.className = "bridge-feedback success";
  }catch(error){
    feedback.textContent = error.message || String(error);
    feedback.className = "bridge-feedback error";
    previewStart.disabled = false;
    showToast(feedback.textContent,true);
  }
});
