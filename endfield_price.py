"""《明日方舟：终末地》弹性物资价格计算器。

算法按“终末地 · 弹性物资分析仪”v5 实现。周内索引固定为周一至周日。
"""

from __future__ import annotations

import argparse
import json
import math
from dataclasses import dataclass
from typing import Any, Iterable, Sequence


DAYS = ("周一", "周二", "周三", "周四", "周五", "周六", "周日")
MATCH_THRESHOLD = 0.003
PRICE_MIN = 300
PRICE_MAX = 5500


@dataclass(frozen=True)
class PriceTemplate:
    id: int
    name: str
    grid: tuple[int, ...]
    coeff: int


TEMPLATES = (
    PriceTemplate(1, "逐渐上升", (1600, 2000, 2200, 2600, 3000, 3200, 3400), 5),
    PriceTemplate(2, "逐渐下降", (2200, 2000, 1800, 1700, 1500, 1200, 1100), 5),
    PriceTemplate(3, "先降后升", (2400, 2000, 1600, 1400, 1600, 2000, 2200), 5),
    PriceTemplate(4, "先升后降", (1800, 2000, 2600, 3000, 3400, 2600, 2000), 5),
    PriceTemplate(5, "奇高小震", (2000, 1600, 2400, 1600, 2400, 1600, 2400), 4),
    PriceTemplate(6, "偶高小震", (2000, 2400, 1600, 2400, 1600, 2400, 1600), 4),
    PriceTemplate(7, "奇高大震", (2200, 1400, 3000, 1200, 3600, 1000, 4000), 6),
    PriceTemplate(8, "偶高大震", (2200, 3000, 1600, 3600, 1200, 4000, 1000), 6),
)

WeekPrices = list[float | None]


def normalize_week(prices: Sequence[float | int | None]) -> WeekPrices:
    """校验并复制一周价格；None 表示当天尚未观测。"""
    if len(prices) != 7:
        raise ValueError("每周必须提供 7 个位置（周一至周日）")
    result: WeekPrices = []
    for day, value in zip(DAYS, prices):
        if value is None:
            result.append(None)
            continue
        number = float(value)
        if not math.isfinite(number) or not PRICE_MIN <= number <= PRICE_MAX:
            raise ValueError(f"{day}价格必须在 {PRICE_MIN}～{PRICE_MAX} 之间")
        result.append(number)
    return result


def compute_epsilon_vec(week_prices: Sequence[float | None], tpl: PriceTemplate) -> list[float | None]:
    """εᵢ = (Pᵢ / Tᵢ - 1) / X。"""
    return [
        (price / tpl.grid[i] - 1) / tpl.coeff if price is not None else None
        for i, price in enumerate(week_prices)
    ]


def _js_round(value: float) -> int:
    """复现 JavaScript Math.round，而不是 Python 的银行家舍入。"""
    return math.floor(value + 0.5)


def predict(
    epsilon_vec: Sequence[float | None],
    target_tpl: PriceTemplate,
    fallback_epsilon_vec: Sequence[float | None] | None = None,
) -> list[int]:
    """Pᵢ = round(Tᵢ × (1 + X × εᵢ))。"""
    result: list[int] = []
    for i, grid in enumerate(target_tpl.grid):
        epsilon = epsilon_vec[i]
        if epsilon is None and fallback_epsilon_vec is not None:
            epsilon = fallback_epsilon_vec[i]
        if epsilon is None:
            epsilon = 0.0
        result.append(_js_round(grid * (1 + target_tpl.coeff * epsilon)))
    return result


def match_residual(predicted: Sequence[int], week_prices: Sequence[float | None]) -> float:
    """返回 mean(((预测价 - 实际价) / 实际价)²)。"""
    differences = [
        ((predicted[i] - price) / price) ** 2
        for i, price in enumerate(week_prices)
        if price is not None
    ]
    return sum(differences) / len(differences) if differences else math.inf


def enum_epsilon_candidates(week_prices: Sequence[float | None]) -> list[dict[str, Any]]:
    return [
        {"src_tpl": tpl, "epsilon_vec": compute_epsilon_vec(week_prices, tpl)}
        for tpl in TEMPLATES
    ]


