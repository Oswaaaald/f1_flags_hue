"""Map F1 events to named effects without depending on a Hue bridge."""

from dataclasses import replace
import threading
import time
from typing import Protocol

from live_events import LiveEvent
from sync_events import delayed_events


class EffectOutput(Protocol):
    def play(self, pattern_name: str) -> None: ...


class EffectRules:
    def __init__(self, conf: dict):
        self.patterns = conf.get("patterns") or {}
        self.overrides = (conf.get("rules") or {}).get("flag_patterns") or {}
        for flag, pattern in self.overrides.items():
            if pattern is not None and pattern not in self.patterns:
                raise ValueError(f"Règle invalide pour {flag} : pattern {pattern} inconnu.")

    def pattern_for(self, event: LiveEvent) -> str | None:
        if event.kind != "flag":
            return None
        pattern = self.overrides.get(str(event.value), event.value)
        return pattern if pattern in self.patterns else None


def play_effects(events, output: EffectOutput, conf: dict, stop_evt: threading.Event,
                 offset_seconds=0, on_play=None):
    rules = EffectRules(conf)
    for event in delayed_events(events, offset_seconds, stop_evt):
        if stop_evt.is_set():
            break
        pattern = rules.pattern_for(event)
        if pattern is None:
            continue
        output.play(pattern)
        if on_play is not None:
            on_play(event, pattern)
        if event.value == "CHEQUERED" and conf.get("flags", {}).get("exit_on_chequered", False):
            break


def replay_events(recorded: list[LiveEvent], speed: float, stop_evt: threading.Event):
    """Yield a local recording at a chosen speed; never contacts the F1 feed."""
    speed = float(speed)
    if not 0.1 <= speed <= 100:
        raise ValueError("La vitesse de replay doit être comprise entre 0,1 et 100.")
    previous = None
    for event in recorded:
        if stop_evt.is_set():
            return
        if previous is not None:
            gap = max(0.0, event.received_at - previous) / speed
            if stop_evt.wait(gap):
                return
        previous = event.received_at
        yield replace(event, received_at=time.time(), received_mono=time.monotonic(),
                      details={**event.details, "recorded_at": event.received_at})
