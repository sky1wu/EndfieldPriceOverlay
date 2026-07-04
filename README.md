# 终末地 · 弹性物资分析仪

Windows 桌面工具。按窗口标题定位 `Endfield`，离线识别商品名称和最近 7 天价格，保存历史并计算本周未来价格。

## 运行

要求：Windows 10/11、[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

双击 `start_overlay.cmd`。脚本会增量构建并启动完整商品总览，不再显示启动小悬浮窗。

使用流程：

1. 游戏使用窗口或无边框全屏模式，打开商品详情页。
2. 点击“识别当前商品”，核对名称及从旧到新的 7 个价格后确认。
3. 左侧查看全部商品，右侧趋势图显示最近 7 个有效日期及具体价格，并提示距离可预测还需记录几天。
4. 识别位置变化时从标题栏“•••”进入调试功能并选择“校准 OCR 识别区域”；按 `Esc` 可立即退出。

窗口捕获按标题查找，不依赖工具与游戏位于同一显示器。OCR、模型和价格数据均留在本机。

## 数据位置

```text
%LOCALAPPDATA%\EndfieldPriceOverlay\prices.db
%LOCALAPPDATA%\EndfieldPriceOverlay\config.json
```

数据库结构兼容旧 Python 版本，升级后会直接显示原有记录。

## 开发

```powershell
dotnet restore .\EndfieldPriceOverlay.slnx
dotnet build .\EndfieldPriceOverlay.slnx
dotnet test .\EndfieldPriceOverlay.slnx
```

生成包含 .NET 运行时的 Windows x64 发布目录：

```powershell
.\scripts\publish.ps1
```

主要目录：

```text
src/EndfieldPriceOverlay/        WPF 应用、OCR、窗口捕获与预测逻辑
tests/EndfieldPriceOverlay.Tests/ 单元测试和算法回归测试
legacy/python/                    原 Python 原型，仅用于回归参考
legacy/reference/                 原算法分析仪存档
```

## 当前实现

- .NET 10 + WPF，Per-Monitor V2 DPI，适配 1080p/4K 和主副屏。
- RapidOcrNet + PP-OCRv5 中文模型，完全离线。
- SQLite 本地存储，按日期合并最近 7 天读数。
- 8 套价格模板、ε 反推、跨周残差收敛和本周实测过滤。
- 校准流程无循环弹窗，任何阶段均可取消。

独占全屏可能阻止普通桌面截图；遇到黑屏时改用无边框全屏。本工具不读取游戏内存，也不模拟输入。
