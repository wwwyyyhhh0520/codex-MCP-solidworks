"""Conservative hole feature analysis.

The analyzer separates reliable model/API facts from weak textual clues such as
feature names. Weak clues are recorded for human review but are not promoted to
validated hole callouts.
"""

from __future__ import annotations

import argparse
import json
import re
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List


DIAMETER_RE = re.compile(r"(?:Ø|⌀)\s*([0-9]+(?:\.[0-9]+)?)|[（(]\s*([0-9]+(?:\.[0-9]+)?)\s*[）)]")


def _is_meaningful_parameter(value: Any) -> bool:
    if value is None:
        return False
    return str(value).strip() not in {"", "0", "0.0", "0.00", "-1", "-1.0"}


def _is_reliable_feature_value(source_key: str, value: Any) -> bool:
    # Zero is a valid SolidWorks end-condition enum, unlike a zero length.
    if source_key == "EndCondition":
        return value is not None and str(value).strip() != ""
    return _is_meaningful_parameter(value)


def _is_clearance_hole(params: Dict[str, Any]) -> bool:
    return "间隙" in str(params.get("FastenerType", ""))


def _topology_diameter_mm(feature: Dict[str, Any]) -> Dict[str, Any] | None:
    geometry = feature.get("topology_geometry") or {}
    values = []
    for item in geometry.get("cylindrical_faces", []):
        try:
            diameter = float(item["diameter_m"]) * 1000.0
        except (KeyError, TypeError, ValueError):
            continue
        if diameter > 0:
            values.append((round(diameter, 6), item.get("source")))
    unique = {value for value, _ in values}
    if len(unique) != 1:
        return None
    diameter = next(iter(unique))
    return {
        "value_mm": diameter,
        "source": next(source for value, source in values if value == diameter),
        "source_type": "hole_feature_topology_cylindrical_face",
        "note": "由 Hole Wizard 所属圆柱面直接读取；不包含孔深、公差、配合或功能意图。",
    }


def _topology_axial_length_mm(feature: Dict[str, Any]) -> Dict[str, Any] | None:
    geometry = feature.get("topology_geometry") or {}
    values = []
    for item in geometry.get("cylindrical_faces", []):
        if not item.get("axis_aligned"):
            continue
        try:
            length = float(item["axial_extent_m"]) * 1000.0
        except (KeyError, TypeError, ValueError):
            continue
        if length > 0:
            values.append((round(length, 6), item.get("source")))
    unique = {value for value, _ in values}
    if len(unique) != 1:
        return None
    length = next(iter(unique))
    return {
        "value_mm": length,
        "source": next(source for value, source in values if value == length),
        "source_type": "hole_feature_topology_axis_aligned_cylindrical_face",
        "note": "由 Hole Wizard 所属圆柱面的轴向几何范围读取；未解释端部条件，不包含公差或功能意图。",
    }


def _name_tokens(name: str) -> Dict[str, Any]:
    tokens: Dict[str, Any] = {}
    matches = [
        float(value)
        for match in DIAMETER_RE.finditer(name)
        for value in match.groups()
        if value is not None
    ]
    if matches:
        tokens["numeric_tokens"] = matches
        tokens["diameter_like_tokens"] = matches
        tokens["warning"] = "这些数值来自特征名称文本，只能作为弱线索，不能自动生成孔/螺纹标注。"
    return tokens


