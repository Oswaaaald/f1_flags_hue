"""Validated, browser-facing effect settings built on the existing YAML config."""

import copy
import math


FLAGS = ("GREEN", "YELLOW", "RED", "SC", "SC_ENDING", "VSC", "VSC_ENDING", "BLUE", "CHEQUERED")


def effect_duration(pattern: dict):
    if "duration_seconds" in pattern:
        return pattern["duration_seconds"]
    if pattern.get("mode") == "solid":
        return float(pattern.get("hold", 0)) if pattern.get("then_off") else None
    if pattern.get("alert_mode") == "select":
        return round(float(pattern.get("select_repeats", 1)) * float(pattern.get("select_gap", 0.6)), 2)
    duration = float(pattern.get("duration", 0) or 0)
    return duration if duration > 0 else None


def flag_enabled(conf: dict, flag: str) -> bool:
    explicit = (conf.get("flags") or {}).get("enabled") or {}
    if flag in explicit:
        return explicit[flag] is True
    if flag == "BLUE" and (conf.get("flags") or {}).get("ignore_blue", True):
        return False
    return ((conf.get("rules") or {}).get("flag_patterns") or {}).get(flag, flag) is not None


def public_settings(conf: dict) -> dict:
    patterns = conf.get("patterns") or {}
    return {
        "effects": {flag: {
            "enabled": flag_enabled(conf, flag),
            "duration_seconds": effect_duration(patterns.get(flag) or {}),
        } for flag in FLAGS},
        "preferences": {
            "brightness": int(conf.get("bri", 254)),
            "transition_seconds": float(conf.get("transition_tenths", 5)) / 10,
            "restore_on_exit": bool((conf.get("baseline") or {}).get("restore_on_exit", True)),
            "alert_watchdog_seconds": float((conf.get("behavior") or {}).get("alert_watchdog_seconds", 600)),
            "exit_on_chequered": bool((conf.get("flags") or {}).get("exit_on_chequered", False)),
        },
    }


def _number(value, name, low, high):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or not low <= value <= high:
        raise ValueError(f"{name} doit être compris entre {low} et {high}.")
    return float(value)


def apply_settings_patch(conf: dict, body: dict) -> dict:
    """Return a new config; reject unknown fields and never accept Hue credentials."""
    if not isinstance(body, dict) or not body or set(body) - {"effects", "preferences"}:
        raise ValueError("Réglages inconnus ou vides.")
    result = copy.deepcopy(conf)
    if "effects" in body:
        effects = body["effects"]
        if not isinstance(effects, dict) or not effects or set(effects) - set(FLAGS):
            raise ValueError("Liste de drapeaux invalide.")
        for flag, change in effects.items():
            if not isinstance(change, dict) or not change or set(change) - {"enabled", "duration_seconds"}:
                raise ValueError(f"Réglages invalides pour {flag}.")
            if flag not in (result.get("patterns") or {}):
                raise ValueError(f"Effet {flag} absent de la configuration.")
            if "enabled" in change:
                if not isinstance(change["enabled"], bool):
                    raise ValueError(f"Activation invalide pour {flag}.")
                result.setdefault("flags", {}).setdefault("enabled", {})[flag] = change["enabled"]
                if change["enabled"] and ((result.get("rules") or {}).get("flag_patterns") or {}).get(flag, flag) is None:
                    result["rules"]["flag_patterns"].pop(flag)
            if "duration_seconds" in change:
                duration = change["duration_seconds"]
                result["patterns"][flag]["duration_seconds"] = None if duration is None else _number(duration, f"Durée de {flag}", 0.1, 3600)
    if "preferences" in body:
        prefs = body["preferences"]
        allowed = {"brightness", "transition_seconds", "restore_on_exit", "alert_watchdog_seconds", "exit_on_chequered"}
        if not isinstance(prefs, dict) or not prefs or set(prefs) - allowed:
            raise ValueError("Préférences invalides.")
        for key, value in prefs.items():
            if key == "brightness":
                number = _number(value, "Luminosité", 1, 254)
                if not number.is_integer():
                    raise ValueError("La luminosité doit être un entier.")
                result["bri"] = int(number)
            elif key == "transition_seconds":
                result["transition_tenths"] = round(_number(value, "Transition", 0, 10) * 10)
            elif key == "alert_watchdog_seconds":
                result.setdefault("behavior", {})[key] = _number(value, "Limite de clignotement", 30, 3600)
            else:
                if not isinstance(value, bool):
                    raise ValueError(f"{key} doit être activé ou désactivé.")
                target = "baseline" if key == "restore_on_exit" else "flags"
                result.setdefault(target, {})[key] = value
    return result
