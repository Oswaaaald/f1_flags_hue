"""Serialize Hue writes so a cancelled effect cannot overwrite a newer one."""

import copy
import threading
import time
from queue import Empty, SimpleQueue

from settings import effect_duration


class LightEngine:
    def __init__(self, bridge, conf: dict, stop_evt: threading.Event, baseline=None, on_error=None, on_state=None):
        self.bridge = bridge
        self.conf = conf  # Fixed Hue target for this run.
        self.stop_evt = stop_evt
        self.baseline = baseline
        self.on_error = on_error
        self.on_state = on_state
        self.cmd = SimpleQueue()
        self.thread = threading.Thread(target=self._run, daemon=True)
        self.running = threading.Event()
        self.running.set()
        self._lock = threading.Lock()
        self._cancel = None

    def _targets(self):
        gid = self.conf.get("group_id")
        if gid is None:
            gid = self.conf.get("_sync_group_id")
        if gid is not None:
            return None, gid
        ids = self.conf.get("_resolved_light_ids") or self.conf.get("light_ids")
        if ids:
            return ids, None
        raise RuntimeError("Aucune lampe Hue sélectionnée.")

    def start(self):
        if not self.thread.is_alive():
            self.thread.start()

    def _enqueue(self, kind, name=None, effect_conf=None):
        with self._lock:
            if not self.running.is_set():
                return
            if self._cancel is not None:
                self._cancel.set()
            cancel = threading.Event()
            self._cancel = cancel
            self.cmd.put((kind, name, copy.deepcopy(effect_conf or self.conf), cancel))

    def play(self, pattern_name: str, effect_conf=None):
        self._enqueue("play", pattern_name, effect_conf)

    def clear(self, effect_conf=None):
        self._enqueue("clear", effect_conf=effect_conf)

    def stop(self):
        with self._lock:
            self.running.clear()
            self.stop_evt.set()
            if self._cancel is not None:
                self._cancel.set()
            self.cmd.put(("stop", None, None, None))
        if self.thread.is_alive():
            self.thread.join()
        if self.on_state:
            self.on_state(None)

    @staticmethod
    def _tt(pattern, conf, key):
        return max(0, int(pattern.get(key, conf.get(key, 0)) or 0))

    def _apply(self, pattern, conf):
        lids, gid = self._targets()
        maximum = int(conf.get("bri", 254))
        desired = int(pattern.get("bri_override", maximum))
        payload = {"on": True, "bri": max(1, min(254, maximum, desired)),
                   "transitiontime": self._tt(pattern, conf, "transition_tenths")}
        if "ct" in pattern:
            payload["ct"] = int(pattern["ct"])
        else:
            payload["xy"] = conf["colors_xy"][pattern["color"]]
        self.bridge.set_state(payload, lids, gid)

    def _restore(self, conf):
        if self.baseline:
            fade = max(0, int((conf.get("baseline") or {}).get("fade_tenths", 5)))
            self.baseline.restore(self.bridge, self.conf, fade_tenths=fade)

    def _wait(self, cancel, seconds):
        if seconds is None:
            while self.running.is_set() and not self.stop_evt.is_set() and not cancel.wait(0.1):
                pass
            return False
        return not cancel.wait(max(0, seconds)) and self.running.is_set() and not self.stop_evt.is_set()

    def _play(self, name, conf, cancel):
        if cancel.is_set() or self.stop_evt.is_set():
            return
        pattern = (conf.get("patterns") or {}).get(name)
        if not pattern:
            raise ValueError(f"Pattern Hue inconnu : {name}")
        lids, gid = self._targets()
        self.bridge.stop_alert(lids, gid)
        if cancel.is_set():
            return
        self._apply(pattern, conf)
        if self.on_state:
            self.on_state(name)
        duration = effect_duration(pattern)
        mode = pattern.get("mode")
        if mode == "solid":
            if self._wait(cancel, duration):
                self._restore(conf)
                if self.on_state:
                    self.on_state(None)
            return
        if mode != "blink":
            raise ValueError(f"Mode Hue inconnu : {mode}")

        watchdog = float((conf.get("behavior") or {}).get("alert_watchdog_seconds", 600) or 600)
        end = time.monotonic() + (duration if duration is not None else watchdog)
        method = (conf.get("behavior") or {}).get("blink_method", "alert").lower()
        if method == "alert":
            alert_mode = (pattern.get("alert_mode") or "lselect").lower()
            if alert_mode in ("lselect", "breathe"):
                self.bridge.set_state({"alert": "lselect"}, lids, gid)
                complete = self._wait(cancel, max(0, end - time.monotonic()))
            elif alert_mode == "select":
                gap = max(0.1, float(pattern.get("select_gap", 0.7)))
                complete = False
                while not cancel.is_set() and not self.stop_evt.is_set():
                    remaining = end - time.monotonic()
                    if remaining <= 0:
                        complete = True
                        break
                    self.bridge.set_state({"alert": "select"}, lids, gid)
                    if cancel.wait(min(gap, remaining)):
                        break
                if not cancel.is_set() and not self.stop_evt.is_set() and time.monotonic() >= end:
                    complete = True
            else:
                raise ValueError(f"Alerte Hue inconnue : {alert_mode}")
            if not cancel.is_set():
                self.bridge.stop_alert(lids, gid)
                if complete:
                    self._restore(conf)
                    if self.on_state:
                        self.on_state(None)
            return

        on_time = max(0.1, float(pattern.get("on", 0.5)))
        off_time = max(0.1, float(pattern.get("off", 0.5)))
        high = min(int(conf.get("bri", 254)), int(pattern.get("bri_override", 254)))
        low = min(high, 25)
        phase_on = True
        while not cancel.is_set() and not self.stop_evt.is_set() and time.monotonic() < end:
            wait = on_time if phase_on else off_time
            if cancel.wait(min(wait, max(0, end - time.monotonic()))):
                break
            if time.monotonic() >= end:
                break
            phase_on = not phase_on
            if method == "onoff":
                if phase_on:
                    self._apply(pattern, conf)
                else:
                    self.bridge.set_state({"on": False, "transitiontime": self._tt(pattern, conf, "off_transition_tenths")}, lids, gid)
            elif method == "bri":
                self.bridge.set_state({"on": True, "bri": high if phase_on else low,
                                       "transitiontime": self._tt(pattern, conf, "transition_tenths")}, lids, gid)
            else:
                raise ValueError(f"Méthode de clignotement inconnue : {method}")
        if not cancel.is_set() and not self.stop_evt.is_set():
            self._restore(conf)
            if self.on_state:
                self.on_state(None)

    def _run(self):
        while True:
            try:
                kind, name, conf, cancel = self.cmd.get(timeout=0.1)
            except Empty:
                continue
            if kind == "stop":
                try:
                    lids, gid = self._targets()
                    self.bridge.stop_alert(lids, gid)
                except Exception:
                    pass
                return
            if cancel.is_set() or not self.running.is_set():
                continue
            try:
                if kind == "clear":
                    lids, gid = self._targets()
                    self.bridge.stop_alert(lids, gid)
                    if not cancel.is_set():
                        self._restore(conf)
                        if self.on_state:
                            self.on_state(None)
                else:
                    self._play(name, conf, cancel)
            except Exception as exc:
                if self.on_error:
                    self.on_error(exc)
                else:
                    print(f"[HUE] {exc}")
