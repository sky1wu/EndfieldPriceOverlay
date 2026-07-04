import unittest

from overlay_app import ui_scale_for_dpi


class OverlayScaleTests(unittest.TestCase):
    def test_4k_200_percent_scale(self):
        self.assertEqual(ui_scale_for_dpi(192), 2.0)

    def test_scale_is_clamped(self):
        self.assertEqual(ui_scale_for_dpi(72), 1.0)
        self.assertEqual(ui_scale_for_dpi(384), 2.5)


if __name__ == "__main__":
    unittest.main()
