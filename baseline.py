#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import json, os, tempfile
from typing import Dict, List
from hue import HueBridge

class BaselineStore:
    """
    Baseline par lampe (état perso de l’utilisateur).
    - capture: lit l'état actuel des lampes ciblées (supporte group_id, group_ids[], light_ids)
    - restore: remet ces états (avec fondu)
    """
    def __init__(self, app_dir: str, conf: dict):
        self.app_dir = app_dir
        bconf = conf.get("baseline", {}) or {}
        path = bconf.get("persist_path") or "baseline.json"
        self.path = os.path.join(app_dir, path)
        self.data: Dict[str, dict] = {}
        self._load()

    def _load(self):
        try:
            if os.path.exists(self.path):
                with open(self.path, "r") as f:
                    self.data = json.load(f)
        except Exception:
            self.data = {}

    def _save(self):
        temporary = None
        try:
            directory = os.path.dirname(self.path)
            os.makedirs(directory, exist_ok=True)
            descriptor, temporary = tempfile.mkstemp(prefix=".baseline-", dir=directory)
            with os.fdopen(descriptor, "w") as f:
                json.dump(self.data, f, indent=2)
            os.chmod(temporary, 0o600)
            os.replace(temporary, self.path)
        except Exception:
            pass
        finally:
            if temporary and os.path.exists(temporary):
                os.unlink(temporary)

    def _target_light_ids(self, bridge: HueBridge, conf: dict) -> List[int]:
        """
        Priorité:
          1) light_ids explicites
          2) group_id == 0 (toutes les lampes)
          3) group_id (unique)
          4) group_ids (liste) -> union des lampes des groupes
          5) aucune cible valide: aucune lampe
        """
        lids = conf.get("light_ids") or []
        if lids:
            return sorted(int(x) for x in lids)

        try:
            lights_all = bridge.lights()
        except Exception:
            return []

        gid = conf.get("group_id", None)
        if gid == 0:
            return sorted(int(k) for k in lights_all.keys())

        if gid is not None:
            try:
                groups = bridge.groups()
                gl = groups.get(str(gid), {}).get("lights", [])
                if gl:
                    return sorted(int(x) for x in gl)
            except Exception:
                pass

        gids = conf.get("group_ids") or []
        if gids:
            try:
                groups = bridge.groups()
                acc: set[int] = set()
                for g in gids:
                    if g == 0:
                        # 0 = toutes les lampes
                        return sorted(int(k) for k in lights_all.keys())
                    gl = groups.get(str(int(g)), {}).get("lights", [])
                    for lid in gl:
                        acc.add(int(lid))
                if acc:
                    return sorted(acc)
            except Exception:
                pass

        return []

    def capture(self, bridge: HueBridge, conf: dict) -> int:
        """Capture l'état actuel (on/bri + ct/xy/hs) des lampes cibles."""
        ids = self._target_light_ids(bridge, conf)
        try:
            lights = bridge.lights()
        except Exception:
            return 0
        snap = {}
        for lid in ids:
            L = lights.get(str(lid))
            if not L: continue
            st = L.get("state", {})
            entry = {"on": bool(st.get("on", True))}
            if "bri" in st: entry["bri"] = int(st["bri"])
            cm = st.get("colormode")
            if cm == "ct" and "ct" in st:
                entry["ct"] = int(st["ct"]); entry["mode"] = "ct"
            elif "xy" in st and isinstance(st["xy"], list) and len(st["xy"]) == 2:
                entry["xy"] = [float(st["xy"][0]), float(st["xy"][1])]; entry["mode"] = "xy"
            elif "hue" in st and "sat" in st:
                entry["hue"] = int(st["hue"]); entry["sat"] = int(st["sat"]); entry["mode"] = "hs"
            else:
                entry["mode"] = "none"
            snap[str(lid)] = entry
        self.data = snap
        self._save()
        return len(self.data)

    def restore(self, bridge: HueBridge, conf: dict, fade_tenths: int = 5):
        """Restaure la baseline par lampe (transitiontime=fade_tenths)."""
        if not self.data: return 0
        selected = set(self._target_light_ids(bridge, conf))
        if not selected: return 0
        tt = int(max(0, fade_tenths))
        applied = 0
        for lid_s, entry in self.data.items():
            try:
                lid = int(lid_s)
                if lid not in selected:
                    continue
                payload = {"on": bool(entry.get("on", True)),
                           "transitiontime": tt}
                m = entry.get("mode")
                if m == "ct" and "ct" in entry:
                    payload["ct"] = int(entry["ct"])
                elif m == "xy" and "xy" in entry:
                    payload["xy"] = [float(entry["xy"][0]), float(entry["xy"][1])]
                elif m == "hs" and "hue" in entry and "sat" in entry:
                    payload["hue"] = int(entry["hue"]); payload["sat"] = int(entry["sat"])
                if "bri" in entry:
                    payload["bri"] = int(entry["bri"])
                bridge.set_light_state(lid, payload)
                applied += 1
            except Exception:
                continue
        return applied

    def print(self):
        return self.data.copy()
