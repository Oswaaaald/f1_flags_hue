import unittest
import queue
import tempfile
import os
import stat
from pathlib import Path
from datetime import datetime, timezone
from unittest.mock import patch

from event_journal import EventJournal
from baseline import BaselineStore
from hue import HueBridgeConnectionError
from live_events import LiveEvent
from web.server import app
from web.server import _save_persist_cache


class WebSecurityTests(unittest.TestCase):
    def test_bridge_link_only_accepts_local_ipv4(self):
        with patch("web.server.HueBridge") as bridge:
            response = app.test_client().post("/api/bridge/link", json={"bridge_ip": "8.8.8.8"})
        self.assertEqual(response.status_code, 400)
        bridge.assert_not_called()

    def test_config_api_does_not_return_hue_key(self):
        conf = {"bridge_ip": "192.168.1.2", "username": "private-local-key", "sync": {}}
        with patch("web.server.ensure_conf", return_value=conf):
            response = app.test_client().get("/api/config")
        self.assertEqual(response.status_code, 200)
        self.assertNotIn("private-local-key", response.get_data(as_text=True))
        self.assertEqual(response.json["username"], "configuré")

    def test_static_routes_cannot_serve_local_configuration(self):
        client = app.test_client()
        for path in ("/config.yml", "/..%2Fconfig.yml", "/%2e%2e/config.yml"):
            with self.subTest(path=path):
                self.assertEqual(client.get(path).status_code, 404)

    def test_config_api_rejects_arbitrary_key_change(self):
        with patch("web.server.ensure_conf") as load, patch("web.server.save_conf") as save:
            response = app.test_client().post("/api/config", json={"username": "attacker"})
        self.assertEqual(response.status_code, 400)
        load.assert_not_called()
        save.assert_not_called()

    def test_cross_origin_cannot_start_light_control(self):
        response = app.test_client().post("/api/start", json={"mode": "live"},
                                          headers={"Origin": "https://example.com"})
        self.assertEqual(response.status_code, 403)

    def test_cross_site_read_cannot_trigger_api_side_effects(self):
        with patch("web.server.live_service") as service:
            response = app.test_client().get("/api/status", headers={"Sec-Fetch-Site": "cross-site"})
        self.assertEqual(response.status_code, 403)
        service.assert_not_called()

    def test_browser_cannot_embed_control_panel_in_another_site(self):
        response = app.test_client().get("/")
        self.assertEqual(response.headers["X-Frame-Options"], "DENY")
        self.assertEqual(response.headers["Content-Security-Policy"], "frame-ancestors 'none'")
        self.assertEqual(response.headers["X-Content-Type-Options"], "nosniff")
        response.close()

    def test_untrusted_host_cannot_read_or_control_local_api(self):
        with patch("web.server.RUNNER") as runner:
            client = app.test_client()
            read = client.get("/api/config", base_url="http://evil.example:8080")
            write = client.post("/api/start", json={"mode": "live"},
                                base_url="http://evil.example:8080",
                                headers={"Origin": "http://evil.example:8080"})
        self.assertEqual(read.status_code, 400)
        self.assertEqual(write.status_code, 400)
        runner.start_live.assert_not_called()

    def test_ip_and_explicitly_allowed_host_remain_accessible(self):
        client = app.test_client()
        local = client.get("/", base_url="http://192.168.1.20:8080")
        self.assertEqual(local.status_code, 200)
        local.close()
        with patch.dict(os.environ, {"F1_HUE_WEB_ALLOWED_HOSTS": "f1.local"}):
            named = client.get("/", base_url="http://f1.local:8080")
            self.assertEqual(named.status_code, 200)
            named.close()

    def test_large_json_body_is_rejected_before_configuration_change(self):
        with patch("web.server.save_conf") as save:
            response = app.test_client().post("/api/config", json={
                "sync": {"offset_seconds": 1}, "padding": "x" * 65536})
        self.assertEqual(response.status_code, 413)
        save.assert_not_called()

    @unittest.skipIf(os.name == "nt", "Windows chmod does not expose POSIX permission bits")
    def test_local_state_files_are_private_after_save(self):
        with tempfile.TemporaryDirectory() as directory:
            baseline = BaselineStore(directory, {"baseline": {"persist_path": "baseline.json"}})
            baseline_path = Path(directory) / "baseline.json"
            baseline_path.write_text("{}")
            baseline_path.chmod(0o644)
            baseline.data = {"1": {"on": True}}
            baseline._save()

            cache_path = Path(directory) / "bridges_cache.json"
            cache_path.write_text("{}")
            cache_path.chmod(0o644)
            with patch("web.server.CACHE_FILE", cache_path):
                _save_persist_cache([{"ip": "192.168.1.2"}])

            self.assertEqual(stat.S_IMODE(baseline_path.stat().st_mode), 0o600)
            self.assertEqual(stat.S_IMODE(cache_path.stat().st_mode), 0o600)

    def test_bridge_link_reports_unreachable_bridge(self):
        with patch("web.server.HueBridge") as bridge, patch("web.server.discover_bridge_ip", return_value=None):
            bridge.return_value.register.side_effect = HueBridgeConnectionError("network error")
            response = app.test_client().post("/api/bridge/link", json={"bridge_ip": "192.168.1.2"})
        self.assertEqual(response.status_code, 400)
        self.assertIn("Impossible de joindre", response.json["error"])
        self.assertNotIn("network error", response.json["error"])

    def test_bridge_link_retries_discovered_address(self):
        conf = {"bridge_ip": "192.168.0.116", "username": "old-key"}
        with patch("web.server.HueBridge") as bridge, \
             patch("web.server.discover_bridge_ip", return_value="192.168.129.6"), \
             patch("web.server.ensure_conf", return_value=conf), \
             patch("web.server.save_conf") as save:
            bridge.return_value.register.side_effect = [HueBridgeConnectionError("offline"), "new-key"]
            response = app.test_client().post("/api/bridge/link", json={"bridge_ip": "192.168.0.116"})
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.json["bridge_ip"], "192.168.129.6")
        self.assertEqual(conf["username"], "new-key")
        save.assert_called_once_with(conf)

    def test_bridge_link_reports_both_unreachable_addresses(self):
        with patch("web.server.HueBridge") as bridge, \
             patch("web.server.discover_bridge_ip", return_value="192.168.129.6"):
            bridge.return_value.register.side_effect = HueBridgeConnectionError("offline")
            response = app.test_client().post("/api/bridge/link", json={"bridge_ip": "192.168.0.116"})
        self.assertEqual(response.status_code, 400)
        self.assertEqual(response.json["bridge_ip"], "192.168.129.6")
        self.assertIn("Réseau local", response.json["error"])

    def test_clock_comparison_does_not_save_offset(self):
        clicked = datetime(2026, 9, 25, 9, 0, 5, tzinfo=timezone.utc)
        clock = {"Utc": "2026-09-25T09:00:00Z", "Remaining": "00:10:00", "Extrapolating": True}
        with patch("web.server.datetime") as date, \
             patch("web.server.live_service") as service, \
             patch("web.server.save_conf") as save:
            date.now.return_value = clicked
            service.return_value.wait_ready.return_value = {
                "clock": clock, "session_status": "Started", "session_name": "Practice 3"}
            response = app.test_client().post("/api/sync/compare-clock", json={"tv_remaining": "10:05"})
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.json["estimated_offset_seconds"], 10.5)
        self.assertEqual(response.json["session_name"], "Practice 3")
        save.assert_not_called()

    def test_start_calibration_uses_shared_feed(self):
        updates = queue.Queue()
        updates.put(LiveEvent(kind="session_status", value="Started", session_name="Race"))
        with patch("web.server.live_service") as service:
            service.return_value.subscribe.return_value = updates
            service.return_value.wait_ready.return_value = {
                "session_type": "Race", "session_status": "Inactive", "session_name": "Race"}
            response = app.test_client().get("/api/sync/start-stream", buffered=True)
        body = response.get_data(as_text=True)
        self.assertIn('"type": "ready"', body)
        self.assertIn('"type": "start"', body)
        service.return_value.unsubscribe.assert_called_once_with(updates)

    def test_flag_calibration_accepts_damier_after_finished(self):
        updates = queue.Queue()
        updates.put(LiveEvent(kind="flag", value="CHEQUERED"))
        with patch("web.server.live_service") as service:
            service.return_value.subscribe.return_value = updates
            service.return_value.wait_ready.return_value = {
                "session_type": "Practice", "session_status": "Finished", "session_name": "Practice 3"}
            response = app.test_client().get("/api/sync/flag-stream", buffered=True)
        self.assertIn('"flag": "CHEQUERED"', response.get_data(as_text=True))

    def test_replay_endpoint_requires_a_recorded_session(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "events.sqlite3"
            with patch("web.server.EventJournal", side_effect=lambda: EventJournal(path)), \
                 patch("web.server.RUNNER") as runner:
                missing = app.test_client().post("/api/replay/start", json={"session_key": "42", "speed": 10})
                self.assertEqual(missing.status_code, 404)
                journal = EventJournal(path)
                journal.append(LiveEvent(kind="flag", value="GREEN", session_key="42"))
                journal.close()
                accepted = app.test_client().post("/api/replay/start", json={"session_key": "42", "speed": 10})
            self.assertEqual(accepted.status_code, 200)
            args, _ = runner.start_replay.call_args
            self.assertEqual([event.value for event in args[0]], ["GREEN"])
            self.assertEqual(args[1], 10.0)

    def test_flag_history_exposes_reception_and_f1_timestamps(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "events.sqlite3"
            journal = EventJournal(path)
            journal.append(LiveEvent(kind="flag", value="YELLOW", session_key="42",
                                     session_name="Practice 3", received_at=100.25))
            journal.append(LiveEvent(kind="flag", value="RED", session_key="42",
                                     session_name="Practice 3", received_at=102.5,
                                     source_utc="2025-09-07T13:01:00Z"))
            journal.close()
            with patch("web.server.EventJournal", side_effect=lambda: EventJournal(path)):
                response = app.test_client().get("/api/journal/flags?limit=1")
                invalid = app.test_client().get("/api/journal/flags?limit=10000")
        self.assertEqual(response.status_code, 200)
        self.assertEqual([(row["flag"], row["received_at"], row["source_utc"])
                          for row in response.json["flags"]],
                         [("RED", 102.5, "2025-09-07T13:01:00Z")])
        self.assertEqual(invalid.status_code, 400)


if __name__ == "__main__":
    unittest.main()
