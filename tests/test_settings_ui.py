import copy
import threading
import time
import unittest
from unittest.mock import Mock, patch

from baseline import BaselineStore
from effects import play_effects
from engine import LightEngine
from f1_sources import F1SourceFormula1Live
from hue import HueBridge, HueBridgeConnectionError
from live_events import LiveEvent
from settings import apply_settings_patch, public_settings
from web.server import app


def sample_conf():
    return {
        "bridge_ip": "192.0.2.4", "username": "private-key", "group_id": 2,
        "bri": 254, "transition_tenths": 5, "sync": {"offset_seconds": 41.5},
        "colors_xy": {"green": [0.2, 0.7], "red": [0.6, 0.3]},
        "flags": {"ignore_blue": True, "exit_on_chequered": False},
        "behavior": {"alert_watchdog_seconds": 600},
        "baseline": {"restore_on_exit": True},
        "patterns": {
            "GREEN": {"mode": "solid", "color": "green", "hold": 8, "then_off": True},
            "RED": {"mode": "solid", "color": "red"},
            "BLUE": {"mode": "blink", "color": "red", "alert_mode": "select", "select_repeats": 3, "select_gap": .7},
        },
    }


class SettingsTests(unittest.TestCase):
    def test_existing_configuration_is_projected_without_mutation_or_secret(self):
        conf = sample_conf()
        original = copy.deepcopy(conf)
        public = public_settings(conf)
        self.assertEqual(public["effects"]["GREEN"]["duration_seconds"], 8)
        self.assertIsNone(public["effects"]["RED"]["duration_seconds"])
        self.assertFalse(public["effects"]["BLUE"]["enabled"])
        self.assertNotIn("private-key", str(public))
        self.assertEqual(conf, original)

    def test_patch_preserves_hue_and_offset_and_rejects_unknown_fields(self):
        conf = sample_conf()
        updated = apply_settings_patch(conf, {"effects": {"BLUE": {"enabled": True, "duration_seconds": 4}},
                                              "preferences": {"brightness": 120}})
        self.assertEqual(updated["username"], "private-key")
        self.assertEqual(updated["sync"]["offset_seconds"], 41.5)
        self.assertTrue(updated["flags"]["enabled"]["BLUE"])
        self.assertEqual(updated["patterns"]["BLUE"]["duration_seconds"], 4)
        self.assertEqual(conf["bri"], 254)
        for bad in ({"username": "oops"}, {"effects": {"RED": {"duration_seconds": -1}}},
                    {"effects": {"RED": {"enabled": 1}}}):
            with self.assertRaises(ValueError):
                apply_settings_patch(conf, bad)

    def test_api_never_returns_key_and_rejects_invalid_patch(self):
        conf = sample_conf()
        with patch("web.server.ensure_conf", return_value=conf), patch("web.server.save_conf") as save:
            public = app.test_client().get("/api/settings")
            invalid = app.test_client().patch("/api/settings", json={"bridge_ip": "127.0.0.1"})
            accepted = app.test_client().patch("/api/settings", json={"effects": {"RED": {"enabled": False}}})
        self.assertEqual(public.status_code, 200)
        self.assertNotIn("private-key", public.get_data(as_text=True))
        self.assertEqual(invalid.status_code, 400)
        self.assertEqual(accepted.status_code, 200)
        save.assert_called_once()

    def test_fixed_duration_is_returned_after_save_and_reload(self):
        saved_conf = [sample_conf()]

        def save(updated):
            saved_conf[0] = updated

        with patch("web.server.ensure_conf", side_effect=lambda: saved_conf[0]), patch("web.server.save_conf", side_effect=save):
            client = app.test_client()
            response = client.patch("/api/settings", json={"effects": {"RED": {"duration_seconds": 12.5}}})
            reloaded = client.get("/api/settings")

        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.get_json()["effects"]["RED"]["duration_seconds"], 12.5)
        self.assertEqual(reloaded.get_json()["effects"]["RED"]["duration_seconds"], 12.5)
        self.assertEqual(saved_conf[0]["patterns"]["RED"]["duration_seconds"], 12.5)

    def test_disabled_event_clears_previous_effect_after_offset(self):
        conf = sample_conf()
        output = Mock()
        event = LiveEvent(kind="flag", value="GREEN", received_mono=time.monotonic())
        worker = threading.Thread(target=play_effects,
                                  args=(iter([event]), output, conf, threading.Event()),
                                  kwargs={"offset_seconds": .06, "conf_provider": lambda: conf})
        worker.start()
        time.sleep(.02)
        conf["flags"]["enabled"] = {"GREEN": False}
        worker.join(timeout=1)
        output.clear.assert_called_once()
        output.play.assert_not_called()

    def test_disabled_chequered_can_still_end_live_mode(self):
        conf = sample_conf()
        conf["patterns"]["CHEQUERED"] = {"mode": "blink", "color": "red"}
        conf["flags"]["enabled"] = {"CHEQUERED": False}
        conf["flags"]["exit_on_chequered"] = True
        output = Mock()
        play_effects(iter([LiveEvent(kind="flag", value="CHEQUERED"),
                           LiveEvent(kind="flag", value="RED")]), output, conf, threading.Event())
        output.clear.assert_called_once()
        output.play.assert_not_called()


