import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from event_journal import EventJournal
from historical_replay import SCENARIOS, parse_archive
from live_events import LiveEvent
from web.server import app


class HistoricalReplayTests(unittest.TestCase):
    def test_archive_uses_session_start_and_normalizes_flags(self):
        statuses = (b'00:00:00.000{"Status":"Inactive"}\n'
                    b'00:10:00.000{"Status":"Started"}\n')
        messages = (
            b'00:09:59.000{"Messages":{"1":{"Category":"Flag","Flag":"GREEN"}}}\n'
            b'00:10:01.000{"Messages":[{"Category":"Flag","Flag":"DOUBLE YELLOW","Utc":"2025-01-01T10:00:01"}]}\n'
            b'00:10:02.000{"Messages":{"2":{"Category":"Flag","Flag":"YELLOW"}}}\n'
            b'00:10:03.000{"Messages":{"3":{"Category":"SafetyCar","Message":"SAFETY CAR DEPLOYED"}}}\n'
            b'00:10:04.000{"Messages":{"4":{"Category":"Flag","Flag":"RED"}}}\n'
            b'00:10:05.000{"Messages":{"5":{"Category":"Flag","Flag":"CHEQUERED"}}}\n')
        events = parse_archive(SCENARIOS[0], statuses, messages)
        self.assertEqual([event.value for event in events],
                         ["YELLOW", "SC", "RED", "CHEQUERED"])
        self.assertEqual([event.received_at for event in events], [601, 603, 604, 605])

    def test_catalog_exists_with_empty_local_journal(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "events.sqlite3"
            with patch("web.server.EventJournal", side_effect=lambda: EventJournal(path)):
                response = app.test_client().get("/api/replay/scenarios")
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.json["local"], [])
        self.assertGreaterEqual(len(response.json["historical"]), 3)

    def test_historical_replay_resolves_events_before_starting_hue(self):
        events = (LiveEvent(kind="flag", value="RED", received_at=10),)
        with patch("web.server.historical_events", return_value=events) as archive, \
             patch("web.server.RUNNER") as runner:
            response = app.test_client().post("/api/replay/start",
                                              json={"scenario_id": SCENARIOS[0].id, "speed": 10})
            self.assertEqual(response.status_code, 200)
            archive.assert_called_once_with(SCENARIOS[0].id)
            runner.start_replay.assert_called_once_with(list(events), 10.0)
            unknown = app.test_client().post("/api/replay/start",
                                             json={"scenario_id": "https://other.example/x", "speed": 10})
            self.assertEqual(unknown.status_code, 404)


if __name__ == "__main__":
    unittest.main()
