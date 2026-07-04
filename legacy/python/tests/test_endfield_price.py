import unittest
import json

from endfield_price import (
    TEMPLATES,
    calculate_future_prices,
    compute_epsilon_vec,
    match_residual,
    predict,
)


class EndfieldPriceTests(unittest.TestCase):
    def test_formula_round_trip(self):
        epsilon = [0.02, -0.01, 0.03, -0.02, 0.01, 0.0, -0.03]
        prices = predict(epsilon, TEMPLATES[0])
        recovered = compute_epsilon_vec(prices, TEMPLATES[0])
        for actual, expected in zip(recovered, epsilon):
            self.assertAlmostEqual(actual, expected)

    def test_residual_exact_match(self):
        prices = [1600, 2000, 2200, 2600, 3000, 3200, 3400]
        self.assertEqual(match_residual(prices, prices), 0.0)

    def test_one_week_produces_64_possibilities(self):
        week = predict([0.01] * 7, TEMPLATES[2])
        result = calculate_future_prices([week])
        self.assertEqual(result["stage"], "first_week")
        self.assertEqual(result["remaining_possibilities"], 64)

    def test_cross_week_lock_and_current_filter(self):
        epsilon = [0.02, -0.01, 0.03, -0.02, 0.01, 0.0, -0.03]
        week1 = predict(epsilon, TEMPLATES[0])
        week2 = predict(epsilon, TEMPLATES[3])
        current_full = predict(epsilon, TEMPLATES[6])
        current = current_full[:2] + [None] * 5
        result = calculate_future_prices([week1, week2], current)
        self.assertEqual(result["stage"], "locked")
        self.assertEqual(result["remaining_possibilities"], 1)
        survivor = next(
            item
            for group in result["groups"]
            for item in group["possibilities"]
            if not item["eliminated"]
        )
        self.assertEqual(survivor["template"]["id"], 7)
        self.assertEqual(survivor["prices"], current_full)

    def test_blank_current_is_valid_json(self):
        week = predict([0.01] * 7, TEMPLATES[0])
        result = calculate_future_prices([week], [None] * 7)
        json.dumps(result, allow_nan=False)


if __name__ == "__main__":
    unittest.main()