class EngineTests(unittest.TestCase):
    def conf(self):
        conf = sample_conf()
        conf["light_ids"] = [1]
        conf["group_id"] = None
        conf["patterns"]["GREEN"]["duration_seconds"] = .05
        return conf

    def test_timed_effect_restores_selected_baseline(self):
        bridge, baseline = Mock(), Mock()
        engine = LightEngine(bridge, self.conf(), threading.Event(), baseline=baseline)
        engine.start()
        engine.play("GREEN")
        time.sleep(.12)
        engine.stop()
        baseline.restore.assert_called_once()

    def test_new_effect_prevents_stale_restore(self):
        bridge, baseline = Mock(), Mock()
        engine = LightEngine(bridge, self.conf(), threading.Event(), baseline=baseline)
        engine.start()
        engine.play("GREEN")
        time.sleep(.015)
        engine.play("RED")
        time.sleep(.11)
        engine.stop()
        baseline.restore.assert_not_called()
        colors = [call.args[0].get("xy") for call in bridge.set_state.call_args_list if "xy" in call.args[0]]
        self.assertEqual(colors[-1], [0.6, 0.3])

    def test_invalid_group_does_not_fall_back_to_every_light(self):
        bridge = Mock()
        bridge.lights.return_value = {"1": {}, "2": {}}
        bridge.groups.return_value = {}
        store = BaselineStore(".", {"baseline": {"persist_path": "/dev/null"}})
        self.assertEqual(store._target_light_ids(bridge, {"group_id": 99}), [])

    def test_restore_only_touches_current_selection(self):
        bridge = Mock()
        bridge.lights.return_value = {"1": {}, "2": {}}
        bridge.groups.return_value = {"2": {"lights": ["1"]}}
        store = BaselineStore(".", {"baseline": {"persist_path": "/dev/null"}})
        store.data = {"1": {"on": True}, "2": {"on": False}}
        self.assertEqual(store.restore(bridge, {"group_id": 2}), 1)
        bridge.set_light_state.assert_called_once()
        self.assertEqual(bridge.set_light_state.call_args.args[0], 1)

    def test_hue_write_checks_api_error_payload(self):
        bridge = HueBridge("192.0.2.4", "private-key")
        bridge._request = Mock(return_value=Mock(json=lambda: [{"error": {"description": "invalid group"}}]))
        with self.assertRaisesRegex(HueBridgeConnectionError, "invalid group"):
            bridge.set_state({"on": True}, group_id=2)
        with self.assertRaises(HueBridgeConnectionError):
            bridge.set_state({"on": True})


