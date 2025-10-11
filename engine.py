import time, threading
from queue import SimpleQueue, Empty
from hue import HueBridge

class LightEngine:
    """
    - Préemption: alert:none, puis nouvelle couleur (fondu configurable).
    - Blink via 'alert' (select/lselect) ultra régulier.
    - Support 'ct' (évite le blanc rosé).
    - Baseline: restore quand un pattern se termine naturellement (pas en cas de préemption).
    - ✅ Si light_ids sont utilisés, on crée un LightGroup et on cible le groupe pour synchroniser.
    """

    def __init__(self, bridge: HueBridge, conf: dict, stop_evt: threading.Event, baseline=None):
        self.bridge = bridge; self.conf = conf; self.stop_evt = stop_evt
        self.baseline = baseline
        self.cmd = SimpleQueue(); self.thread = threading.Thread(target=self._run, daemon=True)
        self.running = threading.Event(); self.running.set()
        self.cancel_event = threading.Event()

    # --- helpers cibles ---
    def _targets(self):
        """Retourne (light_ids, group_id) en privilégiant le groupe sync si présent."""
        gid = self.conf.get("group_id", None)
        if gid is None:
            gid = self.conf.get("_sync_group_id", None)
        if gid is not None:
            return (None, gid)
        return (self.conf.get("light_ids") or None, None)

    def start(self):
        if not self.thread.is_alive(): self.thread.start()

    def stop(self):
        self.running.clear(); self.stop_evt.set(); self.cancel_event.set()
        lids, gid = self._targets()
        try: self.bridge.stop_alert(lids, gid)
        except Exception: pass
        try: self.cmd.put_nowait(("stop", None))
        except Exception: pass
        if self.thread.is_alive(): self.thread.join()

    def _tt(self, p: dict, key: str, default_key: str) -> int:
        if p and key in p and p[key] is not None:
            return int(max(0, p[key]))
        if default_key in self.conf and self.conf[default_key] is not None:
            return int(max(0, self.conf[default_key]))
        return 0

    def _apply_from_pattern(self, p: dict, bri_value: int, tt: int):
        lids, gid = self._targets()
        data = {"on": True, "bri": max(1, min(254, int(bri_value))), "transitiontime": int(max(0, tt))}
        if "ct" in p:
            data["ct"] = int(p["ct"])
        else:
            xy = self.conf["colors_xy"][p["color"]]
            data["xy"] = xy
        self.bridge.set_state(data, lids, gid)

    def play(self, pattern_name: str):
        # 1) annule pattern/clignotement courant
        self.cancel_event.set()
        lids, gid = self._targets()
        self.bridge.stop_alert(lids, gid)
        # 2) applique couleur/bri du nouveau pattern
        p = self.conf["patterns"].get(pattern_name)
        if p:
            bri = int(p.get("bri_override", self.conf["bri"]))
            base_tt = self._tt(p, "transition_tenths", "transition_tenths")
            self._apply_from_pattern(p, bri, base_tt)
        # 3) ordonne au moteur
        try: self.cmd.put_nowait(("play", pattern_name))
        except Exception: pass

    def _restore_baseline_if_enabled(self):
        bconf = self.conf.get("baseline", {}) or {}
        if not bconf.get("restore_on_idle", True): return
        if not self.baseline: return
        fade = int(max(0, bconf.get("fade_tenths", 5)))
        self.baseline.restore(self.bridge, self.conf, fade_tenths=fade)

    def _run(self):
        while self.running.is_set() and not self.stop_evt.is_set():
            try: cmd, val = self.cmd.get(timeout=0.05)
            except Empty: continue
            if cmd == "stop": break
            if cmd != "play": continue

            name = str(val)
            p = self.conf["patterns"].get(name)
            if not p:
                print(f"[WARN] Pattern '{name}' inconnu."); continue

            self.cancel_event.clear()
            mode = p["mode"]
            lids, gid = self._targets()

            # ================= SOLID =================
            if mode == "solid":
                hold = max(0.0, p.get("hold", 0.0))
                end = time.monotonic() + hold
                while time.monotonic() < end:
                    if self.cancel_event.is_set() or self.stop_evt.is_set(): break
                    time.sleep(0.01)

                if not self.cancel_event.is_set():
                    # fin naturelle du SOLID
                    if p.get("then_off", False):
                        off_tt = self._tt(p, "off_transition_tenths", "off_transition_tenths")
                        self.bridge.set_state({"on": False, "transitiontime": int(max(0, off_tt))}, lids, gid)
                        # petite pause puis restore baseline
                        bconf = self.conf.get("baseline", {}) or {}
                        delay = float(bconf.get("delay_after_off_s", 0.05) or 0.0)
                        waited = 0.0
                        step = 0.01
                        while waited < delay and not self.cancel_event.is_set() and not self.stop_evt.is_set():
                            time.sleep(step); waited += step
                        if not self.cancel_event.is_set():
                            self._restore_baseline_if_enabled()
                    # sinon (then_off=false), on ne restaure pas ici
                continue

            # ================= BLINK =================
            if mode == "blink":
                method = (self.conf.get("behavior", {}).get("blink_method","alert")).lower()
                if method == "alert":
                    alert_mode = (p.get("alert_mode") or "lselect").lower()
                    if alert_mode in ("breathe", "lselect"):
                        self.bridge.set_state({"alert": "lselect"}, lids, gid)
                        duration = float(p.get("duration", 0) or 0)
                        watch = float(self.conf.get("behavior", {}).get("alert_watchdog_seconds", 0) or 0)
                        start = time.monotonic()
                        if duration > 0:
                            end = start + duration
                            while not self.cancel_event.is_set() and not self.stop_evt.is_set() and time.monotonic() < end:
                                time.sleep(0.02)
                            self.bridge.stop_alert(lids, gid)
                            if not self.cancel_event.is_set():
                                self._restore_baseline_if_enabled()
                        else:
                            while not self.cancel_event.is_set() and not self.stop_evt.is_set():
                                if watch and (time.monotonic() - start) > watch:
                                    self.bridge.stop_alert(lids, gid)
                                    break
                                time.sleep(0.05)
                        continue

                    elif alert_mode == "select":
                        repeats = int(p.get("select_repeats", 1))
                        gap = float(p.get("select_gap", 0.6))
                        repeats = max(1, repeats)
                        completed = 0
                        for _ in range(repeats):
                            if self.cancel_event.is_set() or self.stop_evt.is_set(): break
                            self.bridge.set_state({"alert": "select"}, lids, gid)
                            completed += 1
                            if self.cancel_event.wait(gap) or self.stop_evt.is_set(): break
                        if not self.cancel_event.is_set() and completed == repeats:
                            self._restore_baseline_if_enabled()
                        continue

                    else:
                        print(f"[WARN] alert_mode '{alert_mode}' inconnu, fallback lselect.")
                        self.bridge.set_state({"alert": "lselect"}, lids, gid)
                        while not self.cancel_event.is_set() and not self.stop_evt.is_set():
                            time.sleep(0.05)
                        continue

                # Fallbacks manuels (onoff/bri) — inchangés sauf qu’on cible (lids,gid)
                elif method == "onoff":
                    on_t  = float(p.get("on", 0.5)); off_t = float(p.get("off", 0.5))
                    start = time.monotonic(); next_sw = start + on_t; phase_on = True
                    while not self.cancel_event.is_set() and not self.stop_evt.is_set():
                        now = time.monotonic()
                        if now >= next_sw:
                            phase_on = not phase_on
                            if phase_on:
                                bri = int(p.get("bri_override", self.conf["bri"]))
                                tt = self._tt(p, "transition_tenths", "transition_tenths")
                                self._apply_from_pattern(p, bri, tt)
                                next_sw = now + on_t
                            else:
                                off_tt = self._tt(p, "off_transition_tenths", "off_transition_tenths")
                                self.bridge.set_state({"on": False, "transitiontime": int(max(0, off_tt))}, lids, gid)
                                next_sw = now + off_t
                    continue

                elif method == "bri":
                    on_t  = float(p.get("on", 0.5)); off_t = float(p.get("off", 0.5))
                    bri_hi = int(p.get("bri_override", self.conf["bri"])); bri_lo = 25
                    tt = self._tt(p, "transition_tenths", "transition_tenths")
                    if "ct" in p:
                        self.bridge.set_state({"on": True, "ct": int(p["ct"]), "bri": bri_hi, "transitiontime": int(max(0, tt))}, lids, gid)
                    else:
                        self.bridge.set_state({"on": True, "bri": bri_hi, "transitiontime": int(max(0, tt))}, lids, gid)
                    phase_on = True; next_sw = time.monotonic() + on_t
                    while not self.cancel_event.is_set() and not self.stop_evt.is_set():
                        now = time.monotonic()
                        if now >= next_sw:
                            phase_on = not phase_on
                            self.bridge.set_state({"on": True, "bri": (bri_hi if phase_on else bri_lo), "transitiontime": int(max(0, tt))}, lids, gid)
                            next_sw = now + (on_t if phase_on else off_t)
                    continue

                else:
                    print(f"[WARN] blink_method '{method}' inconnu, rien à faire.")
                    continue

            print(f"[WARN] Mode '{mode}' non supporté.")
