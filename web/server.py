#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import os, sys, json, time, threading, queue
from datetime import datetime
from typing import Optional, List

from flask import Flask, jsonify, request, Response, send_from_directory

# --- chemins relatifs au repo ---
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
if ROOT not in sys.path:
    sys.path.append(ROOT)

from config import ensure_conf, save_conf, selection_missing  # repo root
from hue import HueBridge, discover_bridge_ip
from engine import LightEngine
from baseline import BaselineStore
from f1_sources import F1SourceOpenF1REST, F1SourceMock

app = Flask("server", static_url_path="", static_folder="static")


# =========================
#   Petit HUB (SSE)
# =========================
class Hub:
    def __init__(self):
        self.lock = threading.Lock()
        self.listeners: List[queue.Queue] = []

    def listen(self) -> queue.Queue:
        q = queue.Queue()
        with self.lock:
            self.listeners.append(q)
        return q

    def remove(self, q: queue.Queue):
        with self.lock:
            try:
                self.listeners.remove(q)
            except Exception:
                pass

    def publish(self, obj: dict):
        with self.lock:
            for q in list(self.listeners):
                try:
                    q.put_nowait(obj)
                except Exception:
                    pass

HUB = Hub()


# =========================
#   Helpers Hue / Sync
# =========================
def _ensure_sync_group_if_needed(conf: dict, bridge: HueBridge):
    """
    Si l'utilisateur a :
      - group_id -> on l'utilise tel quel
      - group_ids -> on crée/MAJ un LightGroup "F1 Hue (sync)" = union des lampes, et on remplace group_id
      - light_ids -> idem LightGroup
    """
    # group_id explicite => rien à faire
    if conf.get("group_id", None) is not None:
        conf.pop("_sync_group_id", None)
        return

    # helpers REST natifs (fallback si HueBridge n'a pas ensure_lightgroup)
    def _ensure_lightgroup(name: str, lids: List[int]) -> Optional[int]:
        try:
            # trouve si déjà présent
            gs = bridge.groups()
            for gid, g in gs.items():
                if g.get("type") == "LightGroup" and g.get("name") == name:
                    # MAJ des lampes
                    bridge.session.put(
                        f"{bridge.base}/{bridge.username}/groups/{gid}",
                        json={"name": name, "lights": [str(x) for x in lids], "type": "LightGroup"},
                        timeout=bridge.timeout,
                    )
                    return int(gid)
            # sinon création
            r = bridge.session.post(
                f"{self.base}/{self.username}/groups",
                json={"name": name, "lights": [str(x) for x in lids], "type": "LightGroup"},
                timeout=bridge.timeout,
            )
            r.raise_for_status()
            arr = r.json()
            if isinstance(arr, list) and "success" in arr[0]:
                # Hue renvoie /groups/<id> dans success
                path = arr[0]["success"]["id"]  # ex: "/groups/7"
                gid = int(str(path).strip("/").split("/")[-1])
                return gid
        except Exception:
            pass
        return None

    # ---- cas group_ids (multi pièces/zones)
    gids = conf.get("group_ids") or []
    if gids:
        try:
            groups = bridge.groups()
            # union des lampes
            acc = set()
            for g in gids:
                gl = groups.get(str(int(g)), {}).get("lights", [])
                for lid in gl:
                    acc.add(int(lid))
            lids = sorted(acc)
            if lids:
                gid = _ensure_lightgroup("F1 Hue (sync)", lids)
                if gid is not None:
                    conf["group_id"] = gid
                    conf["group_ids"] = []
                    conf["light_ids"] = []
                    save_conf(conf)
                    HUB.publish({"type": "sync_group", "group_id": gid, "size": len(lids)})
        except Exception:
            pass
        return

    # ---- cas light_ids
    lids = conf.get("light_ids") or []
    if lids:
        gid = _ensure_lightgroup("F1 Hue (sync)", lids)
        if gid is not None:
            conf["group_id"] = gid
            conf["group_ids"] = []
            conf["light_ids"] = []
            save_conf(conf)
            HUB.publish({"type": "sync_group", "group_id": gid, "size": len(lids)})
        return


