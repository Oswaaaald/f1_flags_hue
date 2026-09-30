import unittest
from datetime import datetime, timezone

from session_clock import api_remaining_seconds, compare_tv_clock, parse_tv_remaining


class SessionClockTests(unittest.TestCase):
    clock = {"Utc": "2026-09-25T09:00:00Z", "Remaining": "00:10:00", "Extrapolating": True}

    def test_compares_tv_to_api_at_click_time(self):
        clicked = datetime(2026, 9, 25, 9, 0, 5, tzinfo=timezone.utc)
        comparison = compare_tv_clock(self.clock, "10:05", clicked)
        self.assertEqual(comparison["api_remaining_seconds"], 595)
        self.assertEqual(comparison["estimated_offset_seconds"], 10.5)
        self.assertTrue(comparison["can_apply"])

    def test_paused_clock_cannot_be_used(self):
        clock = {**self.clock, "Extrapolating": False}
        with self.assertRaisesRegex(ValueError, "arrêtée"):
            api_remaining_seconds(clock, datetime(2026, 9, 25, 9, 0, 5, tzinfo=timezone.utc))

    def test_rejects_invalid_tv_time(self):
        with self.assertRaises(ValueError):
            parse_tv_remaining("12:75")


if __name__ == "__main__":
    unittest.main()
