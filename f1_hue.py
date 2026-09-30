#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import argparse, json, signal, threading, time
import requests
from config import ensure_conf, save_conf, APP_DIR
from hue import HueBridge, HueBridgeConnectionError, connect_bridge
from engine import LightEngine
from setup_wizard import ensure_devices_selected, ensure_hue_credentials
from f1_sources import F1SourceFormula1Live
from baseline import BaselineStore
from effects import EffectRules, play_effects, replay_events
from event_journal import EventJournal
from historical_replay import SCENARIOS, SCENARIOS_BY_ID, historical_events
from hue_targets import prepare_sync_group
from live_events import LiveEvent
from live_service import LiveTimingService

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
    service = LiveTimingService(conf.get("flags", {}))
    subscription = None
    try:
        service.wait_ready()
        conf = ensure_devices_selected(conf, stop_evt)
        bridge, discovered = connect_bridge(conf)
        if discovered:
            save_conf(conf)
            print(f"[HUE] Nouvelle adresse du pont enregistrée : {conf['bridge_ip']}")
        prepare_sync_group(conf, bridge)
        baseline = BaselineStore(APP_DIR, conf)
        if conf.get("baseline", {}).get("capture_on_start", True):
            n = baseline.capture(bridge, conf)
            print(f"[BASELINE] Capturée au démarrage ({n} lampes).")
        engine = LightEngine(bridge, conf, stop_evt, baseline=baseline)
        engine.start()
        subscription = service.subscribe(replay_current_flag=True)
        sync_off = float(conf.get("sync", {}).get("offset_seconds", 0) or 0)
        print(f"[INFO] Live F1 : écoute des événements (offset TV {sync_off:.2f} s)…")
        try:
            play_effects(service.events(subscription, stop_evt, {"flag"}), engine, conf, stop_evt,
                         offset_seconds=sync_off,
                         on_play=lambda event, pattern: print(f"[F1] {event.value} → {pattern}"))
        finally:
            engine.stop()
            _restore_on_exit(bridge, conf, baseline)
    finally:
        if subscription is not None:
            service.unsubscribe(subscription)
        service.stop()

def run_test(conf: dict, flag: str | None, quick: bool, gap: float, stop_evt: threading.Event):
    conf = ensure_devices_selected(conf, stop_evt)
    bridge, discovered = connect_bridge(conf)
    if discovered:
        save_conf(conf)
        print(f"[HUE] Nouvelle adresse du pont enregistrée : {conf['bridge_ip']}")
    prepare_sync_group(conf, bridge)

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
            pattern = EffectRules(conf).pattern_for(LiveEvent(kind="flag", value=f))
            print(f"[TEST] {f}")
            if pattern:
                engine.play(pattern)
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
    service = LiveTimingService(conf.get("flags", {}))
    subscription = service.subscribe()
    try:
        service.wait_ready()
        print("🔧 Calibration : j’attends le prochain drapeau F1…")
        for event in service.events(subscription, stop_evt, {"flag"}):
            if event.initial:
                continue
            print(f"[CAL] Reçu côté API : {event.value}. Dès que TU le vois à l’écran, appuie Entrée.")
            try:
                input()
            except KeyboardInterrupt:
                print("\n[CAL] Annulé.")
                return
            offset = max(0.0, time.monotonic() - event.received_mono)
            conf.setdefault("sync", {})["offset_seconds"] = round(offset, 2)
            save_conf(conf)
            print(f"[CAL] ✅ Offset enregistré : {offset:.2f}s.")
            return
    finally:
        service.unsubscribe(subscription)
        service.stop()


