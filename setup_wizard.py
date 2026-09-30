# setup_wizard.py
import sys, time, threading, select
from config import save_conf, selection_missing
from hue import HueBridge, discover_bridge_ip

# ---------- saisie non bloquante, compatible SIGINT custom ----------
def safe_input(prompt: str, default: str | None = None, stop_evt: threading.Event | None = None) -> str:
    sys.stdout.write(prompt)
    sys.stdout.flush()

    # Boucle: on scrute stdin + stop_evt pour sortir immédiatement au Ctrl+C
    while True:
        if stop_evt is not None and stop_evt.is_set():
            print("\n[SETUP] Annulé (signal).")
            raise SystemExit(130)

        try:
            r, _, _ = select.select([sys.stdin], [], [], 0.1)  # 100 ms
        except (KeyboardInterrupt, ValueError):
            # (par sécurité, si jamais un KeyboardInterrupt arrive quand même)
            print("\n[SETUP] Annulé.")
            raise SystemExit(130)

        if r:
            line = sys.stdin.readline()
            if line == "":  # EOF / Ctrl+D
                print("\n[SETUP] Annulé (EOF).")
                raise SystemExit(130)
            s = line.strip()
            if s.lower() in ("q", "quit", "exit"):
                print("[SETUP] Annulé par l’utilisateur.")
                raise SystemExit(0)
            if s == "" and default is not None:
                return default
            return s

def ensure_hue_credentials(conf: dict, stop_evt: threading.Event) -> dict:
    if not conf.get("bridge_ip"):
        ip = discover_bridge_ip()
        if ip:
            print(f"[SETUP] Bridge détecté: {ip}")
            conf["bridge_ip"] = ip; save_conf(conf)
        else:
            if sys.stdin.isatty():
                ip = safe_input("[SETUP] IP du bridge (ex. 192.168.1.28) : ", stop_evt=stop_evt)
                if not ip: raise SystemExit("❌ Aucune IP de bridge.")
                conf["bridge_ip"] = ip; save_conf(conf)
            else:
                raise SystemExit("❌ Aucune IP de bridge.")

    bridge = HueBridge(conf["bridge_ip"], conf.get("username",""))
    if not conf.get("username"):
        print("➡️  Appuie sur le bouton du bridge Hue (rond) puis patiente… (Ctrl+C pour annuler)")
        deadline = time.monotonic() + 60; last_hint = 0.0
        while time.monotonic() < deadline and not stop_evt.is_set():
            try:
                username = bridge.register(devicetype="f1-hue#raspi")
                conf["username"] = username; save_conf(conf)
                print("✅ Clé locale Hue créée et sauvegardée.")
                break
            except Exception:
                now = time.monotonic()
                if now - last_hint > 5:
                    print("   (Appuie sur le bouton du bridge si ce n'est pas déjà fait)")
                    last_hint = now
                if stop_evt.wait(1.5):  # interruptible
                    break
        if not conf.get("username"):
            raise SystemExit("❌ Échec d'enregistrement. Réessaie en appuyant sur le bouton.")
    return conf

