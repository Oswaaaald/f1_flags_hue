"""Small local journal of derived session events, never raw F1 packets."""

import json
import os
from pathlib import Path
import sqlite3
import threading

from live_events import LiveEvent


DEFAULT_PATH = Path(__file__).resolve().parent / ".data" / "events.sqlite3"
RECORDED_KINDS = {"flag", "session_status", "lap"}


class EventJournal:
    def __init__(self, path=DEFAULT_PATH):
        self.path = Path(path)
        if self.path != Path(":memory:"):
            self.path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
            if not self.path.exists():
                try:
                    descriptor = os.open(self.path, os.O_CREAT | os.O_EXCL | os.O_RDWR, 0o600)
                except FileExistsError:
                    pass
                else:
                    os.close(descriptor)
        self._lock = threading.Lock()
        self._db = sqlite3.connect(str(self.path), check_same_thread=False)
        self._db.execute("""CREATE TABLE IF NOT EXISTS events (
            id INTEGER PRIMARY KEY,
            received_at REAL NOT NULL,
            session_key TEXT,
            session_name TEXT,
            session_type TEXT,
            kind TEXT NOT NULL,
            value_json TEXT,
            source_utc TEXT,
            initial INTEGER NOT NULL,
            details_json TEXT NOT NULL
        )""")
        self._db.execute("CREATE INDEX IF NOT EXISTS events_session_time ON events(session_key, received_at)")
        self._db.execute("""CREATE UNIQUE INDEX IF NOT EXISTS events_source_unique
            ON events(session_key, kind, value_json, source_utc)
            WHERE source_utc IS NOT NULL""")
        self._db.commit()
        if self.path != Path(":memory:"):
            self.path.chmod(0o600)

    def append(self, event: LiveEvent):
        if event.kind not in RECORDED_KINDS or (event.kind == "flag" and event.initial):
            return
        with self._lock:
            self._db.execute("""INSERT OR IGNORE INTO events
                (received_at, session_key, session_name, session_type, kind, value_json,
                 source_utc, initial, details_json) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                (event.received_at, event.session_key, event.session_name, event.session_type,
                 event.kind, json.dumps(event.value), event.source_utc, int(event.initial),
                 json.dumps(event.details, ensure_ascii=False)))
            self._db.commit()

    def sessions(self, limit=20) -> list[dict]:
        with self._lock:
            rows = self._db.execute("""SELECT session_key, MAX(session_name), MAX(session_type),
                MIN(received_at), MAX(received_at),
                SUM(CASE WHEN kind = 'flag' THEN 1 ELSE 0 END)
                FROM events WHERE session_key IS NOT NULL
                GROUP BY session_key
                HAVING SUM(CASE WHEN kind = 'flag' THEN 1 ELSE 0 END) > 0
                ORDER BY MAX(received_at) DESC LIMIT ?""", (limit,)).fetchall()
        return [{"session_key": row[0], "session_name": row[1], "session_type": row[2],
                 "started_at": row[3], "last_event_at": row[4], "flag_count": row[5]} for row in rows]

    def flags(self, session_key: str) -> list[LiveEvent]:
        with self._lock:
            rows = self._db.execute("""SELECT received_at, session_key, session_name,
                session_type, value_json, source_utc, initial, details_json
                FROM events WHERE session_key = ? AND kind = 'flag'
                ORDER BY received_at, id""", (str(session_key),)).fetchall()
        return [LiveEvent(kind="flag", value=json.loads(row[4]), received_at=row[0],
                          session_key=row[1], session_name=row[2], session_type=row[3],
                          source_utc=row[5], initial=bool(row[6]), details=json.loads(row[7]))
                for row in rows]

    def recent_flags(self, limit: int = 20) -> list[dict]:
        limit = max(1, min(int(limit), 100))
        with self._lock:
            rows = self._db.execute("""SELECT id, received_at, source_utc, session_key,
                session_name, session_type, value_json
                FROM events WHERE kind = 'flag'
                ORDER BY received_at DESC, id DESC LIMIT ?""", (limit,)).fetchall()
        return [{"id": row[0], "received_at": row[1], "source_utc": row[2],
                 "session_key": row[3], "session_name": row[4], "session_type": row[5],
                 "flag": json.loads(row[6])} for row in rows]

    def close(self):
        with self._lock:
            self._db.close()
