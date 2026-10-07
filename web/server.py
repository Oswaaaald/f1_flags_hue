#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import os, sys, json, time, threading, queue, tempfile
import requests
import socket, re, ipaddress
import xml.etree.ElementTree as ET
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timezone
from pathlib import Path
from typing import Optional, List, Dict
from urllib.parse import urlsplit

from flask import Flask, jsonify, request, Response, send_from_directory

# --- chemins relatifs au repo ---
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
if ROOT not in sys.path:
    sys.path.append(ROOT)

from config import ensure_conf, save_conf, selection_missing  # repo root
from hue import HueBridge, HueBridgeConnectionError, connect_bridge, discover_bridge_ip
from engine import LightEngine
from baseline import BaselineStore
from effects import EffectRules, play_effects, replay_events
from event_journal import EventJournal
from historical_replay import SCENARIOS, SCENARIOS_BY_ID, historical_events
from hue_targets import prepare_sync_group
from live_events import LiveEvent
from live_service import LiveTimingService
from session_clock import compare_tv_clock, parse_tv_remaining
from settings import FLAGS, apply_settings_patch, effect_duration, public_settings

app = Flask("server", static_url_path="", static_folder=os.path.join(os.path.dirname(__file__), "static"))
app.config["MAX_CONTENT_LENGTH"] = 64 * 1024
CONFIG_WRITE_LOCK = threading.RLock()


def _json_object(force=False):
    body = request.get_json(force=force, silent=True)
    if body is None:
        return {}
    return body if isinstance(body, dict) else None


def _trusted_request_host() -> bool:
    """Accept IP literals and explicitly configured names, not arbitrary DNS names."""
    try:
        parsed = urlsplit(f"http://{request.headers.get('Host', '')}")
        hostname = parsed.hostname
        if not hostname or parsed.username or parsed.password or parsed.path or parsed.query or parsed.fragment:
            return False
        parsed.port  # reject malformed ports
    except ValueError:
        return False
    hostname = hostname.lower().rstrip(".")
    if hostname == "localhost":
        return True
    try:
        ipaddress.ip_address(hostname)
        return True
    except ValueError:
        allowed = {name.strip().lower().rstrip(".") for name in
                   os.environ.get("F1_HUE_WEB_ALLOWED_HOSTS", "").split(",") if name.strip()}
        return hostname in allowed


@app.before_request
def reject_cross_origin_writes():
    # The retained Python UI has no account system: keep it strictly local,
    # including when someone starts it through a different WSGI server.
    try:
        if not ipaddress.ip_address(request.remote_addr or "").is_loopback:
            return jsonify({"ok": False, "error": "L’interface Python est réservée à cet ordinateur. Utilise l’application F1 Hue pour un accès réseau authentifié."}), 403
    except ValueError:
        return jsonify({"ok": False, "error": "Adresse cliente invalide."}), 403
    if not _trusted_request_host():
        return jsonify({"ok": False, "error": "Nom d’hôte non autorisé."}), 400
    if request.path.startswith("/api/") and request.headers.get("Sec-Fetch-Site") == "cross-site":
        return jsonify({"ok": False, "error": "Origine non autorisée."}), 403
    if request.method not in ("POST", "PUT", "PATCH", "DELETE"):
        return None
    origin = request.headers.get("Origin")
    if (origin and origin.rstrip("/") != request.host_url.rstrip("/")) or request.headers.get("Sec-Fetch-Site") == "cross-site":
        return jsonify({"ok": False, "error": "Origine non autorisée."}), 403
    return None


@app.after_request
def set_security_headers(response):
    response.headers["Content-Security-Policy"] = "frame-ancestors 'none'"
    response.headers["X-Frame-Options"] = "DENY"
    response.headers["X-Content-Type-Options"] = "nosniff"
    response.headers["Referrer-Policy"] = "no-referrer"
    return response


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

_LIVE_SERVICE = None
_LIVE_SERVICE_LOCK = threading.Lock()