# =========================
#   Runner (Live/Test)
# =========================
class Runner:
    def __init__(self):
        self.t: Optional[threading.Thread] = None
        self.stop_evt = threading.Event()
        self.engine: Optional[LightEngine] = None
        self.baseline: Optional[BaselineStore] = None
        self.conf = None
        self.mode = None  # "live" | "test"
        self.started_at: Optional[float] = None
        self.last_flag = None
        self.gap = 1.0
        self._last_stop_ts = 0.0  # anti-spam STOP

    def status(self):
        return {
            "running": self.t is not None and self.t.is_alive(),
            "mode": self.mode,
            "started_at": self.started_at,
            "last_flag": self.last_flag,
            "gap": self.gap,
        }

    def _prepare_common(self):
        self.conf = ensure_conf()

        if not self.conf.get("bridge_ip") or not self.conf.get("username"):
            raise RuntimeError("Bridge non configuré (bridge_ip/username). Va dans l’onglet Setup.")

        if selection_missing(self.conf):
            raise RuntimeError("Aucune cible (group_id/group_ids/light_ids). Va dans l’onglet Setup.")

        self.bridge = HueBridge(self.conf["bridge_ip"], self.conf["username"])

        # Multi-groupes / lampes -> lightgroup
        _ensure_sync_group_if_needed(self.conf, self.bridge)

        self.baseline = BaselineStore(".", self.conf)
        if self.conf.get("baseline", {}).get("capture_on_start", True):
            n = self.baseline.capture(self.bridge, self.conf)
            HUB.publish({"type": "baseline", "captured": n})

        self.engine = LightEngine(self.bridge, self.conf, self.stop_evt, baseline=self.baseline)
        self.engine.start()

    def _restore_on_exit(self):
        try:
            bconf = self.conf.get("baseline", {}) or {}
            if bconf.get("restore_on_exit", True):
                fade = int(max(0, bconf.get("fade_tenths", 5)))
                n = self.baseline.restore(self.bridge, self.conf, fade_tenths=fade)
                HUB.publish({"type": "baseline_restored", "count": n, "tt": fade})
        except Exception as e:
            HUB.publish({"type": "error", "message": f"Restore baseline: {e}"})

    def _normalize_flag(self, name: str) -> str:
        if not name:
            return name
        up = name.upper()
        map_ = {
            "CHECKERED": "CHEQUERED",
            "CHEQUERED FLAG": "CHEQUERED",
            "DRAPEAU À DAMIERS": "CHEQUERED",
            "DOUBLE YELLOW": "YELLOW",
        }
        return map_.get(up, up)

    # -------- live ----------
    def _run_live(self):
        try:
            self._prepare_common()
        except Exception as e:
            HUB.publish({"type": "error", "message": str(e)})
            self.t = None
            return

        self.mode = "live"
        self.started_at = time.time()
        HUB.publish({"type": "running", "mode": self.mode})

        try:
            src = F1SourceOpenF1REST(self.conf["openf1"]["base_url"],
                                     self.conf["openf1"]["poll_seconds"],
                                     self.conf["openf1"].get("auth_header"),
                                     self.conf.get("flags", {}), self.stop_evt)
            # offset TV éventuel
            sync_off = float(self.conf.get("sync", {}).get("offset_seconds", 0) or 0)
            if sync_off > 0:
                HUB.publish({"type":"sync", "offset_seconds": sync_off})

            for raw in src.events():
                if self.stop_evt.is_set(): break
                flag = self._normalize_flag(raw)
                # offset si demandé
                if sync_off > 0:
                    if self.stop_evt.wait(sync_off): break
                self.last_flag = flag
                HUB.publish({"type": "flag", "flag": flag, "ts": time.time()})
                self.engine.play(flag)
        except Exception as e:
            HUB.publish({"type": "error", "message": str(e)})
        finally:
            try:
                if self.engine:
                    self.engine.stop()
            except Exception:
                pass
            self._restore_on_exit()
            self.mode = None
            self.t = None
            HUB.publish({"type": "stopped"})

    # -------- test ----------
    def _run_test(self, gap: float):
        try:
            self._prepare_common()
        except Exception as e:
            HUB.publish({"type": "error", "message": str(e)})
            self.t = None
            return

        self.mode = "test"; self.gap = float(gap)
        self.started_at = time.time()
        HUB.publish({"type": "running", "mode": self.mode, "gap": self.gap})

        # Séquence FIXE demandée
        seq = ["GREEN","YELLOW","SC","SC_ENDING","VSC","VSC_ENDING","RED","GREEN","CHEQUERED"]

        try:
            for f in seq:
                if self.stop_evt.is_set(): break
                flag = self._normalize_flag(f)
                self.last_flag = flag
                HUB.publish({"type": "flag", "flag": flag, "ts": time.time()})
                self.engine.play(flag)
                end = time.monotonic() + max(0.1, self.gap)
                while time.monotonic() < end:
                    if self.stop_evt.wait(0.02): break
        finally:
            try:
                if self.engine:
                    self.engine.stop()
            except Exception:
                pass
            self._restore_on_exit()
            self.mode = None
            self.t = None
            HUB.publish({"type": "stopped"})

    def start_live(self):
        if self.t and self.t.is_alive(): return
        self.stop_evt.clear()
        self.t = threading.Thread(target=self._run_live, daemon=True)
        self.t.start()

    def start_test(self, gap: float = 1.0):
        if self.t and self.t.is_alive(): return
        self.stop_evt.clear()
        self.t = threading.Thread(target=self._run_test, args=(gap,), daemon=True)
        self.t.start()

    def stop(self) -> bool:
        """Idempotent, ignore les STOP rapprochés (<300 ms)."""
        now = time.monotonic()
        if now - self._last_stop_ts < 0.3:
            return False
        self._last_stop_ts = now

        self.stop_evt.set()
        t = self.t
        if t and t.is_alive():
            t.join(timeout=5.0)
        self.t = None
        self.mode = None
        return True

