#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import argparse, json, signal, threading, time
from config import ensure_conf, save_conf, APP_DIR
from hue import HueBridge
from engine import LightEngine
from setup_wizard import ensure_devices_selected, ensure_hue_credentials
from f1_sources import F1SourceOpenF1REST, F1SourceMock
from baseline import BaselineStore

# --- Normalisation des noms de drapeaux ----
def normalize_flag(name: str) -> str:
    if not name:
        return name
    upper = name.upper()
    map_ = {
        "CHECKERED": "CHEQUERED",
        "CHEQUERED FLAG": "CHEQUERED",
        "DRAPEAU À DAMIERS": "CHEQUERED",
        "DOUBLE YELLOW": "YELLOW",  # on mappe vers YELLOW
    }
    return map_.get(upper, upper)

def _ensure_sync_group_if_needed(conf: dict, bridge: HueBridge):
    """
    Si l'utilisateur a:
      - group_id -> on l'utilise tel quel (pas de sync group)
      - group_ids -> on crée/MAJ un LightGroup = union des lampes
      - light_ids -> on crée/MAJ un LightGroup avec ces lampes
    """
    # si un group_id explicite est fourni, priorité à celui-ci
    if conf.get("group_id", None) is not None:
        conf.pop("_sync_group_id", None)
        return

    # union des lampes depuis group_ids ?
    gids = conf.get("group_ids") or []
    if gids:
        try:
            groups = bridge.groups()
            # union des lampes de chaque group_id
            if 0 in gids:
                lids = sorted(int(k) for k in bridge.lights().keys())
            else:
                acc: set[int] = set()
                for g in gids:
                    gl = groups.get(str(int(g)), {}).get("lights", [])
                    for lid in gl:
                        acc.add(int(lid))
                lids = sorted(acc)
            if lids and hasattr(bridge, "ensure_lightgroup"):
                gid = bridge.ensure_lightgroup("F1 Hue (sync)", lids)
                if gid is not None:
                    conf["_sync_group_id"] = gid
                    print(f"[SYNC] Groupe LightGroup pour multi-zones: id={gid} (lampes={len(lids)})")
            return
        except Exception:
            return

    # sinon, light_ids ?
    lids = conf.get("light_ids") or []
    if lids and hasattr(bridge, "ensure_lightgroup"):
        gid = bridge.ensure_lightgroup("F1 Hue (sync)", lids)
        if gid is not None:
            conf["_sync_group_id"] = gid
            print(f"[SYNC] Groupe LightGroup créé/à jour: id={gid} (pour synchro parfaite)")

def _restore_on_exit(bridge: HueBridge, conf: dict, baseline: BaselineStore):
    """Restaure la baseline si activée (utilisé après Ctrl+C / fin propre)."""
    try:
        bconf = conf.get("baseline", {}) or {}
        if bconf.get("restore_on_exit", True):
            fade = int(max(0, bconf.get("fade_tenths", 5)))
            print(f"[EXIT] Restauration de la baseline (tt={fade})…")
            n = baseline.restore(bridge, conf, fade_tenths=fade)
            print(f"[EXIT] Baseline restaurée sur {n} lampe(s).")
    except Exception as e:
        print(f"[EXIT] Impossible de restaurer la baseline: {e}")

def run_live(conf: dict, stop_evt: threading.Event):
    conf = ensure_devices_selected(conf, stop_evt)
    bridge = HueBridge(conf["bridge_ip"], conf["username"])
    _ensure_sync_group_if_needed(conf, bridge)

    baseline = BaselineStore(APP_DIR, conf)
    if conf.get("baseline", {}).get("capture_on_start", True):
        n = baseline.capture(bridge, conf)
        print(f"[BASELINE] Capturée au démarrage ({n} lampes).")

    engine = LightEngine(bridge, conf, stop_evt, baseline=baseline); engine.start()

    src = F1SourceOpenF1REST(conf["openf1"]["base_url"], conf["openf1"]["poll_seconds"],
                             conf["openf1"].get("auth_header"), conf.get("flags", {}), stop_evt) \
          if conf.get("source") == "openf1-rest" else \
          F1SourceMock(["GREEN","YELLOW","SC","VSC","RED","GREEN","CHEQUERED"], 0.8, stop_evt)

    # Offset de synchro TV
    sync_off = float(conf.get("sync", {}).get("offset_seconds", 0) or 0)
    if sync_off > 0:
        print(f"[SYNC] Offset appliqué: {sync_off:.2f}s (tous les drapeaux seront retardés)")

    print("[INFO] Live F1: écoute des évènements… (Ctrl+C pour quitter)")
    for raw in src.events():
        if stop_evt.is_set(): break
        flag = normalize_flag(raw)

        # appliquer l'offset AVANT de jouer le drapeau
        if sync_off > 0:
            if stop_evt.wait(sync_off): break

        print(f"[F1] {flag}")
        engine.play(flag)

        if flag == "CHEQUERED" and conf.get("flags",{}).get("exit_on_chequered", False): break

    # arrêt propre + restore baseline au Ctrl+C / fin
    engine.stop()
    _restore_on_exit(bridge, conf, baseline)

