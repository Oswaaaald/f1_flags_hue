"""Delay live events by their arrival time, without adding delay per event."""

import threading
import time
from queue import Empty, Queue


def delayed_events(events, offset_seconds, stop_evt):
    delay = max(0.0, float(offset_seconds))
    incoming = Queue()
    finished = object()

    def collect():
        try:
            for event in events:
                if stop_evt.is_set():
                    break
                incoming.put((getattr(event, "received_mono", time.monotonic()), event))
        except Exception as exc:
            incoming.put(exc)
        finally:
            incoming.put(finished)

    worker = threading.Thread(target=collect, daemon=True)
    worker.start()
    while not stop_evt.is_set():
        try:
            item = incoming.get(timeout=0.25)
        except Empty:
            continue
        if item is finished:
            return
        if isinstance(item, Exception):
            raise item
        received_at, event = item
        if stop_evt.wait(max(0.0, received_at + delay - time.monotonic())):
            return
        yield event