RUNNER = Runner()


# =========================
#         ROUTES
# =========================

# --- Static ---
@app.get("/")
def index():
    return send_from_directory("static", "index.html")


# --- SSE events ---
@app.get("/api/events")
def api_events():
    q = HUB.listen()
    def gen():
        try:
            while True:
                try:
                    obj = q.get(timeout=15.0)
                except queue.Empty:
                    # ping pour garder la connexion
                    yield ": ping\n\n"
                    continue
                yield f"data: {json.dumps(obj, ensure_ascii=False)}\n\n"
        except GeneratorExit:
            pass
        finally:
            HUB.remove(q)

    return Response(gen(), mimetype="text/event-stream",
                    headers={"Cache-Control": "no-cache", "X-Accel-Buffering":"no"})

# --- Status / Control ---
@app.get("/api/status")
def api_status():
    return jsonify(RUNNER.status())

@app.get("/api/config")
def api_config():
    return jsonify(ensure_conf())

@app.post("/api/config")
def api_config_set():
    body = request.get_json(force=True, silent=True) or {}
    conf = ensure_conf()
    conf.update(body)
    save_conf(conf)
    return jsonify({"ok": True})

@app.post("/api/start")
def api_start():
    body = request.get_json(force=True, silent=True) or {}
    mode = body.get("mode","live")
    if mode == "live":
        RUNNER.start_live()
    else:
        gap = float(body.get("gap", 1.0) or 1.0)
        RUNNER.start_test(gap=gap)
    return jsonify({"ok": True})

@app.post("/api/stop")
def api_stop():
    actually = RUNNER.stop()
    return jsonify({"ok": True, "actually_stopped": bool(actually)})


# --- Bridge setup ---
@app.get("/api/bridge/discover")
def api_bridge_discover():
    ip = discover_bridge_ip()
    return jsonify({"ip": ip})

@app.post("/api/bridge/link")
def api_bridge_link():
    body = request.get_json(force=True, silent=True) or {}
    ip = (body.get("bridge_ip") or "").strip()
    if not ip:
        return jsonify({"ok": False, "error": "bridge_ip manquant"}), 400
    br = HueBridge(ip, "")
    try:
        username = br.register(devicetype="f1-hue#webui")
    except Exception as e:
        return jsonify({"ok": False, "error": str(e)}), 400
    conf = ensure_conf()
    conf["bridge_ip"] = ip
    conf["username"] = username
    save_conf(conf)
    return jsonify({"ok": True, "bridge_ip": ip, "username": username})

def _ensure_hue_from_conf():
    conf = ensure_conf()
    ip = conf.get("bridge_ip"); user = conf.get("username")
    if not ip or not user:
        raise RuntimeError("Bridge non configuré.")
    return HueBridge(ip, user), conf

@app.get("/api/hue/lights")
def api_hue_lights():
    try:
        br, _ = _ensure_hue_from_conf()
        return jsonify(br.lights())
    except Exception as e:
        return jsonify({"ok": False, "error": str(e)}), 400

@app.get("/api/hue/groups")
def api_hue_groups():
    try:
        br, _ = _ensure_hue_from_conf()
        return jsonify(br.groups())
    except Exception as e:
        return jsonify({"ok": False, "error": str(e)}), 400

@app.get("/api/setup/selection")
def api_setup_selection_get():
    conf = ensure_conf()
    sel = {
        "group_id": conf.get("group_id", None),
        "group_ids": conf.get("group_ids", []),
        "light_ids": conf.get("light_ids", []),
    }
    return jsonify(sel)

@app.post("/api/setup/targets")
def api_setup_targets():
    body = request.get_json(force=True, silent=True) or {}
    conf = ensure_conf()

    if "group_id" in body and body["group_id"] is not None:
        gid = int(body["group_id"])
        conf["group_id"] = gid
        conf["group_ids"] = []
        conf["light_ids"] = []
    elif "group_ids" in body:
        gids = [int(x) for x in (body.get("group_ids") or [])]
        if not gids:
            return jsonify({"ok": False, "error": "group_ids vide"}), 400
        conf["group_id"] = None
        conf["group_ids"] = gids
        conf["light_ids"] = []
    elif "light_ids" in body:
        lids = [int(x) for x in (body.get("light_ids") or [])]
        if not lids:
            return jsonify({"ok": False, "error": "light_ids vide"}), 400
        conf["group_id"] = None
        conf["group_ids"] = []
        conf["light_ids"] = lids
    else:
        return jsonify({"ok": False, "error": "Aucun champ de sélection fourni"}), 400

    save_conf(conf)
    return jsonify({"ok": True, "selection": {
        "group_id": conf.get("group_id"),
        "group_ids": conf.get("group_ids", []),
        "light_ids": conf.get("light_ids", []),
    }})


if __name__ == "__main__":
    # 0.0.0.0 pour accès depuis LAN ; port 8080
    app.run(host="0.0.0.0", port=8080, debug=False)
