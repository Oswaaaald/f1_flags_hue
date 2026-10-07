import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest.mock import Mock
from unittest.mock import patch

from effects import EffectRules, play_effects, replay_events
from engine import LightEngine
from event_journal import EventJournal
from f1_sources import F1SourceFormula1Live
from hue_targets import prepare_sync_group
from live_events import LiveEvent
from live_service import LiveTimingService
from web.server import Runner


class FeedEventTests(unittest.TestCase):
    def test_source_emits_typed_chequered_after_finished(self):
        observed = []
        source = F1SourceFormula1Live({}, threading.Event(), on_event=observed.append)
        source._on_message(Mock(result={
            "SessionInfo": {"Key": 9912, "Name": "Practice 3", "Type": "Practice",
                            "SessionStatus": "Started"},
            "TrackStatus": {"Status": "1"},
        }))
        source._on_message(["SessionStatus", {"Status": "Finished"}])
        source._on_message(["RaceControlMessages", {"Messages": {
            "1": {"Category": "Flag", "Flag": "CHEQUERED", "Utc": "2025-09-06T11:30:00Z"},
        }}])
        self.assertEqual([(event.kind, event.value) for event in observed if event.kind == "flag"],
                         [("flag", "GREEN"), ("flag", "CHEQUERED")])
        self.assertEqual(observed[-1].session_key, "9912")
        self.assertEqual(observed[-1].source_utc, "2025-09-06T11:30:00Z")

    def test_new_session_resets_previous_flag_and_lap(self):
        observed = []
        source = F1SourceFormula1Live({}, threading.Event(), on_event=observed.append)
        source._on_message(Mock(result={
            "SessionInfo": {"Key": 1, "Type": "Race", "SessionStatus": "Started"},
            "TrackStatus": {"Status": "1"}, "LapCount": {"CurrentLap": 5},
        }))
        source._on_message(Mock(result={
            "SessionInfo": {"Key": 2, "Type": "Practice", "SessionStatus": "Started"},
            "TrackStatus": {"Status": "1"},
        }))
        self.assertEqual(source.current_lap, None)
        self.assertEqual([event.value for event in observed if event.kind == "flag"],
                         ["GREEN", "GREEN"])


class JournalAndRulesTests(unittest.TestCase):
    def test_journal_replays_only_local_derived_flags(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "events.sqlite3"
            journal = EventJournal(path)
            journal.append(LiveEvent(kind="flag", value="RED", session_key="42",
                                     session_name="Race", received_at=100.0,
                                     source_utc="2025-09-07T13:01:00Z"))
            journal.append(LiveEvent(kind="flag", value="RED", session_key="42",
                                     session_name="Race", received_at=100.1,
                                     source_utc="2025-09-07T13:01:00Z"))
            journal.append(LiveEvent(kind="flag", value="GREEN", session_key="42", initial=True))
            journal.append(LiveEvent(kind="clock", session_key="42", details={"Remaining": "00:30:00"}))
            journal.close()
            journal = EventJournal(path)
            try:
                self.assertEqual(journal.sessions()[0]["flag_count"], 1)
                self.assertEqual([event.value for event in journal.flags("42")], ["RED"])
                self.assertEqual(journal.flags("43"), [])
                self.assertEqual(journal.recent_flags()[0]["source_utc"], "2025-09-07T13:01:00Z")
                self.assertEqual(journal.recent_flags()[0]["received_at"], 100.0)
            finally:
                journal.close()

    def test_rules_map_flags_without_a_hue_dependency(self):
        conf = {"patterns": {"RED": {}, "YELLOW": {}},
                "rules": {"flag_patterns": {"RED": "YELLOW"}}}
        output = Mock()
        play_effects(iter([LiveEvent(kind="flag", value="RED"),
                           LiveEvent(kind="lap", value=2)]),
                     output, conf, threading.Event())
        output.play.assert_called_once_with("YELLOW")
        self.assertIsNone(EffectRules(conf).pattern_for(LiveEvent(kind="lap", value=2)))

    def test_replay_uses_recorded_spacing(self):
        events = [LiveEvent(kind="flag", value="GREEN", received_at=100.0),
                  LiveEvent(kind="flag", value="RED", received_at=100.04)]
        started = time.monotonic()
        self.assertEqual([event.value for event in replay_events(events, 2, threading.Event())],
                         ["GREEN", "RED"])
        self.assertGreaterEqual(time.monotonic() - started, 0.015)

    def test_group_selection_remains_in_configuration(self):
        bridge = Mock()
        bridge.groups.return_value = {"2": {"lights": ["1", "3"]}}
        bridge.ensure_lightgroup.return_value = 7
        conf = {"group_id": None, "group_ids": [2], "light_ids": []}
        self.assertEqual(prepare_sync_group(conf, bridge), (7, 2))
        self.assertEqual(conf["group_ids"], [2])
        self.assertEqual(conf["_sync_group_id"], 7)

    def test_group_creation_failure_targets_only_selected_lights(self):
        bridge = Mock()
        bridge.groups.return_value = {"2": {"lights": ["1", "3"]}}
        bridge.ensure_lightgroup.return_value = None
        conf = {"group_id": None, "group_ids": [2], "light_ids": []}
        self.assertEqual(prepare_sync_group(conf, bridge), (None, 2))
        self.assertEqual(conf["_resolved_light_ids"], [1, 3])
        self.assertEqual(LightEngine(bridge, conf, threading.Event())._targets(), ([1, 3], None))


class ServiceTests(unittest.TestCase):
    def test_one_source_serves_state_subscribers_and_journal(self):
        created = []

        class FakeSource:
            def __init__(self, flags_conf, stop_evt, on_event):
                created.append(self)
                self.stop_evt = stop_evt
                self.on_event = on_event

            def run(self):
                self.on_event(LiveEvent(kind="connection", value="connected"))
                self.on_event(LiveEvent(kind="snapshot", session_key="7", details={
                    "session_key": "7", "session_name": "Race", "session_type": "Race",
                    "session_status": "Started", "current_lap": 1,
                    "total_laps": 50, "clock": None}))
                self.on_event(LiveEvent(kind="flag", value="GREEN", session_key="7",
                                        session_name="Race", session_type="Race"))
                while not self.stop_evt.wait(0.01):
                    pass

        journal = EventJournal(":memory:")
        service = LiveTimingService(journal=journal, source_factory=FakeSource)
        subscriber = service.subscribe()
        try:
            self.assertTrue(service.wait_ready()["connected"])
            self.assertEqual(service.wait_ready()["session_key"], "7")
            self.assertEqual(len(created), 1)
            received = [subscriber.get(timeout=1) for _ in range(3)]
            self.assertEqual(received[-1].value, "GREEN")
            self.assertEqual(journal.flags("7")[0].value, "GREEN")
        finally:
            service.unsubscribe(subscriber)
            service.stop()

    def test_replay_plays_hue_effect_without_a_feed_connection(self):
        runner = Runner()
        runner.conf = {"patterns": {"RED": {}}, "flags": {}}
        runner.engine = Mock()
        with patch.object(runner, "_prepare_common"), \
             patch.object(runner, "_restore_on_exit"):
            runner._run_replay([LiveEvent(kind="flag", value="RED", session_key="42")], 100)
        runner.engine.play.assert_called_once()
        self.assertEqual(runner.engine.play.call_args.args, ("RED",))
        self.assertIn("patterns", runner.engine.play.call_args.kwargs["effect_conf"])
        runner.engine.stop.assert_called_once()
