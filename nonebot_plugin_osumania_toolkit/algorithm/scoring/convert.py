"""Score conversion: compute cvtscore, format results."""
from __future__ import annotations

import re
from math import isfinite, sqrt
from typing import Any, Optional

from ..matching.matching import match_notes_and_presses
from ...parser.osr_file_parser import osr_file
from ...parser.osu_file_parser import NOTE_HOLD_HEAD, NOTE_HOLD_TAIL, NOTE_NORMAL, osu_file
from ...parser.ruleset_file_parser import ruleset_file

from .score import get_score_result
from .ruleset import BakaScoreSpec


_BAKA_STUDENT_T_DF = 2.0
_BAKA_REFERENCE_MISS_WINDOW_MS = 100.0
_BAKA_REFERENCE_STUDENT_T_SCALE_MS = 20.0


def _baka_quadratic_accuracy(delta: Any, miss_window_ms: float) -> float:
    """Return the original Baka quadratic score."""
    try:
        error = abs(float(delta))
    except (TypeError, ValueError):
        return 0.0
    if miss_window_ms <= 0 or not isfinite(error) or error >= miss_window_ms:
        return 0.0
    ratio = error / miss_window_ms
    return max(0.0, 1.0 - ratio * ratio)


def _baka_student_t_accuracy(delta: Any, miss_window_ms: float) -> float:
    """Return direct Student-t(df=2) tail quality.

    This is intentionally the strict, unwarped probability-derived quality
    requested for the capitalized Baka target. The fixed 20ms scale is the
    reference scale at M=100ms and scales proportionally with M.
    """
    try:
        error = abs(float(delta))
    except (TypeError, ValueError):
        return 0.0
    if miss_window_ms <= 0 or not isfinite(error) or error >= miss_window_ms:
        return 0.0

    scale_ms = (
        _BAKA_REFERENCE_STUDENT_T_SCALE_MS
        * miss_window_ms
        / _BAKA_REFERENCE_MISS_WINDOW_MS
    )

    # For Student-t(df=2), the symmetric absolute-error survival is:
    # 1 - x / sqrt(x^2 + 2 * scale^2).
    def survival(x: float) -> float:
        return 1.0 - x / sqrt(
            x * x + _BAKA_STUDENT_T_DF * scale_ms**2
        )

    boundary = survival(miss_window_ms)
    denominator = max(1e-12, 1.0 - boundary)
    return max(0.0, min(1.0, (survival(error) - boundary) / denominator))


def _baka_single_accuracy(
    delta: Any,
    miss_window_ms: float,
    curve: str,
) -> float:
    if curve == "student_t":
        return _baka_student_t_accuracy(delta, miss_window_ms)
    return _baka_quadratic_accuracy(delta, miss_window_ms)