def live_service() -> LiveTimingService:
    global _LIVE_SERVICE
    with _LIVE_SERVICE_LOCK:
        if _LIVE_SERVICE is None:
            _LIVE_SERVICE = LiveTimingService(ensure_conf().get("flags", {}))
        service = _LIVE_SERVICE
    service.start()
    return service


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
        self.active_pattern = None
        self._last_effect_duration = None
        self._last_effect_started = None
        self.gap = 1.0
        self._last_stop_ts = 0.0  # anti-spam STOP
        self._start_lock = threading.Lock()

    def status(self):
        return {
            "running": self.t is not None and self.t.is_alive(),
            "mode": self.mode,
            "started_at": self.started_at,
            "last_flag": self.last_flag,
            "active_pattern": self.active_pattern,
            "gap": self.gap,
        }

    def _prepare_common(self):
        self.conf = ensure_conf()
        self.last_flag = None
        self.active_pattern = None
        self._last_effect_duration = None
        self._last_effect_started = None

        if not self.conf.get("bridge_ip") or not self.conf.get("username"):
            raise RuntimeError("Bridge non configuré (bridge_ip/username). Va dans l’onglet Setup.")

        if selection_missing(self.conf):
            raise RuntimeError("Aucune cible (group_id/group_ids/light_ids). Va dans l’onglet Setup.")

        self.bridge, discovered = connect_bridge(self.conf)
        if discovered:
            with CONFIG_WRITE_LOCK:
                current = ensure_conf()
                current["bridge_ip"] = self.conf["bridge_ip"]
                save_conf(current)
            HUB.publish({"type": "bridge_discovered", "bridge_ip": self.conf["bridge_ip"]})

        # Multi-groupes / lampes -> lightgroup
        group_id, size = prepare_sync_group(self.conf, self.bridge)
        if group_id is not None:
            HUB.publish({"type": "sync_group", "group_id": group_id, "size": size})

        self.baseline = BaselineStore(".", self.conf)
        if self.conf.get("baseline", {}).get("capture_on_start", True):
            n = self.baseline.capture(self.bridge, self.conf)
            HUB.publish({"type": "baseline", "captured": n})

        self.engine = LightEngine(
            self.bridge, self.conf, self.stop_evt, baseline=self.baseline,
            on_error=lambda exc: HUB.publish({"type": "error", "message": f"Hue : {exc}"}),
            on_state=self._effect_state,
        )
        self.engine.start()

    def _effect_state(self, pattern):
        self.active_pattern = pattern
        HUB.publish({"type": "effect", "pattern": pattern})

    def _skip_flag(self, event):
        self.last_flag = str(event.value)
        self._last_effect_duration = None
        self._last_effect_started = None
        HUB.publish({"type": "flag_skipped", "flag": self.last_flag})

    def _remember_effect(self, pattern):
        self._last_effect_duration = effect_duration(ensure_conf()["patterns"][pattern])
        self._last_effect_started = time.monotonic()

    def _wait_for_last_effect(self):
        if self._last_effect_started is None or self.stop_evt.is_set():
            return
        if self._last_effect_duration is not None:
            remaining = self._last_effect_started + self._last_effect_duration - time.monotonic()
            self.stop_evt.wait(max(0, remaining) + 0.1)

    def _restore_on_exit(self):
        try:
            bconf = ensure_conf().get("baseline", {}) or {}
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

    def _run_live(self):
        subscription = None
        service = None
        try:
            self.conf = ensure_conf()
            service = live_service()
            service.wait_ready()
            self._prepare_common()
            subscription = service.subscribe(replay_current_flag=True)
        except Exception as e:
            HUB.publish({"type": "error", "message": str(e)})
            if service is not None and subscription is not None:
                service.unsubscribe(subscription)
            self.t = None
            return

        self.mode = "live"
        self.started_at = time.time()
        HUB.publish({"type": "running", "mode": self.mode})

        try:
            # offset TV éventuel
            sync_off = float(self.conf.get("sync", {}).get("offset_seconds", 0) or 0)
            if sync_off > 0:
                HUB.publish({"type":"sync", "offset_seconds": sync_off})

            def report(event, pattern):
                self.last_flag = str(event.value)
                self._remember_effect(pattern)
                HUB.publish({"type": "flag", "flag": str(event.value),
                             "pattern": pattern, "ts": time.time()})

            play_effects(service.events(subscription, self.stop_evt, {"flag"}),
                         self.engine, self.conf, self.stop_evt,
                         offset_seconds=sync_off, on_play=report,
                         conf_provider=ensure_conf,
                         on_skip=self._skip_flag)
            if self.last_flag == "CHEQUERED" and ensure_conf().get("flags", {}).get("exit_on_chequered", False):
                self._wait_for_last_effect()
        except Exception as e:
            HUB.publish({"type": "error", "message": str(e)})
        finally:
            service.unsubscribe(subscription)
            try:
                if self.engine:
                    self.engine.stop()
            except Exception:
                pass
            self._restore_on_exit()
            self.mode = None
            self.t = None
            HUB.publish({"type": "stopped"})

    def _run_test(self, gap: float, preview_flag=None):
        try:
            self._prepare_common()
        except Exception as e:
            HUB.publish({"type": "error", "message": str(e)})
            self.t = None
            return

        self.mode = "preview" if preview_flag else "test"; self.gap = float(gap)
        self.started_at = time.time()
        HUB.publish({"type": "running", "mode": self.mode, "gap": self.gap})

        seq = [preview_flag] if preview_flag else ["GREEN","YELLOW","SC","SC_ENDING","VSC","VSC_ENDING","RED","BLUE","GREEN","CHEQUERED"]

        try:
            last_started = None
            last_duration = None
            for f in seq:
                if self.stop_evt.is_set(): break
                flag = self._normalize_flag(f)
                current = ensure_conf()
                pattern = flag if preview_flag else EffectRules(current).pattern_for(LiveEvent(kind="flag", value=flag))
                if not pattern:
                    continue
                self.last_flag = flag
                HUB.publish({"type": "flag", "flag": flag, "ts": time.time()})
                self.engine.play(pattern, effect_conf=current)
                last_duration = effect_duration(current["patterns"][pattern])
                last_started = time.monotonic()
                end = last_started + max(0.1, self.gap)
                while time.monotonic() < end:
                    if self.stop_evt.wait(0.02): break
            if not preview_flag and last_started is not None and last_duration is not None and not self.stop_evt.is_set():
                self.stop_evt.wait(max(0, last_started + last_duration - time.monotonic()) + 0.1)
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

    def _run_replay(self, recorded: list[LiveEvent], speed: float):
        try:
            if not recorded:
                raise ValueError("Aucun drapeau à rejouer pour cette séance.")
            self._prepare_common()
        except Exception as exc:
            HUB.publish({"type": "error", "message": str(exc)})
            self.t = None
            return

        self.mode = "replay"
        self.started_at = time.time()
        HUB.publish({"type": "running", "mode": self.mode})
        try:
            def report(event, pattern):
                self.last_flag = str(event.value)
                self._remember_effect(pattern)
                HUB.publish({"type": "flag", "flag": str(event.value), "pattern": pattern,
                             "ts": time.time(), "replay": True})

            play_effects(replay_events(recorded, speed, self.stop_evt),
                         self.engine, self.conf, self.stop_evt, on_play=report,
                         conf_provider=ensure_conf,
                         on_skip=self._skip_flag)
            self._wait_for_last_effect()
        except Exception as exc:
            HUB.publish({"type": "error", "message": str(exc)})
        finally:
            try:
                self.engine.stop()
            except Exception:
                pass
            self._restore_on_exit()
            self.mode = None
            self.t = None
            HUB.publish({"type": "stopped"})

    def start_live(self):
        with self._start_lock:
            if self.t and self.t.is_alive():
                return False
            self.stop_evt.clear()
            self.t = threading.Thread(target=self._run_live, daemon=True)
            self.t.start()
            return True

    def start_test(self, gap: float = 1.0):
        with self._start_lock:
            if self.t and self.t.is_alive():
                return False
            self.stop_evt.clear()
            self.t = threading.Thread(target=self._run_test, args=(gap,), daemon=True)
            self.t.start()
            return True

    def start_preview(self, flag: str):
        with self._start_lock:
            if self.t and self.t.is_alive():
                return False
            self.stop_evt.clear()
            duration = effect_duration((ensure_conf().get("patterns") or {})[flag])
            gap = min(30, duration + 0.25) if duration is not None else 10
            self.t = threading.Thread(target=self._run_test, args=(gap, flag), daemon=True)
            self.t.start()
            return True

    def start_replay(self, recorded: list[LiveEvent], speed: float):
        with self._start_lock:
            if self.t and self.t.is_alive():
                return False
            self.stop_evt.clear()
            self.t = threading.Thread(target=self._run_replay, args=(recorded, speed), daemon=True)
            self.t.start()
            return True

    def stop(self) -> bool:
        now = time.monotonic()
        if now - self._last_stop_ts < 0.3:
            return False
        self._last_stop_ts = now

        self.stop_evt.set()
        t = self.t
        if t and t.is_alive():
            t.join(timeout=5.0)
        if t and t.is_alive():
            return False
        self.t = None
        self.mode = None
        return True

