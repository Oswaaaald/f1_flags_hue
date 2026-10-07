"""Exercise every browser route without contacting F1 or the Hue bridge."""

import copy
import queue
import unittest
from datetime import datetime, timezone
from unittest.mock import Mock, patch

from live_events import LiveEvent
from tests.test_settings_ui import sample_conf
from web.server import HUB, app


class RouteSmokeTests(unittest.TestCase):
    def setUp(self):
        self.client = app.test_client()

    def get(self, path, *, buffered=True):
        response = self.client.get(path, buffered=buffered)
        self.assertEqual(response.status_code, 200, path)
        return response

    def write(self, method, path, body=None):
        response = self.client.open(path, method=method, json=body)
        self.assertEqual(response.status_code, 200, f"{method} {path}: {response.get_data(as_text=True)}")
        self.assertTrue(response.json.get("ok"), f"{method} {path}")
        return response

    def test_page_assets_and_event_stream(self):
        for path in ("/", "/direct", "/hue", "/tests", "/flags", "/preferences"):
            self.assertIn(b"<html", self.get(path).data.lower())
        self.assertIn(b"renderEffects", self.get("/app.js").data)
        self.assertIn(b"effect-card", self.get("/app.css").data)

        events = queue.Queue()
        events.put({"type": "route_smoke"})
        with patch.object(HUB, "listen", return_value=events):
            stream = self.get("/api/events", buffered=False)
            try:
                self.assertIn(b'"route_smoke"', next(stream.response))
            finally:
                stream.close()

    def test_read_apis(self):
        conf = sample_conf()
        bridge = Mock()
        bridge.lights.return_value = {"1": {"name": "Lampe"}}
        bridge.groups.return_value = {"2": {"name": "Salon", "lights": ["1"]}}
        service = Mock()
        service.status.return_value = {"connected": True, "session_name": "Race"}
        journal = Mock()
        journal.sessions.return_value = []
        journal.recent_flags.return_value = []
        with patch("web.server.ensure_conf", return_value=conf), \
             patch("web.server.RUNNER") as runner, \
             patch("web.server.live_service", return_value=service), \
             patch("web.server._ensure_hue_from_conf", return_value=(bridge, conf)), \
             patch("web.server.EventJournal", return_value=journal), \
             patch("web.server._DISC_CACHE", {"ts": 0.0, "bridges": []}), \
             patch("web.server._discover_bridges_all", return_value=[]), \
             patch("web.server._load_persist_cache", return_value=([], 0.0)), \
             patch("web.server.discover_bridge_ip", return_value="192.168.1.2"):
            runner.status.return_value = {"running": False, "mode": None}
            checks = {
                "/api/status": "feed",
                "/api/config": "bridge_ip",
                "/api/settings": "effects",
                "/api/journal/sessions": "sessions",
                "/api/journal/flags": "flags",
                "/api/replay/scenarios": "historical",
                "/api/bridge/discover_all": "bridges",
                "/api/bridge/discover": "ip",
                "/api/setup/selection": "group_id",
            }
            for path, key in checks.items():
                with self.subTest(path=path):
                    self.assertIn(key, self.get(path).json)
            self.assertIn("1", self.get("/api/hue/lights").json)
            self.assertIn("2", self.get("/api/hue/groups").json)

    def test_calibration_routes(self):
        clock = {"Utc": datetime.now(timezone.utc).isoformat(), "Remaining": "00:10:00", "Extrapolating": True}
        service = Mock()
        service.wait_ready.return_value = {
            "clock": clock, "session_status": "Started", "session_type": "Race",
            "session_name": "Race", "current_lap": 2, "total_laps": 50,
        }
        with patch("web.server.live_service", return_value=service):
            self.assertIn("estimated_offset_seconds", self.write(
                "POST", "/api/sync/compare-clock", {"tv_remaining": "09:55"}).json)

            for path, update, expected in (
                ("/api/sync/lap-stream", LiveEvent(kind="lap", value=3), '"type": "lap"'),
                ("/api/sync/flag-stream", LiveEvent(kind="flag", value="YELLOW"), '"type": "flag"'),
            ):
                events = queue.Queue()
                events.put(update)
                service.subscribe.return_value = events
                self.assertIn(expected, self.get(path).get_data(as_text=True))

            service.wait_ready.return_value["session_status"] = "Inactive"
            events = queue.Queue()
            events.put(LiveEvent(kind="session_status", value="Started"))
            service.subscribe.return_value = events
            self.assertIn('"type": "start"', self.get("/api/sync/start-stream").get_data(as_text=True))

    def test_write_apis(self):
        saved_conf = [sample_conf()]
        bridge = Mock()
        bridge.lights.return_value = {"1": {"name": "Lampe"}}
        bridge.groups.return_value = {"2": {"name": "Salon", "lights": ["1"]}}
        journal = Mock()
        journal.flags.return_value = [LiveEvent(kind="flag", value="GREEN", session_key="session-1")]

        def save(conf):
            saved_conf[0] = copy.deepcopy(conf)

        with patch("web.server.ensure_conf", side_effect=lambda: copy.deepcopy(saved_conf[0])), \
             patch("web.server.save_conf", side_effect=save), \
             patch("web.server.RUNNER") as runner, \
             patch("web.server.HueBridge") as hue_class, \
             patch("web.server._ensure_hue_from_conf", return_value=(bridge, saved_conf[0])), \
             patch("web.server.EventJournal", return_value=journal), \
             patch("web.server._save_persist_cache"):
            runner.start_live.return_value = True
            runner.start_test.return_value = True
            runner.start_preview.return_value = True
            runner.start_replay.return_value = True
            runner.stop.return_value = True
            runner.t = None
            hue_class.return_value.register.return_value = "hue-test-key"

            self.assertEqual(self.write("PATCH", "/api/settings", {
                "effects": {"RED": {"duration_seconds": 5}}}).json["effects"]["RED"]["duration_seconds"], 5)
            self.write("POST", "/api/config", {"sync": {"offset_seconds": 12}})
            self.write("POST", "/api/start", {"mode": "live"})
            self.write("POST", "/api/start", {"mode": "test", "gap": 1})
            self.write("POST", "/api/test/preview", {"flag": "GREEN"})
            self.write("POST", "/api/replay/start", {"session_key": "session-1", "speed": 2})
            self.write("POST", "/api/stop")
            self.write("POST", "/api/bridge/link", {"bridge_ip": "192.168.1.2"})
            self.write("POST", "/api/setup/targets", {"group_id": 2})
            self.write("POST", "/api/bridge/unlink")
            self.assertEqual(saved_conf[0]["group_id"], None)

    def test_json_routes_reject_non_object_payloads_without_server_errors(self):
        paths = (
            ("PATCH", "/api/settings"),
            ("POST", "/api/config"),
            ("POST", "/api/sync/compare-clock"),
            ("POST", "/api/start"),
            ("POST", "/api/test/preview"),
            ("POST", "/api/replay/start"),
            ("POST", "/api/bridge/link"),
            ("POST", "/api/setup/targets"),
        )
        with patch("web.server.ensure_conf", return_value=sample_conf()), \
             patch("web.server.RUNNER") as runner, \
             patch("web.server.live_service") as service, \
             patch("web.server.HueBridge") as bridge:
            runner.t = None
            for method, path in paths:
                for body in ("invalid", []):
                    with self.subTest(method=method, path=path, body=body):
                        response = self.client.open(path, method=method, json=body)
                        self.assertEqual(response.status_code, 400)
                        self.assertIn("error", response.json)
            runner.start_live.assert_not_called()
            service.assert_not_called()
            bridge.assert_not_called()

    def test_all_application_routes_are_exercised(self):
        known = {
            (method, rule.rule)
            for rule in app.url_map.iter_rules()
            for method in rule.methods & {"GET", "POST", "PATCH"}
            if rule.endpoint != "static"
        }
        exercised = {
            ("GET", "/"), ("GET", "/direct"), ("GET", "/hue"),
            ("GET", "/tests"), ("GET", "/flags"), ("GET", "/preferences"),
            ("GET", "/api/events"), ("GET", "/api/status"),
            ("GET", "/api/config"), ("POST", "/api/config"),
            ("GET", "/api/settings"), ("PATCH", "/api/settings"),
            ("POST", "/api/sync/compare-clock"),
            ("GET", "/api/sync/lap-stream"), ("GET", "/api/sync/start-stream"),
            ("GET", "/api/sync/flag-stream"), ("POST", "/api/start"),
            ("POST", "/api/test/preview"),
            ("GET", "/api/journal/sessions"), ("GET", "/api/journal/flags"),
            ("GET", "/api/replay/scenarios"), ("POST", "/api/replay/start"),
            ("POST", "/api/stop"), ("GET", "/api/bridge/discover_all"),
            ("GET", "/api/bridge/discover"), ("POST", "/api/bridge/link"),
            ("POST", "/api/bridge/unlink"), ("GET", "/api/hue/lights"),
            ("GET", "/api/hue/groups"), ("GET", "/api/setup/selection"),
            ("POST", "/api/setup/targets"),
        }
        self.assertEqual(known, exercised)


if __name__ == "__main__":
    unittest.main()