def compute_baka_score(match_result: dict[str, Any], spec: BakaScoreSpec) -> dict[str, Any]:
    """Score lowercase quadratic or capitalized strict Student-t Baka."""
    if not isinstance(match_result, dict) or match_result.get("status") != "OK":
        return {
            "status": "InvalidInput",
            "error": "BakaScore 匹配结果无效。",
            "accuracy_fraction": 0.0,
            "points_sum": 0.0,
            "max_points": 0.0,
            "judgement_counts": [0, 0],
            "per_note": [],
            "combo": {"best_combo": 0, "combo_breaks": 0},
            "lamps": {},
            "lamp": "",
            "grade": {"name": "Baka", "accuracy_threshold": 0.0},
            "meta": {},
            "warnings": ["BakaScore 匹配结果无效。"],
        }

    events = match_result.get("matched_events", [])
    if not isinstance(events, list):
        events = []

    objects: dict[int, dict[str, Any]] = {}
    for event in events:
        if not isinstance(event, dict):
            continue
        try:
            index = int(event.get("notes_map_index", event.get("index", -1)))
            kind = int(event.get("note_kind", NOTE_NORMAL))
        except (TypeError, ValueError):
            continue
        if index < 0 or kind not in {NOTE_NORMAL, NOTE_HOLD_HEAD, NOTE_HOLD_TAIL}:
            continue

        item = objects.setdefault(
            index,
            {
                "index": index,
                "kind": kind,
                "time": float(event.get("time", 0.0) or 0.0),
                "accuracy": None,
                "matched": False,
                "action": "MISS",
            },
        )
        action = str(event.get("action", "")).upper()
        if action in {"HIT", "HOLD_HEAD"} and kind in {NOTE_NORMAL, NOTE_HOLD_HEAD}:
            item["accuracy"] = _baka_single_accuracy(
                event.get("delta"),
                spec.miss_window_ms,
                spec.curve,
            )
            item["matched"] = True
            item["action"] = action
        elif action == "RELEASE" and kind == NOTE_HOLD_TAIL and spec.include_tail:
            item["accuracy"] = _baka_single_accuracy(
                event.get("delta"),
                spec.tail_window_ms,
                spec.curve,
            )
            item["matched"] = True
            item["action"] = action
        elif action in {"MISS", "DROP_HOLD"}:
            item["accuracy"] = 0.0
            item["matched"] = False
            item["action"] = action

    scoring_objects = [
        item
        for item in objects.values()
        if item["kind"] in {NOTE_NORMAL, NOTE_HOLD_HEAD}
        or (spec.include_tail and item["kind"] == NOTE_HOLD_TAIL)
    ]
    scoring_objects.sort(key=lambda item: (float(item["time"]), int(item["index"])))

    if not scoring_objects:
        return {
            "status": "InvalidInput",
            "error": "谱面没有可评分的 note。",
            "accuracy_fraction": 0.0,
            "points_sum": 0.0,
            "max_points": 0.0,
            "judgement_counts": [0, 0],
            "per_note": [],
            "combo": {"best_combo": 0, "combo_breaks": 0},
            "lamps": {},
            "lamp": "",
            "grade": {"name": "Baka", "accuracy_threshold": 0.0},
            "meta": {},
            "warnings": ["谱面没有可评分的 note。"],
        }

    total = 0.0
    hit_count = 0
    miss_count = 0
    current_combo = 0
    best_combo = 0
    per_note: list[dict[str, Any]] = []

    for item in scoring_objects:
        value = float(item["accuracy"] or 0.0)
        total += value
        if item["matched"]:
            hit_count += 1
            current_combo += 1
        else:
            miss_count += 1
            current_combo = 0
        best_combo = max(best_combo, current_combo)
        per_note.append(
            {
                "index": int(item["index"]),
                "note_kind": int(item["kind"]),
                "accuracy": value,
                "matched": bool(item["matched"]),
                "action": item["action"],
            }
        )

    object_count = len(scoring_objects)
    arithmetic_fraction = total / object_count
    # Student-t determines the per-object quality curve, not a unique
    # aggregation rule. Keep Acc as an object-wise arithmetic mean for both
    # targets; MISS and severe timing errors already contribute zero/low q.
    accuracy_fraction = arithmetic_fraction
    aggregation = "arithmetic_mean"

    return {
        "status": "OK",
        "error": None,
        "accuracy_fraction": accuracy_fraction,
        "points_sum": accuracy_fraction * object_count,
        "max_points": float(object_count),
        "judgement_counts": [hit_count, miss_count],
        "per_note": per_note,
        "matched_events": events,
        "offset_vector": list(match_result.get("offset_vector", []) or []),
        "delta_list": list(match_result.get("delta_list", []) or []),
        "matched_pairs": list(match_result.get("matched_pairs", []) or []),
        "unmatched_presses": list(match_result.get("unmatched_presses", []) or []),
        "unmatched_notes": list(match_result.get("unmatched_notes", []) or []),
        "note_count": object_count,
        "press_count": int(match_result.get("press_count", 0) or 0),
        "combo": {"best_combo": best_combo, "combo_breaks": miss_count},
        "lamps": {},
        "lamp": "",
        "grade": {"name": "Baka", "accuracy_threshold": 0.0},
        "meta": {
            "algorithm_version": (
                "baka_quadratic_v1"
                if spec.curve == "quadratic"
                else "baka_student_t_strict_v1"
            ),
            "curve": spec.curve,
            "aggregation": aggregation,
            "arithmetic_fraction": arithmetic_fraction,
            "distribution": "Student-t" if spec.curve == "student_t" else None,
            "distribution_df": _BAKA_STUDENT_T_DF if spec.curve == "student_t" else None,
            "distribution_scale_ms": (
                (
                    _BAKA_REFERENCE_STUDENT_T_SCALE_MS
                    * spec.miss_window_ms
                    / _BAKA_REFERENCE_MISS_WINDOW_MS
                )
                if spec.curve == "student_t"
                else None
            ),
            "distribution_reference_scale_ms": (
                _BAKA_REFERENCE_STUDENT_T_SCALE_MS
                if spec.curve == "student_t"
                else None
            ),
            "miss_window_ms": spec.miss_window_ms,
            "tail_judgement": spec.include_tail,
            "tail_window_ms": spec.tail_window_ms if spec.include_tail else None,
        },
        "warnings": [],
    }