def run_replay(conf: dict, session_key: str | None, speed: float, stop_evt: threading.Event):
    if not 0.1 <= speed <= 100:
        raise ValueError("La vitesse de replay doit être comprise entre 0,1 et 100.")
    if not session_key:
        print("[REPLAY] Séances passées (archive Formula 1 ; Internet requis) :")
        for scenario in SCENARIOS:
            print(f"  archive:{scenario.id}  {scenario.label}")
        journal = EventJournal()
        try:
            sessions = journal.sessions()
            print("[REPLAY] Séances enregistrées sur ce Mac :")
            for session in sessions:
                print(f"  {session['session_key']}  {session['session_name']}  ({session['flag_count']} drapeaux)")
            if not sessions:
                print("  Aucune pour l’instant.")
        finally:
            journal.close()
        return
    if session_key.startswith("archive:"):
        scenario_id = session_key.removeprefix("archive:")
        if scenario_id not in SCENARIOS_BY_ID:
            raise ValueError("Scénario historique inconnu.")
        try:
            recorded = list(historical_events(scenario_id))
        except (requests.RequestException, ValueError, UnicodeError) as exc:
            raise ValueError(f"Archive F1 indisponible : {exc}") from exc
    else:
        journal = EventJournal()
        try:
            recorded = journal.flags(session_key)
        finally:
            journal.close()
    if not recorded:
        raise ValueError("Aucun drapeau pour cette séance.")
    conf = ensure_devices_selected(conf, stop_evt)
    bridge, discovered = connect_bridge(conf)
    if discovered:
        save_conf(conf)
    prepare_sync_group(conf, bridge)
    baseline = BaselineStore(APP_DIR, conf)
    if conf.get("baseline", {}).get("capture_on_start", True):
        baseline.capture(bridge, conf)
    engine = LightEngine(bridge, conf, stop_evt, baseline=baseline)
    engine.start()
    print(f"[REPLAY] {len(recorded)} drapeaux à vitesse ×{speed:g} (Ctrl+C pour arrêter).")
    try:
        play_effects(replay_events(recorded, speed, stop_evt), engine, conf, stop_evt,
                     on_play=lambda event, pattern: print(f"[REPLAY] {event.value} → {pattern}"))
    finally:
        engine.stop()
        _restore_on_exit(bridge, conf, baseline)

def run_sync_show(conf: dict):
    off = float((conf.get("sync", {}) or {}).get("offset_seconds", 0) or 0)
    print(f"[SYNC] offset_seconds = {off:.2f}s")

def run_check_live(conf: dict, stop_evt: threading.Event):
    src = F1SourceFormula1Live(conf.get("flags", {}), stop_evt)
    started = src.check_connection()
    print("[F1] Connexion au flux gratuit OK.")
    print("[F1] Séance en cours." if started else "[F1] Aucune séance active dans le flux pour l'instant.")

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

    live = sub.add_parser("live", help="Mode live (flux Formula 1)")
    live.add_argument("--bridge-ip", help="Override bridge IP (n’écrit pas dans le YAML)")
    sub.add_parser("check-live", help="Vérifier la source F1 sans commander les lampes")

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

    replay = sub.add_parser("replay", help="Lister ou rejouer les séances locales et les archives Formula 1")
    replay.add_argument("session_key", nargs="?", help="Clé locale ou archive:ID ; sans clé, liste les séances")
    replay.add_argument("--speed", type=float, default=10.0, help="Accélération entre drapeaux (0.1 à 100)")

    args = p.parse_args()
    if getattr(args, "bridge_ip", None):
        conf["bridge_ip"] = args.bridge_ip

    if not args.cmd: args.cmd = "live"

    try:
        if args.cmd == "live":
            run_live(conf, stop_evt)
        elif args.cmd == "check-live":
            run_check_live(conf, stop_evt)
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
        elif args.cmd == "replay":
            run_replay(conf, args.session_key, args.speed, stop_evt)
        else:
            p.print_help()
    except (HueBridgeConnectionError, ConnectionError, ValueError) as e:
        print(f"[ERREUR] {e}")
        raise SystemExit(2) from None
    finally:
        stop_evt.set()

if __name__ == "__main__":
    main()
