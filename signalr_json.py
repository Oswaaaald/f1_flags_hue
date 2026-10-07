"""Small JSON-only adapter for the legacy F1 client.

Wire format: dotnet/aspnetcore/src/SignalR/docs/specs/HubProtocol.md.
Only the official fixed endpoint, Subscribe completion and feed calls are used.
Reconnection and session processing remain owned by F1SourceFormula1Live.
"""
import json
import logging
import threading
import time
from types import SimpleNamespace
from urllib.parse import quote

import requests
import websocket


class Records:
    def __init__(self):
        self.pending = ""

    def read(self, text):
        if not isinstance(text, str):
            raise ValueError("Le flux F1 doit utiliser le protocole JSON.")
        self.pending += text
        if len(self.pending) > 2_000_000:
            raise ValueError("Message F1 trop volumineux.")
        rows = self.pending.split("\x1e")
        self.pending = rows.pop()
        result = [json.loads(row) for row in rows if row]
        if any(not isinstance(row, dict) for row in result):
            raise ValueError("Message SignalR invalide.")
        return result


class JsonF1Connection:
    ROOT = "https://livetiming.formula1.com/signalrcore"
    TOPICS = ["RaceControlMessages", "TrackStatus", "SessionInfo", "SessionStatus", "ExtrapolatedClock", "LapCount"]

    def __init__(self, on_message, on_close):
        self.on_message, self.on_close = on_message, on_close
        self.socket = None
        self.thread = None
        self.stopped = threading.Event()
        self.records = Records()

    def _send(self, value):
        self.socket.send(json.dumps(value, separators=(",", ":")) + "\x1e")

    def start(self):
        try:
            with requests.Session() as session:
                preflight = session.options(self.ROOT + "/negotiate", timeout=8)
                if preflight.status_code != 405:
                    preflight.raise_for_status()
                response = session.post(self.ROOT + "/negotiate?negotiateVersion=1", timeout=8, allow_redirects=False)
                response.raise_for_status()
                negotiation = response.json()
                token = negotiation.get("connectionToken") or negotiation.get("connectionId")
                if not isinstance(token, str) or not token:
                    raise ConnectionError("Négociation F1 invalide.")
                cookie = "; ".join(f"{key}={value}" for key, value in session.cookies.items())
                self.socket = websocket.create_connection(
                    self.ROOT.replace("https://", "wss://") + "?id=" + quote(token, safe=""),
                    timeout=8, cookie=cookie, sslopt={"ca_certs": requests.certs.where()},
                )
            self._send({"protocol": "json", "version": 1})
            deadline = time.monotonic() + 10
            while True:
                if time.monotonic() >= deadline:
                    raise TimeoutError("Ouverture du flux F1 trop lente.")
                self.socket.settimeout(max(0.1, deadline - time.monotonic()))
                chunk = self.socket.recv()
                if not chunk:
                    raise ConnectionError("Le flux F1 a fermé la connexion.")
                rows = self.records.read(chunk)
                if rows:
                    handshake, *remaining = rows
                    if "error" in handshake or "type" in handshake:
                        raise ConnectionError("Le flux F1 a refusé le protocole JSON.")
                    break
            self._send({"type": 1, "invocationId": "1", "target": "Subscribe", "arguments": [self.TOPICS]})
            for row in remaining:
                self.dispatch(row)
            self.socket.settimeout(1)
            self.thread = threading.Thread(target=self._listen, name="f1-json-feed", daemon=True)
            self.thread.start()
        except Exception:
            self.stop()
            raise

    def dispatch(self, row):
        kind = row.get("type")
        if kind == 1 and row.get("target") == "feed":
            self.on_message(row.get("arguments", []))
        elif kind == 3 and row.get("invocationId") == "1":
            if "error" in row:
                raise ConnectionError("Le flux F1 a refusé l’abonnement.")
            self.on_message(SimpleNamespace(result=row.get("result")))
        elif kind == 7:
            raise ConnectionError("Le flux F1 a fermé la connexion.")

    def _listen(self):
        last_received = last_ping = time.monotonic()
        try:
            while not self.stopped.is_set():
                try:
                    text = self.socket.recv()
                    if not text:
                        break
                    last_received = time.monotonic()
                    for row in self.records.read(text):
                        self.dispatch(row)
                except websocket.WebSocketTimeoutException:
                    pass
                now = time.monotonic()
                if now - last_received > 30:
                    raise TimeoutError("Le flux F1 ne répond plus.")
                if now - last_ping >= 10:
                    self._send({"type": 6})
                    last_ping = now
        except Exception as error:
            if not self.stopped.is_set():
                logging.warning("Connexion JSON F1 interrompue : %s", type(error).__name__)
        finally:
            self.socket.close()
            self.on_close()

    def stop(self):
        self.stopped.set()
        if self.socket is not None:
            self.socket.close()
        if self.thread is not None and self.thread is not threading.current_thread():
            self.thread.join(timeout=3)