def enum_all_predictions(epsilon_candidates: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    results: list[dict[str, Any]] = []
    for candidate in epsilon_candidates:
        for target_tpl in TEMPLATES:
            results.append(
                {
                    "src_tpl": candidate["src_tpl"],
                    "target_tpl": target_tpl,
                    "epsilon_vec": candidate["epsilon_vec"],
                    "predicted": predict(candidate["epsilon_vec"], target_tpl),
                }
            )
    return results


def filter_predictions(
    predictions: Iterable[dict[str, Any]], week_prices: Sequence[float | None]
) -> list[dict[str, Any]]:
    scored = [
        {**item, "residual": match_residual(item["predicted"], week_prices)}
        for item in predictions
    ]
    scored.sort(key=lambda item: item["residual"])
    if not scored:
        return []
    threshold = max(scored[0]["residual"] * 5, MATCH_THRESHOLD)
    filtered = [item for item in scored if item["residual"] <= threshold]
    return filtered or [scored[0]]


def filter_eight_predictions(
    predictions: Iterable[dict[str, Any]], current_week_prices: Sequence[float | None]
) -> list[dict[str, Any]]:
    predictions = list(predictions)
    if not any(price is not None for price in current_week_prices):
        return [
            {**item, "eliminated": False, "residual": None, "confidence": None}
            for item in predictions
        ]

    scored = [
        {**item, "residual": match_residual(item["predicted"], current_week_prices)}
        for item in predictions
    ]
    scored.sort(key=lambda item: item["residual"])
    minimum = scored[0]["residual"]
    threshold = max(minimum * 5, MATCH_THRESHOLD)
    return [
        {
            **item,
            "eliminated": item["residual"] > threshold,
            "confidence": 1.0
            if minimum < 1e-9
            else min(1.0, minimum / item["residual"]),
        }
        for item in scored
    ]


def analyze_item(records: Sequence[dict[str, Any]]) -> dict[str, Any] | None:
    """复现原分析器的跨周候选交集与回退规则。记录必须按时间先后排列。"""
    if not records:
        return None
    week_data = [
        {"week": record.get("week", f"W{i + 1}"), "prices": record["prices"]}
        for i, record in enumerate(records)
    ]

    if len(week_data) == 1:
        candidates = enum_epsilon_candidates(week_data[0]["prices"])
        return {
            "stage": "first_week",
            "week": week_data[0]["week"],
            "candidates": candidates,
            "predictions": enum_all_predictions(candidates),
            "epsilon_vec": None,
            "locked_src_tpl": None,
        }

    candidate_set: dict[int, dict[str, Any]] | None = None
    for first, second in zip(week_data, week_data[1:]):
        predictions = enum_all_predictions(enum_epsilon_candidates(first["prices"]))
        matched = filter_predictions(predictions, second["prices"])
        if not matched:
            continue

        this_round: dict[int, dict[str, Any]] = {}
        for match in matched:
            template_id = match["src_tpl"].id
            previous = this_round.get(template_id)
            if previous is None or match["residual"] < previous["residual"]:
                this_round[template_id] = match

        if candidate_set is None:
            candidate_set = this_round
        else:
            intersection = {
                template_id: (
                    value
                    if value["residual"] < candidate_set[template_id]["residual"]
                    else candidate_set[template_id]
                )
                for template_id, value in this_round.items()
                if template_id in candidate_set
            }
            candidate_set = intersection if intersection else this_round

    if not candidate_set:
        latest = week_data[-1]
        candidates = enum_epsilon_candidates(latest["prices"])
        return {
            "stage": "first_week",
            "week": latest["week"],
            "candidates": candidates,
            "predictions": enum_all_predictions(candidates),
            "epsilon_vec": None,
            "locked_src_tpl": None,
        }

    ordered_candidates = sorted(candidate_set.values(), key=lambda item: item["residual"])
    best = ordered_candidates[0]
    complete_weeks = [week for week in week_data if all(p is not None for p in week["prices"])]
    is_unique = len(candidate_set) == 1 and bool(complete_weeks)

    full_epsilon_vec = best["epsilon_vec"]
    if complete_weeks:
        full_epsilon_vec = compute_epsilon_vec(
            complete_weeks[-1]["prices"], best["src_tpl"]
        )

    eight_predictions = [
        {
            "target_tpl": tpl,
            "epsilon_vec": best["epsilon_vec"],
            "predicted": predict(best["epsilon_vec"], tpl, full_epsilon_vec),
        }
        for tpl in TEMPLATES
    ]

    all_candidate_predictions = None
    if not is_unique:
        all_candidate_predictions = []
        for candidate in ordered_candidates:
            candidate_full_epsilon = candidate["epsilon_vec"]
            if complete_weeks:
                candidate_full_epsilon = compute_epsilon_vec(
                    complete_weeks[-1]["prices"], candidate["src_tpl"]
                )
            all_candidate_predictions.append(
                {
                    "src_tpl": candidate["src_tpl"],
                    "residual": candidate["residual"],
                    "epsilon_vec": candidate_full_epsilon,
                    "eight": [
                        {
                            "target_tpl": tpl,
                            "epsilon_vec": candidate_full_epsilon,
                            "predicted": predict(
                                candidate_full_epsilon, tpl, candidate_full_epsilon
                            ),
                        }
                        for tpl in TEMPLATES
                    ],
                }
            )

    return {
        "stage": "locked" if is_unique else "pending",
        "epsilon_vec": full_epsilon_vec,
        "raw_epsilon_vec": best["epsilon_vec"],
        "locked_src_tpl": best["src_tpl"],
        "locked_target_tpl": best["target_tpl"],
        "eight_predictions": eight_predictions,
        "all_candidates_predictions": all_candidate_predictions,
        "week_count": len(week_data),
        "candidate_count": len(candidate_set),
    }


def analyze_item_safe(records: Sequence[dict[str, Any]]) -> dict[str, Any] | None:
    """优先使用完整周/缺 1～2 天的周，避免零散周干扰收敛。"""
    if not records:
        return None
    complete = [r for r in records if all(p is not None for p in r["prices"])]
    near_complete = [
        r for r in records if 1 <= sum(p is None for p in r["prices"]) <= 2
    ]

    if not complete and not near_complete:
        result = analyze_item(records)
        if result and result["stage"] == "locked":
            result["stage"] = "pending"
        return result
    if len(complete) == len(records):
        return analyze_item(records)

    fitting_records = complete if complete else near_complete
    fit_result = analyze_item(fitting_records)
    full_result = analyze_item(records)

    if not full_result or full_result["stage"] == "first_week":
        final = fit_result or analyze_item(records)
    elif not fit_result or fit_result["stage"] == "first_week":
        final = full_result
    else:
        fit_count = fit_result.get("candidate_count", 8)
        full_count = full_result.get("candidate_count", 8)
        final = full_result if full_count <= fit_count else fit_result

    if not complete and final and final["stage"] == "locked":
        final["stage"] = "pending"
    return final


def _template_json(template: PriceTemplate) -> dict[str, Any]:
    return {"id": template.id, "name": template.name, "coeff": template.coeff}


def _possibility_json(item: dict[str, Any]) -> dict[str, Any]:
    result = {
        "template": _template_json(item["target_tpl"]),
        "prices": item["predicted"],
        "eliminated": item.get("eliminated", False),
        "residual": item.get("residual"),
        "confidence": item.get("confidence"),
    }
    return result


def _finite_or_none(value: float | None) -> float | None:
    return value if value is not None and math.isfinite(value) else None


def calculate_future_prices(
    history_weeks: Sequence[Sequence[float | int | None]],
    current_week: Sequence[float | int | None] | None = None,
) -> dict[str, Any]:
    """高层入口。

    history_weeks 按从旧到新排列；current_week 可为本周已观测的零散价格。
    没有 current_week 时返回下一周所有可能；有时用实测值过滤本周模板。
    """
    normalized_history = [normalize_week(week) for week in history_weeks]
    normalized_current = normalize_week(current_week) if current_week is not None else None
    all_weeks = normalized_history + ([normalized_current] if normalized_current is not None else [])
    if not all_weeks:
        raise ValueError("至少需要提供一周价格")
    records = [
        {"week": f"W{i + 1}", "prices": prices}
        for i, prices in enumerate(all_weeks)
    ]
    analysis = analyze_item_safe(records)
    assert analysis is not None

    observed = normalized_current or [None] * 7
    groups: list[dict[str, Any]] = []
    if analysis["stage"] == "first_week":
        for candidate in analysis["candidates"]:
            items = [
                item
                for item in analysis["predictions"]
                if item["src_tpl"].id == candidate["src_tpl"].id
            ]
            groups.append(
                {
                    "source_template": _template_json(candidate["src_tpl"]),
                    "residual": None,
                    "possibilities": [_possibility_json(item) for item in items],
                }
            )
    elif analysis.get("all_candidates_predictions"):
        for candidate in analysis["all_candidates_predictions"]:
            filtered = filter_eight_predictions(candidate["eight"], observed)
            groups.append(
                {
                    "source_template": _template_json(candidate["src_tpl"]),
                    "residual": _finite_or_none(candidate["residual"]),
                    "possibilities": [_possibility_json(item) for item in filtered],
                }
            )
    else:
        filtered = filter_eight_predictions(analysis["eight_predictions"], observed)
        groups.append(
            {
                "source_template": _template_json(analysis["locked_src_tpl"]),
                "residual": None,
                "possibilities": [_possibility_json(item) for item in filtered],
            }
        )

    remaining = sum(
        not possibility["eliminated"]
        for group in groups
        for possibility in group["possibilities"]
    )
    return {
        "stage": analysis["stage"],
        "scope": "current_week"
        if current_week is not None and analysis["stage"] != "first_week"
        else "next_week",
        "candidate_count": analysis.get("candidate_count", 8),
        "remaining_possibilities": remaining,
        "epsilon": analysis.get("epsilon_vec"),
        "groups": groups,
    }


def parse_week(text: str) -> WeekPrices:
    """解析逗号分隔价格；空、-、null、? 表示缺失。"""
    tokens = [token.strip() for token in text.split(",")]
    if len(tokens) != 7:
        raise ValueError("一周价格必须是 7 个逗号分隔值")
    missing = {"", "-", "null", "none", "?", "空"}
    values: list[float | None] = []
    for token in tokens:
        values.append(None if token.lower() in missing else float(token))
    return normalize_week(values)


def _print_human(result: dict[str, Any]) -> None:
    print(f"阶段: {result['stage']}  范围: {result['scope']}  剩余: {result['remaining_possibilities']} 组")
    if result["epsilon"] is not None:
        print("ε: " + ", ".join(f"{value:.6f}" if value is not None else "-" for value in result["epsilon"]))
    for group in result["groups"]:
        source = group["source_template"]
        print(f"\nε 来源 T{source['id']}·{source['name']}")
        for possibility in group["possibilities"]:
            if possibility["eliminated"]:
                continue
            target = possibility["template"]
            prices = ", ".join(str(price) for price in possibility["prices"])
            print(f"  T{target['id']}·{target['name']}: {prices}")


def main() -> int:
    parser = argparse.ArgumentParser(description="终末地弹性物资未来价格计算器")
    parser.add_argument(
        "--week",
        action="append",
        default=[],
        help="历史周价格，按旧到新重复传入；缺失值写 -（PowerShell 中请加引号）",
    )
    parser.add_argument("--current", help="本周已观测价格，用于过滤本周模板")
    parser.add_argument("--json", action="store_true", help="输出完整 JSON")
    args = parser.parse_args()
    try:
        history = [parse_week(value) for value in args.week]
        current = parse_week(args.current) if args.current is not None else None
        result = calculate_future_prices(history, current)
    except ValueError as error:
        parser.error(str(error))
    if args.json:
        print(json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False))
    else:
        _print_human(result)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
