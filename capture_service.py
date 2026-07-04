"""屏幕识别、按日期存储及预测编排。"""

from __future__ import annotations

import ctypes
import json
import math
import re
import sqlite3
import threading
from contextlib import closing
from dataclasses import asdict, dataclass
from datetime import date, datetime, timedelta
from pathlib import Path
from typing import Any, Sequence

from endfield_price import PRICE_MAX, PRICE_MIN, calculate_future_prices


APP_DIR = Path.home() / "AppData" / "Local" / "EndfieldPriceOverlay"
DEFAULT_DB_PATH = APP_DIR / "prices.db"
DEFAULT_CONFIG_PATH = APP_DIR / "config.json"
DEBUG_DIR = APP_DIR / "debug"


@dataclass(frozen=True)
class ScreenLayout:
    # 1080p 默认布局。游戏在 4K 下会改变 UI 的归一化位置，见 effective_layout。
    # 名称区只覆盖商品名所在行，避免把下方“弹性需求物资”当成名称。
    name_roi: tuple[float, float, float, float] = (0.385, 0.260, 0.625, 0.325)
    chart_roi: tuple[float, float, float, float] = (0.500, 0.445, 0.865, 0.650)


FOUR_K_CHART_ROI = (0.472, 0.445, 0.810, 0.650)
FOUR_K_NAME_ROI = (0.370, 0.245, 0.625, 0.325)


def effective_layout(layout: ScreenLayout, screenshot_width: int) -> ScreenLayout:
    """仅对未校准的默认布局应用实测 4K 坐标；用户校准结果保持原样。"""
    default = ScreenLayout()
    if screenshot_width >= 3_000 and layout == default:
        return ScreenLayout(name_roi=FOUR_K_NAME_ROI, chart_roi=FOUR_K_CHART_ROI)
    return layout


@dataclass(frozen=True)
class OCRToken:
    text: str
    score: float
    center_x: float
    center_y: float


@dataclass
class CaptureReading:
    item_name: str
    prices: list[int | None]
    name_confidence: float
    price_confidences: list[float | None]
    captured_at: datetime

    @property
    def complete(self) -> bool:
        return bool(self.item_name.strip()) and all(price is not None for price in self.prices)

    @property
    def confident(self) -> bool:
        scores = [score for score in self.price_confidences if score is not None]
        return self.complete and self.name_confidence >= 0.55 and len(scores) == 7 and min(scores) >= 0.45


def load_layout(path: Path = DEFAULT_CONFIG_PATH) -> ScreenLayout:
    if not path.exists():
        return ScreenLayout()
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        return ScreenLayout(
            name_roi=tuple(float(v) for v in data["name_roi"]),
            chart_roi=tuple(float(v) for v in data["chart_roi"]),
        )
    except (OSError, KeyError, TypeError, ValueError, json.JSONDecodeError):
        return ScreenLayout()


def save_layout(layout: ScreenLayout, path: Path = DEFAULT_CONFIG_PATH) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(asdict(layout), ensure_ascii=False, indent=2), encoding="utf-8")


def crop_ratio(image: Any, roi: Sequence[float]) -> Any:
    width, height = image.size
    left, top, right, bottom = roi
    return image.crop(
        (
            round(left * width),
            round(top * height),
            round(right * width),
            round(bottom * height),
        )
    )


def find_window_client_bbox(title_contains: str = "Endfield") -> tuple[int, int, int, int]:
    """按标题查找最大的可见窗口，返回其客户区屏幕坐标。"""
    if not hasattr(ctypes, "windll"):
        raise RuntimeError("窗口标题识别仅支持 Windows")
    from ctypes import wintypes

    user32 = ctypes.windll.user32
    matches: list[tuple[int, tuple[int, int, int, int], str]] = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def callback(hwnd: int, _lparam: int) -> bool:
        if not user32.IsWindowVisible(hwnd) or user32.IsIconic(hwnd):
            return True
        length = user32.GetWindowTextLengthW(hwnd)
        if length <= 0:
            return True
        buffer = ctypes.create_unicode_buffer(length + 1)
        user32.GetWindowTextW(hwnd, buffer, length + 1)
        title = buffer.value
        if title_contains.casefold() not in title.casefold():
            return True

        rect = wintypes.RECT()
        if not user32.GetClientRect(hwnd, ctypes.byref(rect)):
            return True
        top_left = wintypes.POINT(rect.left, rect.top)
        bottom_right = wintypes.POINT(rect.right, rect.bottom)
        if not user32.ClientToScreen(hwnd, ctypes.byref(top_left)):
            return True
        if not user32.ClientToScreen(hwnd, ctypes.byref(bottom_right)):
            return True
        bbox = (top_left.x, top_left.y, bottom_right.x, bottom_right.y)
        area = max(0, bbox[2] - bbox[0]) * max(0, bbox[3] - bbox[1])
        if area > 0:
            matches.append((area, bbox, title))
        return True

    if not user32.EnumWindows(callback, 0):
        raise RuntimeError("无法枚举 Windows 窗口")
    if not matches:
        raise RuntimeError(f"未找到标题包含“{title_contains}”的可见游戏窗口")
    return max(matches, key=lambda item: item[0])[1]


