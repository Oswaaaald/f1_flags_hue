import threading
import time
import unittest
from types import SimpleNamespace
from unittest.mock import Mock, patch

from sync_events import delayed_events


class DelayedEventsTests(unittest.TestCase):
    def test_events_keep_their_original_spacing(self):
        now = [0.0]
        stop = Mock(spec=threading.Event)
        stop.is_set.return_value = False

        def wait(seconds):
            now[0] += seconds
            return False

        stop.wait.side_effect = wait
        source = [SimpleNamespace(value="GREEN", received_mono=0.0),
                  SimpleNamespace(value="CHEQUERED", received_mono=0.04)]
        with patch("sync_events.time.monotonic", side_effect=lambda: now[0]):
            received = [(event.value, now[0])
                        for event in delayed_events(iter(source), 0.12, stop)]
        self.assertEqual([event for event, _ in received], ["GREEN", "CHEQUERED"])
        self.assertAlmostEqual(received[0][1], 0.12)
        self.assertAlmostEqual(received[1][1], 0.16)
        self.assertAlmostEqual(received[1][1] - received[0][1], 0.04)

    def test_stop_cancels_pending_event(self):
        stop = threading.Event()
        timer = threading.Timer(0.02, stop.set)
        timer.start()
        try:
            self.assertEqual(list(delayed_events(iter(["RED"]), 0.2, stop)), [])
        finally:
            timer.join()

    def test_final_event_plays_after_source_has_ended(self):
        stop = threading.Event()
        started = time.monotonic()
        self.assertEqual(list(delayed_events(iter(["CHEQUERED"]), 0.06, stop)), ["CHEQUERED"])
        self.assertGreaterEqual(time.monotonic() - started, 0.05)