def run_test(conf: dict, flag: str | None, quick: bool, gap: float, stop_evt: threading.Event):
    conf = ensure_devices_selected(conf, stop_evt)
    bridge = HueBridge(conf["bridge_ip"], conf["username"])
    _ensure_sync_group_if_needed(conf, bridge)

    baseline = BaselineStore(APP_DIR, conf)
    if conf.get("baseline", {}).get("capture_on_start", True):
        baseline.capture(bridge, conf)

    engine = LightEngine(bridge, conf, stop_evt, baseline=baseline); engine.start()
    seq = ["GREEN","YELLOW","SC","SC_ENDING","VSC","VSC_ENDING","RED","GREEN","CHEQUERED"] if (quick or not flag) else [flag.upper()]
    print("[TEST] Simulation… Ctrl+C pour arrêter.")
    try:
        for raw in seq:
            if stop_evt.is_set(): break
            f = normalize_flag(raw)
            print(f"[TEST] {f}"); engine.play(f)
            end = time.monotonic() + max(0.1, gap)
            while time.monotonic() < end:
                if stop_evt.wait(0.02): break
        print("[TEST] Terminé.")
    except KeyboardInterrupt:
        print("\n[TEST] Interrompu proprement.")
    finally:
        engine.stop()
        _restore_on_exit(bridge, conf, baseline)

def run_setup(conf: dict, action: str, stop_evt: threading.Event):
    conf = ensure_hue_credentials(conf, stop_evt); br = HueBridge(conf["bridge_ip"], conf["username"])
    if action == "link":
        print("✅ Bridge et username prêts.")
    elif action == "lights":
        try: print(json.dumps(br.lights(), indent=2, ensure_ascii=False))
        except Exception as e: print(f"❌ {e}")
    elif action == "groups":
        try:
            print(json.dumps(br.groups(), indent=2, ensure_ascii=False))
            print("\nℹ️  Choisis un group (Room/Zone) et mets son id dans config.yml → group_id: <id>")
        except Exception as e: print(f"❌ {e}")
    elif action in ("wizard","devices"):
        from setup_wizard import wizard_pick_devices
        wizard_pick_devices(br, conf)

def run_baseline(conf: dict, sub: str, stop_evt: threading.Event):
    conf = ensure_devices_selected(conf, stop_evt)
    br = HueBridge(conf["bridge_ip"], conf["username"])
    base = BaselineStore(APP_DIR, conf)
    if sub == "capture":
        n = base.capture(br, conf)
        print(f"[BASELINE] Capturée ({n} lampes).")
    elif sub == "restore":
        fade = int(max(0, (conf.get("baseline", {}) or {}).get("fade_tenths", 5)))
        n = base.restore(br, conf, fade_tenths=fade)
        print(f"[BASELINE] Restaurée sur {n} lampes (tt={fade}).")
    elif sub == "print":
        print(json.dumps(base.print(), indent=2, ensure_ascii=False))

