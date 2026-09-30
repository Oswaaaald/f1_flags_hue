"""Small, fixed catalog of past sessions from Formula 1's own timing archive."""

from dataclasses import dataclass
from functools import lru_cache
import json
import re

import requests

from live_events import LiveEvent


ARCHIVE_ROOT = "https://livetiming.formula1.com/static/"
MAX_STREAM_BYTES = 2_000_000


@dataclass(frozen=True)
class HistoricalScenario:
    id: str
    label: str
    description: str
    path: str


SCENARIOS = (
    HistoricalScenario("baku-qualifying-2025", "Bakou 2025 · Qualifications",
                       "Plusieurs drapeaux rouges, jaunes et drapeaux à damier",
                       "2025/2025-09-21_Azerbaijan_Grand_Prix/2025-09-20_Qualifying/"),
    HistoricalScenario("silverstone-race-2025", "Silverstone 2025 · Course",
                       "Voitures de sécurité, VSC, jaunes et arrivée",
                       "2025/2025-07-06_British_Grand_Prix/2025-07-06_Race/"),
    HistoricalScenario("monza-practice-2025", "Monza 2025 · Essais libres 3",
                       "Séance courte avec drapeaux jaunes et damier",
                       "2025/2025-09-07_Italian_Grand_Prix/2025-09-06_Practice_3/"),
)
SCENARIOS_BY_ID = {scenario.id: scenario for scenario in SCENARIOS}


def _stream_rows(content: bytes):
    for line in content.decode("utf-8-sig").splitlines():
        match = re.match(r"^(\d{2}):(\d{2}):(\d{2})\.(\d{3})(\{.*\})$", line)
        if match is None:
            continue
        hours, minutes, seconds, milliseconds = map(int, match.groups()[:4])
        yield hours * 3600 + minutes * 60 + seconds + milliseconds / 1000, json.loads(match[5])


def _archive_stream(scenario: HistoricalScenario, topic: str) -> bytes:
    # The path and topic are selected by this module; user input never becomes a URL.
    response = requests.get(ARCHIVE_ROOT + scenario.path + topic + ".jsonStream",
                            timeout=10, allow_redirects=False)
    response.raise_for_status()
    if len(response.content) > MAX_STREAM_BYTES:
        raise ValueError("Archive F1 trop volumineuse.")
    return response.content


def parse_archive(scenario: HistoricalScenario, statuses: bytes,
                  messages: bytes) -> tuple[LiveEvent, ...]:
    starts = [at for at, payload in _stream_rows(statuses)
              if payload.get("Status") == "Started"]
    if not starts:
        raise ValueError("Aucun départ de séance dans l’archive F1.")
    started_at = min(starts)
    result = []
    last_flag = None
    for at, payload in _stream_rows(messages):
        if at < started_at or payload.get("_kf"):
            continue
        rows = payload.get("Messages") or []
        if isinstance(rows, dict):
            rows = [rows[key] for key in sorted(rows, key=lambda key: (0, int(key)) if str(key).isdigit() else (1, str(key)))]
        if not isinstance(rows, list):
            continue
        for row in rows:
            if not isinstance(row, dict):
                continue
            category = str(row.get("Category") or "").lower()
            flag = str(row.get("Flag") or "").upper()
            message = str(row.get("Message") or "").upper()
            if category == "flag":
                if flag == "DOUBLE YELLOW":
                    flag = "YELLOW"
                if flag not in {"GREEN", "YELLOW", "RED", "CHEQUERED"}:
                    continue
            elif category == "safetycar":
                if "VSC" in message or "VIRTUAL SAFETY CAR" in message:
                    flag = "VSC_ENDING" if "ENDING" in message else "VSC"
                elif "SAFETY CAR" in message:
                    flag = "SC_ENDING" if "ENDING" in message or "IN THIS LAP" in message else "SC"
                else:
                    continue
            else:
                continue
            if flag == last_flag:
                continue
            last_flag = flag
            result.append(LiveEvent(kind="flag", value=flag, session_name=scenario.label,
                                    source_utc=row.get("Utc"), received_at=at,
                                    details={"archive": scenario.id}))
    if not result:
        raise ValueError("Aucun drapeau exploitable dans l’archive F1.")
    return tuple(result)


@lru_cache(maxsize=len(SCENARIOS))
def historical_events(scenario_id: str) -> tuple[LiveEvent, ...]:
    scenario = SCENARIOS_BY_ID[scenario_id]
    return parse_archive(scenario, _archive_stream(scenario, "SessionStatus"),
                         _archive_stream(scenario, "RaceControlMessages"))
