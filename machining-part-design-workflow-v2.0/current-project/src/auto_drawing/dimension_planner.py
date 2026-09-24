"""Conservative dimension planning from traceable part analysis facts."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any, Dict, List


AXES = (
    ("x", "overall_width_x", "min_x", "max_x", "front_or_top"),
    ("y", "overall_depth_y", "min_y", "max_y", "top"),
    ("z", "overall_height_z", "min_z", "max_z", "front_or_right"),
)


def _round_mm(value_m: float) -> float:
    return round(value_m * 1000.0, 3)


def _bbox_dimensions(analysis: Dict[str, Any]) -> List[Dict[str, Any]]:
    bbox = analysis.get("bounding_box", {})
    dimensions: List[Dict[str, Any]] = []
    for axis, kind, low_key, high_key, preferred_view in AXES:
        if low_key not in bbox or high_key not in bbox:
            continue
        low = float(bbox[low_key])
        high = float(bbox[high_key])
        value_mm = _round_mm(abs(high - low))
        dimensions.append({
            "id": f"DIM-BBOX-{axis.upper()}",
            "kind": kind,
            "value": value_mm,
            "unit": "mm",
            "source": f"part_analysis.bounding_box.{low_key}/{high_key}",
            "source_type": "model_geometry_bounding_box",
            "status": "planned_traceable_geometry",
            "preferred_view": preferred_view,
            "annotation_role": "overall_size",
            "tolerance": None,
            "fit": None,
            "gdandt": None,
            "surface_finish": None,
            "note": "仅表达模型包络总体尺寸；不推断功能尺寸、公差、配合或加工要求。",
        })
    return dimensions


def enrich_dimensions(analysis: Dict[str, Any], plan: Dict[str, Any]) -> Dict[str, Any]:
    existing = {item.get("id"): item for item in plan.get("dimensions", [])}
    for dimension in _bbox_dimensions(analysis):
        existing[dimension["id"]] = dimension
    plan["dimensions"] = list(existing.values())
    plan.setdefault("uncertain_items", [])
    if not any(item.get("id") == "PLAN-DIM-SEMANTIC-001" for item in plan["uncertain_items"]):
        plan["uncertain_items"].append({
            "id": "PLAN-DIM-SEMANTIC-001",
            "topic": "dimensions",
            "description": "已生成总体包络尺寸计划，但功能尺寸、定位尺寸、孔/槽尺寸链和加工基准仍未完整规划。",
            "source": "part_analysis.bounding_box",
            "severity": "warning",
            "requires_human_confirmation": True,
        })
    return plan


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("analysis_file")
    parser.add_argument("plan_file")
    parser.add_argument("output_file")
    args = parser.parse_args()
    analysis = json.loads(Path(args.analysis_file).read_text(encoding="utf-8"))
    plan = json.loads(Path(args.plan_file).read_text(encoding="utf-8"))
    plan = enrich_dimensions(analysis, plan)
    Path(args.output_file).write_text(json.dumps(plan, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({
        "status": "ok",
        "dimension_count": len(plan.get("dimensions", [])),
        "output": args.output_file,
    }, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