# --------- NOUVEAU : calibration de l'offset ----------
def run_sync_calibrate(conf: dict, stop_evt: threading.Event):
    """Attend le prochain message API, puis te demande d'appuyer Entrée quand tu le vois à l’écran.
       Enregistre sync.offset_seconds = (now - t_api)."""
    conf = ensure_devices_selected(conf, stop_evt)
    src = F1SourceOpenF1REST(conf["openf1"]["base_url"],
                             conf["openf1"]["poll_seconds"],
                             conf["openf1"].get("auth_header"),
                             conf.get("flags", {}), stop_evt)
    print("🔧 Calibration: j’attends le prochain message (GREEN/YELLOW/SC/VSC/RED/CHEQUERED)…")
    for raw in src.events():
        if stop_evt.is_set(): return
        flag = normalize_flag(raw)
        t_api = time.monotonic()
        print(f"[CAL] Reçu côté API: {flag}. Dès que TU le vois à l’écran, appuie Entrée.")
        try:
            input()
        except KeyboardInterrupt:
            print("\n[CAL] Annulé."); return
        offset = max(0.0, time.monotonic() - t_api)
        conf.setdefault("sync", {})["offset_seconds"] = round(offset, 2)
        save_conf(conf)
        print(f"[CAL] ✅ Offset enregistré: {offset:.2f}s (sync.offset_seconds).")
        return

def run_sync_show(conf: dict):
    off = float((conf.get("sync", {}) or {}).get("offset_seconds", 0) or 0)
    print(f"[SYNC] offset_seconds = {off:.2f}s")

# --- handler qui RE-lève KeyboardInterrupt (pour le setup) ---
def _sigint_raise(sig, frame):
    raise KeyboardInterrupt

def main():
    conf = ensure_conf(); stop_evt = threading.Event(); printed_stop = {"done": False}

    # handler "doux" par défaut (pour live/test) : message puis stop_evt
    def handle_soft(sig, frame):
        if not printed_stop["done"]:
            print("\n[INFO] Arrêt propre (Ctrl+C)."); printed_stop["done"] = True
        stop_evt.set()

    signal.signal(signal.SIGINT, handle_soft)
    signal.signal(signal.SIGTERM, handle_soft)

    p = argparse.ArgumentParser(description="Synchronise Philips Hue avec les drapeaux F1")
    sub = p.add_subparsers(dest="cmd")

    live = sub.add_parser("live", help="Mode live (OpenF1)")
    live.add_argument("--bridge-ip", help="Override bridge IP (n’écrit pas dans le YAML)")

    t = sub.add_parser("test", help="Mode test (simulation)")
    t.add_argument("flag", nargs="?", help="GREEN|YELLOW|RED|SC|VSC|SC_ENDING|VSC_ENDING|CHEQUERED|BLUE")
    t.add_argument("--quick", action="store_true", help="Démo rapide de tous les drapeaux (+ ENDING)")
    t.add_argument("--gap", type=float, default=1.0, help="Pause entre étapes (s)")

    setup = sub.add_parser("setup", help="Outils de configuration")
    setup.add_argument("action", choices=["link","lights","groups","wizard","devices"],
                       help="Créer username / lister lampes / lister groupes / assistant interactif")

    bl = sub.add_parser("baseline", help="Gérer la baseline utilisateur")
    bl.add_argument("action", choices=["capture","restore","print"])

    sync = sub.add_parser("sync", help="Outils de synchronisation TV")
    sync.add_argument("action", choices=["calibrate","show"])

    args = p.parse_args()
    if getattr(args, "bridge_ip", None):
        conf["bridge_ip"] = args.bridge_ip

    if not args.cmd: args.cmd = "live"

    try:
        if args.cmd == "live":
            run_live(conf, stop_evt)
        elif args.cmd == "test":
            run_test(conf, getattr(args,"flag",None), getattr(args,"quick",False), getattr(args,"gap",1.0), stop_evt)
        elif args.cmd == "setup":
            # Pendant le setup (menus interactifs), Ctrl+C doit interrompre immédiatement
            prev = signal.getsignal(signal.SIGINT)
            signal.signal(signal.SIGINT, _sigint_raise)
            try:
                run_setup(conf, args.action, stop_evt)
            except KeyboardInterrupt:
                print("\n[SETUP] Annulé (Ctrl+C).")
            finally:
                signal.signal(signal.SIGINT, prev)  # restaurer le handler par défaut
        elif args.cmd == "baseline":
            run_baseline(conf, args.action, stop_evt)
        elif args.cmd == "sync":
            if args.action == "show": run_sync_show(conf)
            else: run_sync_calibrate(conf, stop_evt)
        else:
            p.print_help()
    finally:
        stop_evt.set()

if __name__ == "__main__":
    main()
