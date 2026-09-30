import unittest
from unittest.mock import Mock, patch

import requests

from hue import HueBridge, HueBridgeConnectionError, connect_bridge


class HueConnectionTests(unittest.TestCase):
    def test_network_error_does_not_expose_hue_key(self):
        bridge = HueBridge("192.168.0.116", "private-local-key")
        bridge.session.request = Mock(side_effect=requests.ConnectTimeout(
            "request failed at http://192.168.0.116/api/private-local-key/lights"
        ))
        with self.assertRaises(HueBridgeConnectionError) as caught:
            bridge.lights()
        self.assertNotIn("private-local-key", str(caught.exception))

    @patch("hue.discover_bridge_ip", return_value="192.168.129.6")
    @patch("hue.HueBridge")
    def test_uses_discovered_address_only_after_successful_probe(self, bridge_class, _discovery):
        old = Mock()
        old.lights.side_effect = requests.ConnectTimeout()
        new = Mock()
        new.lights.return_value = {"1": {"name": "Lamp"}}
        bridge_class.side_effect = [old, new]
        conf = {"bridge_ip": "192.168.0.116", "username": "local-key"}

        bridge, changed = connect_bridge(conf)

        self.assertIs(bridge, new)
        self.assertTrue(changed)
        self.assertEqual(conf["bridge_ip"], "192.168.129.6")
        old.close.assert_called_once()

    @patch("hue.discover_bridge_ip", return_value="192.168.129.6")
    @patch("hue.HueBridge")
    def test_fails_cleanly_when_both_addresses_are_unreachable(self, bridge_class, _discovery):
        bridge_class.return_value.lights.side_effect = requests.ConnectTimeout()
        conf = {"bridge_ip": "192.168.0.116", "username": "local-key"}

        with self.assertRaisesRegex(HueBridgeConnectionError, "ne répond pas non plus"):
            connect_bridge(conf)

        self.assertEqual(conf["bridge_ip"], "192.168.0.116")


if __name__ == "__main__":
    unittest.main()
