import os
import logging
import requests, time, threading
from live_events import LiveEvent

class F1SourceFormula1Live:
    """Unofficial consumer of Formula 1's public live timing SignalR feed."""

    URL = "wss://livetiming.formula1.com/signalrcore"
    NEGOTIATE_URL = "https://livetiming.formula1.com/signalrcore/negotiate"
    TRACK_FLAGS = {"1": "GREEN", "2": "YELLOW", "3": "GREEN", "4": "SC",
                   "5": "RED", "6": "VSC", "7": "VSC_ENDING"}

    def __init__(self, flags_conf: dict, stop_evt: threading.Event, on_event=None):
        self.flags_conf = flags_conf
        self.stop_evt = stop_evt
        self.on_event = on_event
        self._last_emitted = None
        self._session_started = False
        self._session_status = None
        self.session_name = None
        self.session_key = None
        self._clock = None
        self._snapshot_event = threading.Event()
        self.session_type = None
        self.current_lap = None
        self.total_laps = None
        self._closed_event = threading.Event()

    def _publish(self, kind, value=None, *, initial=False, source_utc=None, details=None):
        if self.on_event is None:
            return
        event = LiveEvent(kind=kind, value=value,
                          session_key=str(self.session_key) if self.session_key is not None else None,
                          session_name=self.session_name, session_type=self.session_type,
                          source_utc=source_utc, initial=initial, details=details or {})
        try:
            self.on_event(event)
        except Exception:
            logging.exception("L'observateur du flux F1 a échoué")

    def _emit(self, flag, *, initial=False, source_utc=None):
        if flag and flag != self._last_emitted:
            self._last_emitted = flag
            self._publish("flag", flag, initial=initial, source_utc=source_utc)

    def _set_session_status(self, status, initial=False):
        previous = self._session_status
        self._session_status = status
        self._session_started = status == "Started"
        if status != previous or initial:
            self._publish("session_status", status, initial=initial)

    def _race_control(self, data, only_chequered=False):
        # A keyframe contains the whole session history, not new events.
        if isinstance(data, dict) and data.get("_kf"):
            return
        messages = data.get("Messages", {}) if isinstance(data, dict) else {}
        if isinstance(messages, dict):
            messages = [messages[k] for k in sorted(messages, key=lambda k: (0, int(k)) if str(k).isdigit() else (1, str(k)))]
        for row in messages:
            if not isinstance(row, dict):
                continue
            category = (row.get("Category") or "").lower()
            flag = (row.get("Flag") or "").upper()
            msg = (row.get("Message") or "").upper()
            if only_chequered and flag != "CHEQUERED":
                continue
            if category == "flag":
                if flag == "DOUBLE YELLOW":
                    flag = "YELLOW"
                if flag in ("GREEN", "YELLOW", "RED", "CHEQUERED"):
                    self._emit(flag, source_utc=row.get("Utc"))
                elif flag == "BLUE":
                    self._emit("BLUE", source_utc=row.get("Utc"))
            elif category == "safetycar":
                if "VSC" in msg or "VIRTUAL SAFETY CAR" in msg:
                    self._emit("VSC_ENDING" if "ENDING" in msg else "VSC", source_utc=row.get("Utc"))
                elif "SAFETY CAR" in msg:
                    self._emit("SC_ENDING" if "ENDING" in msg or "IN THIS LAP" in msg else "SC", source_utc=row.get("Utc"))

    def _on_message(self, msg):
        # Subscribe returns a snapshot; subsequent feed callbacks contain deltas.
        if isinstance(getattr(msg, "result", None), dict):
            snapshot = msg.result
            session = snapshot.get("SessionInfo") or {}
            key = session.get("Key")
            if key is not None and key != self.session_key:
                self._last_emitted = None
                self.current_lap = None
                self.total_laps = None
                self._clock = None
            self.session_key = key
            self.session_name = session.get("Name")
            self.session_type = session.get("Type")
            status = session.get("SessionStatus")
            session_status = snapshot.get("SessionStatus") or {}
            if isinstance(session_status, dict) and session_status.get("Status"):
                status = session_status["Status"]
            self._set_session_status(status, initial=True)
            lap_count = snapshot.get("LapCount")
            if isinstance(lap_count, dict):
                self._update_lap_count(lap_count, emit=False)
            clock = snapshot.get("ExtrapolatedClock")
            if isinstance(clock, dict):
                self._clock = clock
                self._publish("clock", details=dict(clock), initial=True)
            self._publish("snapshot", initial=True, details=self.state_snapshot())
            if self._session_started:
                status = snapshot.get("TrackStatus") or {}
                self._emit(self.TRACK_FLAGS.get(str(status.get("Status"))), initial=True)
            self._snapshot_event.set()
            return
        if not isinstance(msg, list) or len(msg) < 2:
            return
        topic, data = msg[:2]
        if topic == "SessionInfo" and isinstance(data, dict):
            if "Name" in data:
                self.session_name = data["Name"]
            if "Type" in data:
                self.session_type = data["Type"]
            if "Key" in data:
                if data["Key"] != self.session_key:
                    self._last_emitted = None
                    self.current_lap = None
                    self.total_laps = None
                    self._clock = None
                self.session_key = data["Key"]
            if "SessionStatus" in data:
                self._set_session_status(data["SessionStatus"])
            self._publish("session_info", details=self.state_snapshot())
        elif topic == "SessionStatus" and isinstance(data, dict) and "Status" in data:
            self._set_session_status(data["Status"])
        elif topic == "ExtrapolatedClock" and isinstance(data, dict):
            self._clock = {**(self._clock or {}), **data}
            self._publish("clock", details=dict(self._clock))
        elif topic == "LapCount" and isinstance(data, dict):
            self._update_lap_count(data, emit=True)
        elif topic == "TrackStatus" and isinstance(data, dict):
            status = str(data.get("Status"))
            if self._session_started or (self._session_status == "Aborted" and status == "5"):
                self._emit(self.TRACK_FLAGS.get(status))
        elif topic == "RaceControlMessages" and self._session_status in ("Started", "Finished", "Aborted"):
            self._race_control(data, only_chequered=self._session_status == "Finished")

    def _update_lap_count(self, data: dict, emit: bool):
        try:
            if "TotalLaps" in data:
                self.total_laps = int(data["TotalLaps"])
            if "CurrentLap" not in data:
                return
            lap = int(data["CurrentLap"])
            if lap < 1 or lap > 500:
                return
        except (TypeError, ValueError):
            return
        previous = self.current_lap
        self.current_lap = lap
        if previous != lap:
            self._publish("lap", lap, initial=not emit,
                          details={"total_laps": self.total_laps})

    def _connect(self):
        from signalrcore.hub_connection_builder import HubConnectionBuilder
        from signalrcore.types import HubProtocolEncoding

        # signalrcore negotiates via urllib, which may not use the system CA on macOS.
        os.environ.setdefault("SSL_CERT_FILE", requests.certs.where())
        response = requests.options(self.NEGOTIATE_URL, timeout=8)
        cookie = response.cookies.get("AWSALBCORS")
        headers = {"Cookie": f"AWSALBCORS={cookie}"} if cookie else {}
        connection = (HubConnectionBuilder()
                      .with_url(self.URL, options={"verify_ssl": True, "headers": headers})
                      .with_hub_protocol(HubProtocolEncoding.text)
                      .configure_logging(logging.ERROR)
                      .build())
        opened = threading.Event()
        self._closed_event.clear()
        connection.on_open(opened.set)
        connection.on("feed", self._on_message)
        def on_close():
            self._closed_event.set()
            self._publish("connection", "disconnected")
        connection.on_close(on_close)
        connection.start()
        if not opened.wait(10):
            connection.stop()
            raise ConnectionError("Le flux F1 ne s'est pas ouvert en 10 secondes.")
        self._publish("connection", "connected")
        try:
            connection.send("Subscribe", [["RaceControlMessages", "TrackStatus", "SessionInfo", "SessionStatus", "ExtrapolatedClock", "LapCount"]],
                            on_invocation=self._on_message)
        except Exception:
            connection.stop()
            raise
        return connection

    def state_snapshot(self):
        return {"session_key": str(self.session_key) if self.session_key is not None else None,
                "session_name": self.session_name, "session_type": self.session_type,
                "session_status": self._session_status, "current_lap": self.current_lap,
                "total_laps": self.total_laps, "clock": dict(self._clock) if self._clock else None}

    def run(self):
        while not self.stop_evt.is_set():
            connection = None
            self._session_started = False
            self._session_status = None
            self._snapshot_event.clear()
            try:
                connection = self._connect()
                print("[F1] Connecté au live timing Formula 1.")
                while not self.stop_evt.is_set() and not self._closed_event.is_set():
                    self.stop_evt.wait(0.25)
            except Exception as e:
                print(f"[WARN] Live timing F1: {e}")
                self._publish("error", str(e))
            finally:
                if connection is not None:
                    try:
                        connection.stop()
                    except Exception:
                        pass
            self.stop_evt.wait(5)

    def check_connection(self):
        connection = self._connect()
        try:
            if not self._snapshot_event.wait(5):
                raise RuntimeError("État de séance absent du flux F1.")
            return self._session_started
        finally:
            connection.stop()