def analyze_holes_from_part_analysis(analysis: Dict[str, Any]) -> Dict[str, Any]:
    holes: List[Dict[str, Any]] = []
    risks: List[str] = []
    for feature in analysis.get("features", []):
        if feature.get("type") != "HoleWzd":
            continue
        params = feature.get("parameters") or {}
        name = str(feature.get("name", ""))
        reliable: Dict[str, Any] = {}
        missing = (
            ["diameter", "depth", "end_condition", "tolerance"]
            if _is_clearance_hole(params)
            else [
                "end_condition", "thread_specification", "thread_depth", "tolerance",
            ]
        )
        if params:
            for source_key, target_key in (
                ("Diameter", "diameter"),
                ("HoleDiameter", "diameter"),
                ("Depth", "depth"),
                ("HoleDepth", "depth"),
                ("EndCondition", "end_condition"),
                ("ThreadType", "thread_specification"),
                ("FastenerSize", "thread_specification"),
                ("ThreadPitch", "thread_pitch"),
                ("ThreadClass", "thread_class"),
                ("ThreadDepth", "thread_depth"),
                ("ThreadDiameter", "thread_diameter"),
            ):
                if source_key in params and _is_reliable_feature_value(source_key, params[source_key]):
                    if _is_clearance_hole(params) and target_key.startswith("thread_"):
                        continue
                    reliable[target_key] = params[source_key]
            missing = [item for item in missing if item not in reliable]
        topology_diameter = _topology_diameter_mm(feature)
        if topology_diameter:
            reliable["diameter"] = topology_diameter
            missing = [item for item in missing if item != "diameter"]
        topology_length = _topology_axial_length_mm(feature)
        if topology_length:
            reliable["depth"] = topology_length
            missing = [item for item in missing if item != "depth"]
        geometry_complete_tolerance_pending = missing == ["tolerance"]
        holes.append({
            "id": f"HOLE-{len(holes) + 1:03d}",
            "feature_name": name,
            "feature_type": feature.get("type"),
            "suppressed": bool(feature.get("suppressed", False)),
            "reliable_parameters": reliable,
            "weak_name_clues": _name_tokens(name),
            "missing_reliable_parameters": missing,
            "status": "geometry_complete_tolerance_pending" if geometry_complete_tolerance_pending else ("parameters_pending" if missing else "parameters_read"),
            "callout_allowed": False,
            "callout_block_reason": "孔的几何直径和轴向长度已确认，但未发现可追溯公差来源；不得自动生成孔公差。" if geometry_complete_tolerance_pending else "完整孔标注仍缺少可靠参数；不能由特征名称推断。",
        })
    if not holes:
        risks.append("part_analysis 中未发现 HoleWzd 特征。")
    if any(not hole["reliable_parameters"] for hole in holes):
        risks.append("HoleWzd 特征定义参数为空；当前读取链路不能确认孔径、深度、螺纹或公差。")
    return {
        "schema_version": "1.0",
        "mode": "hole_analysis_from_part_analysis",
        "created_at": datetime.now().isoformat(timespec="seconds"),
        "part_number": analysis.get("part_number"),
        "source_file": analysis.get("source_file"),
        "holes": holes,
        "summary": {
            "hole_count": len(holes),
            "validated_callout_count": sum(1 for hole in holes if hole.get("callout_allowed")),
            "weak_clue_count": sum(1 for hole in holes if hole.get("weak_name_clues")),
        },
        "risks": risks,
        "policy": {
            "may_use_for_callout": ["reliable_parameters", "PMI", "DimXpert", "SolidWorks HoleCallout API", "human_metadata"],
            "must_not_use_for_callout": ["feature_name_text", "filename", "part_number", "visual_guess"],
        },
    }


def enrich_plan_with_hole_analysis(plan: Dict[str, Any], hole_report: Dict[str, Any], hole_source: str) -> Dict[str, Any]:
    hole_by_name = {hole.get("feature_name"): hole for hole in hole_report.get("holes", [])}
    for callout in plan.get("hole_callouts", []):
        hole = hole_by_name.get(callout.get("feature_name"))
        if not hole:
            continue
        callout["analysis_source"] = hole_source
        callout["status"] = "blocked_pending_reliable_parameters"
        callout["reliable_parameters"] = hole.get("reliable_parameters", {})
        callout["weak_name_clues"] = hole.get("weak_name_clues", {})
        callout["missing_reliable_parameters"] = hole.get("missing_reliable_parameters", [])
        callout["callout_allowed"] = False
    plan.setdefault("uncertain_items", [])
    if not any(item.get("id") == "PLAN-HOLE-001" for item in plan["uncertain_items"]):
        plan["uncertain_items"].append({
            "id": "PLAN-HOLE-001",
            "topic": "hole_callouts",
            "description": "Hole Wizard 特征已识别，但孔/螺纹可靠参数仍未读取，孔标注不得自动生成。",
            "source": hole_source,
            "severity": "warning",
            "requires_human_confirmation": True,
        })
    return plan


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("analysis_file")
    parser.add_argument("hole_output_file")
    parser.add_argument("--plan-file", default=None)
    parser.add_argument("--plan-output-file", default=None)
    args = parser.parse_args()
    analysis = json.loads(Path(args.analysis_file).read_text(encoding="utf-8"))
    report = analyze_holes_from_part_analysis(analysis)
    Path(args.hole_output_file).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    if args.plan_file and args.plan_output_file:
        plan = json.loads(Path(args.plan_file).read_text(encoding="utf-8"))
        plan = enrich_plan_with_hole_analysis(plan, report, args.hole_output_file)
        Path(args.plan_output_file).write_text(json.dumps(plan, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({
        "status": "ok",
        "hole_count": report["summary"]["hole_count"],
        "validated_callout_count": report["summary"]["validated_callout_count"],
        "output": args.hole_output_file,
    }, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
