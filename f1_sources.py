from datetime import datetime
import requests, time, threading

class F1SourceOpenF1REST:
    def __init__(self, base_url: str, poll_seconds: float, headers: dict | None, flags_conf: dict, stop_evt: threading.Event):
        self.base = base_url.rstrip("/"); self.poll = poll_seconds
        self.headers = headers or {}; self.flags_conf = flags_conf; self.stop_evt = stop_evt
        self.session = requests.Session()
        self.session.mount("https://", requests.adapters.HTTPAdapter(pool_connections=2, pool_maxsize=4, max_retries=1))
        self._last_dt: datetime | None = None; self._initialized = False; self._last_emitted: str | None = None

    @staticmethod
    def _dt(s: str) -> datetime: return datetime.fromisoformat(s.replace("Z","+00:00"))

    def _fetch(self) -> list[dict]:
        r = self.session.get(
            f"{self.base}/race_control",
            params={"meeting_key":"latest","session_key":"latest"},
            headers=self.headers,
            timeout=(0.8, 1.2)  # connect, read
        )
        r.raise_for_status(); return r.json() if "application/json" in r.headers.get("Content-Type","") else []

    def events(self):
        important = {"GREEN","YELLOW","DOUBLE YELLOW","RED","CHEQUERED"}
        ignore_blue = bool(self.flags_conf.get("ignore_blue", True))
        seek_live = bool(self.flags_conf.get("seek_live", True))
        while not self.stop_evt.is_set():
            try:
                rows = self._fetch(); rows.sort(key=lambda x: x.get("date",""))
                if seek_live and not self._initialized and rows:
                    try:
                        latest = max(self._dt(r["date"]) for r in rows if r.get("date"))
                        self._last_dt = latest; self._initialized = True
                        # attente granulaire
                        remaining = float(self.poll)
                        step = 0.05
                        while remaining > 0 and not self.stop_evt.is_set():
                            t = step if remaining > step else remaining
                            if self.stop_evt.wait(t): break
                            remaining -= t
                        if self.stop_evt.is_set(): break
                        continue
                    except Exception:
                        self._initialized = True

                for row in rows:
                    try: dt = self._dt(row.get("date","1970-01-01T00:00:00Z"))
                    except Exception: continue
                    if self._last_dt and dt <= self._last_dt: continue

                    cat = (row.get("category") or "")
                    flag = (row.get("flag") or "").upper()
                    msg = (row.get("message") or "").upper()
                    out = None

                    if cat == "Flag" and flag in important:
                        out = flag
                    elif cat == "SafetyCar":
                        # VSC
                        if "VIRTUAL SAFETY CAR" in msg or "VSC" in msg:
                            if "ENDING" in msg: out = "VSC_ENDING"
                            else: out = "VSC"
                        # SC
                        elif "SAFETY CAR" in msg:
                            if "ENDING" in msg or "IN THIS LAP" in msg: out = "SC_ENDING"
                            else: out = "SC"
                    elif cat == "Flag" and "BLUE" in flag and not ignore_blue:
                        out = "BLUE"

                    self._last_dt = dt if not self._last_dt or dt > self._last_dt else self._last_dt
                    if out and out != self._last_emitted:
                        self._last_emitted = out; yield out

                # attente granulaire
                remaining = float(self.poll)
                step = 0.05
                while remaining > 0 and not self.stop_evt.is_set():
                    t = step if remaining > step else remaining
                    if self.stop_evt.wait(t): break
                    remaining -= t
                if self.stop_evt.is_set(): break
            except Exception as e:
                print(f"[WARN] OpenF1 REST: {e}")
                # backoff granulaire
                backoff = max(2.0, float(self.poll))
                remaining = backoff
                step = 0.05
                while remaining > 0 and not self.stop_evt.is_set():
                    t = step if remaining > step else remaining
                    if self.stop_evt.wait(t): break
                    remaining -= t
                if self.stop_evt.is_set(): break

class F1SourceMock:
    def __init__(self, sequence: list[str], gap: float, stop_evt: threading.Event):
        self.sequence = sequence; self.gap = gap; self.stop_evt = stop_evt
    def events(self):
        last = None
        for f in self.sequence:
            if self.stop_evt.is_set(): return
            if f != last: yield f; last = f
            if self.stop_evt.wait(self.gap): return
