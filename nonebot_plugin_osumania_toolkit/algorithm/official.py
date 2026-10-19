# algorithm/mapview/official.py
import math
from typing import List, Dict, Any


def parse_hit_objects(content: str, keys: int, speed_rate: float) -> List[Dict[str, Any]]:
    """
    从 .osu 文本中解析 HitObjects，返回格式：
    [
        {
            "StartTime": float,
            "EndTime": float,
            "Column": int,
            "IsHold": bool
        },
        ...
    ]
    已按 StartTime 排序，并应用倍速缩放所有时间。
    """
    lines = content.splitlines()
    hit_objects_section = False
    hit_objects = []

    for line in lines:
        line = line.strip()
        if line == "[HitObjects]":
            hit_objects_section = True
            continue
        if not hit_objects_section or not line:
            continue

        parts = line.split(",")
        if len(parts) < 5:
            continue

        x = int(parts[0])
        start_time = float(parts[2])
        type_ = int(parts[3])

        # osu!mania column: floor(x * keys / 512)
        column = math.floor(x * keys / 512)
        column = max(0, min(column, keys - 1))

        end_time = start_time
        is_hold = (type_ & 128) != 0

        if is_hold:
            extras = parts[5].split(":")
            end_time = float(extras[0])

        # 应用倍速：时间除以 speed_rate
        hit_objects.append({
            "StartTime": start_time / speed_rate,
            "EndTime": end_time / speed_rate,
            "Column": column,
            "IsHold": is_hold
        })

    # 按 StartTime 排序
    hit_objects.sort(key=lambda o: o["StartTime"])
    return hit_objects


class ManiaDifficultyHitObject:
    def __init__(self, hit_object, previous, total_columns, previous_by_column):
        self.BaseObject = hit_object
        self.StartTime = hit_object["StartTime"]
        self.EndTime = hit_object["EndTime"]
        self.Column = hit_object["Column"]
        self.DeltaTime = (self.StartTime - previous["StartTime"]) if previous else 0.0
        self.PreviousHitObjects = list(previous_by_column)


def logistic(x, midpoint_offset, multiplier, max_value=1.0):
    return max_value / (1 + math.exp(multiplier * (midpoint_offset - x)))


def definitely_bigger(value1, value2, tolerance=1):
    return value1 > value2 + tolerance


def evaluate_individual(current: ManiaDifficultyHitObject):
    hold_factor = 1.0
    for prev in current.PreviousHitObjects:
        if prev is None:
            continue
        if (definitely_bigger(prev.EndTime, current.EndTime) and
                definitely_bigger(current.StartTime, prev.StartTime)):
            hold_factor = 1.25
            break
    return 2.0 * hold_factor


def evaluate_overall(current: ManiaDifficultyHitObject):
    is_overlapping = False
    closest_end_time = abs(current.EndTime - current.StartTime)
    hold_factor = 1.0

    for prev in current.PreviousHitObjects:
        if prev is None:
            continue
        if (definitely_bigger(prev.EndTime, current.StartTime) and
                definitely_bigger(current.EndTime, prev.EndTime) and
                definitely_bigger(current.StartTime, prev.StartTime)):
            is_overlapping = True

        if (definitely_bigger(prev.EndTime, current.EndTime) and
                definitely_bigger(current.StartTime, prev.StartTime)):
            hold_factor = 1.25

        gap = abs(current.EndTime - prev.EndTime)
        if gap < closest_end_time:
            closest_end_time = gap

    hold_addition = 0.0
    if is_overlapping:
        hold_addition = logistic(closest_end_time, 30, 0.27)

    return (1 + hold_addition) * hold_factor


def calculate_official_sr(file_path: str, speed_rate: float) -> float:
    """
    计算 osu!mania 官方星级。
    file_path: .osu 文件路径
    speed_rate: 倍速，例如 1.0, 1.5 等
    """
    with open(file_path, "r", encoding="utf-8") as f:
        content = f.read()

    # 提取 CircleSize (keys) 和 Mode
    keys = 4
    mode = 0
    general_section = False
    difficulty_section = False
    for line in content.splitlines():
        line = line.strip()
        if line.startswith("[General]"):
            general_section = True
            difficulty_section = False
            continue
        if line.startswith("[Difficulty]"):
            general_section = False
            difficulty_section = True
            continue
        if general_section and line.startswith("Mode:"):
            mode = int(line.split(":")[1].strip())
        if difficulty_section and line.startswith("CircleSize:"):
            keys = float(line.split(":")[1].strip())
            keys = int(keys)  # mania 中 CS 表示键数，取整

    if mode != 3:
        return 0.0  # 不是 mania 模式，返回 0 或抛出异常

    hit_objects = parse_hit_objects(content, keys, speed_rate)
    if not hit_objects:
        return 0.0

    # 构建预处理对象
    total_columns = keys
    individual_strains = [0.0] * total_columns
    overall_strain = 1.0

    previous_by_column = [None] * total_columns
    diff_objects = []

    for i, raw_obj in enumerate(hit_objects):
        prev_obj = hit_objects[i - 1] if i > 0 else None
        diff_obj = ManiaDifficultyHitObject(raw_obj, prev_obj, total_columns, previous_by_column)
        diff_objects.append(diff_obj)
        previous_by_column[raw_obj["Column"]] = diff_obj

    # 分段参数
    SECTION_LENGTH = 400
    INDIVIDUAL_DECAY_BASE = 0.125
    OVERALL_DECAY_BASE = 0.30
    DIFFICULTY_MULTIPLIER = 0.018

    strain_peaks = []
    current_section_peak = 0.0
    current_section_end = math.ceil(diff_objects[0].StartTime / SECTION_LENGTH) * SECTION_LENGTH

    for current in diff_objects:
        while current.StartTime > current_section_end:
            strain_peaks.append(current_section_peak)
            current_section_peak = 0.0
            current_section_end += SECTION_LENGTH

        time_elapsed = current.DeltaTime / 1000.0

        for k in range(total_columns):
            individual_strains[k] *= math.pow(INDIVIDUAL_DECAY_BASE, time_elapsed)
        overall_strain *= math.pow(OVERALL_DECAY_BASE, time_elapsed)

        individual_impulse = evaluate_individual(current)
        overall_impulse = evaluate_overall(current)

        individual_strains[current.Column] += individual_impulse
        overall_strain += overall_impulse

        max_individual = max(individual_strains)
        current_total_strain = max_individual + overall_strain

        if current_total_strain > current_section_peak:
            current_section_peak = current_total_strain

    strain_peaks.append(current_section_peak)

    # 加权求和
    strain_peaks.sort(reverse=True)
    difficulty = 0.0
    weight = 1.0
    DECAY_WEIGHT = 0.9

    for strain in strain_peaks:
        difficulty += strain * weight
        weight *= DECAY_WEIGHT

    return difficulty * DIFFICULTY_MULTIPLIER