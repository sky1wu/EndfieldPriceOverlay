"""Summarize backtest.json using only the Python standard library (UTF-8 throughout)."""

import csv
import json
import statistics
import sys
from collections import Counter, defaultdict
from datetime import date, timedelta
from pathlib import Path


def mean(values):
    values = list(values)
    return statistics.mean(values) if values else None


def write_csv(path, rows):
    if not rows:
        return
    with path.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)


def show(value, digits=2):
    return "—" if value is None else f"{value:.{digits}f}"


def summarize(folder):
    data = json.loads((folder / "backtest.json").read_text(encoding="utf-8"))
    diagnostics = json.loads((folder / "diagnostics.json").read_text(encoding="utf-8"))
    runs, points = data["Runs"], data["Points"]
    observed_cutoffs = defaultdict(set)
    for run in runs:
        if run["Mode"] == "recorded-at":
            observed_cutoffs[(run["Item"], run["Week"])].add(run["PrefixDays"])
    complete_weeks = {(w["Item"], w["Week"]) for w in data["Weeks"] if all(p is not None for p in w["Prices"])}
    cohort = {key for key, days in observed_cutoffs.items() if days == set(range(1, 7))} & complete_weeks

    # Guard evaluation alignment and confirm the two data-availability definitions agree
    # on the common cohort. No current-day price is scored as a forecast.
    for point in points:
        cutoff, target, monday = map(date.fromisoformat, (point["Cutoff"], point["Date"], point["Week"]))
        assert monday.weekday() == 0 and cutoff < target <= monday + timedelta(days=6)
        assert point["Horizon"] == (target - cutoff).days
        assert point["PrefixDays"] == cutoff.weekday() + 1
    comparable = defaultdict(dict)
    for point in points:
        if (point["Item"], point["Week"]) in cohort:
            key = (point["Item"], point["Cutoff"], point["Date"])
            comparable[point["Mode"]][key] = tuple(point[k] for k in ("State", "Actual", "Predicted", "Minimum", "Maximum"))
    availability_agrees = comparable["price-date"] == comparable["recorded-at"]

    summary = []
    for mode in ("recorded-at", "price-date", "previous-week"):
        for prefix in range(1, 7):
            selected_runs = [r for r in runs if r["Mode"] == mode and r["PrefixDays"] == prefix and (r["Item"], r["Week"]) in cohort]
            selected_points = [p for p in points if p["Mode"] == mode and p["PrefixDays"] == prefix and (p["Item"], p["Week"]) in cohort]
            exact = [p for p in selected_points if p["Predicted"] is not None]
            ranges = [p for p in selected_points if p["Minimum"] is not None]
            failed_runs = {(p["Item"], p["Cutoff"]) for p in exact if abs(p["Predicted"] / p["Actual"] - 1) > .05}
            summary.append({
                "Mode": mode, "PrefixDays": prefix, "Runs": len(selected_runs),
                "ReadyRuns": sum(r["State"] == "Ready" for r in selected_runs),
                "ReadyRunsWithErrorOver5Percent": len(failed_runs), "PointCount": len(exact),
                "MAE": mean(abs(p["Predicted"] - p["Actual"]) for p in exact),
                "MAPEPercent": mean(100 * abs(p["Predicted"] / p["Actual"] - 1) for p in exact),
                "Within1UnitPercent": mean(100 * (abs(p["Predicted"] - p["Actual"]) <= 1) for p in exact),
                "Within5Percent": mean(100 * (abs(p["Predicted"] / p["Actual"] - 1) <= .05) for p in exact),
                "RangePointCount": len(ranges),
                "RangeCoveragePercent": mean(100 * (p["Minimum"] <= p["Actual"] <= p["Maximum"]) for p in ranges),
                "RangeCoverageWith1UnitPercent": mean(100 * (p["Minimum"] - 1 <= p["Actual"] <= p["Maximum"] + 1) for p in ranges),
                "MeanRangeWidth": mean(p["Maximum"] - p["Minimum"] for p in ranges),
                "LastPriceMAPEPercentOnReadyPoints": mean(100 * abs(p["LastPrice"] / p["Actual"] - 1) for p in exact),
                "PreviousWeekMAPEPercentOnReadyPoints": mean(100 * abs(p["PreviousWeekPrice"] / p["Actual"] - 1) for p in exact if p["PreviousWeekPrice"] is not None),
                "PreviousWeekBaselinePointCount": sum(p["PreviousWeekPrice"] is not None for p in exact),
                "EnvelopeCoverageWith1UnitPercent": mean(100 * (
                    abs(p["Predicted"] - p["Actual"]) <= 1 if p["Predicted"] is not None else
                    p["Minimum"] - 1 <= p["Actual"] <= p["Maximum"] + 1 if p["Minimum"] is not None else False
                ) for p in selected_points),
            })

    by_week = []
    for week in sorted({week for _, week in cohort}):
        selected_runs = [r for r in runs if r["Mode"] == "recorded-at" and r["PrefixDays"] == 3 and r["Week"] == week and (r["Item"], week) in cohort]
        selected_points = [p for p in points if p["Mode"] == "recorded-at" and p["PrefixDays"] == 3 and p["Week"] == week and p["Predicted"] is not None and (p["Item"], week) in cohort]
        by_week.append({"Week": week, "Runs": len(selected_runs), "ReadyRuns": sum(r["State"] == "Ready" for r in selected_runs),
            "PointCount": len(selected_points), "MAPEPercent": mean(100 * abs(p["Predicted"] / p["Actual"] - 1) for p in selected_points)})

    write_csv(folder / "points.csv", points)
    write_csv(folder / "runs.csv", [{k: v for k, v in r.items() if k not in ("Epsilon", "RawEpsilon")} for r in runs])
    write_csv(folder / "summary.csv", summary)
    write_csv(folder / "weekly-wednesday.csv", by_week)
    write_csv(folder / "cohort.csv", [{"Item": item, "Week": week} for item, week in sorted(cohort)])
    main = [row for row in summary if row["Mode"] == "recorded-at"]
    lines = [
        "# 本地价格预测回测统计", "",
        f"数据：{data['FirstDate']}～{data['LastDate']}，{data['ItemCount']} 种物资，{data['PriceCount']} 个合并后的物资日价格。",
        f"主样本：{len(cohort)} 个物资周；只纳入周一至周六均能按录入时间回放、且实际价格完整的周。",
        f"按价格日期截断与按录入时间回放在主样本上的结果一致：{availability_agrees}。", "",
        "预测在游戏日结束、次日 04:00 重置前生成。仅评价截止日之后到周日的价格；使用此前历史及当周已知价格。",
        "误差仅统计 Ready 点预测；区间覆盖率单独统计 Filtering。不同截止日的误差不可视作固定样本上的改善。", "",
        "| 已知天数 | 确定预测物资周 | 预测点数 | MAE | MAPE | 误差≤5% | 区间点数 | 区间覆盖率 | 区间±1覆盖率 |",
        "|---|---:|---:|---:|---:|---:|---:|---:|---:|",
    ]
    for row in main:
        lines.append(f"| {row['PrefixDays']} | {row['ReadyRuns']}/{row['Runs']} | {row['PointCount']} | {show(row['MAE'])} | {show(row['MAPEPercent'])}% | {show(row['Within5Percent'])}% | {row['RangePointCount']} | {show(row['RangeCoveragePercent'])}% | {show(row['RangeCoverageWith1UnitPercent'])}% |")
    lines += ["", "## 每周使用前三天数据", "", "| 周一日期 | 确定预测物资周 | 预测点数 | MAPE |", "|---|---:|---:|---:|"]
    for row in by_week:
        lines.append(f"| {row['Week']} | {row['ReadyRuns']}/{row['Runs']} | {row['PointCount']} | {show(row['MAPEPercent'])}% |")
    lines += ["", "## 历史窗口对照（同一物资周集合）", "", "只保留上周历史，仍调用当前算法；这是故障诊断，不是已验证的新算法。两组 Ready 和区间子样本不同。", "",
        "| 历史 | 已知天数 | 确定预测物资周 | 点预测 MAPE | 区间覆盖率 | 区间±1覆盖率 | 全部输出±1覆盖率 |",
        "|---|---:|---:|---:|---:|---:|---:|"]
    for row in summary:
        if row["Mode"] in ("recorded-at", "previous-week") and row["PrefixDays"] in (1, 3, 4):
            lines.append(f"| {row['Mode']} | {row['PrefixDays']} | {row['ReadyRuns']}/{row['Runs']} | {show(row['MAPEPercent'])}% | {show(row['RangeCoveragePercent'])}% | {show(row['RangeCoverageWith1UnitPercent'])}% | {show(row['EnvelopeCoverageWith1UnitPercent'])}% |")
    lines += ["", "全部输出覆盖率将点预测视为退化区间；它不能衡量区间是否过宽。", "", "## 合成数据诊断", "",
        "使用相同 ε 和算法自身的 Predict（包含取整）生成价格，分别枚举 512 种三周组合，以及在中间重复一周后的四周组合。只暴露最后一周前三天。", "",
        "| 场景 | 单一存活走势 | 未来任一天误差>1 的单一走势 | 未来任一天误差>5%的单一走势 | 没有任何存活曲线完整命中未来（容差1） |",
        "|---|---:|---:|---:|---:|"]
    for name in ("Control", "Repeated"):
        row = diagnostics["Synthetic"][name]
        lines.append(f"| {name} | {row['Ready']} | {row['WrongReady']} | {row['WrongReadyOver5Percent']} | {row['ExcludedTruth']} |")
    lines += ["", "这些是确定性故障探针，不是实际市场出错概率。", "",
        "录入时间回放只能重建数据库仍保留的版本：daily_prices 的历史覆盖版本并未保存。实际值来自当前合并记录，未重新核对截图。",
        "不同天的预测窗口重叠，同一物资和重复模板相关；不把预测点数当作独立样本数。", "",
        f"输入快照 SHA-256：`{data['SnapshotSha256']}`", ""]
    (folder / "summary.md").write_text("\n".join(lines), encoding="utf-8")
    print(f"Cohort: {len(cohort)} item-weeks; date/time replay identical: {availability_agrees}")
    print(f"Summary: {folder / 'summary.md'}")


if __name__ == "__main__":
    summarize(Path(sys.argv[1] if len(sys.argv) > 1 else "artifacts/prediction-backtest"))