RUNNER = Runner()


# =========================
#     DÉCOUVERTE & CACHE
# =========================
CACHE_MEM_TTL = 300.0  # 5 min (mémoire)
CACHE_FILE = Path("data/bridges_cache.json")
_DISC_CACHE = {"ts": 0.0, "bridges": []}

# OUIs connus Philips/Signify (Hue)
PHILIPS_OUIS = {
    "00:17:88",  # Philips Lighting BV
    "EC:B5:FA",  # Signify
    "D0:73:D5",  # Signify
}

_HUE_DESC_RE = re.compile(br"LOCATION:\s*(http://[^\r\n]+)/description\.xml", re.I)

def _load_persist_cache():
    try:
        if CACHE_FILE.exists():
            with CACHE_FILE.open("r") as f:
                j = json.load(f)
            return j.get("bridges", []), float(j.get("ts", 0.0))
    except Exception:
        pass
    return [], 0.0

def _save_persist_cache(bridges):
    temporary = None
    try:
        CACHE_FILE.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        descriptor, temporary = tempfile.mkstemp(prefix=".bridges-", dir=CACHE_FILE.parent)
        with os.fdopen(descriptor, "w") as f:
            json.dump({"ts": time.time(), "bridges": bridges}, f, indent=2)
        os.chmod(temporary, 0o600)
        os.replace(temporary, CACHE_FILE)
    except Exception:
        pass
    finally:
        if temporary and os.path.exists(temporary):
            os.unlink(temporary)

def _local_ipv4() -> str:
    """Trouve l'IP locale (ex: 192.168.0.16) sans dépendances externes."""
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect(("8.8.8.8", 80))
        ip = s.getsockname()[0]
    except Exception:
        ip = "192.168.0.1"
    finally:
        s.close()
    return ip

