import unittest
from unittest.mock import Mock
from signalr_json import JsonF1Connection, Records
from web.server import app


class JsonTransportTests(unittest.TestCase):
    def test_records_handle_fragments_and_coalesced_messages(self):
        records = Records()
        self.assertEqual(records.read('{"type":'), [])
        self.assertEqual(records.read('6}\x1e{}\x1e{"ty'), [{"type": 6}, {}])
        self.assertEqual(records.read('pe":7}\x1e'), [{"type": 7}])

    def test_invalid_or_oversized_records_are_rejected(self):
        for text in ('[]\x1e', 'invalid\x1e', b'{}\x1e', 'x' * 2_000_001):
            with self.subTest(text_type=type(text).__name__), self.assertRaises(ValueError):
                Records().read(text)

    def test_subscribe_snapshot_and_feed_use_existing_parser_contract(self):
        callback = Mock()
        connection = JsonF1Connection(callback, Mock())
        connection.dispatch({"type": 3, "invocationId": "1", "result": {"SessionInfo": {"Key": 2}}})
        self.assertEqual(callback.call_args.args[0].result, {"SessionInfo": {"Key": 2}})
        connection.dispatch({"type": 1, "target": "feed", "arguments": ["TrackStatus", {"Status": "4"}]})
        callback.assert_called_with(["TrackStatus", {"Status": "4"}])
        with self.assertRaises(ConnectionError):
            connection.dispatch({"type": 7})
        with self.assertRaises(ConnectionError):
            connection.dispatch({"type": 3, "invocationId": "1", "error": "refused"})

    def test_legacy_ui_refuses_lan_clients_even_behind_wsgi(self):
        client = app.test_client()
        for address in ('192.168.1.30', '10.0.0.20', '::ffff:192.168.1.30'):
            response = client.post('/api/stop', environ_overrides={'REMOTE_ADDR': address})
            self.assertEqual(response.status_code, 403)