class FlowTests(unittest.TestCase):
    def test_red_and_blue_survive_source_status_changes(self):
        events = []
        source = F1SourceFormula1Live({"ignore_blue": True}, threading.Event(), on_event=events.append)
        source._set_session_status("Started")
        source._set_session_status("Aborted")
        source._on_message(["RaceControlMessages", {"Messages": {
            "1": {"Category": "Flag", "Flag": "RED"},
            "2": {"Category": "Flag", "Flag": "BLUE"},
        }}])
        self.assertEqual([e.value for e in events if e.kind == "flag"], ["RED", "BLUE"])

    def test_invalid_hue_selection_is_rejected(self):
        conf = sample_conf()
        bridge = Mock()
        bridge.lights.return_value = {"1": {}}
        bridge.groups.return_value = {}
        with patch("web.server.ensure_conf", return_value=conf), \
             patch("web.server._ensure_hue_from_conf", return_value=(bridge, conf)), \
             patch("web.server.save_conf") as save, \
             patch("web.server.RUNNER") as runner:
            runner.t = None
            response = app.test_client().post("/api/setup/targets", json={"group_id": 99})
        self.assertEqual(response.status_code, 400)
        save.assert_not_called()

    def test_valid_hue_selection_is_saved(self):
        conf = sample_conf()
        bridge = Mock()
        bridge.lights.return_value = {"1": {}}
        bridge.groups.return_value = {"2": {"lights": ["1"]}}
        with patch("web.server.ensure_conf", return_value=conf), \
             patch("web.server._ensure_hue_from_conf", return_value=(bridge, conf)), \
             patch("web.server.save_conf") as save, \
             patch("web.server.RUNNER") as runner:
            runner.t = None
            response = app.test_client().post("/api/setup/targets", json={"group_id": 2})
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.json["selection"]["group_id"], 2)
        save.assert_called_once()

    def test_hue_inventory_error_is_not_reported_as_empty_success(self):
        with patch("web.server._ensure_hue_from_conf", side_effect=HueBridgeConnectionError("pont hors ligne")):
            response = app.test_client().get("/api/hue/lights")
        self.assertEqual(response.status_code, 503)
        self.assertIn("pont hors ligne", response.json["error"])

    def test_selection_is_locked_while_a_mode_is_running(self):
        with patch("web.server.RUNNER") as runner:
            runner.t.is_alive.return_value = True
            response = app.test_client().post("/api/setup/targets", json={"group_id": 0})
        self.assertEqual(response.status_code, 409)

    def test_preview_can_play_a_disabled_flag(self):
        from web.server import Runner
        conf = sample_conf()
        conf["flags"]["enabled"] = {"BLUE": False}
        runner = Runner()
        runner.engine = Mock()
        with patch("web.server.ensure_conf", return_value=conf), \
             patch.object(runner, "_prepare_common"), patch.object(runner, "_restore_on_exit"):
            runner._run_test(.1, preview_flag="BLUE")
        self.assertEqual(runner.engine.play.call_args.args, ("BLUE",))

    def test_automatic_sequence_skips_disabled_flags(self):
        from web.server import Runner
        conf = sample_conf()
        conf["flags"]["enabled"] = {flag: flag == "RED" for flag in
                                      ("GREEN", "YELLOW", "RED", "SC", "SC_ENDING", "VSC", "VSC_ENDING", "BLUE", "CHEQUERED")}
        runner = Runner()
        runner.engine = Mock()
        with patch("web.server.ensure_conf", return_value=conf), \
             patch.object(runner, "_prepare_common"), patch.object(runner, "_restore_on_exit"):
            runner._run_test(.1)
        self.assertEqual(runner.engine.play.call_args.args, ("RED",))
        runner.engine.play.assert_called_once()
        runner.engine.clear.assert_not_called()

    def test_navigation_routes_are_reloadable(self):
        client = app.test_client()
        for route in ("/direct", "/hue", "/tests", "/flags", "/preferences"):
            response = client.get(route)
            self.assertEqual(response.status_code, 200)
            response.close()


if __name__ == "__main__":
    unittest.main()
