from __future__ import annotations

import blackboxprotobuf
from typing import List, Tuple
from pathlib import Path

from . import replay_pb2
from .osr_file_parser import ReplayCursor, ReplayEvent


def _decode_mrv_to_dict(file_path: str) -> dict:
    """保留黑盒解析，用于获取 ScoreRecord 等尚未加入 proto 的字段"""
    with open(file_path, 'rb') as f:
        data = f.read()
    msg, _ = blackboxprotobuf.decode_message(data)

    def convert_bytes(obj):
        if isinstance(obj, dict):
            for k, v in obj.items():
                obj[k] = convert_bytes(v)
            return obj
        elif isinstance(obj, list):
            return [convert_bytes(i) for i in obj]
        elif isinstance(obj, bytes):
            try:
                return obj.decode('utf-8')
            except UnicodeDecodeError:
                return obj.hex()
        return obj

    return convert_bytes(msg)


class MrvReplay:
    def __init__(self, file_path: str):
        self.file_path = str(file_path)
        self.status = "init"
        self.error_message = ""

        # ---------- 原生 protobuf 解析 ----------
        try:
            with open(file_path, 'rb') as f:
                raw_bytes = f.read()
            replay_pb = replay_pb2.ReplayFile()
            replay_pb.ParseFromString(raw_bytes)
        except Exception as e:
            self.status = "ParseError"
            self.error_message = f"Protobuf解析失败: 请反馈bot主人"
            self.actions = []
            return

        # ---------- 黑盒字典（用于 ScoreRecord 等） ----------
        self.raw_data = _decode_mrv_to_dict(file_path)
        header = self.raw_data

        # ---------- 基础信息 ----------
        self.song_title = header.get("1", "")
        self.song_artist = header.get("2", "")
        self.creator = header.get("3", "")
        self.difficulty_name = header.get("4", "")
        self.beatmap_md5 = header.get("5", "")
        self.player_name = header.get("6", "Unknown")
        self.user_id = header.get("7", 0)

        # ---------- 分数与判定 ----------
        score_data = header.get("8", {})
        self.score = score_data.get("5", 0)
        self.max_combo = score_data.get("6", 0)
        self.best_count = score_data.get("7", 0)
        self.cool_count = score_data.get("8", 0)
        self.good_count = score_data.get("9", 0)
        self.miss_count = score_data.get("10", 0)
        self.unknown_count = 0
        self.rank = score_data.get("3", 0)
        self.mods_flags = 0

        self.timestamp = header.get("12", 0)
        self.seed = header.get("11", 0)
        self.client = header.get("13", 0)
        self.skinref = header.get("14", "")

        # ---------- 动作解析（原生 Record 列表） ----------
        try:
            self.actions = self._parse_records_from_pb(replay_pb.records)
            if not self.actions:
                self.status = "NoValidActions"
                self.error_message = "没有有效动作"
            else:
                self.status = "OK"
        except Exception as e:
            self.status = "ParseError"
            self.error_message = str(e)
            self.actions = []

        # ---------- 内部数据结构初始化 ----------
        self.pressset = [[] for _ in range(18)]
        self.pressset_raw = [[] for _ in range(18)]
        self.intervals = []
        self.intervals_raw = []

        self.press_times = []
        self.press_times_float = []
        self.press_times_raw = []
        self.press_events = []
        self.press_events_float = []
        self.press_events_raw = []

        self.press_times_real = []
        self.press_times_real_float = []
        self.press_events_real = []
        self.press_events_real_float = []

        self.press_times_chart = []
        self.press_times_chart_float = []
        self.press_events_chart = []
        self.press_events_chart_float = []

        self.play_data: List[ReplayEvent] = []
        self.replay_data_real = []
        self.replay_data_chart = []

        self.corrector = 1.0
        self.speed_factor = 1.0
        self.keep_float_times = True

        if self.status == "OK":
            self._build_replay_views()

    def _parse_records_from_pb(self, records_pb) -> List[Tuple[int, int, int]]:
        """从原生 protobuf Record 列表中提取 (time_ms, action, column)"""
        actions = []
        for rec in records_pb:
            time_ms = rec.time
            for cell in rec.cells:
                field = cell.WhichOneof('cell_oneof')
                if field == 'input_indexed_down':
                    col = cell.input_indexed_down
                    if 0 <= col < 18:
                        actions.append((time_ms, 1, col))
                elif field == 'input_indexed_up':
                    col = cell.input_indexed_up
                    if 0 <= col < 18:
                        actions.append((time_ms, 2, col))
                # 忽略其他 oneof 字段
        actions.sort(key=lambda x: x[0])
        return actions

    def _build_replay_views(self):
        """与 mr_file._build_replay_views 完全一致"""
        actions = sorted(self.actions, key=lambda a: a[0])
        if not actions:
            return

        current_state = [False] * 18
        pressed_start = [None] * 18
        prev_time = None
        intervals_raw = []
        replay_data = []
        play_data = []

        press_events_real_float = []
        press_times_real_float = []
        pressset_real = [[] for _ in range(18)]

        for time_ms, action, col in actions:
            t = float(time_ms)

            if action == 1:       # 按下
                if not current_state[col]:
                    current_state[col] = True
                    pressed_start[col] = t
                    press_times_real_float.append(t)
                    press_events_real_float.append((col, t))
            elif action == 2:     # 抬起
                if current_state[col]:
                    current_state[col] = False
                    if pressed_start[col] is not None:
                        duration = t - float(pressed_start[col])
                        if duration >= 0:
                            pressset_real[col].append(duration)
                    pressed_start[col] = None

            keys_mask = 0
            for idx in range(18):
                if current_state[idx]:
                    keys_mask |= (1 << idx)

            delta = t if prev_time is None else max(0.0, t - prev_time)
            prev_time = t

            intervals_raw.append(delta)
            replay_data.append((t, keys_mask))
            play_data.append(ReplayEvent(int(round(delta)), keys_mask))

        self.pressset_raw = pressset_real
        self.intervals_raw = intervals_raw
        self.press_events_raw = press_events_real_float
        self.press_times_raw = press_times_real_float
        self.replay_data_real = replay_data
        self.play_data = play_data

        self.press_times_real_float = list(self.press_times_raw)
        self.press_events_real_float = list(self.press_events_raw)
        self.press_times_real = [int(round(t)) for t in self.press_times_real_float]
        self.press_events_real = [(c, int(round(t))) for c, t in self.press_events_real_float]

        self.press_times_chart_float = list(self.press_times_real_float)
        self.press_events_chart_float = list(self.press_events_real_float)
        self.press_times_chart = list(self.press_times_real)
        self.press_events_chart = list(self.press_events_real)
        self.replay_data_chart = list(self.replay_data_real)

        self.intervals = [int(round(v)) for v in self.intervals_raw]
        self.pressset = [
            [int(round(d)) for d in durations] if durations else []
            for durations in self.pressset_raw
        ]

        self.press_times_float = list(self.press_times_chart_float)
        self.press_events_float = list(self.press_events_chart_float)
        self.press_times = list(self.press_times_chart)
        self.press_events = list(self.press_events_chart)

        if not self.keep_float_times:
            self.press_times_real_float = [float(t) for t in self.press_times_real]
            self.press_events_real_float = [(c, float(t)) for c, t in self.press_events_real]
            self.press_times_chart_float = [float(t) for t in self.press_times_chart]
            self.press_events_chart_float = [(c, float(t)) for c, t in self.press_events_chart]
            self.press_times_float = [float(t) for t in self.press_times]
            self.press_events_float = [(c, float(t)) for c, t in self.press_events]

    # ---------- 以下接口方法与 mr_file 保持一致 ----------
    def get_data(self):
        return {
            "status": self.status,
            "magic": "",
            "version": (0, 0, 0),
            "beatmap_md5": self.beatmap_md5,
            "difficulty_name": self.difficulty_name,
            "song_title": self.song_title,
            "song_artist": self.song_artist,
            "score": self.score,
            "max_combo": self.max_combo,
            "best_count": self.best_count,
            "cool_count": self.cool_count,
            "good_count": self.good_count,
            "miss_count": self.miss_count,
            "unknown_count": self.unknown_count,
            "mods_flags": self.mods_flags,
            "rank": self.rank,
            "timestamp": self.timestamp,
            "actions": self.actions,
            "corrector": self.corrector,
            "speed_factor": self.speed_factor,
            "pressset": self.pressset,
            "pressset_raw": self.pressset_raw,
            "intervals": self.intervals,
            "intervals_raw": self.intervals_raw,
            "press_times": self.press_times,
            "press_times_float": self.press_times_float,
            "press_times_raw": self.press_times_raw,
            "press_events": self.press_events,
            "press_events_float": self.press_events_float,
            "press_events_raw": self.press_events_raw,
            "press_times_real": self.press_times_real,
            "press_times_real_float": self.press_times_real_float,
            "press_events_real": self.press_events_real,
            "press_events_real_float": self.press_events_real_float,
            "press_times_chart": self.press_times_chart,
            "press_times_chart_float": self.press_times_chart_float,
            "press_events_chart": self.press_events_chart,
            "press_events_chart_float": self.press_events_chart_float,
            "replay_data_real": self.replay_data_real,
            "replay_data_chart": self.replay_data_chart,
        }

    def get_summary(self) -> dict:
        return {
            "status": self.status,
            "player": getattr(self, "player_name", "Malody Player"),
            "song": f"{self.song_title} - {self.song_artist}",
            "difficulty": self.difficulty_name,
            "score": self.score,
            "accuracy": self.calculate_accuracy(),
            "max_combo": self.max_combo,
            "judge": {
                "best": self.best_count,
                "cool": self.cool_count,
                "good": self.good_count,
                "miss": self.miss_count,
            },
            "mods": self.get_mods_list(),
            "action_count": len(self.actions),
            "timestamp": self.timestamp,
        }

    def calculate_accuracy(self) -> float:
        total_notes = self.best_count + self.cool_count + self.good_count + self.miss_count
        if total_notes == 0:
            return 0.0
        accuracy = (
            (self.best_count * 100 + self.cool_count * 75 + self.good_count * 40)
            / (total_notes * 100)
            * 100
        )
        return round(accuracy, 2)

    def get_mods_list(self) -> list:
        return []  # 可后续从 Custom 字段解析

    def is_valid(self) -> bool:
        return self.status == "OK" and len(self.actions) > 0

    def get_action_stats(self) -> dict:
        if not self.actions:
            return {}
        times = [a[0] for a in self.actions]
        action_values = [a[1] for a in self.actions]
        columns = [a[2] for a in self.actions]
        return {
            "total_actions": len(self.actions),
            "key_down_count": action_values.count(1),
            "key_up_count": action_values.count(2),
            "min_time": min(times) if times else 0,
            "max_time": max(times) if times else 0,
            "duration_ms": max(times) - min(times) if len(times) >= 2 else 0,
            "unique_columns": len(set(columns)),
            "column_distribution": {col: columns.count(col) for col in set(columns)},
        }

    def to_interlude_replay(self, use_chart_time: bool = True, compressed: bool = False):
        replay = self.replay_data_chart if use_chart_time else self.replay_data_real
        if not compressed:
            return list(replay)
        if not replay:
            return ""
        out = []
        prev = 0.0
        for time_ms, mask in replay:
            delta = float(time_ms) - prev
            if delta < 0:
                delta = 0.0
            out.append(f"{delta:.3f}|{int(mask)}|0|0")
            prev = float(time_ms)
        return ",".join(out)

    def as_IReplay(self, use_chart_time: bool = True) -> ReplayCursor:
        replay = self.replay_data_chart if use_chart_time else self.replay_data_real
        return ReplayCursor(replay)


def mrv_file(file_path: str) -> MrvReplay:
    return MrvReplay(file_path)