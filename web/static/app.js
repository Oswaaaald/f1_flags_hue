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
}
function fmtTime(ts){
  if (!ts) return "—";
  const d = new Date(ts*1000);
  return d.toLocaleString();
}
function setBadgeOffset(val){
  offsetBadge.textContent = (val!=null && !isNaN(val)) ? `${Number(val).toFixed(2)} s` : "—";
  stOff.textContent = offsetBadge.textContent;
}
function updateSelPreview(sel){
  if (sel.group_id !== null && sel.group_id !== undefined)
    selPreview.textContent = `Sélection: group_id=${sel.group_id}`;
  else if (sel.group_ids && sel.group_ids.length)
    selPreview.textContent = `Sélection: group_ids=[${sel.group_ids.join(", ")}]`;
  else if (sel.light_ids && sel.light_ids.length)
    selPreview.textContent = `Sélection: light_ids=[${sel.light_ids.join(", ")}]`;
  else
    selPreview.textContent = `Sélection: (aucune)`;
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
  if (connected){
    bridgeStatus.textContent = "Statut : connecté !";
    bridgeStatus.classList.remove("not-connected");
    bridgeStatus.classList.add("connected");
    if (bridgeHint) bridgeHint.style.display = "none";

    // essaie d'afficher une jolie carte avec nom/id/ip
    const ip = (confCache && confCache.bridge_ip) || null;
    const meta = await findBridgeMetaByIp(ip);
    renderLinkedBridgeCard(meta || { ip });
    showLinkedBridgeUI(true);
  }else{
    bridgeStatus.textContent = "Statut : non connecté";
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
    stopBtn.disabled = true;
    setRunStatus("Statut: démarrage…", "pending");
    return;
  }
  if (pendingStop){
    startLive.disabled = true;
    startTest.disabled = true;
    stopBtn.disabled = true;
    setRunStatus("Statut: arrêt en cours…", "stopping");
    return;
  }

  if (isRunning){
    startLive.disabled = true;
    startTest.disabled = true;
    stopBtn.disabled = false;
    if (mode === "live"){
      setRunStatus("Statut: live en cours", "live");
    } else if (mode === "test"){
      setRunStatus(`Statut: test en cours${gap ? " (gap "+gap+"s)" : ""}`, "test");
    } else {
      setRunStatus("Statut: en cours", "live");
    }
  }else{
    startLive.disabled = false;
    startTest.disabled = false;
    stopBtn.disabled = true;
    setRunStatus("Statut: idle", "idle");
  }
}

// --- inventory load ---
async function loadInventory(){
  try{
    const gr = await fetch("/api/hue/groups");
    const groups = await gr.json();
    const lr = await fetch("/api/hue/lights");
    const lights = await lr.json();
    INVENTORY.groups = groups || {};
    INVENTORY.lights = lights || {};
  }catch(e){
    addLog(`ERROR: inventaire — ${e}`, "err");
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
  }
}

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
    stMode.textContent = "idle";
    stFlag.textContent = "—";
    stStarted.textContent = "—";
    stBase.textContent = "—";
    setBadgeOffset(0);

    await discoverBridges();
    addLog("Bridge délié. Choisissez un bridge dans la liste pour relier.", "");
  }catch(e){
    addLog("Erreur réseau pendant le déliage.", "err");
  }finally{
    btn.disabled = false;
  }
}

// --- start/stop ---
startLive.onclick = async ()=>{
  if (starting || isRunning) return;
  updateRunUI({running:false, pendingStart:true});
  try{
    await fetch("/api/start", {method:"POST", headers:{'Content-Type':'application/json'}, body: JSON.stringify({mode:"live"})});
  }catch(e){
    addLog("Erreur réseau au démarrage live", "err");
    updateRunUI({running:false});
  }
};
startTest.onclick = async ()=>{
  if (starting || isRunning) return;
  const gap = parseFloat(gapInput.value || "1.0");
  updateRunUI({running:false, pendingStart:true});
  try{
    await fetch("/api/start", {method:"POST", headers:{'Content-Type':'application/json'}, body: JSON.stringify({mode:"test", gap})});
  }catch(e){
    addLog("Erreur réseau au démarrage test", "err");
    updateRunUI({running:false});
  }
};

stopBtn.onclick = async ()=>{
  if (stopping || !isRunning) return;
  updateRunUI({running:true, pendingStop:true});
  try{
    await fetch("/api/stop", {method:"POST"});
    setTimeout(()=>{
      if (stopping) updateRunUI({running:false});
    }, 2000);
  }catch(e){
    addLog("Erreur réseau pendant l’arrêt", "err");
    updateRunUI({running:true});
  }
};

// --- offset quick buttons ---
function getCurrentOffset(){
  const v = (confCache && confCache.sync && confCache.sync.offset_seconds) || 0;
  return Number(v) || 0;
}
async function setOffset(val){
  confCache = confCache || {};
  confCache.sync = confCache.sync || {};
  confCache.sync.offset_seconds = Number(val);
  setBadgeOffset(val);
  await fetch("/api/config", {method:"POST", headers:{'Content-Type':'application/json'}, body: JSON.stringify({sync: confCache.sync})});
  addLog(`Offset enregistré: ${Number(val).toFixed(2)}s`, "");
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

calStart.onclick = () => {
  calWaiting = true;
  calLastEventTs = null;
  calState.textContent = "État: attente message API…";
  addLog("Calibration: en attente du prochain message API (FLAG/SC/VSC…)", "");
};

calStop.onclick = () => {
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

  if (calLastEventTs === null){
    calState.textContent = "État: idle";
    addLog("Calibration: aucun message reçu — offset non modifié.", "err");
    return;
  }

  const now = performance.now();
  const offsetSec = Math.max(0, (now - calLastEventTs) / 1000);
  await setOffset(offsetSec);

  calState.textContent = "État: calibré (auto)";
  addLog(`Calibration auto: offset=${offsetSec.toFixed(2)}s`, "");
};

// --- SSE (temps réel) ---
try{
  const ev = new EventSource("/api/events");

  ev.onopen = async ()=>{
    try{
      const st = await (await fetch("/api/status")).json();
      updateRunUI({running: st.running, mode: st.mode, gap: st.gap});
      stMode.textContent    = st.mode || (st.running ? "running" : "idle");
      stFlag.textContent    = st.last_flag || "—";
      stStarted.textContent = st.started_at ? fmtTime(st.started_at) : "—";
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
      if (calWaiting){
        calLastEventTs = performance.now();
        calState.textContent = "État: visible ? clique “Je le vois maintenant”";
      }
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
      stMode.textContent = "idle";
      addLog(`STOPPED`, "");
    } else if (data.type === "error"){
      addLog(`ERROR: ${data.message}`, "err");
      stMode.textContent = "error";
    } else if (data.type === "sync_group"){
      addLog(`LightGroup sync id=${data.group_id} (lampes=${data.size})`, "");
    } else if (data.type === "status"){
      updateRunUI({running: !!data.mode, mode: data.mode, gap: data.gap});
      stMode.textContent    = data.mode || "idle";
      stFlag.textContent    = data.last_flag || "—";
      stStarted.textContent = data.started_at ? fmtTime(data.started_at) : "—";
    }
  };
}catch(e){
  addLog("SSE indisponible", "err");
}