def capture_game_screen(window_title: str = "Endfield") -> Any:
    """按窗口标题截取游戏客户区，工具可放在任意显示器。"""
    from PIL import ImageGrab

    bbox = find_window_client_bbox(window_title)
    return ImageGrab.grab(bbox=bbox, all_screens=True)


def _clean_price(text: str) -> int | None:
    normalized = text.translate(str.maketrans({"O": "0", "o": "0", "I": "1", "l": "1"}))
    matches = re.findall(r"\d{3,4}", normalized.replace(",", ""))
    candidates = [int(value) for value in matches if PRICE_MIN <= int(value) <= PRICE_MAX]
    return candidates[-1] if candidates else None


def map_price_tokens(tokens: Sequence[OCRToken], width: float) -> tuple[list[int | None], list[float | None]]:
    """把价格区 OCR 结果按横坐标映射到从旧到新的 7 个日期。"""
    prices: list[int | None] = [None] * 7
    scores: list[float | None] = [None] * 7
    if width <= 0:
        return prices, scores
    for token in tokens:
        value = _clean_price(token.text)
        if value is None:
            continue
        # 按最近柱中心匹配，不用左边界取整；ROI 有少量留白时也不会整列偏移。
        center_position = token.center_x / width * 7 - 0.5
        index = max(0, min(6, math.floor(center_position + 0.5)))
        if scores[index] is None or token.score > scores[index]:
            prices[index] = value
            scores[index] = token.score
    return prices, scores