def _probe_description(ip: str, timeout=0.12):
    """Essaye /description.xml très vite; renvoie dict bridge ou None."""
    try:
        r = requests.get(f"http://{ip}/description.xml", timeout=timeout)
        if not r.ok or "xml" not in (r.headers.get("Content-Type","")):
            return None
        root = ET.fromstring(r.text)
        ns = "{urn:schemas-upnp-org:device-1-0}"
        fn = root.find(f".//{ns}friendlyName")
        sn = root.find(f".//{ns}serialNumber")
        name = (fn.text.strip() if fn is not None and fn.text else f"Hue Bridge {ip}")
        bid  = (sn.text.strip().lower() if sn is not None and sn.text else ip)
        return {"id": bid, "ip": ip, "name": name}
    except Exception:
        return None

def _discover_ssdp(timeout=0.5) -> List[Dict[str,str]]:
    """
    SSDP burst (3 envois * 2 ST), timeout court.
    """
    msgs = []
    for st in ("upnp:rootdevice", "urn:schemas-upnp-org:device:basic:1"):
        msgs.append(
            (
                "M-SEARCH * HTTP/1.1\r\n"
                "HOST: 239.255.255.250:1900\r\n"
                "MAN: \"ssdp:discover\"\r\n"
                "MX: 1\r\n"
                f"ST: {st}\r\n\r\n"
            ).encode()
        )
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM, socket.IPPROTO_UDP)
    s.settimeout(timeout)
    try:
        for _ in range(3):  # 3 bursts
            for msg in msgs:
                s.sendto(msg, ("239.255.255.250", 1900))
            time.sleep(0.1)
    except Exception:
        return []

    found = {}
    t0 = time.time()
    while time.time() - t0 < timeout:
        try:
            data, _ = s.recvfrom(65535)
        except socket.timeout:
            break
        except Exception:
            break
        m = _HUE_DESC_RE.search(data)
        if not m:
            continue
        url = m.group(1).decode()
        ip = url.replace("http://","").split(":")[0]
        item = _probe_description(ip, timeout=0.2)  # parse rapide
        if item:
            found[ip] = item
    return list(found.values())

def _arp_candidates() -> List[str]:
    """Retourne les IP vues dans l'ARP dont le MAC match des OUIs connus Hue."""
    out = []
    try:
        with open("/proc/net/arp", "r") as f:
            # IP address  HW type  Flags  HW address       Mask  Device
            lines = f.read().strip().splitlines()[1:]
        for line in lines:
            parts = [p for p in line.split() if p]
            if len(parts) < 4:
                continue
            ip, mac = parts[0], parts[3]
            if len(mac) < 8 or mac.count(":") < 2:
                continue
            oui = mac[:8].upper()
            if oui in PHILIPS_OUIS:
                out.append(ip)
    except Exception:
        pass
    # dédupe en gardant l'ordre
    seen = set(); res = []
    for ip in out:
        if ip not in seen:
            seen.add(ip); res.append(ip)
    return res

def _focused_scan(candidates: List[str], timeout=0.2, max_workers=64) -> List[Dict[str,str]]:
    if not candidates:
        return []
    results = {}
    def probe(ip):
        return _probe_description(ip, timeout=timeout)
    with ThreadPoolExecutor(max_workers=max_workers) as ex:
        futs = {ex.submit(probe, ip): ip for ip in candidates}
        for f in as_completed(futs):
            item = f.result()
            if item:
                results[item["ip"]] = item
    return list(results.values())

def _discover_bridges_all(timeout=2.5) -> List[Dict[str,str]]:
    """
    Ordre ultra-rapide:
      0) Last-known IPs (config + cache disque) -> probes 120ms
      1) SSDP burst (<= 500ms)
      2) ARP OUI -> focused scan (<= 200ms)
      3) Focus autour des IP vues (+/-3)
      4) Cloud meethue (fallback)
      5) discover_bridge_ip() (ultime fallback)
    """
    bridges_acc: Dict[str, Dict[str,str]] = {}

    # 0) Last-known (config + cache disque)
    conf = ensure_conf()
    last_ips = []
    if conf.get("bridge_ip"):
        last_ips.append(conf["bridge_ip"])
    disk_list, _disk_ts = _load_persist_cache()
    last_ips += [b.get("ip") for b in (disk_list or []) if b.get("ip")]
    # dédupe en gardant l'ordre
    seen=set(); last_ips = [ip for ip in last_ips if ip and not (ip in seen or seen.add(ip))]
    fast0 = _focused_scan(last_ips, timeout=0.12, max_workers=16)
    for b in fast0: bridges_acc[b["ip"]] = b
    if bridges_acc:
        return list(bridges_acc.values())

    # 1) SSDP (burst)
    ssdp = _discover_ssdp(timeout=0.5)
    for b in ssdp: bridges_acc[b["ip"]] = b
    if bridges_acc:
        return list(bridges_acc.values())

    # 2) ARP -> focused scan
    arp_ips = _arp_candidates()
    fast2 = _focused_scan(arp_ips, timeout=0.18, max_workers=32)
    for b in fast2: bridges_acc[b["ip"]] = b
    if bridges_acc:
        return list(bridges_acc.values())

    # 3) autour des IP candidates (+/-3)
    around: List[str] = []
    def neigh(ip):
        try:
            x = ipaddress.ip_address(ip)
            for d in (-3,-2,-1,1,2,3):
                y = int(x) + d
                try:
                    around.append(str(ipaddress.ip_address(y)))
                except Exception:
                    pass
        except Exception:
            pass
    for ip in last_ips + arp_ips:
        neigh(ip)
    around = list(dict.fromkeys(around))  # dédupe
    fast3 = _focused_scan(around, timeout=0.18, max_workers=48)
    for b in fast3: bridges_acc[b["ip"]] = b
    if bridges_acc:
        return list(bridges_acc.values())

    # 4) Cloud
    try:
        r = requests.get("https://discovery.meethue.com/", timeout=timeout)
        arr = r.json() if r.ok else []
    except Exception:
        arr = []
    for item in arr:
        ip = item.get("internalipaddress")
        bid = (item.get("id") or "").lower()
        if ip:
            item2 = _probe_description(ip, timeout=0.25) or {"id": bid or ip, "ip": ip, "name": f"Hue Bridge {ip}"}
            bridges_acc[ip] = item2

    # 5) Ultime fallback local
    if not bridges_acc:
        ip = discover_bridge_ip()
        if ip:
            item = _probe_description(ip, timeout=0.25) or {"id": ip, "ip": ip, "name": f"Hue Bridge {ip}"}
            bridges_acc[ip] = item

    return list(bridges_acc.values())


