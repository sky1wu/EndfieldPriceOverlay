import tempfile
import unittest
from datetime import date, datetime
from pathlib import Path

from capture_service import (
    CaptureReading,
    CaptureStore,
    OCRToken,
    ScreenLayout,
    effective_layout,
    map_price_tokens,
    prediction_status,
)
from capture_service import OCRService


class CaptureServiceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.store = CaptureStore(Path(self.temp.name) / "prices.db")

    def tearDown(self):
        self.temp.cleanup()

    def save(self, name, when, prices):
        return self.store.save(CaptureReading(name, prices, 1.0, [1.0] * 7, when))

    def test_capture_maps_oldest_to_today(self):
        self.save("天师龙泡泡货组", datetime(2026, 7, 4, 12), [1_400, 2_605, 2_343, 2_755, 1_360, 1_218, 3_416])
        values = self.store.dated_prices("天师龙泡泡货组")
        self.assertEqual(values[date(2026, 6, 28)], 1_400)
        self.assertEqual(values[date(2026, 7, 4)], 3_416)

    def test_image_regression_predicts_missing_sunday(self):
        source = [1_959, 1_707, 1_980, 1_492, 2_964, 1_098, 2_400]
        # 周日 2/8 捕获完整源周。
        self.save("测试商品", datetime(2026, 2, 8, 12), source)
        # 周六 2/14 的详情页覆盖最近 7 天：上周日 + 本周一至周六。
        self.save("测试商品", datetime(2026, 2, 14, 12), [2_400, 2_339, 2_168, 1_250, 1_282, 2_070, 1_216])
        status = prediction_status(self.store, "测试商品", date(2026, 2, 14))
        self.assertEqual(status["kind"], "ready")
        self.assertEqual(status["future"], [{"date": "2026-02-15", "weekday": 6, "price": 2_200}])

    def test_tokens_are_mapped_to_seven_columns(self):
        width = 700
        tokens = [OCRToken(str(1_000 + i), 0.9, i * 100 + 50, 40) for i in range(7)]
        prices, scores = map_price_tokens(tokens, width)
        self.assertEqual(prices, [1_000, 1_001, 1_002, 1_003, 1_004, 1_005, 1_006])
        self.assertEqual(scores, [0.9] * 7)

    def test_4k_tokens_use_nearest_column_center(self):
        width = 1_400
        tokens = [OCRToken(str(1_400 + i), 0.9, i * 200 + 110, 80) for i in range(7)]
        prices, _ = map_price_tokens(tokens, width)
        self.assertEqual(prices, [1_400, 1_401, 1_402, 1_403, 1_404, 1_405, 1_406])

    def test_name_prefers_line_above_misread_category(self):
        tokens = [
            OCRToken("天师龙泡泡货组", 0.92, 200, 30),
            OCRToken("单性需求物资", 0.99, 190, 70),
        ]
        self.assertEqual(OCRService._choose_name(tokens), ("天师龙泡泡货组", 0.92))

    def test_4k_uses_measured_chart_bounds(self):
        layout = effective_layout(ScreenLayout(), 3_840)
        self.assertEqual(layout.chart_roi, (0.472, 0.445, 0.810, 0.650))
        self.assertEqual(layout.name_roi, (0.370, 0.245, 0.625, 0.325))

    def test_custom_calibration_is_not_overridden(self):
        custom = ScreenLayout(chart_roi=(0.4, 0.4, 0.8, 0.7))
        self.assertEqual(effective_layout(custom, 3_840), custom)


if __name__ == "__main__":
    unittest.main()
