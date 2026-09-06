# 预测回测

Windows / .NET 10 / Python 3。直接编译应用中的预测、状态、存储源码，不复制算法实现。

在仓库根目录运行：

```powershell
dotnet run --project scripts/PredictionBacktest
python scripts/PredictionBacktest/summarize.py
```

指定数据库和输出目录：

```powershell
dotnet run --project scripts/PredictionBacktest -- C:\path\prices.db artifacts/prediction-backtest
python scripts/PredictionBacktest/summarize.py artifacts/prediction-backtest
```

输入数据库只读，通过 SQLite Backup API 创建一致快照。所有回放写入输出目录的独立 `work-*` 数据库，保留以便审计。原数据库、应用算法均不修改。

- `price-date`：按当前合并价格的日期截断；旧价格即使后来补录，也视为当时已知。
- `recorded-at`：按截止游戏日结束（次日 04:00 前）过滤原始录入和修改时间。覆盖写入的旧版本无法恢复。
- `previous-week`：只保留上周及本周已知数据的诊断对照，使用同一算法。

每周一至周六分别作为截止日，保留当日及以前的信息，仅评分次日到周日。跳过当周完全没有已知价格或未来没有实际值的回放。主统计取 `recorded-at` 中六个截止日都可回放、实际七天完整的物资周，各截止日共享同一集合。输出目录默认被 Git 忽略。

输出：

- `backtest.json`：合并实际周价格、每次分析的 ε / 残差 / 状态、逐日预测。
- `diagnostics.json`：典型错误的逐轮匹配信息；用自身公式生成数据的重复周探针；置信度探针。典型错误追踪使用价格日期截断口径。
- `summary.md` / `summary.csv`：按截止日、状态分别统计；包含两个简单基线和历史窗口对照。
- `points.csv` / `runs.csv` / `cohort.csv` / `weekly-wednesday.csv`：完整明细及主样本清单，CSV 使用 UTF-8 BOM。

当前 `PredictionStatusService.Get(item, day)` 已按价格日期隐藏未来值；旧版本没有此保护。本工具仍先隔离数据库，以支持不同算法版本和按录入时间的严格回放。

修复前后对比应保留两份独立输出，避免覆盖基线：

```powershell
dotnet run --project scripts/PredictionBacktest -- artifacts/prediction-backtest/source-snapshot.db artifacts/prediction-backtest-fixed
python scripts/PredictionBacktest/summarize.py artifacts/prediction-backtest-fixed
python scripts/PredictionBacktest/compare.py artifacts/prediction-backtest artifacts/prediction-backtest-fixed
```

`comparison.md` / `comparison.csv` 同时报告误差、发布数量和暂缓数量。参考区间不作为购买成本的保证上限。