# =========================
#         ROUTES
# =========================

@app.get("/")
@app.get("/direct")
@app.get("/hue")
@app.get("/tests")
@app.get("/flags")
@app.get("/preferences")
def index():
    return send_from_directory(app.static_folder, "index.html")


@app.get("/api/events")
def api_events():
    q = HUB.listen()
    def gen():
        try:
            while True:
                try:
                    obj = q.get(timeout=15.0)
                except queue.Empty:
                    yield ": ping\n\n"
                    continue
                yield f"data: {json.dumps(obj, ensure_ascii=False)}\n\n"
        except GeneratorExit:
            pass
        finally:
            HUB.remove(q)

    return Response(gen(), mimetype="text/event-stream",
                    headers={"Cache-Control": "no-cache", "X-Accel-Buffering":"no"})

@app.get("/api/status")
def api_status():
    return jsonify({**RUNNER.status(), "feed": live_service().status()})

@app.get("/api/config")
def api_config():
    conf = ensure_conf()
    return jsonify({
        "bridge_ip": conf.get("bridge_ip"),
        "username": "configuré" if conf.get("username") else None,
        "sync": conf.get("sync", {}),
    })


@app.get("/api/settings")
def api_settings():
    return jsonify(public_settings(ensure_conf()))


@app.patch("/api/settings")
def api_settings_patch():
    body = request.get_json(silent=True)
    try:
        with CONFIG_WRITE_LOCK:
            updated = apply_settings_patch(ensure_conf(), body)
            save_conf(updated)
    except ValueError as exc:
        return jsonify({"ok": False, "error": str(exc)}), 400
    HUB.publish({"type": "settings_updated"})
    return jsonify({"ok": True, **public_settings(updated)})

@app.post("/api/config")
def api_config_set():
    body = _json_object(force=True)
    if body is None:
        return jsonify({"ok": False, "error": "Un objet JSON est attendu."}), 400
    sync = body.get("sync")
    if not isinstance(sync, dict) or "offset_seconds" not in sync:
        return jsonify({"ok": False, "error": "Seul sync.offset_seconds peut être modifié ici."}), 400
    try:
        offset = float(sync["offset_seconds"])
        if not 0 <= offset <= 3600:
            raise ValueError
    except (TypeError, ValueError):
        return jsonify({"ok": False, "error": "Offset invalide."}), 400
    with CONFIG_WRITE_LOCK:
        conf = ensure_conf()
        conf.setdefault("sync", {})["offset_seconds"] = offset
        save_conf(conf)
    return jsonify({"ok": True})


@app.post("/api/sync/compare-clock")
def api_sync_compare_clock():
    clicked_at = datetime.now(timezone.utc)
    body = _json_object()
    if body is None:
        return jsonify({"ok": False, "error": "Un objet JSON est attendu."}), 400
    tv_remaining = body.get("tv_remaining")
    try:
        parse_tv_remaining(tv_remaining)
    except ValueError as exc:
        return jsonify({"ok": False, "error": str(exc)}), 400
    try:
        state = live_service().wait_ready()
        if state["session_status"] != "Started":
            raise ValueError("Aucune séance F1 active pour calibrer le chrono TV.")
        if not state["clock"]:
            raise RuntimeError("Horloge de séance absente du flux F1.")
        result = compare_tv_clock(state["clock"], tv_remaining, clicked_at)
    except ValueError as exc:
        return jsonify({"ok": False, "error": str(exc)}), 409
    except Exception as exc:
        return jsonify({"ok": False, "error": f"Horloge F1 indisponible : {exc}"}), 503
    return jsonify({"ok": True, "session_name": state["session_name"], **result})


