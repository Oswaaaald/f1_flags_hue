"""One F1 connection, shared state and event subscriptions per process."""

from queue import Empty, Full, Queue
import threading

from event_journal import EventJournal
from f1_sources import F1SourceFormula1Live
from live_events import LiveEvent


class LiveTimingService:
    def __init__(self, flags_conf=None, journal=None, source_factory=F1SourceFormula1Live):
        self.flags_conf = flags_conf or {}
        self.journal = journal if journal is not None else EventJournal()
        self.source_factory = source_factory
        self.stop_evt = threading.Event()
        self._lock = threading.Lock()
        self._updates = Queue()
        self._subscribers = set()
        self._ready = threading.Event()
        self._started = False
        self._dispatch_thread = None
        self._source_thread = None
        self.source = None
        self._state = {
            "connected": False, "session_key": None, "session_name": None,
            "session_type": None, "session_status": None, "current_lap": None,
            "total_laps": None, "clock": None, "last_flag": None,
            "last_data_at": None, "last_error": None,
        }

    def start(self):
        with self._lock:
            if self._started:
                return
            self._started = True
            self.source = self.source_factory(self.flags_conf, self.stop_evt, on_event=self._ingest)
            self._dispatch_thread = threading.Thread(target=self._dispatch, daemon=True, name="f1-events")
            self._source_thread = threading.Thread(target=self._run_source, daemon=True, name="f1-feed")
            self._dispatch_thread.start()
            self._source_thread.start()

    def _run_source(self):
        try:
            self.source.run()
        except Exception as exc:
            self._ingest(LiveEvent(kind="error", value=str(exc)))
        finally:
            self._ingest(LiveEvent(kind="connection", value="disconnected"))

    def _ingest(self, event: LiveEvent):
        self._updates.put(event)

    def _dispatch(self):
        while not self.stop_evt.is_set() or not self._updates.empty():
            try:
                event = self._updates.get(timeout=0.25)
            except Empty:
                continue
            with self._lock:
                state = self._state
                if event.kind == "connection":
                    state["connected"] = event.value == "connected"
                    if not state["connected"]:
                        self._ready.clear()
                    else:
                        state["last_error"] = None
                elif event.kind == "snapshot":
                    if event.details.get("session_key") != state["session_key"]:
                        state["last_flag"] = None
                    state.update(event.details)
                    self._ready.set()
                elif event.kind == "session_info":
                    if event.details.get("session_key") != state["session_key"]:
                        state["last_flag"] = None
                    state.update(event.details)
                elif event.kind == "session_status":
                    state["session_status"] = event.value
                elif event.kind == "lap":
                    state["current_lap"] = event.value
                    state["total_laps"] = event.details.get("total_laps")
                elif event.kind == "clock":
                    state["clock"] = event.details
                elif event.kind == "flag":
                    state["last_flag"] = event.value
                elif event.kind == "error":
                    state["last_error"] = event.value
                if event.kind not in ("connection", "error"):
                    state["last_data_at"] = event.received_at
                subscribers = tuple(self._subscribers)
            try:
                self.journal.append(event)
            except Exception as exc:
                with self._lock:
                    self._state["last_error"] = f"Journal : {type(exc).__name__}"
            for subscriber in subscribers:
                try:
                    subscriber.put_nowait(event)
                except Full:
                    # A slow client must not block the SignalR callback or Hue.
                    try:
                        subscriber.get_nowait()
                        subscriber.put_nowait(event)
                    except (Empty, Full):
                        pass

    def wait_ready(self, timeout=10):
        self.start()
        if not self._ready.wait(timeout):
            raise ConnectionError("Le flux F1 n'a pas fourni d'état initial.")
        state = self.status()
        if not state["connected"]:
            raise ConnectionError("Connexion au flux F1 interrompue.")
        return state

    def status(self) -> dict:
        with self._lock:
            return dict(self._state)

    def subscribe(self, replay_current_flag=False) -> Queue:
        subscriber = Queue(maxsize=512)
        with self._lock:
            self._subscribers.add(subscriber)
            if replay_current_flag and self._state["session_status"] == "Started" and self._state["last_flag"]:
                subscriber.put_nowait(LiveEvent(
                    kind="flag", value=self._state["last_flag"],
                    session_key=self._state["session_key"],
                    session_name=self._state["session_name"],
                    session_type=self._state["session_type"], initial=True))
        return subscriber

    def unsubscribe(self, subscriber: Queue):
        with self._lock:
            self._subscribers.discard(subscriber)

    def events(self, subscriber: Queue, consumer_stop: threading.Event, kinds=None):
        try:
            while not consumer_stop.is_set() and not self.stop_evt.is_set():
                try:
                    event = subscriber.get(timeout=0.25)
                except Empty:
                    continue
                if kinds is None or event.kind in kinds:
                    yield event
        finally:
            self.unsubscribe(subscriber)

    def stop(self):
        self.stop_evt.set()
        if self._source_thread is not None:
            self._source_thread.join(timeout=3)
        if self._dispatch_thread is not None:
            self._dispatch_thread.join(timeout=3)
        self.journal.close()
