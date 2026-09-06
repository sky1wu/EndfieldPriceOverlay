"""Compare two replay outputs on the same complete item-week cohort."""

import csv
import json
import statistics
import sys
from pathlib import Path


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def cohort(folder):
    with (folder / "cohort.csv").open(encoding="utf-8-sig", newline="") as stream:
        return {(row["Item"], row["Week"]) for row in csv.DictReader(stream)}


def stats(data, selected, prefix):
    runs = [r for r in data["Runs"] if r["Mode"] == "recorded-at" and r["PrefixDays"] == prefix and (r["Item"], r["Week"]) in selected]
    points = [p for p in data["Points"] if p["Mode"] == "recorded-at" and p["PrefixDays"] == prefix and (p["Item"], p["Week"]) in selected]
    exact = [p for p in points if p["Predicted"] is not None]
    ranges = [p for p in points if p["Minimum"] is not None]
    errors = [100 * abs(p["Predicted"] / p["Actual"] - 1) for p in exact]
    return {
        "Ready": sum(r["State"] == "Ready" for r in runs), "Points": len(exact),
        "MAPE": statistics.mean(errors) if errors else None,
        "MaxAPE": max(errors) if errors else None,
        "Over5": sum(error > 5 for error in errors),
        "RangePoints": len(ranges),
        "RangeCoverage": 100 * sum(p["Minimum"] <= p["Actual"] <= p["Maximum"] for p in ranges) / len(ranges) if ranges else None,
        "Withheld": len(points) - len(exact) - len(ranges),
    }


def percent(number):
    return "—" if number is None else f"{number:.4f}%"


def compare(before, after):
    baseline = read_json(before / "backtest.json")
    revised = read_json(after / "backtest.json")
    assert baseline["Weeks"] == revised["Weeks"], "Different actual-price snapshots"
    selected = cohort(before) & cohort(after)
    assert selected, "No common complete item-weeks"
    lines = ["# 修复前后回测对比", "", f"使用相同实际价格和 {len(selected)} 个物资周，按录入时间回放。", "",
        "MAPE 仅统计已发布的点预测；区间覆盖率仅统计已发布区间。主动暂缓输出会提高这些条件指标，必须同时查看发布数量。", "",
        "| 已知天数 | 版本 | 点预测物资周 | 预测点数 | MAPE | 最大相对误差 | 误差>5%点数 | 区间点数 | 区间覆盖率 | 暂缓输出点数 |",
        "|---|---|---:|---:|---:|---:|---:|---:|---:|---:|"]
    rows = []
    for prefix in range(1, 7):
        for name, data in (("修复前", baseline), ("修复后", revised)):
            row = stats(data, selected, prefix)
            rows.append({"PrefixDays": prefix, "Version": name, **row})
            lines.append(f"| {prefix} | {name} | {row['Ready']}/{len(selected)} | {row['Points']} | {percent(row['MAPE'])} | {percent(row['MaxAPE'])} | {row['Over5']} | {row['RangePoints']} | {percent(row['RangeCoverage'])} | {row['Withheld']} |")
    lines += ["", "## 两个版本都发布点预测的同一批目标", "",
        "| 已知天数 | 共同预测点数 | 修复前 MAPE | 修复后 MAPE |", "|---|---:|---:|---:|"]
    for prefix in range(2, 7):
        def published(data):
            return {(p["Item"], p["Cutoff"], p["Date"]): p for p in data["Points"]
                if p["Mode"] == "recorded-at" and p["PrefixDays"] == prefix and p["Predicted"] is not None
                and (p["Item"], p["Week"]) in selected}
        old_points, new_points = published(baseline), published(revised)
        shared = old_points.keys() & new_points.keys()
        errors = [statistics.mean(100 * abs(points[key]["Predicted"] / points[key]["Actual"] - 1)
            for key in shared) if shared else None for points in (old_points, new_points)]
        lines.append(f"| {prefix} | {len(shared)} | {percent(errors[0])} | {percent(errors[1])} |")
    lines += ["", "暂缓输出包括：模型失配、未验证的新高/新低、超出应用支持范围，以及样本不足。其实际价格仍计入总评价点数。", "",
        "这是修复后的同批历史重放，不是尚未见过的新周测试；不能据此承诺未来准确率或保证区间上限。", ""]
    (after / "comparison.md").write_text("\n".join(lines), encoding="utf-8")
    with (after / "comparison.csv").open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)
    print(f"Compared {len(selected)} item-weeks: {after / 'comparison.md'}")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit("Usage: python compare.py BASELINE_FOLDER REVISED_FOLDER")
    compare(Path(sys.argv[1]), Path(sys.argv[2]))
