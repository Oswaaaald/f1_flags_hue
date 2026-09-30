import threading
import unittest
from unittest.mock import Mock

from f1_sources import F1SourceFormula1Live


class Formula1LiveSourceTests(unittest.TestCase):
    def test_session_clock_snapshot_and_update(self):
        src = F1SourceFormula1Live({}, threading.Event())
        src._on_message(Mock(result={
            "SessionInfo": {"SessionStatus": "Started", "Name": "Practice 3"},
            "ExtrapolatedClock": {"Utc": "2026-09-25T09:00:00Z", "Remaining": "00:10:00", "Extrapolating": True},
        }))
        src._on_message(["ExtrapolatedClock", {"Remaining": "00:09:50"}])
        self.assertEqual(src.session_name, "Practice 3")
        self.assertEqual(src._clock["Remaining"], "00:09:50")
        self.assertTrue(src._clock["Extrapolating"])

    def test_snapshot_and_live_race_control_flags(self):
        observed = []
        src = F1SourceFormula1Live({"ignore_blue": True}, threading.Event(), on_event=observed.append)
        snapshot = Mock(result={
            "SessionInfo": {"SessionStatus": "Started"},
            "TrackStatus": {"Status": "1"},
        })
        src._on_message(snapshot)
        src._on_message(["TrackStatus", {"Status": "5"}])
        src._on_message(["RaceControlMessages", {"Messages": {
            "2": {"Category": "SafetyCar", "Message": "VIRTUAL SAFETY CAR DEPLOYED"},
            "3": {"Category": "Flag", "Flag": "CHEQUERED"},
        }}])
        self.assertEqual([event.value for event in observed if event.kind == "flag"],
                         ["GREEN", "RED", "VSC", "CHEQUERED"])

    def test_snapshot_outside_session_does_not_replay_old_flag(self):
        observed = []
        src = F1SourceFormula1Live({}, threading.Event(), on_event=observed.append)
        src._on_message(Mock(result={
            "SessionInfo": {"SessionStatus": "Finished"},
            "TrackStatus": {"Status": "5"},
        }))
        self.assertFalse(any(event.kind == "flag" for event in observed))

    def test_race_control_keyframe_does_not_replay_session_history(self):
        observed = []
        src = F1SourceFormula1Live({}, threading.Event(), on_event=observed.append)
        src._on_message(Mock(result={
            "SessionInfo": {"SessionStatus": "Started"},
            "TrackStatus": {"Status": "1"},
        }))
        src._on_message(["RaceControlMessages", {"_kf": True, "Messages": [
            {"Category": "Flag", "Flag": "YELLOW"},
            {"Category": "Flag", "Flag": "RED"},
        ]}])
        self.assertEqual([event.value for event in observed if event.kind == "flag"], ["GREEN"])

    def test_chequered_after_finished_is_emitted(self):
        observed = []
        src = F1SourceFormula1Live({}, threading.Event(), on_event=observed.append)
        src._on_message(Mock(result={
            "SessionInfo": {"SessionStatus": "Started", "Type": "Practice"},
            "TrackStatus": {"Status": "1"},
        }))
        src._on_message(["SessionStatus", {"Status": "Finished"}])
        src._on_message(["RaceControlMessages", {"Messages": {
            "4": {"Category": "Flag", "Flag": "YELLOW"},
            "5": {"Category": "Flag", "Flag": "CHEQUERED"},
        }}])
        self.assertEqual([event.value for event in observed if event.kind == "flag"],
                         ["GREEN", "CHEQUERED"])

    def test_race_start_and_lap_are_only_live_transitions(self):
        observed = []
        src = F1SourceFormula1Live({}, threading.Event(), on_event=observed.append)
        src._on_message(Mock(result={
            "SessionInfo": {"SessionStatus": "Inactive", "Type": "Race", "Name": "Race"},
            "LapCount": {"CurrentLap": 1, "TotalLaps": 53},
        }))
        self.assertEqual([(event.value, event.initial) for event in observed if event.kind == "lap"],
                         [(1, True)])
        src._on_message(["SessionStatus", {"Status": "Started"}])
        src._on_message(["SessionInfo", {"SessionStatus": "Started"}])
        src._on_message(["LapCount", {"CurrentLap": 2}])
        self.assertEqual([(event.value, event.initial) for event in observed if event.kind == "session_status"],
                         [("Inactive", True), ("Started", False)])
        self.assertEqual([(event.value, event.initial) for event in observed if event.kind == "lap"],
                         [(1, True), (2, False)])

    def test_session_info_start_can_arrive_before_session_status(self):
        observed = []
        src = F1SourceFormula1Live({}, threading.Event(), on_event=observed.append)
        src._on_message(Mock(result={
            "SessionInfo": {"SessionStatus": "Inactive", "Type": "Race"},
        }))
        src._on_message(["SessionInfo", {"SessionStatus": "Started"}])
        src._on_message(["SessionStatus", {"Status": "Started"}])
        self.assertEqual([event.value for event in observed if event.kind == "session_status"],
                         ["Inactive", "Started"])


if __name__ == "__main__":
    unittest.main()