class OCRService:
    """RapidOCR 延迟加载封装，模型完全在本机运行。"""

    def __init__(self) -> None:
        self._engine: Any = None
        self._lock = threading.Lock()

    def _get_engine(self) -> Any:
        with self._lock:
            if self._engine is None:
                from rapidocr import RapidOCR

                self._engine = RapidOCR()
            return self._engine

    @staticmethod
    def _prepare(image: Any) -> tuple[Any, int]:
        from PIL import Image, ImageEnhance, ImageOps

        image = image.convert("RGB")
        # 1080p 放大两倍；4K 原图已经足够清晰，避免超大图被 OCR 再次缩放。
        scale = 2 if image.width < 1_000 else 1
        if scale > 1:
            image = image.resize(
                (image.width * scale, image.height * scale),
                Image.Resampling.LANCZOS,
            )
        image = ImageOps.autocontrast(image)
        return ImageEnhance.Contrast(image).enhance(1.25), scale

    def _tokens(self, image: Any) -> list[OCRToken]:
        import numpy as np

        prepared, scale = self._prepare(image)
        output = self._get_engine()(np.asarray(prepared))
        if output.txts is None or output.boxes is None or output.scores is None:
            return []
        tokens: list[OCRToken] = []
        for box, text, score in zip(output.boxes, output.txts, output.scores):
            xs = [float(point[0]) for point in box]
            ys = [float(point[1]) for point in box]
            tokens.append(
                OCRToken(
                    text=str(text).strip(),
                    score=float(score),
                    # 对外始终返回原始裁剪图坐标，调用方无需知道预处理倍率。
                    center_x=sum(xs) / len(xs) / scale,
                    center_y=sum(ys) / len(ys) / scale,
                )
            )
        return tokens

    @staticmethod
    def _choose_name(tokens: Sequence[OCRToken]) -> tuple[str, float]:
        ignored = (
            "需求物资",
            "库存",
            "拥有",
            "购买",
            "出售",
            "今日售价",
            "建议价格",
        )
        candidates: list[tuple[OCRToken, str]] = []
        for token in tokens:
            text = re.sub(r"\s+", "", token.text)
            if any(word in text for word in ignored):
                continue
            if not re.search(r"[\u4e00-\u9fff]", text) or not 2 <= len(text) <= 24:
                continue
            candidates.append((token, text))
        if not candidates:
            return "", 0.0
        # 商品名在分类文字上方；纵向位置优先于 OCR 置信度和文字长度。
        token, text = min(
            candidates,
            key=lambda item: (item[0].center_y, -item[0].score, -len(item[1])),
        )
        return text, token.score

    def recognize(self, screenshot: Any, layout: ScreenLayout, captured_at: datetime | None = None) -> CaptureReading:
        layout = effective_layout(layout, screenshot.width)
        name_crop = crop_ratio(screenshot, layout.name_roi)
        chart_crop = crop_ratio(screenshot, layout.chart_roi)
        name_tokens = self._tokens(name_crop)
        name, name_score = self._choose_name(name_tokens)

        chart_tokens = self._tokens(chart_crop)
        prices, scores = map_price_tokens(chart_tokens, chart_crop.width)
        column_debug: list[dict[str, Any]] = []
        column_images: list[Any] = []

        # 每一柱都单独识别并覆盖整体结果。整体 OCR 在漏掉首柱时可能把后六项
        # 连续映射到前六列；只补空位无法纠正这种“看似完整”的错位。
        for index in range(7):
            left = round(chart_crop.width * index / 7)
            right = round(chart_crop.width * (index + 1) / 7)
            column = chart_crop.crop((left, 0, right, chart_crop.height))
            column_images.append(column)
            column_tokens = self._tokens(column)
            candidates = [
                (_clean_price(token.text), token.score) for token in column_tokens
            ]
            candidates = [(value, score) for value, score in candidates if value is not None]
            if candidates:
                value, score = max(candidates, key=lambda item: item[1])
                prices[index] = value
                scores[index] = score
            column_debug.append(
                {
                    "index": index,
                    "crop": [left, 0, right, chart_crop.height],
                    "tokens": [asdict(token) for token in column_tokens],
                    "selected": prices[index],
                }
            )

        self._save_debug(
            screenshot_size=screenshot.size,
            layout=layout,
            name_crop=name_crop,
            chart_crop=chart_crop,
            column_images=column_images,
            name_tokens=name_tokens,
            chart_tokens=chart_tokens,
            column_debug=column_debug,
            prices=prices,
        )

        return CaptureReading(
            item_name=name,
            prices=prices,
            name_confidence=name_score,
            price_confidences=scores,
            captured_at=captured_at or datetime.now(),
        )

    @staticmethod
    def _save_debug(
        *,
        screenshot_size: Sequence[int],
        layout: ScreenLayout,
        name_crop: Any,
        chart_crop: Any,
        column_images: Sequence[Any],
        name_tokens: Sequence[OCRToken],
        chart_tokens: Sequence[OCRToken],
        column_debug: Sequence[dict[str, Any]],
        prices: Sequence[int | None],
    ) -> None:
        """只保存识别区域，不保存完整游戏截图。文件会被下一次识别覆盖。"""
        try:
            DEBUG_DIR.mkdir(parents=True, exist_ok=True)
            name_crop.save(DEBUG_DIR / "latest_name.png")
            chart_crop.save(DEBUG_DIR / "latest_chart.png")
            for index, image in enumerate(column_images):
                image.save(DEBUG_DIR / f"latest_column_{index}.png")
            payload = {
                "screenshot_size": list(screenshot_size),
                "layout": asdict(layout),
                "name_tokens": [asdict(token) for token in name_tokens],
                "chart_tokens": [asdict(token) for token in chart_tokens],
                "columns": list(column_debug),
                "prices": list(prices),
            }
            (DEBUG_DIR / "latest_ocr.json").write_text(
                json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8"
            )
        except OSError:
            pass


