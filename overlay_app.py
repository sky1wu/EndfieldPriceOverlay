"""终末地弹性物资桌面悬浮记录器。"""

from __future__ import annotations

import ctypes
import threading
import tkinter as tk
from datetime import date, datetime, timedelta
from tkinter import messagebox
from typing import Any

from capture_service import (
    CaptureReading,
    CaptureStore,
    OCRService,
    calibrate_layout,
    capture_game_screen,
    load_layout,
)
from capture_service import prediction_status
from endfield_price import DAYS, PRICE_MAX, PRICE_MIN


BG = "#141816"
SURFACE = "#1d231f"
SURFACE_2 = "#252c27"
TEXT = "#eef3ef"
MUTED = "#93a098"
ACCENT = "#a8dc3d"
WARNING = "#e8ba55"
ERROR = "#e36c72"
FONT = "Microsoft YaHei UI"
MONO = "Consolas"
BUILD_VERSION = "0.6.1"


def ui_scale_for_dpi(dpi: float) -> float:
    """把以 96 DPI 设计的窗口尺寸换算到当前显示器。"""
    return max(1.0, min(2.5, dpi / 96.0))


class OverlayApp:
    def __init__(self, root: tk.Tk, store: CaptureStore | None = None) -> None:
        self.root = root
        self.store = store or CaptureStore()
        self.ocr = OCRService()
        self.layout = load_layout()
        self.expanded = False
        self.busy = False
        self.last_reading: CaptureReading | None = None
        self.last_capture_id: int | None = None
        self.drag_origin: tuple[int, int, int, int] | None = None
        self.ui_scale = ui_scale_for_dpi(float(root.winfo_fpixels("1i")))

        root.overrideredirect(True)
        root.attributes("-topmost", True)
        root.configure(bg=BG)
        root.bind("<Escape>", lambda _event: self.show_dashboard())
        root.bind("<Control-r>", lambda _event: self.scan())
        self.show_dashboard()

    def _px(self, value: int | float) -> int:
        return max(1, round(value * self.ui_scale))

    def _clear(self) -> None:
        self.root.unbind_all("<MouseWheel>")
        for child in self.root.winfo_children():
            child.destroy()

    def _screen_position(self, width: int, height: int) -> tuple[int, int]:
        self.root.update_idletasks()
        screen_w = self.root.winfo_screenwidth()
        screen_h = self.root.winfo_screenheight()
        if self.root.winfo_x() > 0:
            x = min(self.root.winfo_x(), screen_w - width - self._px(18))
            y = min(self.root.winfo_y(), screen_h - height - self._px(48))
            return max(self._px(10), x), max(self._px(10), y)
        return screen_w - width - self._px(28), max(self._px(80), (screen_h - height) // 2)

    def _start_drag(self, event: tk.Event) -> None:
        self.drag_origin = (event.x_root, event.y_root, self.root.winfo_x(), self.root.winfo_y())

    def _drag(self, event: tk.Event) -> None:
        if self.drag_origin is None:
            return
        start_x, start_y, window_x, window_y = self.drag_origin
        self.root.geometry(f"+{window_x + event.x_root - start_x}+{window_y + event.y_root - start_y}")

    def _topbar(self, title: str, status: str = "ONLINE") -> tk.Frame:
        top = tk.Frame(self.root, bg=BG, height=self._px(54))
        top.pack(fill="x")
        top.pack_propagate(False)
        top.bind("<Button-1>", self._start_drag)
        top.bind("<B1-Motion>", self._drag)

        mark = tk.Label(top, text="EP", bg=ACCENT, fg="#111510", font=(MONO, 11, "bold"), padx=8, pady=5)
        mark.pack(side="left", padx=(16, 10), pady=13)
        tk.Label(top, text=title, bg=BG, fg=TEXT, font=(FONT, 12, "bold")).pack(side="left")
        tk.Label(top, text=f"v{BUILD_VERSION} · {status}", bg=BG, fg=ACCENT, font=(MONO, 8)).pack(side="left", padx=9)
        tk.Button(
            top, text="×", command=self.root.destroy, bg=BG, fg=MUTED, activebackground=BG,
            activeforeground=TEXT, relief="flat", bd=0, font=(MONO, 16), cursor="hand2",
        ).pack(side="right", padx=(0, 12))
        return top

    def _show_shell(
        self,
        title: str = "弹性物资记录器",
        status: str = "ONLINE",
        size: tuple[int, int] = (540, 470),
    ) -> tk.Frame:
        self.expanded = True
        self._clear()
        width, height = self._px(size[0]), self._px(size[1])
        x, y = self._screen_position(width, height)
        self.root.geometry(f"{width}x{height}+{x}+{y}")
        self.root.configure(bg=BG)
        self.root.deiconify()
        self._topbar(title, status)
        divider = tk.Frame(self.root, height=1, bg="#344038")
        divider.pack(fill="x")
        body = tk.Frame(self.root, bg=SURFACE)
        body.pack(fill="both", expand=True)
        return body

    def _label(self, parent: tk.Misc, text: str, *, color: str = TEXT, size: int = 10, bold: bool = False) -> tk.Label:
        return tk.Label(parent, text=text, bg=parent.cget("bg"), fg=color, font=(FONT, size, "bold" if bold else "normal"))

    def show_dashboard(self) -> None:
        body = self._show_shell(title="弹性物资分析", status="OVERVIEW", size=(760, 600))
        content = tk.Frame(body, bg=SURFACE)
        content.pack(fill="both", expand=True, padx=24, pady=(18, 18))

        header = tk.Frame(content, bg=SURFACE)
        header.pack(fill="x", pady=(0, 14))
        names = self.store.item_names()
        title = tk.Frame(header, bg=SURFACE)
        title.pack(side="left")
        self._label(title, "已记录商品", size=19, bold=True).pack(anchor="w")
        self._label(title, f"{len(names)} 个商品 · 趋势按日期排列", color=MUTED, size=9).pack(anchor="w", pady=(3, 0))
        self._action_button(header, "识别当前商品", self.scan).pack(side="right")
        self._action_button(header, "校准区域", self.start_calibration, secondary=True).pack(side="right", padx=8)

        tk.Frame(content, bg="#354039", height=1).pack(fill="x")
        if not names:
            empty = tk.Frame(content, bg=SURFACE)
            empty.pack(fill="both", expand=True)
            self._label(empty, "暂无商品记录", color=MUTED, size=14, bold=True).pack(pady=(90, 6))
            self._label(empty, "打开游戏商品详情页后点击“识别当前商品”", color=MUTED, size=9).pack()
            return

        canvas = tk.Canvas(content, bg=SURFACE, highlightthickness=0, bd=0)
        scrollbar = tk.Scrollbar(content, orient="vertical", command=canvas.yview)
        canvas.configure(yscrollcommand=scrollbar.set)
        scrollbar.pack(side="right", fill="y", pady=(10, 0))
        canvas.pack(side="left", fill="both", expand=True, pady=(10, 0))
        rows = tk.Frame(canvas, bg=SURFACE)
        window_id = canvas.create_window((0, 0), window=rows, anchor="nw")
        rows.bind("<Configure>", lambda _event: canvas.configure(scrollregion=canvas.bbox("all")))
        canvas.bind("<Configure>", lambda event: canvas.itemconfigure(window_id, width=event.width))

        def wheel(event: tk.Event) -> None:
            canvas.yview_scroll(-1 if event.delta > 0 else 1, "units")

        self.root.bind_all("<MouseWheel>", wheel)

        for item_index, name in enumerate(names):
            dated = self.store.dated_prices(name)
            series = sorted(dated.items())[-30:]
            row = tk.Frame(rows, bg=SURFACE, height=self._px(104))
            row.pack(fill="x")
            row.pack_propagate(False)
            row.grid_propagate(False)
            row.columnconfigure(0, weight=0, minsize=self._px(265))
            row.columnconfigure(1, weight=1)

            info = tk.Frame(row, bg=SURFACE)
            info.grid(row=0, column=0, sticky="nsew", padx=(2, 18), pady=15)
            self._label(info, name, size=12, bold=True).pack(anchor="w")
            latest_day, latest_price = series[-1]
            self._label(
                info,
                f"最新 {latest_price:,} · {latest_day:%m/%d} · 已记录 {len(series)} 天",
                color=MUTED,
                size=9,
            ).pack(anchor="w", pady=(4, 0))
            try:
                status = prediction_status(self.store, name, date.today())
                status_color = ACCENT if status["kind"] == "ready" else WARNING
                self._label(info, status["message"], color=status_color, size=8).pack(anchor="w", pady=(5, 0))
            except (ValueError, ArithmeticError):
                pass

            chart = tk.Canvas(
                row,
                bg=SURFACE_2,
                highlightthickness=0,
                height=self._px(70),
            )
            chart.grid(row=0, column=1, sticky="nsew", pady=13, padx=(0, 3))
            self.root.after_idle(lambda target=chart, data=series: self._draw_trend(target, data))
            if item_index < len(names) - 1:
                tk.Frame(rows, bg="#303a33", height=1).pack(fill="x")

    def _draw_trend(self, canvas: tk.Canvas, series: list[tuple[date, int]]) -> None:
        canvas.delete("all")
        canvas.update_idletasks()
        width = max(canvas.winfo_width(), self._px(280))
        height = max(canvas.winfo_height(), self._px(70))
        pad_x, pad_y = self._px(14), self._px(11)
        values = [value for _, value in series]
        low, high = min(values), max(values)
        span = max(1, high - low)
        first_ordinal = series[0][0].toordinal()
        day_span = max(1, series[-1][0].toordinal() - first_ordinal)
        points: list[float] = []
        for day, value in series:
            x = pad_x + (day.toordinal() - first_ordinal) / day_span * (width - pad_x * 2)
            y = height - pad_y - (value - low) / span * (height - pad_y * 2)
            points.extend((x, y))
        canvas.create_line(pad_x, height - pad_y, width - pad_x, height - pad_y, fill="#3a443d", width=1)
        if len(series) > 1:
            canvas.create_line(*points, fill=ACCENT, width=self._px(2), smooth=True)
        for x, y in zip(points[0::2], points[1::2]):
            canvas.create_oval(x - self._px(2), y - self._px(2), x + self._px(2), y + self._px(2), fill=TEXT, outline="")
        canvas.create_text(
            width - pad_x,
            pad_y,
            text=f"{values[-1]:,}",
            anchor="ne",
            fill=TEXT,
            font=(MONO, 9, "bold"),
        )

    def show_loading(self) -> None:
        body = self._show_shell(status="SCANNING")
        content = tk.Frame(body, bg=SURFACE)
        content.pack(fill="both", expand=True, padx=26, pady=30)
        self._label(content, "正在读取商品详情", size=18, bold=True).pack(anchor="w")
        self._label(content, "首次运行会加载本地 OCR 模型，之后会更快。", color=MUTED, size=10).pack(anchor="w", pady=(8, 0))
        bar = tk.Frame(content, bg="#344038", height=3)
        bar.pack(fill="x", pady=(30, 0))
        tk.Frame(bar, bg=ACCENT, width=170, height=3).pack(side="left")

    def scan(self) -> None:
        if self.busy:
            return
        self.busy = True
        self.root.withdraw()
        self.root.after(260, self._capture_in_background)

    def _capture_in_background(self) -> None:
        def worker() -> None:
            try:
                screenshot = capture_game_screen()
                self.root.after(0, self.show_loading)
                reading = self.ocr.recognize(screenshot, self.layout)
                self.root.after(0, lambda: self._recognition_done(reading))
            except Exception as error:  # GUI 边界：转成可操作的错误文案
                self.root.after(0, lambda message=str(error): self._show_error(message))

        threading.Thread(target=worker, daemon=True).start()

    def _recognition_done(self, reading: CaptureReading) -> None:
        self.busy = False
        self.last_reading = reading
        if reading.confident:
            try:
                self.last_capture_id = self.store.save(reading)
                self.show_result(reading, "已识别并记录")
                return
            except ValueError as error:
                self._show_error(str(error))
                return
        self.last_capture_id = None
        self.show_editor(reading, "识别结果不完整，请确认后记录")

    def _show_error(self, message: str) -> None:
        self.busy = False
        body = self._show_shell(status="ERROR")
        content = tk.Frame(body, bg=SURFACE)
        content.pack(fill="both", expand=True, padx=26, pady=26)
        self._label(content, "无法完成识别", color=ERROR, size=17, bold=True).pack(anchor="w")
        self._label(content, message, color=MUTED, size=10).pack(anchor="w", pady=(10, 24))
        actions = tk.Frame(content, bg=SURFACE)
        actions.pack(anchor="w")
        self._action_button(actions, "重新识别", self.scan).pack(side="left")
        self._action_button(actions, "返回总览", self.show_dashboard, secondary=True).pack(side="left", padx=8)

    def _action_button(self, parent: tk.Misc, text: str, command: Any, *, secondary: bool = False) -> tk.Button:
        return tk.Button(
            parent,
            text=text,
            command=command,
            bg=SURFACE_2 if secondary else ACCENT,
            fg=TEXT if secondary else "#111510",
            activebackground="#b9ec51" if not secondary else "#303a33",
            activeforeground="#111510" if not secondary else TEXT,
            relief="flat",
            bd=0,
            padx=18,
            pady=9,
            font=(FONT, 10, "bold"),
            cursor="hand2",
        )

    def _price_strip(self, parent: tk.Misc, reading: CaptureReading) -> None:
        strip = tk.Frame(parent, bg=SURFACE)
        strip.pack(fill="x", pady=(18, 8))
        for index, (day, price) in enumerate(zip(DAYS, reading.prices)):
            cell = tk.Frame(strip, bg=SURFACE, width=68)
            cell.pack(side="left", fill="x", expand=True)
            if index:
                tk.Frame(cell, width=1, bg="#354039").pack(side="left", fill="y", padx=(0, 7))
            tk.Label(cell, text=day[-1], bg=SURFACE, fg=MUTED, font=(FONT, 9)).pack()
            tk.Label(
                cell,
                text=str(price) if price is not None else "—",
                bg=SURFACE,
                fg=TEXT if price is not None else ERROR,
                font=(MONO, 11, "bold"),
            ).pack(pady=(5, 0))

    def show_result(self, reading: CaptureReading, notice: str = "已记录") -> None:
        body = self._show_shell(status="RECORDED")
        content = tk.Frame(body, bg=SURFACE)
        content.pack(fill="both", expand=True, padx=24, pady=(20, 16))
        self._label(content, notice, color=ACCENT, size=9, bold=True).pack(anchor="w")
        self._label(content, reading.item_name, size=19, bold=True).pack(anchor="w", pady=(4, 0))
        self._label(
            content,
            f"{reading.captured_at:%Y-%m-%d %H:%M} · 今日 {reading.prices[-1]}",
            color=MUTED,
            size=9,
        ).pack(anchor="w", pady=(3, 0))
        self._price_strip(content, reading)

        status = prediction_status(self.store, reading.item_name, reading.captured_at.date())
        color = ACCENT if status["kind"] == "ready" else WARNING
        tk.Frame(content, bg="#354039", height=1).pack(fill="x", pady=(12, 16))
        self._label(content, "预测状态", color=MUTED, size=9, bold=True).pack(anchor="w")
        self._label(content, status["message"], color=color, size=12, bold=True).pack(anchor="w", pady=(5, 8))

        if status["future"]:
            line = "  ·  ".join(
                f"{DAYS[item['weekday']]} {item['price']}" for item in status["future"]
            )
            self._label(content, line, color=TEXT, size=11, bold=True).pack(anchor="w")
        elif status["ranges"]:
            line = "  ·  ".join(
                f"{DAYS[item['weekday']]} {item['minimum']}–{item['maximum']}" for item in status["ranges"]
            )
            self._label(content, line, color=MUTED, size=9).pack(anchor="w")

        actions = tk.Frame(content, bg=SURFACE)
        actions.pack(side="bottom", fill="x")
        self._action_button(actions, "再次识别", self.scan).pack(side="left")
        self._action_button(actions, "修正", lambda: self.show_editor(reading, "修正本次记录"), secondary=True).pack(side="left", padx=8)
        self._action_button(actions, "商品总览", self.show_dashboard, secondary=True).pack(side="left")
        self._action_button(actions, "校准区域", self.start_calibration, secondary=True).pack(side="right")

    def show_editor(self, reading: CaptureReading, notice: str) -> None:
        body = self._show_shell(status="CONFIRM")
        content = tk.Frame(body, bg=SURFACE)
        content.pack(fill="both", expand=True, padx=22, pady=(16, 14))
        self._label(content, notice, color=WARNING, size=10, bold=True).pack(anchor="w")
        name_var = tk.StringVar(value=reading.item_name)
        name_entry = tk.Entry(
            content, textvariable=name_var, bg=SURFACE_2, fg=TEXT, insertbackground=TEXT,
            relief="flat", font=(FONT, 13, "bold"),
        )
        name_entry.pack(fill="x", pady=(8, 14), ipady=7)

        grid = tk.Frame(content, bg=SURFACE)
        grid.pack(fill="x")
        for column in range(7):
            grid.columnconfigure(column, weight=1, uniform="price")
        price_vars: list[tk.StringVar] = []
        start_day = reading.captured_at.date() - timedelta(days=6)
        for index, price in enumerate(reading.prices):
            cell = tk.Frame(grid, bg=SURFACE)
            cell.grid(row=0, column=index, sticky="nsew", padx=(0 if index == 0 else 3, 0))
            day = start_day + timedelta(days=index)
            self._label(cell, f"{day:%m/%d}\n{DAYS[day.weekday()][-1]}", color=MUTED, size=8).pack()
            variable = tk.StringVar(value="" if price is None else str(price))
            price_vars.append(variable)
            tk.Entry(
                cell, textvariable=variable, justify="center", bg=SURFACE_2, fg=TEXT,
                insertbackground=TEXT, relief="flat", font=(MONO, 9), width=1,
            ).pack(fill="x", pady=(5, 0), ipady=6)

        hint_var = tk.StringVar(value=f"价格范围 {PRICE_MIN}～{PRICE_MAX}")
        tk.Label(content, textvariable=hint_var, bg=SURFACE, fg=MUTED, font=(FONT, 9)).pack(anchor="w", pady=(12, 0))

        def confirm() -> None:
            try:
                name = name_var.get().strip()
                if not name:
                    raise ValueError("请输入商品名称")
                values = [int(variable.get().strip()) for variable in price_vars]
                if any(not PRICE_MIN <= value <= PRICE_MAX for value in values):
                    raise ValueError(f"价格必须在 {PRICE_MIN}～{PRICE_MAX} 之间")
                corrected = CaptureReading(name, values, 1.0, [1.0] * 7, reading.captured_at)
                if self.last_capture_id is None:
                    self.last_capture_id = self.store.save(corrected)
                else:
                    self.store.update(self.last_capture_id, corrected)
                self.last_reading = corrected
                self.show_result(corrected, "已确认并记录")
            except (ValueError, TypeError) as error:
                hint_var.set(str(error))

        actions = tk.Frame(content, bg=SURFACE)
        actions.pack(side="bottom", fill="x")
        self._action_button(actions, "确认记录", confirm).pack(side="left")
        self._action_button(actions, "重新识别", self.scan, secondary=True).pack(side="left", padx=8)
        self._action_button(actions, "返回总览", self.show_dashboard, secondary=True).pack(side="left")

    def start_calibration(self) -> None:
        proceed = messagebox.askokcancel(
            "校准识别区域",
            "请保持 Endfield 商品详情页打开。\n接下来依次框选商品名称和 7 根价格柱。\n\n按 Esc/Q 或关闭框选窗口可随时取消。",
        )
        if not proceed:
            return
        self.root.withdraw()

        def worker() -> None:
            try:
                screenshot = capture_game_screen()
                layout = calibrate_layout(screenshot)
                if layout is not None:
                    self.layout = layout
                self.root.after(0, lambda: self._calibration_done(layout is not None))
            except Exception as error:
                self.root.after(0, lambda message=str(error): self._show_error(message))

        threading.Thread(target=worker, daemon=True).start()

    def _calibration_done(self, saved: bool) -> None:
        if saved:
            messagebox.showinfo("校准完成", "识别区域已保存。")
        self.show_dashboard()


def enable_dpi_awareness() -> None:
    if not hasattr(ctypes, "windll"):
        return
    try:
        ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
    except (AttributeError, OSError):
        try:
            ctypes.windll.shcore.SetProcessDpiAwareness(2)
        except (AttributeError, OSError):
            pass


def main() -> int:
    enable_dpi_awareness()
    root = tk.Tk()
    OverlayApp(root)
    root.mainloop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