def wizard_pick_devices(bridge: HueBridge, conf: dict, stop_evt: threading.Event | None = None):
    if stop_evt is not None and stop_evt.is_set():
        raise SystemExit(130)

    print("\n🛠️  Configuration des lampes (Ctrl+C, Ctrl+D ou 'q' pour annuler)")
    print("   1) Utiliser une ou plusieurs Pièces/Zones (Room/Zone)  ← NEW (multi)")
    print("   2) Choisir des lampes individuelles")
    print("   3) Utiliser toutes les lampes (group 0)")
    choice = safe_input("   Ton choix [1]: ", default="1", stop_evt=stop_evt)
    if choice not in {"1","2","3"}:
        choice = "1"

    # --- 1) Pièces/Zones (multi) ---
    if choice == "1":
        groups = bridge.groups()
        roomzone, ent = [], []
        for gid, g in groups.items():
            item = {"id": int(gid), "name": g.get("name","(sans nom)"),
                    "type": g.get("type","?"), "n": len(g.get("lights",[]))}
            (roomzone if item["type"] in ("Room","Zone") else ent).append(item)
        roomzone.sort(key=lambda x: (x["type"], x["name"].lower()))
        ent.sort(key=lambda x: (x["type"], x["name"].lower()))

        print("\n📦 Pièces & Zones disponibles :")
        for it in roomzone:
            print(f"   - id {it['id']:>2} | {it['type']:<4} | {it['name']} (🕯 {it['n']})")
        if ent:
            print("\n(Autres groupes — ex. Entertainment) :")
            for it in ent:
                print(f"   - id {it['id']:>2} | {it['type']:<13} | {it['name']} (🕯 {it['n']})")

        print("\n👉 Tu peux saisir **un id** (ex: 1) OU **plusieurs ids séparés par des virgules** (ex: 1,3,7)")
        while True:
            if stop_evt is not None and stop_evt.is_set():
                print("\n[SETUP] Annulé (signal).")
                raise SystemExit(130)
            s = safe_input("   Id(s) du/des groupe(s) à utiliser : ", stop_evt=stop_evt)
            try:
                ids = [int(x.strip()) for x in s.split(",") if x.strip()]
                if not ids:
                    print("❌ Liste vide. Réessaie."); continue
                for gid in ids:
                    if str(gid) not in groups:
                        raise ValueError(f"id {gid} introuvable")
                if len(ids) == 1:
                    conf["group_id"]  = ids[0]
                    conf["group_ids"] = []
                else:
                    conf["group_id"]  = None
                    conf["group_ids"] = ids
                conf["light_ids"] = []
                save_conf(conf)
                txt = f"group_id={conf['group_id']}" if conf["group_id"] is not None else f"group_ids={conf['group_ids']}"
                print(f"✅ Config sauvegardée: {txt}")
                break
            except ValueError as e:
                print(f"❌ {e}. Réessaie.")
        return

    # --- 2) Lampes individuelles ---
    if choice == "2":
        lights = bridge.lights()
        rows = []
        for lid, L in lights.items():
            rows.append({"id": int(lid), "name": L.get("name","(sans nom)"),
                         "prod": L.get("productname",""),
                         "on": "on" if L.get("state",{}).get("on") else "off"})
        rows.sort(key=lambda x: x["id"])
        print("\n💡 Lampes disponibles :")
        for it in rows:
            print(f"   - id {it['id']:>2} | {it['name']} ({it['prod']})  [{it['on']}]")

        while True:
            if stop_evt is not None and stop_evt.is_set():
                print("\n[SETUP] Annulé (signal).")
                raise SystemExit(130)
            s = safe_input("\n👉 Entre les id séparés par des virgules (ex: 1,2,3) ou 'all' : ", stop_evt=stop_evt)
            try:
                lids = [it["id"] for it in rows] if s.lower()=="all" else [int(x.strip()) for x in s.split(",") if x.strip()]
                for lid in lids:
                    if str(lid) not in lights:
                        raise ValueError(f"id {lid} introuvable")
                if not lids:
                    print("❌ Liste vide. Réessaie."); continue
                conf["group_id"]  = None
                conf["group_ids"] = []
                conf["light_ids"] = lids
                save_conf(conf)
                print(f"✅ Config sauvegardée: light_ids={lids}")
                break
            except ValueError as e:
                print(f"❌ {e}. Réessaie.")
        return

    # --- 3) Toutes les lampes ---
    if choice == "3":
        conf["group_id"]  = 0
        conf["group_ids"] = []
        conf["light_ids"] = []
        save_conf(conf)
        print("✅ Config sauvegardée: group_id=0 (toutes les lampes)")

def ensure_devices_selected(conf: dict, stop_evt: threading.Event) -> dict:
    conf = ensure_hue_credentials(conf, stop_evt)
    br = HueBridge(conf["bridge_ip"], conf["username"])
    if selection_missing(conf):
        print("\n[SETUP] Aucune cible de lampes configurée.")
        # PASSAGE DU stop_evt AU WIZARD (important pour Ctrl+C)
        wizard_pick_devices(br, conf, stop_evt=stop_evt)
    return conf