class CaptureStore:
    """每次识别保留原始快照；同商品同日期以最后一次识别为准。"""

    def __init__(self, path: Path = DEFAULT_DB_PATH) -> None:
        self.path = path
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self._initialize()

    def _connect(self) -> sqlite3.Connection:
        connection = sqlite3.connect(self.path)
        connection.row_factory = sqlite3.Row
        return connection

    def _initialize(self) -> None:
        with closing(self._connect()) as connection:
            connection.execute(
                """
                CREATE TABLE IF NOT EXISTS captures (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    captured_at TEXT NOT NULL,
                    item_name TEXT NOT NULL,
                    prices_json TEXT NOT NULL
                )
                """
            )
            connection.execute(
                "CREATE INDEX IF NOT EXISTS idx_captures_item_time ON captures(item_name, captured_at)"
            )
            connection.commit()

    @staticmethod
    def _clean_name(name: str) -> str:
        return re.sub(r"\s+", "", name).strip()

    def save(self, reading: CaptureReading) -> int:
        name = self._clean_name(reading.item_name)
        if not name:
            raise ValueError("商品名称不能为空")
        if len(reading.prices) != 7 or any(price is None for price in reading.prices):
            raise ValueError("必须确认完整的 7 天价格")
        with closing(self._connect()) as connection:
            cursor = connection.execute(
                "INSERT INTO captures(captured_at, item_name, prices_json) VALUES (?, ?, ?)",
                (
                    reading.captured_at.isoformat(timespec="seconds"),
                    name,
                    json.dumps(reading.prices, ensure_ascii=False),
                ),
            )
            connection.commit()
            capture_id = int(cursor.lastrowid)
        return capture_id

    def update(self, capture_id: int, reading: CaptureReading) -> None:
        name = self._clean_name(reading.item_name)
        if not name or len(reading.prices) != 7 or any(price is None for price in reading.prices):
            raise ValueError("名称和 7 天价格必须完整")
        with closing(self._connect()) as connection:
            connection.execute(
                "UPDATE captures SET captured_at=?, item_name=?, prices_json=? WHERE id=?",
                (
                    reading.captured_at.isoformat(timespec="seconds"),
                    name,
                    json.dumps(reading.prices, ensure_ascii=False),
                    capture_id,
                ),
            )
            connection.commit()

    def dated_prices(self, item_name: str) -> dict[date, int]:
        values: dict[date, int] = {}
        with closing(self._connect()) as connection:
            rows = connection.execute(
                "SELECT captured_at, prices_json FROM captures WHERE item_name=? ORDER BY captured_at, id",
                (self._clean_name(item_name),),
            ).fetchall()
        for row in rows:
            captured = datetime.fromisoformat(row["captured_at"]).date()
            prices = json.loads(row["prices_json"])
            for index, price in enumerate(prices):
                values[captured - timedelta(days=6 - index)] = int(price)
        return values

    def item_names(self) -> list[str]:
        with closing(self._connect()) as connection:
            rows = connection.execute(
                "SELECT item_name, MAX(captured_at) AS latest FROM captures GROUP BY item_name ORDER BY latest DESC"
            ).fetchall()
        return [str(row["item_name"]) for row in rows]


def _week_start(day: date) -> date:
    return day - timedelta(days=day.weekday())


def _group_weeks(dated_prices: dict[date, int]) -> dict[date, list[int | None]]:
    weeks: dict[date, list[int | None]] = {}
    for day, price in dated_prices.items():
        monday = _week_start(day)
        weeks.setdefault(monday, [None] * 7)[day.weekday()] = price
    return weeks


def prediction_status(store: CaptureStore, item_name: str, today: date | None = None) -> dict[str, Any]:
    today = today or date.today()
    dated = store.dated_prices(item_name)
    weeks = _group_weeks(dated)
    current_monday = _week_start(today)
    history = [weeks[monday] for monday in sorted(weeks) if monday < current_monday]
    current = weeks.get(current_monday, [None] * 7)
    complete_count = sum(all(value is not None for value in week) for week in weeks.values())

    if not dated:
        return {"kind": "insufficient", "message": "还没有价格记录", "future": [], "ranges": []}

    result = calculate_future_prices(history, current if any(v is not None for v in current) else None)
    stage = result["stage"]
    survivors = [
        possibility
        for group in result["groups"]
        for possibility in group["possibilities"]
        if not possibility["eliminated"]
    ]

    if stage == "first_week":
        message = (
            "先补齐任意一周的 7 天价格"
            if complete_count == 0
            else "已有完整周；下周再记录 1～2 天即可校准"
        )
        return {"kind": "insufficient", "message": message, "future": [], "ranges": []}

    if stage != "locked":
        return {
            "kind": "pending",
            "message": f"ε 尚有 {result['candidate_count']} 个候选，继续记录新的价格",
            "future": [],
            "ranges": [],
        }

    future_indexes = list(range(today.weekday() + 1, 7))
    if len(survivors) == 1:
        prices = survivors[0]["prices"]
        future = [
            {
                "date": (current_monday + timedelta(days=index)).isoformat(),
                "weekday": index,
                "price": prices[index],
            }
            for index in future_indexes
        ]
        message = "本周走势已确定" if future else "本周已结束；下周模板将在周一重新随机"
        return {"kind": "ready", "message": message, "future": future, "ranges": []}

    ranges = [
        {
            "date": (current_monday + timedelta(days=index)).isoformat(),
            "weekday": index,
            "minimum": min(item["prices"][index] for item in survivors),
            "maximum": max(item["prices"][index] for item in survivors),
        }
        for index in future_indexes
    ]
    return {
        "kind": "filtering",
        "message": f"ε 已锁定，本周走势还剩 {len(survivors)} 种；再记录一天可继续排除",
        "future": [],
        "ranges": ranges,
    }