def compute_cvtscore(
    *,
    osu_obj: osu_file,
    osr_obj: osr_file,
    source_ruleset: ruleset_file,
    target_ruleset: ruleset_file | BakaScoreSpec,
) -> tuple[Optional[dict[str, Any]], Optional[str]]:
    source_match = match_notes_and_presses(osu_obj, osr_obj, source_ruleset, use_chart_time=True)
    if source_match.get("status") != "OK":
        return None, f"源规则匹配失败: {source_match.get('error') or source_match.get('status')}"

    source_score = get_score_result(source_ruleset, source_match)
    if source_score.get("status") != "OK":
        return None, f"源规则计分失败: {source_score.get('error') or source_score.get('status')}"

    source_score = _align_source_score_with_osr_header(
        score=source_score,
        source_ruleset=source_ruleset,
        osr_obj=osr_obj,
    )

    target_match = match_notes_and_presses(osu_obj, osr_obj, target_ruleset, use_chart_time=True)
    if target_match.get("status") != "OK":
        return None, f"目标规则匹配失败: {target_match.get('error') or target_match.get('status')}"

    if isinstance(target_ruleset, BakaScoreSpec):
        target_score = compute_baka_score(target_match, target_ruleset)
    else:
        target_score = get_score_result(target_ruleset, target_match)
    if target_score.get("status") != "OK":
        return None, f"目标规则计分失败: {target_score.get('error') or target_score.get('status')}"

    return {
        "source_match": source_match,
        "source_score": source_score,
        "target_match": target_match,
        "target_score": target_score,
    }, None


def _normalize_judgement_name(name: str) -> str:
    return re.sub(r"[^A-Za-z0-9]", "", str(name or "")).upper()


def _counts_from_osr_for_ruleset(source_ruleset: ruleset_file, osr_obj: osr_file) -> Optional[list[int]]:
    judge = getattr(osr_obj, "judge", None)
    if not isinstance(judge, dict):
        return None

    judgements = source_ruleset.raw_data.get("Judgements", []) if isinstance(source_ruleset.raw_data, dict) else []
    if not isinstance(judgements, list) or not judgements:
        return None

    out: list[int] = []
    for item in judgements:
        name = str(item.get("Name", "")) if isinstance(item, dict) else ""
        key = _normalize_judgement_name(name)

        value = 0
        if "300G" in key or "MARV" in key or "GEKI" in key:
            value = int(judge.get("320", 0) or 0)
        elif "MISS" in key or key == "0":
            value = int(judge.get("0", 0) or 0)
        elif "300" in key:
            value = int(judge.get("300", 0) or 0)
        elif "200" in key or "KATU" in key:
            value = int(judge.get("200", 0) or 0)
        elif "100" in key:
            value = int(judge.get("100", 0) or 0)
        elif "50" in key:
            value = int(judge.get("50", 0) or 0)
        out.append(max(0, value))

    return out


def _accuracy_from_counts(source_ruleset: ruleset_file, counts: list[int], fallback: float) -> tuple[float, float, float]:
    raw = source_ruleset.raw_data if isinstance(source_ruleset.raw_data, dict) else {}
    acc = raw.get("Accuracy") if isinstance(raw.get("Accuracy"), dict) else {}
    points = acc.get("PointsPerJudgement") if isinstance(acc.get("PointsPerJudgement"), list) else None
    if not isinstance(points, list) or len(points) != len(counts):
        return fallback, 0.0, 0.0

    total_objects = float(sum(int(c) for c in counts))
    if total_objects <= 0:
        return 1.0, 0.0, 0.0

    weights = [float(v) for v in points]
    points_sum = sum(float(c) * w for c, w in zip(counts, weights))
    max_weight = max(weights) if weights else 1.0
    max_points = total_objects * max_weight
    if max_points <= 0:
        return fallback, points_sum, max_points
    return points_sum / max_points, points_sum, max_points


def _align_source_score_with_osr_header(
    *,
    score: dict[str, Any],
    source_ruleset: ruleset_file,
    osr_obj: osr_file,
) -> dict[str, Any]:
    if not isinstance(score, dict):
        return score
    if score.get("status") != "OK":
        return score

    template = str((getattr(source_ruleset, "template_context", {}) or {}).get("template", "")).lower()
    if template not in {"osu", "osu-sv2"}:
        return score

    counts = _counts_from_osr_for_ruleset(source_ruleset, osr_obj)
    if not counts:
        return score

    merged = dict(score)
    merged["judgement_counts"] = counts

    fallback_acc = float(score.get("accuracy_fraction", 0.0) or 0.0)
    acc_frac, points_sum, max_points = _accuracy_from_counts(source_ruleset, counts, fallback_acc)
    merged["accuracy_fraction"] = float(acc_frac)
    if max_points > 0:
        merged["points_sum"] = float(points_sum)
        merged["max_points"] = float(max_points)

    combo = dict(score.get("combo", {}) if isinstance(score.get("combo"), dict) else {})
    best_combo = int(getattr(osr_obj, "max_combo", combo.get("best_combo", 0)) or 0)
    combo_breaks = int(getattr(osr_obj, "judge", {}).get("0", combo.get("combo_breaks", 0)) or 0)
    combo["best_combo"] = max(0, best_combo)
    combo["combo_breaks"] = max(0, combo_breaks)
    merged["combo"] = combo

    return merged


