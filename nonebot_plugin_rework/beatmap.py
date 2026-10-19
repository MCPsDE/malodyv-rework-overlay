"""osu! 谱面文本的轻量解析与准入校验。

只需要读取 [General] / [Metadata] / [Difficulty] / [Events] 几个节，不解析
HitObjects —— 真正的分析由 tosu 插件的前端完成。所有 osu! 模式都可渲染卡片，
RC/LN 固定密度估算只对 4K mania 启用。
"""

from __future__ import annotations

import re
from dataclasses import dataclass

MANIA_MODE = 3
_SECTION_RE = re.compile(r"^\[(.+)\]$")


@dataclass
class BeatmapInfo:
    """谱面元信息与校验结果。"""

    mode: int | None = None
    column_count: int | None = None
    title: str = ""
    artist: str = ""
    version: str = ""
    creator: str = ""
    background: str = ""
    has_hit_objects: bool = False
    error: str | None = None

    @property
    def ok(self) -> bool:
        return self.error is None

    @property
    def display_name(self) -> str:
        parts = [p for p in (self.artist, self.title, self.version) if p]
        name = " - ".join(parts) if len(parts) >= 2 else (parts[0] if parts else "未知谱面")
        if self.creator:
            name = f"{name} // {self.creator}"
        return name


def _parse_float(value: str, default: float) -> float:
    try:
        return float(value.strip())
    except (TypeError, ValueError):
        return default


def parse_beatmap(text: str) -> BeatmapInfo:
    """解析谱面文本并校验其是否包含可渲染的 HitObjects。

    非 mania 与非 4K mania 仍可生成卡片；其固定密度 RC/LN 会显示为不可用。
    error 非空时表示输入不是可渲染的 osu! 谱面，其内容即为错误提示。
    """
    info = BeatmapInfo()
    section = ""
    circle_size: float | None = None
    mode_raw: str | None = None

    for raw_line in text.splitlines():
        line = raw_line.strip()
        if not line or line.startswith("//"):
            continue

        section_match = _SECTION_RE.match(line)
        if section_match:
            section = section_match.group(1).strip()
            if section == "HitObjects":
                info.has_hit_objects = True
            continue

        if section == "General":
            if line.startswith("Mode"):
                key, _, value = line.partition(":")
                if key.strip() == "Mode":
                    mode_raw = value.strip()
        elif section == "Metadata":
            if ":" in line:
                key, _, value = line.partition(":")
                key = key.strip()
                value = value.strip()
                if key == "Title":
                    info.title = value
                elif key == "Artist":
                    info.artist = value
                elif key == "Version":
                    info.version = value
                elif key == "Creator":
                    info.creator = value
        elif section == "Difficulty":
            if ":" in line:
                key, _, value = line.partition(":")
                if key.strip() == "CircleSize":
                    circle_size = _parse_float(value, 4.0)
        elif section == "Events":
            # 背景行形如：0,0,"bg.jpg",0,0
            if line.startswith("0,0,") and not info.background:
                match = re.search(r'"([^"]+)"', line)
                if match:
                    info.background = match.group(1)

    info.mode = int(_parse_float(mode_raw, 0.0)) if mode_raw is not None else 0
    info.column_count = int(round(circle_size)) if circle_size is not None else None

    if not info.has_hit_objects:
        info.error = "该文件不包含 [HitObjects] 段落，不是有效的 osu! 谱面文件。"
        return info

    if info.mode == MANIA_MODE and info.column_count is None:
        info.error = "该谱面缺少 CircleSize 字段，无法判断轨道数。"
        return info

    return info