def _select_roi_cancellable(image: Any, title: str) -> tuple[int, int, int, int] | None:
    """支持 Enter 确认、Esc/Q/关闭取消的 OpenCV 框选器。"""
    import cv2
    import numpy as np

    source = cv2.cvtColor(np.asarray(image.convert("RGB")), cv2.COLOR_RGB2BGR)
    height, width = source.shape[:2]
    scale = min(1.0, 1600 / width, 900 / height)
    preview = cv2.resize(source, (round(width * scale), round(height * scale)))
    state: dict[str, Any] = {"start": None, "end": None, "dragging": False}

    def mouse(event: int, x: int, y: int, _flags: int, _param: Any) -> None:
        if event == cv2.EVENT_LBUTTONDOWN:
            state.update(start=(x, y), end=(x, y), dragging=True)
        elif event == cv2.EVENT_MOUSEMOVE and state["dragging"]:
            state["end"] = (x, y)
        elif event == cv2.EVENT_LBUTTONUP and state["dragging"]:
            state.update(end=(x, y), dragging=False)

    cv2.namedWindow(title, cv2.WINDOW_AUTOSIZE)
    cv2.setMouseCallback(title, mouse)
    try:
        while True:
            frame = preview.copy()
            if state["start"] and state["end"]:
                cv2.rectangle(frame, state["start"], state["end"], (70, 220, 160), 2)
            cv2.putText(
                frame,
                "Drag to select | ENTER confirm | ESC/Q cancel",
                (20, 36),
                cv2.FONT_HERSHEY_SIMPLEX,
                0.8,
                (70, 220, 160),
                2,
                cv2.LINE_AA,
            )
            cv2.imshow(title, frame)
            try:
                if cv2.getWindowProperty(title, cv2.WND_PROP_VISIBLE) < 1:
                    return None
            except cv2.error:
                return None
            key = cv2.waitKey(20) & 0xFF
            if key in (27, ord("q"), ord("Q"), ord("c"), ord("C")):
                return None
            if key in (10, 13, 32) and state["start"] and state["end"]:
                x1, y1 = state["start"]
                x2, y2 = state["end"]
                left, right = sorted((x1, x2))
                top, bottom = sorted((y1, y2))
                if right - left >= 8 and bottom - top >= 8:
                    return (
                        round(left / scale),
                        round(top / scale),
                        round((right - left) / scale),
                        round((bottom - top) / scale),
                    )
    finally:
        try:
            cv2.destroyWindow(title)
        except cv2.error:
            pass


def calibrate_layout(screenshot: Any, config_path: Path = DEFAULT_CONFIG_PATH) -> ScreenLayout | None:
    """通过两个可取消框选区域生成分辨率无关的识别配置。"""
    name = _select_roi_cancellable(screenshot, "Endfield calibration 1/2 - item name")
    if name is None:
        return None
    chart = _select_roi_cancellable(screenshot, "Endfield calibration 2/2 - seven prices")
    if chart is None:
        return None
    width, height = screenshot.size

    def normalized(rect: Sequence[int]) -> tuple[float, float, float, float]:
        x, y, w, h = rect
        return (x / width, y / height, (x + w) / width, (y + h) / height)

    layout = ScreenLayout(name_roi=normalized(name), chart_roi=normalized(chart))
    save_layout(layout, config_path)
    return layout