def _decimal_places_from_ruleset(rs: ruleset_file) -> int:
    fmt = rs.raw_data.get("Formatting") if isinstance(rs.raw_data, dict) else None
    if not isinstance(fmt, dict):
        return 2

    value = str(fmt.get("DecimalPlaces", "TWO")).upper()
    if value == "THREE":
        return 3
    if value == "FOUR":
        return 4
    return 2


def _format_accuracy_percent(score: dict[str, Any], rs: ruleset_file) -> str:
    frac = float(score.get("accuracy_fraction", 0.0) or 0.0)
    dp = _decimal_places_from_ruleset(rs)
    return f"{frac * 100:.{dp}f}%"


def _format_judgement_counts(score: dict[str, Any], rs: ruleset_file) -> str:
    counts = score.get("judgement_counts", [])
    judgements = rs.raw_data.get("Judgements", []) if isinstance(rs.raw_data, dict) else []

    if not isinstance(counts, list) or not isinstance(judgements, list):
        return "无"

    lines: list[str] = []
    for idx, judgement in enumerate(judgements):
        name = f"J{idx}"
        if isinstance(judgement, dict):
            name = str(judgement.get("Name", name))
        value = 0
        if idx < len(counts):
            try:
                value = int(counts[idx])
            except Exception:
                value = 0
        lines.append(f"{name}:{value}")
    return " | ".join(lines) if lines else "无"


def _format_lamp(score: dict[str, Any]) -> str:
    lamp = score.get("lamp")
    if isinstance(lamp, str):
        lamp_name = lamp.strip()
        if lamp_name:
            return lamp_name

    lamps = score.get("lamps")
    if not isinstance(lamps, dict):
        return "None"

    passed = [name for name, ok in lamps.items() if bool(ok)]
    return passed[0] if passed else "None"


def _format_grade(score: dict[str, Any]) -> str:
    grade = score.get("grade")
    if not isinstance(grade, dict):
        return "None"
    name = str(grade.get("name", "")).strip()
    return name if name else "None"


def _visible_warnings(score: dict[str, Any]) -> list[str]:
    raw = score.get("warnings")
    if not isinstance(raw, list):
        return []

    severe_flags = ("[severe]", "[critical]", "[fatal]", "严重", "错误", "error", "critical", "fatal")
    visible: list[str] = []
    for item in raw:
        text = str(item).strip()
        if not text:
            continue
        low = text.lower()
        if any(flag in low for flag in severe_flags):
            visible.append(text)
    return visible


def _format_score_block(title: str, score: dict[str, Any], rs: ruleset_file) -> str:
    combo = score.get("combo", {}) if isinstance(score.get("combo"), dict) else {}
    best_combo = int(combo.get("best_combo", 0) or 0)
    combo_breaks = int(combo.get("combo_breaks", 0) or 0)
    grade_text = _format_grade(score)
    lamp_text = _format_lamp(score)

    grade_and_lamp = grade_text
    if lamp_text not in {"", "None", "NONE", "无"}:
        grade_and_lamp = f"{grade_text} | {lamp_text}"

    lines = [
        f"[{title}]",
        grade_and_lamp,
        f"准确度: {_format_accuracy_percent(score, rs)}",
        f"判定: {_format_judgement_counts(score, rs)}",
        f"最大连击: {best_combo} ({combo_breaks}X)",
    ]

    warnings = _visible_warnings(score)
    if warnings:
        lines.append("警告: " + "；".join(warnings[:3]))

    return "\n".join(lines)


def format_cvtscore_message(
    *,
    source_info: dict[str, Any],
    target_info: dict[str, Any],
    source_ruleset: ruleset_file,
    target_ruleset: ruleset_file,
    source_score: dict[str, Any],
    target_score: dict[str, Any],
) -> str:
    source_display = str(source_info.get("display", "未知"))
    target_display = str(target_info.get("display", "未知"))

    head = [
        "转换完成。",
        f"源规则: {source_display}",
        f"目标规则: {target_display}",
    ]
    body = [
        _format_score_block("源规则成绩", source_score, source_ruleset),
        "\n",
        _format_score_block("转换后成绩", target_score, target_ruleset),
    ]

    return "\n".join(head + [""] + body)

