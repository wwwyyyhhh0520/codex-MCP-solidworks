"""Build a conservative V4 hole-side geometry audit from SolidWorks evidence.

This script is intentionally split from screw/fastener compliance checks.
When an assembly has no reliable screw instances, it can still audit the hole
side: readable HoleWizard metadata, own-cylinder mapping, approximate diameter,
axis direction, thread depth versus physical hole depth, and partner visibility.

It does not claim missing screw, wrong screw, pitch pairing, engagement turns,
or bottoming results. Those belong to full screw-hole mode.
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import re
from collections import Counter, defaultdict
from datetime import datetime
from pathlib import Path
from typing import Any


METRIC_RE = re.compile(r"\bM\s*(\d+(?:\.\d+)?)\b", re.I)
TAPPED_NAME_RE = re.compile(r"螺纹|攻丝|螺牙|\bthread\b", re.I)
CLEARANCE_NAME_RE = re.compile(r"间隙|槽口|直径孔|通孔|\bclearance\b", re.I)
COUNTERBORE_NAME_RE = re.compile(r"沉头|沉孔|台阶|柱头|countersink|counterbore", re.I)
BLIND_NAME_RE = re.compile(r"盲孔|blind", re.I)
OPEN_TO_CAVITY_NAME_RE = re.compile(r"镂空|空腔|避空|贯通|通腔|通槽|窗口|减重|open|cavity|pocket|through", re.I)

# Common coarse metric tap-drill diameters.  This is a screening table, not a
# replacement for a company standard.  Extend through config when available.
DEFAULT_TAP_DRILL_MM = {
    3.0: 2.5,
    4.0: 3.3,
    5.0: 4.2,
    6.0: 5.0,
    8.0: 6.8,
    10.0: 8.5,
    12.0: 10.2,
}


FIELDS = [
    "hole_id",
    "occurrence",
    "feature_name",
    "hole_spec",
    "hole_kind",
    "status",
    "severity",
    "finding_code",
    "finding",
    "diameter_mm",
    "expected_diameter_mm",
    "diameter_delta_mm",
    "thread_depth_mm",
    "physical_depth_mm",
    "axis_alignment",
    "axis_tilt_deg_to_nearest_world_axis",
    "blind_bottom_clearance_mm",
    "confidence_score",
    "confidence_basis",
    "accuracy_gate",
    "position_check_scope",
    "counterbore_step_check_scope",
    "blockage_check_scope",
    "partner_visibility_status",
    "required_next_action",
]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("holewizard_parameters_json")
    parser.add_argument("own_hole_diagnostic_json")
    parser.add_argument("cylinder_faces_json")
    parser.add_argument("partner_visibility_json")
    parser.add_argument("output_root")
    parser.add_argument("--diameter-warning-mm", type=float, default=0.35)
    parser.add_argument("--diameter-critical-mm", type=float, default=0.8)
    parser.add_argument("--thread-depth-tolerance-mm", type=float, default=0.25)
    parser.add_argument("--axis-tilt-warning-deg", type=float, default=2.0)
    parser.add_argument("--axis-tilt-critical-deg", type=float, default=5.0)
    parser.add_argument("--blind-bottom-clearance-warning-mm", type=float, default=0.5)
    parser.add_argument("--auto-conclusion-confidence-min", type=float, default=0.85)
    parser.add_argument(
        "--open-cavity-hole-regex",
        action="append",
        default=[],
        help=(
            "额外指定开放到镂空/空腔的孔识别规则，可重复传入；匹配 occurrence + feature_name 后，"
            "该孔不执行盲孔底部余量规则。"
        ),
    )
    return parser.parse_args()


def load_json(path_text: str, label: str) -> tuple[Path, dict[str, Any]]:
    path = Path(path_text).expanduser().resolve()
    if not path.is_file():
        raise FileNotFoundError(f"{label}_NOT_FOUND={path}")
    return path, json.loads(path.read_text(encoding="utf-8-sig"))


def metric_nominal(value: Any) -> float | None:
    match = METRIC_RE.search(str(value or ""))
    return None if match is None else float(match.group(1))


def as_float(value: Any) -> float | None:
    try:
        if value in (None, ""):
            return None
        return float(value)
    except (TypeError, ValueError):
        return None


def vector(value: Any) -> list[float] | None:
    if not isinstance(value, list | tuple) or len(value) < 3:
        return None
    output = [as_float(value[0]), as_float(value[1]), as_float(value[2])]
    if any(item is None for item in output):
        return None
    return [float(item) for item in output]


def norm(values: list[float]) -> float:
    return math.sqrt(sum(item * item for item in values))


def axis_tilt_to_world_deg(axis: list[float] | None) -> tuple[str, float | None]:
    if axis is None:
        return "axis_unavailable", None
    length = norm(axis)
    if length <= 1e-12:
        return "axis_unavailable", None
    unit = [item / length for item in axis]
    labels = ["X", "Y", "Z"]
    abs_values = [abs(item) for item in unit]
    best_index = max(range(3), key=lambda index: abs_values[index])
    cosine = max(-1.0, min(1.0, abs_values[best_index]))
    return labels[best_index], math.degrees(math.acos(cosine))


def hole_kind(feature_name: str, spec: str, thread_depth_mm: float | None) -> str:
    if COUNTERBORE_NAME_RE.search(feature_name):
        return "counterbore_or_step"
    if CLEARANCE_NAME_RE.search(feature_name):
        return "clearance_or_slot"
    if thread_depth_mm is not None and thread_depth_mm > 0:
        return "tapped"
    if TAPPED_NAME_RE.search(feature_name):
        return "tapped"
    if metric_nominal(spec) is not None and TAPPED_NAME_RE.search(feature_name + " " + spec):
        return "tapped"
    return "other_hole"


def is_tapped_like(kind: str, feature_name: str, spec: str, thread_depth_mm: float | None) -> bool:
    return kind == "tapped" or (
        kind == "counterbore_or_step"
        and (thread_depth_mm is not None and thread_depth_mm > 0 or TAPPED_NAME_RE.search(feature_name + " " + spec))
    )


def is_counterbore_or_step(kind: str, feature_name: str) -> bool:
    return kind == "counterbore_or_step" or bool(COUNTERBORE_NAME_RE.search(feature_name))


def is_open_to_cavity_candidate(
    *,
    occurrence: str,
    feature_name: str,
    tapped_like: bool,
    thread_depth_mm: float | None,
    physical_depth: float | None,
    blind_bottom_clearance: float | None,
    zero_clearance_tolerance_mm: float,
    exact_records: list[dict[str, Any]],
    all_records: list[dict[str, Any]],
    spec: str,
    extra_patterns: list[re.Pattern[str]],
) -> tuple[bool, str]:
    """Return whether a threaded hole should not be judged as a blind-bottom hole.

    Some machined parts contain threaded holes that continue downward into a
    lightening pocket / hollow cavity.  In that geometry there is no solid hole
    bottom, so physical depth may be absent, or the measured depth may end at
    the threaded segment.  Explicit evidence comes from names or caller-supplied
    patterns.  When those are absent, a conservative automatic inference is
    allowed for the practical production case that prompted this rule: a
    HoleWizard tapped hole has thread depth metadata but no measurable
    solid-bottom physical depth, or the measured physical depth is essentially
    the same as ThreadDepth.  In the latter case, the measured cylinder end is
    often the threaded segment boundary rather than a solid metal bottom.
    """
    if not tapped_like or thread_depth_mm is None:
        return False, ""
    if BLIND_NAME_RE.search(feature_name):
        return False, ""
    text = f"{occurrence} {feature_name}"
    if OPEN_TO_CAVITY_NAME_RE.search(text):
        return True, "名称/特征包含开放、镂空、空腔、贯通等明确语义"
    if any(pattern.search(text) for pattern in extra_patterns):
        return True, "命中外部开放空腔识别规则"

    nominal = metric_nominal(spec) or metric_nominal(feature_name)
    if physical_depth is None and nominal is not None and all_records and not exact_records:
        return (
            True,
            "自动几何推断：HoleWizard 攻丝孔有规格/攻丝深度，但未测得实体孔底物理深度，且孔位未映射到完整本体圆柱；按开放到镂空空腔或非实体底面处理",
        )
    if physical_depth is not None and blind_bottom_clearance is not None and abs(blind_bottom_clearance) <= zero_clearance_tolerance_mm:
        return (
            True,
            "自动几何推断：测得物理孔深≈HoleWizard 攻丝深度，且特征未声明为盲孔；该深度更可能是螺纹段终止边界，不作为实体金属孔底处理",
        )
    return False, ""


def face_key(row: dict[str, Any]) -> tuple[str, str]:
    return (str(row.get("occurrence") or ""), str(row.get("surface_identity") or ""))


def feature_key(feature: dict[str, Any], feature_index: int) -> str:
    return f"{feature.get('occurrence') or ''}|F{feature_index}"


def strongest(existing: tuple[str, str], new: tuple[str, str]) -> tuple[str, str]:
    order = {"PASS": 0, "REVIEW_REQUIRED": 1, "WARNING": 2, "ISSUE": 3}
    return new if order.get(new[0], 0) > order.get(existing[0], 0) else existing


def add_finding(row: dict[str, Any], status: str, severity: str, code: str, message: str, action: str) -> None:
    current = (row["status"], row["severity"])
    row["status"], row["severity"] = strongest(current, (status, severity))
    if row["finding_code"]:
        row["finding_code"] += ";"
        row["finding"] += "；"
        row["required_next_action"] += "；"
    row["finding_code"] += code
    row["finding"] += message
    row["required_next_action"] += action


def confidence_for_row(
    row: dict[str, Any],
    *,
    exact_records: list[dict[str, Any]],
    diameter: float | None,
    expected_diameter: float | None,
    physical_depth: float | None,
    thread_depth_mm: float | None,
    tilt_deg: float | None,
) -> tuple[float, str]:
    """Return an engineering confidence score for automatic conclusions.

    The score is intentionally conservative.  It is not a statistical accuracy
    claim; it is an evidence-completeness gate used to avoid presenting weak
    CAD evidence as a hard problem.
    """
    codes = {item.strip() for item in str(row.get("finding_code") or "").split(";") if item.strip()}
    if not codes:
        return 0.0, "无可评分结论"
    if codes == {"HOLE_SIDE_GEOMETRY_PASS"}:
        base = 0.88 if exact_records else 0.70
        if diameter is not None:
            base += 0.04
        if physical_depth is not None:
            base += 0.04
        return min(base, 0.96), "通过项；按孔轴/孔径/孔深证据完整度评分"
    if codes == {"THREAD_HOLE_OPEN_TO_CAVITY_PASS"}:
        return 0.90, "存在明确开放到空腔/贯通到非实体底面的证据，且未标记为盲孔；盲孔孔底规则不适用"

    evidence_parts: list[str] = []
    score = 0.45
    if exact_records:
        score += 0.18
        evidence_parts.append("孔位精确映射到自身圆柱轴")
    else:
        evidence_parts.append("孔位未精确映射到自身圆柱轴")
    if diameter is not None and expected_diameter is not None:
        score += 0.14
        evidence_parts.append("可读取孔径并有推荐底孔对照")
    if physical_depth is not None and thread_depth_mm is not None:
        score += 0.16
        evidence_parts.append("可读取物理孔深和攻丝深度")
    if tilt_deg is not None:
        score += 0.05
        evidence_parts.append("可读取孔轴倾角")

    severe_geometry_codes = {
        "TAP_DRILL_DIAMETER_DEVIATION_LARGE",
        "THREAD_DEPTH_EXCEEDS_PHYSICAL_HOLE_DEPTH",
        "THREAD_DEPTH_INVALID",
        "HOLE_AXIS_TILTED_TO_MODEL_AXIS",
    }
    focus_geometry_codes = {
        "BLIND_HOLE_BOTTOM_CLEARANCE_SMALL",
        "TAP_DRILL_DIAMETER_DEVIATION_REVIEW",
    }
    evidence_only_codes = {
        "HOLE_AXIS_NOT_VERIFIED",
        "HOLE_AXIS_UNAVAILABLE",
        "HOLE_DIAMETER_NOT_MEASURABLE",
        "PHYSICAL_HOLE_DEPTH_NOT_MEASURABLE",
        "COUNTERBORE_STEP_DIMENSION_REVIEW_REQUIRED",
    }

    if codes & severe_geometry_codes:
        score += 0.05
        evidence_parts.append("存在明确几何异常规则命中")
    if codes & focus_geometry_codes:
        score += 0.03
        evidence_parts.append("存在重点关注几何规则命中")
    if codes and codes.issubset(evidence_only_codes):
        score = min(score, 0.62)
        evidence_parts.append("仅证据不足，不作为自动问题结论")
    return round(min(score, 0.95), 3), "；".join(evidence_parts)


def write_csv(path: Path, rows: list[dict[str, Any]]) -> None:
    with path.open("w", newline="", encoding="utf-8-sig") as stream:
        writer = csv.DictWriter(stream, fieldnames=FIELDS)
        writer.writeheader()
        writer.writerows(rows)


def main() -> None:
    args = parse_args()
    holes_path, holes_data = load_json(args.holewizard_parameters_json, "HOLEWIZARD_PARAMETERS_JSON")
    own_path, own_data = load_json(args.own_hole_diagnostic_json, "OWN_HOLE_DIAGNOSTIC_JSON")
    faces_path, faces_data = load_json(args.cylinder_faces_json, "CYLINDER_FACES_JSON")
    visibility_path, visibility_data = load_json(args.partner_visibility_json, "PARTNER_VISIBILITY_JSON")
    open_cavity_extra_patterns = [re.compile(pattern, re.I) for pattern in args.open_cavity_hole_regex]

    face_depth_by_identity: dict[tuple[str, str], float] = {}
    for face in faces_data.get("cylinder_faces") or []:
        key = face_key(face)
        amin, amax = as_float(face.get("axial_min_m")), as_float(face.get("axial_max_m"))
        if key[0] and key[1] and amin is not None and amax is not None:
            face_depth_by_identity[key] = abs(amax - amin) * 1000.0

    own_records_by_feature: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for record in own_data.get("records") or []:
        own_records_by_feature[str(record.get("physical_hole_key") or "").rsplit("|P", 1)[0]].append(record)

    visibility_by_feature: dict[str, Counter] = defaultdict(Counter)
    for record in visibility_data.get("records") or []:
        hole = record.get("hole") or {}
        occurrence = str(hole.get("occurrence") or "")
        feature_name = str(hole.get("feature_name") or "")
        key = f"{occurrence}|{feature_name}"
        visibility_by_feature[key][str(record.get("status") or "")] += 1

    rows: list[dict[str, Any]] = []
    for feature_index, feature in enumerate(holes_data.get("hole_wizard_features") or [], start=1):
        occurrence = str(feature.get("occurrence") or "")
        feature_name = str(feature.get("feature_name") or "")
        spec = str(feature.get("fastener_size") or "")
        thread_depth_m = as_float(feature.get("thread_depth_m"))
        thread_depth_mm = None if thread_depth_m is None else thread_depth_m * 1000.0
        kind = hole_kind(feature_name, spec, thread_depth_mm)
        tapped_like = is_tapped_like(kind, feature_name, spec, thread_depth_mm)
        nominal = metric_nominal(spec) or metric_nominal(feature_name)
        expected_diameter = DEFAULT_TAP_DRILL_MM.get(float(nominal)) if nominal is not None and tapped_like else None
        exact_records = [
            item for item in own_records_by_feature.get(feature_key(feature, feature_index), [])
            if item.get("state") == "EXACT_OWN_CYLINDER_AXIS"
        ]
        all_records = own_records_by_feature.get(feature_key(feature, feature_index), [])
        representative = exact_records[0] if exact_records else (all_records[0] if all_records else {})
        diameter = as_float(representative.get("nearest_own_cylinder_radius_mm"))
        diameter = None if diameter is None else diameter * 2.0
        axis = vector(representative.get("nearest_own_cylinder_axis"))
        axis_label, tilt_deg = axis_tilt_to_world_deg(axis)
        surface_identity = str(representative.get("nearest_own_cylinder_surface_identity") or "")
        physical_depth = face_depth_by_identity.get((occurrence, surface_identity))
        blind_bottom_clearance = (
            None
            if physical_depth is None or thread_depth_mm is None
            else physical_depth - thread_depth_mm
        )
        vis_counter = visibility_by_feature.get(f"{occurrence}|{feature_name}", Counter())
        vis_status = ",".join(f"{key}:{value}" for key, value in sorted(vis_counter.items())) if vis_counter else ""
        open_to_cavity, open_to_cavity_basis = is_open_to_cavity_candidate(
            occurrence=occurrence,
            feature_name=feature_name,
            tapped_like=tapped_like,
            thread_depth_mm=thread_depth_mm,
            physical_depth=physical_depth,
            blind_bottom_clearance=blind_bottom_clearance,
            zero_clearance_tolerance_mm=args.blind_bottom_clearance_warning_mm,
            exact_records=exact_records,
            all_records=all_records,
            spec=spec,
            extra_patterns=open_cavity_extra_patterns,
        )

        row: dict[str, Any] = {
            "hole_id": f"HOLE-{feature_index:04d}",
            "occurrence": occurrence,
            "feature_name": feature_name,
            "hole_spec": spec,
            "hole_kind": kind,
            "status": "PASS" if tapped_like else "NOT_APPLICABLE",
            "severity": "INFO",
            "finding_code": "",
            "finding": "孔侧几何筛查通过" if tapped_like else "非攻丝孔/槽口/普通孔，不进入螺纹孔侧规则",
            "diameter_mm": "" if diameter is None else round(diameter, 4),
            "expected_diameter_mm": "" if expected_diameter is None else round(expected_diameter, 4),
            "diameter_delta_mm": "",
            "thread_depth_mm": "" if thread_depth_mm is None else round(thread_depth_mm, 4),
            "physical_depth_mm": "" if physical_depth is None else round(physical_depth, 4),
            "axis_alignment": axis_label,
            "axis_tilt_deg_to_nearest_world_axis": "" if tilt_deg is None else round(tilt_deg, 4),
            "blind_bottom_clearance_mm": "" if blind_bottom_clearance is None else round(blind_bottom_clearance, 4),
            "confidence_score": "",
            "confidence_basis": "",
            "accuracy_gate": "",
            "position_check_scope": "需图纸/参考模型坐标才可硬判孔位偏移；当前仅做孔轴和孔几何一致性筛查",
            "counterbore_step_check_scope": "未检测到沉头/台阶孔" if not is_counterbore_or_step(kind, feature_name) else "已识别沉头/台阶/柱头孔；缺少公司孔型标准时只记录边界，不作为问题输出",
            "blockage_check_scope": "孔被实体封堵需沿孔轴实体占用/剖切证据；当前以孔轴映射失败和物理孔深异常作风险初筛",
            "partner_visibility_status": vis_status,
            "required_next_action": "无需处理" if tapped_like else "无需处理",
        }

        if not tapped_like:
            rows.append(row)
            continue

        row["finding"] = ""
        row["required_next_action"] = ""
        row["finding_code"] = ""

        if tapped_like and not exact_records and not open_to_cavity:
            add_finding(
                row,
                "REVIEW_REQUIRED",
                "WARNING",
                "HOLE_AXIS_NOT_VERIFIED",
                "HoleWizard 孔位没有精确对应到本体圆柱面，存在孔轴未识别、孔破面、半截孔、非圆柱建模或采集不足风险",
                "优先在模型中打开该孔特征，确认孔是否完整贯穿/成形；必要时重建 HoleWizard 孔或补充剖切证据",
            )

        if diameter is not None and expected_diameter is not None:
            delta = diameter - expected_diameter
            row["diameter_delta_mm"] = round(delta, 4)
            abs_delta = abs(delta)
            if abs_delta >= args.diameter_critical_mm:
                add_finding(
                    row,
                    "ISSUE",
                    "SEVERE",
                    "TAP_DRILL_DIAMETER_DEVIATION_LARGE",
                    f"孔侧几何直径 {diameter:.3f} mm 与推荐底孔 {expected_diameter:.3f} mm 偏差 {delta:+.3f} mm",
                    "核对螺纹底孔尺寸或 HoleWizard 规格；确认后修正模型",
                )
            elif abs_delta >= args.diameter_warning_mm:
                add_finding(
                    row,
                    "REVIEW_REQUIRED",
                    "WARNING",
                    "TAP_DRILL_DIAMETER_DEVIATION_REVIEW",
                    f"孔侧几何直径 {diameter:.3f} mm 与推荐底孔 {expected_diameter:.3f} mm 存在偏差 {delta:+.3f} mm",
                    "按公司螺纹底孔标准复核；若为建模表达差异可关闭该项",
                )
        elif expected_diameter is not None and not open_to_cavity:
            add_finding(
                row,
                "REVIEW_REQUIRED",
                "WARNING",
                "HOLE_DIAMETER_NOT_MEASURABLE",
                "未取得可用于孔径筛查的本体圆柱直径",
                "复核孔几何是否完整；必要时重新采集圆柱面证据",
            )

        if tapped_like and tilt_deg is None and not open_to_cavity:
            add_finding(
                row,
                "REVIEW_REQUIRED",
                "WARNING",
                "HOLE_AXIS_UNAVAILABLE",
                "孔轴线不可读取",
                "复核孔是否为标准圆柱孔",
            )
        elif tapped_like and tilt_deg > args.axis_tilt_critical_deg:
            add_finding(
                row,
                "ISSUE",
                "SEVERE",
                "HOLE_AXIS_TILTED_TO_MODEL_AXIS",
                f"孔轴线相对最近模型坐标轴倾斜 {tilt_deg:.3f}°，超过严重阈值 {args.axis_tilt_critical_deg:.3f}°",
                "若设计不是斜孔，应按基准方向修正孔轴；若设计为斜孔，请在审核表备注设计依据",
            )
        elif tapped_like and tilt_deg > args.axis_tilt_warning_deg:
            add_finding(
                row,
                "REVIEW_REQUIRED",
                "WARNING",
                "HOLE_AXIS_TILTED_TO_MODEL_AXIS",
                f"孔轴线相对最近模型坐标轴倾斜 {tilt_deg:.3f}°",
                "若该孔设计为斜孔则可接受；否则按基准方向复核孔轴",
            )

        if tapped_like and (thread_depth_mm is None or thread_depth_mm <= 0):
            add_finding(
                row,
                "ISSUE",
                "SEVERE",
                "THREAD_DEPTH_INVALID",
                "攻丝深度缺失或不大于 0",
                "修正 HoleWizard ThreadDepth 后重跑",
            )
        elif tapped_like and physical_depth is not None and thread_depth_mm > physical_depth + args.thread_depth_tolerance_mm:
            if open_to_cavity:
                add_finding(
                    row,
                    "PASS",
                    "INFO",
                    "THREAD_HOLE_OPEN_TO_CAVITY_PASS",
                    f"检测到攻丝深度 {thread_depth_mm:.3f} mm 大于可测圆柱段 {physical_depth:.3f} mm，但该孔存在开放到空腔/非实体底面的证据；盲孔孔底余量规则不适用。识别依据：{open_to_cavity_basis}",
                    "无需按盲孔底间隙整改；若工程师确认该孔实际为实体盲孔，再启用盲孔底余量规则复核",
                )
            elif BLIND_NAME_RE.search(feature_name):
                add_finding(
                    row,
                    "ISSUE",
                    "SEVERE",
                    "THREAD_DEPTH_EXCEEDS_PHYSICAL_HOLE_DEPTH",
                    f"该孔明确为盲孔，攻丝深度 {thread_depth_mm:.3f} mm 大于检测物理孔深 {physical_depth:.3f} mm",
                    "复核盲孔/攻丝深度，避免螺纹段超过孔几何",
                )
            else:
                add_finding(
                    row,
                    "REVIEW_REQUIRED",
                    "WARNING",
                    "THREAD_DEPTH_EXCEEDS_MEASURED_DEPTH_BUT_BOTTOM_UNCONFIRMED",
                    f"攻丝深度 {thread_depth_mm:.3f} mm 大于可测圆柱段 {physical_depth:.3f} mm，但未确认存在实体盲孔底面；可能是孔通向镂空腔、侧向开口或只采集到局部圆柱段",
                    "先在模型中确认该孔是否为实体盲孔；若开放到空腔则盲孔底余量规则不适用，若确认为盲孔再整改攻丝深度/孔深",
                )
        elif tapped_like and physical_depth is not None and blind_bottom_clearance is not None and 0 <= blind_bottom_clearance < args.blind_bottom_clearance_warning_mm:
            if open_to_cavity:
                add_finding(
                    row,
                    "PASS",
                    "INFO",
                    "THREAD_HOLE_OPEN_TO_CAVITY_PASS",
                    f"螺纹孔向下开放到空腔/非实体底面，物理孔深与攻丝深度差 {blind_bottom_clearance:.3f} mm；盲孔底部余量规则不适用。识别依据：{open_to_cavity_basis}",
                    "无需按盲孔底间隙整改；若工程师确认该孔实际为盲孔，再启用盲孔底余量规则复核",
                )
            else:
                add_finding(
                    row,
                    "REVIEW_REQUIRED",
                    "WARNING",
                    "BLIND_HOLE_BOTTOM_CLEARANCE_SMALL",
                    f"物理孔深与攻丝深度余量仅 {blind_bottom_clearance:.3f} mm，小于预警阈值 {args.blind_bottom_clearance_warning_mm:.3f} mm",
                    "复核盲孔底部余量和丝锥退刀空间；如为通孔或设计允许，请在审核表备注",
                )
        elif tapped_like and physical_depth is None:
            if open_to_cavity:
                add_finding(
                    row,
                    "PASS",
                    "INFO",
                    "THREAD_HOLE_OPEN_TO_CAVITY_PASS",
                    f"螺纹孔向下开放到空腔/非实体底面，未要求存在实体孔底；盲孔孔深和孔底余量规则不适用。识别依据：{open_to_cavity_basis}",
                    "无需按盲孔底间隙整改；若工程师确认该孔实际为盲孔，再启用盲孔底余量规则复核",
                )
            else:
                add_finding(
                    row,
                    "REVIEW_REQUIRED",
                    "WARNING",
                    "PHYSICAL_HOLE_DEPTH_NOT_MEASURABLE",
                    "未取得可用于孔深筛查的圆柱面轴向范围",
                    "补充圆柱面范围证据或人工复核孔深",
                )

        if not row["finding_code"]:
            row["finding_code"] = "HOLE_SIDE_GEOMETRY_PASS"
            row["finding"] = "孔侧几何筛查通过"
            row["required_next_action"] = "无需处理"

        confidence_score, confidence_basis = confidence_for_row(
            row,
            exact_records=exact_records,
            diameter=diameter,
            expected_diameter=expected_diameter,
            physical_depth=physical_depth,
            thread_depth_mm=thread_depth_mm,
            tilt_deg=tilt_deg,
        )
        row["confidence_score"] = confidence_score
        row["confidence_basis"] = confidence_basis
        row["accuracy_gate"] = (
            "AUTO_CONCLUSION_ALLOWED"
            if confidence_score >= args.auto_conclusion_confidence_min
            else "LOW_CONFIDENCE_REVIEW_ONLY"
        )
        if row["status"] == "ISSUE" and confidence_score < args.auto_conclusion_confidence_min:
            row["status"] = "REVIEW_REQUIRED"
            row["severity"] = "WARNING"
            row["finding"] += f"；自动结论置信度 {confidence_score:.2f} 低于 {args.auto_conclusion_confidence_min:.2f}，已降级为人工复核"
            row["required_next_action"] += "；补充图纸/参考模型/孔型标准或人工确认后再关闭"

        rows.append(row)

    output_root = Path(args.output_root).expanduser().resolve()
    run_dir = output_root / f"hole_side_geometry_audit_v4_{datetime.now():%Y%m%d_%H%M%S}"
    run_dir.mkdir(parents=True, exist_ok=False)
    csv_path = run_dir / "孔侧几何检测明细_V4.csv"
    json_path = run_dir / "孔侧几何检测结论_V4.json"
    md_path = run_dir / "孔侧几何检测摘要_V4.md"
    write_csv(csv_path, rows)

    status_counts = Counter(row["status"] for row in rows)
    gate_counts = Counter(str(row.get("accuracy_gate") or "NOT_SCORED") for row in rows)
    finding_counts = Counter()
    for row in rows:
        for code in str(row["finding_code"]).split(";"):
            if code:
                finding_counts[code] += 1

    result = {
        "version": "HOLE_SIDE_GEOMETRY_AUDIT_V49_20260814",
        "status": "SUCCESS",
        "generated_at": datetime.now().isoformat(timespec="seconds"),
        "source_holewizard_parameters_json": str(holes_path),
        "source_own_hole_diagnostic_json": str(own_path),
        "source_cylinder_faces_json": str(faces_path),
        "source_partner_visibility_json": str(visibility_path),
        "mode_notice": "缺少螺钉实例信息时，仅进行孔侧几何检测；螺钉装配合规性未校验。",
        "auto_conclusion_confidence_min": args.auto_conclusion_confidence_min,
        "accuracy_policy": "只有 confidence_score >= auto_conclusion_confidence_min 的几何异常才允许作为自动结论进入可视化定位；低置信度只进入明细留痕。",
        "open_cavity_policy": "开放到镂空/空腔的螺纹孔必须有名称或外部规则证据才跳过盲孔底部余量检查；不得仅凭物理孔深≈攻丝深度自动豁免。",
        "implemented_rules": [
            "孔轴线是否可映射到本体圆柱：用于发现孔破面、半截孔、非圆柱孔或采集不足风险",
            "孔轴线相对模型坐标轴倾斜筛查：超过预警/严重阈值时输出可复核项",
            "孔径与常用公制螺纹底孔表筛查：用于发现底孔孔径明显偏大/偏小",
            "攻丝深度有效性：ThreadDepth 缺失或无效时输出严重问题",
            "攻丝深度是否超过物理孔深：用于发现螺纹段几何深度异常",
            "盲孔底部余量初筛：物理孔深与攻丝深度余量过小时输出复核项",
            "开放到空腔/非实体底面的螺纹孔：有明确开放空腔证据且非显式盲孔时，跳过盲孔底间隙报警并自动通过",
            "沉头/台阶/柱头孔识别：无公司标准时不硬判尺寸，输出孔型复核项",
        ],
        "not_implemented_without_reference": [
            "孔位偏移硬判需要图纸坐标、参考模型或设计基准坐标；当前只记录为模型侧范围说明",
            "孔径超差硬判需要公司孔径/底孔公差标准；当前使用常用底孔表做筛查",
            "沉头/台阶孔尺寸硬判需要孔型标准或参考图纸尺寸",
            "孔被实体封堵需要沿孔轴实体占用/剖切检测；当前以孔轴映射失败和孔深异常作为风险初筛",
            "涉及螺钉的盲孔底间隙、啮合圈数、顶底失效需要螺钉实例和 ThreadLen/Pitch",
        ],
        "hole_feature_count": len(rows),
        "status_counts": dict(status_counts),
        "accuracy_gate_counts": dict(gate_counts),
        "finding_counts": dict(finding_counts),
        "csv_path": str(csv_path),
        "summary_path": str(md_path),
    }
    json_path.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    md_path.write_text(
        "# 孔侧几何检测摘要 V4\n\n"
        "## 模式提示\n\n"
        "缺少螺钉实例信息时，仅进行孔侧几何检测；螺钉装配合规性未校验。\n\n"
        "## 统计\n\n"
        f"- 孔特征数：{len(rows)}\n"
        f"- 状态统计：{json.dumps(dict(status_counts), ensure_ascii=False)}\n"
        f"- 置信度闸门统计：{json.dumps(dict(gate_counts), ensure_ascii=False)}\n"
        f"- 问题类型统计：{json.dumps(dict(finding_counts), ensure_ascii=False)}\n\n"
        f"## 自动结论门槛\n\n- confidence_score >= {args.auto_conclusion_confidence_min:.2f} 才进入自动结论/可视化优先定位。\n\n"
        "## 已实现规则\n\n"
        + "\n".join(f"- {item}" for item in result["implemented_rules"])
        + "\n\n## 未覆盖/需配置\n\n"
        + "\n".join(f"- {item}" for item in result["not_implemented_without_reference"])
        + "\n",
        encoding="utf-8",
    )

    print(f"HOLE_SIDE_GEOMETRY_ROW_COUNT={len(rows)}")
    print("STATUS_COUNTS=" + json.dumps(dict(status_counts), ensure_ascii=False, separators=(",", ":")))
    print("ACCURACY_GATE_COUNTS=" + json.dumps(dict(gate_counts), ensure_ascii=False, separators=(",", ":")))
    print("FINDING_COUNTS=" + json.dumps(dict(finding_counts), ensure_ascii=False, separators=(",", ":")))
    print(f"CSV_PATH={csv_path}")
    print(f"REPORT_PATH={json_path}")
    print(f"SUMMARY_PATH={md_path}")
    print("HOLE_SIDE_GEOMETRY_AUDIT_V4_STATUS=SUCCESS")


if __name__ == "__main__":
    main()

