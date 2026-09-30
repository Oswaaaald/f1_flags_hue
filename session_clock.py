"""Compare the F1 session countdown with a TV countdown."""

from datetime import datetime, timezone
import math
import re


def parse_tv_remaining(value: str) -> int:
    """Accept M:SS or H:MM:SS and return whole seconds."""
    if not isinstance(value, str) or not re.fullmatch(r"\d{1,3}:[0-5]\d(?::[0-5]\d)?", value.strip()):
        raise ValueError("Entre le temps TV au format MM:SS ou H:MM:SS.")
    parts = [int(part) for part in value.strip().split(":")]
    seconds = parts[0] * 60 + parts[1] if len(parts) == 2 else parts[0] * 3600 + parts[1] * 60 + parts[2]
    if seconds > 6 * 3600:
        raise ValueError("Le temps TV dépasse six heures.")
    return seconds


def api_remaining_seconds(clock: dict, at: datetime) -> float:
    """Project the feed clock to the moment the user clicked Compare."""
    if clock.get("Extrapolating") is not True:
        raise ValueError("L’horloge F1 est arrêtée : attends qu’elle reparte.")
    try:
        anchor = datetime.fromisoformat(clock["Utc"].replace("Z", "+00:00"))
        if anchor.tzinfo is None:
            raise ValueError
        anchor_remaining = parse_tv_remaining(clock["Remaining"])
        elapsed = (at.astimezone(timezone.utc) - anchor).total_seconds()
    except (KeyError, TypeError, AttributeError, ValueError) as e:
        raise ValueError("Horloge F1 incomplète ou invalide.") from e
    if not math.isfinite(elapsed) or not -30 <= elapsed <= 6 * 3600:
        raise ValueError("Horloge F1 périmée ou horloge du Mac désynchronisée.")
    remaining = anchor_remaining - elapsed
    if remaining <= 0:
        raise ValueError("Le chrono F1 est terminé.")
    return remaining


def compare_tv_clock(clock: dict, tv_value: str, clicked_at: datetime) -> dict:
    tv_seconds = parse_tv_remaining(tv_value)
    api_seconds = api_remaining_seconds(clock, clicked_at)
    # The TV displays whole seconds; use the middle of that one-second interval.
    estimated_delay = round(tv_seconds + 0.5 - api_seconds, 1)
    return {
        "tv_remaining": tv_value.strip(),
        "api_remaining_seconds": round(api_seconds, 2),
        "estimated_offset_seconds": estimated_delay,
        "can_apply": 0 <= estimated_delay <= 3600,
    }