def _calibration_stream(kind):
    service = live_service()
    subscriber = service.subscribe()

    def event(data):
        return f"data: {json.dumps(data, ensure_ascii=False)}\n\n"

    def generate():
        try:
            state = service.wait_ready()
            if kind != "flag" and state["session_type"] not in ("Race", "Sprint"):
                raise RuntimeError("Cette calibration attend une course ou un sprint en direct.")
            if kind == "start":
                if state["session_status"] == "Started":
                    raise RuntimeError("La course a déjà démarré. Utilise la calibration par tours.")
                if state["session_status"] != "Inactive":
                    raise RuntimeError("Le départ de cette course n'est plus attendu par le flux F1.")
                yield event({"type": "ready", "session_name": state["session_name"]})
            elif kind == "lap":
                if state["session_status"] != "Started":
                    raise RuntimeError("La calibration par tours attend une course ou un sprint en direct.")
                baseline_lap = state["current_lap"]
                if baseline_lap is not None and state["total_laps"] and baseline_lap >= state["total_laps"]:
                    raise RuntimeError("La course est dans son dernier tour : aucun nouveau tour à attendre.")
                yield event({"type": "ready", "session_name": state["session_name"],
                             "current_lap": baseline_lap, "total_laps": state["total_laps"]})
            else:
                yield event({"type": "ready", "session_name": state["session_name"]})
            while True:
                try:
                    update = subscriber.get(timeout=5)
                except queue.Empty:
                    current = service.status()
                    if (not current["connected"] or
                        current["session_status"] in ("Finalised", "Ends") or
                        (kind != "flag" and current["session_status"] == "Finished")):
                        raise RuntimeError("La séance ou la connexion F1 s’est arrêtée avant le repère attendu.")
                    yield ": ping\n\n"
                    continue
                if update.kind == "connection" and update.value == "disconnected":
                    raise RuntimeError("La connexion F1 s’est arrêtée avant le repère attendu.")
                if kind == "flag" and update.kind == "flag" and not update.initial:
                    yield event({"type": "flag", "flag": update.value})
                    return
                if kind == "start" and update.kind == "session_status" and update.value == "Started" and not update.initial:
                    yield event({"type": "start", "session_name": update.session_name})
                    return
                if kind == "lap" and update.kind == "lap" and not update.initial:
                    lap = int(update.value)
                    if baseline_lap is None or lap > baseline_lap:
                        yield event({"type": "lap", "lap": lap,
                                     "total_laps": update.details.get("total_laps")})
                        return
                if kind != "flag" and update.kind == "session_status" and update.value in ("Finished", "Finalised", "Ends"):
                    raise RuntimeError("La séance s’est terminée avant le repère attendu.")
        except GeneratorExit:
            raise
        except Exception as exc:
            yield event({"type": "error", "message": str(exc)})
        finally:
            service.unsubscribe(subscriber)

    return Response(generate(), mimetype="text/event-stream",
                    headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"})


@app.get("/api/sync/lap-stream")
def api_sync_lap_stream():
    return _calibration_stream("lap")


@app.get("/api/sync/start-stream")
def api_sync_start_stream():
    return _calibration_stream("start")


@app.get("/api/sync/flag-stream")
def api_sync_flag_stream():
    return _calibration_stream("flag")

@app.post("/api/start")
def api_start():
    body = _json_object(force=True)
    if body is None:
        return jsonify({"ok": False, "error": "Un objet JSON est attendu."}), 400
    mode = body.get("mode","live")
    if mode == "live":
        started = RUNNER.start_live()
    elif mode == "test":
        try:
            gap = float(body.get("gap", 1.0) or 1.0)
            if not 0.1 <= gap <= 3600:
                raise ValueError
        except (TypeError, ValueError):
            return jsonify({"ok": False, "error": "Intervalle de test invalide."}), 400
        started = RUNNER.start_test(gap=gap)
    else:
        return jsonify({"ok": False, "error": "Mode inconnu."}), 400
    if not started:
        return jsonify({"ok": False, "error": "Un mode est déjà en cours."}), 409
    return jsonify({"ok": True})


@app.post("/api/test/preview")
def api_test_preview():
    body = _json_object()
    if body is None:
        return jsonify({"ok": False, "error": "Un objet JSON est attendu."}), 400
    flag = body.get("flag")
    if flag not in FLAGS:
        return jsonify({"ok": False, "error": "Drapeau inconnu."}), 400
    if not RUNNER.start_preview(flag):
        return jsonify({"ok": False, "error": "Un mode est déjà en cours."}), 409
    return jsonify({"ok": True})


@app.get("/api/journal/sessions")
def api_journal_sessions():
    journal = EventJournal()
    try:
        sessions = journal.sessions()
    finally:
        journal.close()
    return jsonify({"sessions": sessions})


@app.get("/api/journal/flags")
def api_journal_flags():
    try:
        limit = int(request.args.get("limit", 20))
        if not 1 <= limit <= 100:
            raise ValueError
    except ValueError:
        return jsonify({"ok": False, "error": "Limite invalide (1 à 100)."}), 400
    journal = EventJournal()
    try:
        flags = journal.recent_flags(limit)
    finally:
        journal.close()
    return jsonify({"flags": flags})


@app.get("/api/replay/scenarios")
def api_replay_scenarios():
    journal = EventJournal()
    try:
        local = journal.sessions()
    finally:
        journal.close()
    return jsonify({"historical": [{"id": scenario.id, "label": scenario.label,
                                     "description": scenario.description} for scenario in SCENARIOS],
                    "local": local})


@app.post("/api/replay/start")
def api_replay_start():
    body = _json_object()
    if body is None:
        return jsonify({"ok": False, "error": "Un objet JSON est attendu."}), 400
    scenario_id = str(body.get("scenario_id") or "").strip()
    session_key = str(body.get("session_key") or "").strip()
    try:
        speed = float(body.get("speed", 10))
        if (not scenario_id and not session_key) or not 0.1 <= speed <= 100:
            raise ValueError
    except (TypeError, ValueError):
        return jsonify({"ok": False, "error": "Séance ou vitesse de replay invalide."}), 400
    if scenario_id:
        if scenario_id not in SCENARIOS_BY_ID:
            return jsonify({"ok": False, "error": "Scénario historique inconnu."}), 404
        try:
            recorded = list(historical_events(scenario_id))
        except (requests.RequestException, ValueError, json.JSONDecodeError) as exc:
            return jsonify({"ok": False, "error": f"Archive F1 indisponible : {exc}"}), 503
    else:
        journal = EventJournal()
        try:
            recorded = journal.flags(session_key)
        finally:
            journal.close()
    if not recorded:
        return jsonify({"ok": False, "error": "Aucun drapeau pour cette séance."}), 404
    if not RUNNER.start_replay(recorded, speed):
        return jsonify({"ok": False, "error": "Un mode est déjà en cours."}), 409
    return jsonify({"ok": True})

@app.post("/api/stop")
def api_stop():
    actually = RUNNER.stop()
    return jsonify({"ok": True, "actually_stopped": bool(actually)})


# --- Bridge: découverte (cache mémoire 5 min + cache disque) + lien/délien ---
@app.get("/api/bridge/discover_all")
def api_bridge_discover_all():
    now = time.time()
    age = now - _DISC_CACHE["ts"]
    if age < CACHE_MEM_TTL and _DISC_CACHE["bridges"]:
        return jsonify({"bridges": _DISC_CACHE["bridges"]})

    # Découverte rapide
    fresh = _discover_bridges_all()
    if fresh:
        _DISC_CACHE["bridges"] = fresh
        _DISC_CACHE["ts"] = now
        _save_persist_cache(fresh)  # persiste pour fast path au prochain démarrage
        return jsonify({"bridges": fresh})

    # Rien trouvé: renvoie cache mémoire si dispo, sinon cache disque, sinon []
    if _DISC_CACHE["bridges"]:
        return jsonify({"bridges": _DISC_CACHE["bridges"]})
    disk_list, _ = _load_persist_cache()
    return jsonify({"bridges": disk_list or []})

@app.get("/api/bridge/discover")
def api_bridge_discover():
    ip = discover_bridge_ip()
    return jsonify({"ip": ip})

@app.post("/api/bridge/link")
def api_bridge_link():
    body = _json_object(force=True)
    if body is None:
        return jsonify({"ok": False, "error": "Un objet JSON est attendu."}), 400
    raw_ip = body.get("bridge_ip")
    ip = raw_ip.strip() if isinstance(raw_ip, str) else ""
    if not ip:
        return jsonify({"ok": False, "error": "bridge_ip manquant"}), 400
    try:
        address = ipaddress.ip_address(ip)
        private_networks = (
            ipaddress.ip_network("10.0.0.0/8"),
            ipaddress.ip_network("172.16.0.0/12"),
            ipaddress.ip_network("192.168.0.0/16"),
            ipaddress.ip_network("169.254.0.0/16"),
        )
        if address.version != 4 or not any(address in network for network in private_networks):
            raise ValueError
    except ValueError:
        return jsonify({"ok": False, "error": "Adresse IPv4 locale invalide."}), 400
    br = HueBridge(ip, "")
    try:
        username = br.register(devicetype="f1-hue#webui")
    except HueBridgeConnectionError:
        discovered_ip = discover_bridge_ip()
        if discovered_ip and discovered_ip != ip:
            try:
                username = HueBridge(discovered_ip, "").register(devicetype="f1-hue#webui")
                ip = discovered_ip
            except HueBridgeConnectionError:
                return jsonify({"ok": False, "bridge_ip": discovered_ip, "error": (
                    f"L’ancienne adresse {ip} ne répond pas. Le pont est détecté à {discovered_ip}, "
                    "mais le serveur ne peut pas le joindre. Si le pont s’ouvre dans le navigateur du Mac, "
                    "vérifie l’autorisation Réseau local de l’application qui lance ce projet."
                )}), 400
            except Exception as e:
                return jsonify({"ok": False, "bridge_ip": discovered_ip, "error": str(e)}), 400
        else:
            return jsonify({"ok": False, "error": (
                f"Impossible de joindre le pont Hue à {ip}. Si cette adresse s’ouvre dans le navigateur "
                "du Mac, vérifie l’autorisation Réseau local de l’application qui lance ce projet. "
                "Sinon, vérifie la connexion du pont."
            )}), 400
    except Exception as e:
        return jsonify({"ok": False, "error": str(e)}), 400
    with CONFIG_WRITE_LOCK:
        conf = ensure_conf()
        conf["bridge_ip"] = ip
        conf["username"] = username
        save_conf(conf)
    _save_persist_cache([{"id": ip, "ip": ip, "name": f"Hue Bridge {ip}"}])
    _DISC_CACHE["ts"] = 0.0
    return jsonify({"ok": True, "bridge_ip": ip, "username": "configuré"})

@app.post("/api/bridge/unlink")
def api_bridge_unlink():
    RUNNER.stop()
    with CONFIG_WRITE_LOCK:
        conf = ensure_conf()
        # Effacer IP + username + sélection
        conf["bridge_ip"] = None
        conf["username"] = None
        conf["group_id"] = None
        conf["group_ids"] = []
        conf["light_ids"] = []
        save_conf(conf)
    # on garde le cache disque (utile), mais on invalide le cache mémoire
    _DISC_CACHE["ts"] = 0.0
    HUB.publish({"type": "stopped"})
    return jsonify({"ok": True})

def _ensure_hue_from_conf():
    conf = ensure_conf()
    bridge, discovered = connect_bridge(conf)
    if discovered:
        with CONFIG_WRITE_LOCK:
            current = ensure_conf()
            current["bridge_ip"] = conf["bridge_ip"]
            save_conf(current)
    return bridge, conf

@app.get("/api/hue/lights")
def api_hue_lights():
    try:
        br, _ = _ensure_hue_from_conf()
        return jsonify(br.lights())
    except Exception as exc:
        return jsonify({"ok": False, "error": str(exc)}), 503

@app.get("/api/hue/groups")
def api_hue_groups():
    try:
        br, _ = _ensure_hue_from_conf()
        return jsonify(br.groups())
    except Exception as exc:
        return jsonify({"ok": False, "error": str(exc)}), 503

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
    body = _json_object(force=True)
    if body is None:
        return jsonify({"ok": False, "error": "Un objet JSON est attendu."}), 400
    if RUNNER.t is not None and RUNNER.t.is_alive():
        return jsonify({"ok": False, "error": "Arrête le mode en cours avant de changer les lampes."}), 409
    def ids(value, allow_zero=False):
        if not isinstance(value, list) or not value:
            raise ValueError("Sélection vide.")
        parsed = []
        for item in value:
            if isinstance(item, bool) or not str(item).isdigit():
                raise ValueError("Identifiant Hue invalide.")
            number = int(item)
            if number < (0 if allow_zero else 1):
                raise ValueError("Identifiant Hue invalide.")
            parsed.append(number)
        return sorted(set(parsed))

    try:
        if "group_id" in body and body["group_id"] is not None:
            gid = ids([body["group_id"]], allow_zero=True)[0]
            proposed = {"group_id": gid, "group_ids": [], "light_ids": []}
        elif "group_ids" in body:
            proposed = {"group_id": None, "group_ids": ids(body["group_ids"], allow_zero=True), "light_ids": []}
        elif "light_ids" in body:
            proposed = {"group_id": None, "group_ids": [], "light_ids": ids(body["light_ids"])}
        else:
            raise ValueError("Aucune sélection fournie.")
        bridge, _ = _ensure_hue_from_conf()
        lights = bridge.lights()
        groups = bridge.groups() if proposed["group_id"] not in (0, None) or proposed["group_ids"] else {}
        if proposed["group_id"] == 0 and not lights:
            raise ValueError("Aucune lampe disponible sur le pont.")
        if proposed["group_id"] not in (0, None) and not (groups.get(str(proposed["group_id"])) or {}).get("lights"):
            raise ValueError("Zone Hue vide ou introuvable.")
        for gid in proposed["group_ids"]:
            if gid != 0 and not (groups.get(str(gid)) or {}).get("lights"):
                raise ValueError(f"Zone Hue {gid} vide ou introuvable.")
        if any(str(lid) not in lights for lid in proposed["light_ids"]):
            raise ValueError("Une lampe sélectionnée est introuvable.")
    except (ValueError, HueBridgeConnectionError, requests.RequestException) as exc:
        return jsonify({"ok": False, "error": str(exc)}), 400
    with CONFIG_WRITE_LOCK:
        conf = ensure_conf()
        conf.update(proposed)
        save_conf(conf)
    return jsonify({"ok": True, "selection": proposed})


if __name__ == "__main__":
    host = os.environ.get("F1_HUE_WEB_HOST", "127.0.0.1")
    if host not in ("127.0.0.1", "::1", "localhost"):
        raise SystemExit("La version Python est locale uniquement. Utilise la version .NET pour héberger F1 Hue sur le réseau.")
    app.run(host=host, port=8080, debug=False)
