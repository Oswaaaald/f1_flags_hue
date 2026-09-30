import threading
import time
import unittest

from sync_events import delayed_events


class DelayedEventsTests(unittest.TestCase):
    def test_events_keep_their_original_spacing(self):
        stop = threading.Event()

        def source():
            yield "GREEN"
            time.sleep(0.04)
            yield "CHEQUERED"

        start = time.monotonic()
        received = [(event, time.monotonic() - start)
                    for event in delayed_events(source(), 0.12, stop)]
        self.assertEqual([event for event, _ in received], ["GREEN", "CHEQUERED"])
        self.assertGreaterEqual(received[0][1], 0.10)
        self.assertGreaterEqual(received[1][1] - received[0][1], 0.025)
        self.assertLess(received[1][1] - received[0][1], 0.10)

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
