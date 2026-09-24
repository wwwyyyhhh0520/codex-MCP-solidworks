"""Plan section/detail views from verified projection evidence, conservatively."""

from __future__ import annotations

import argparse
import json
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List


def assess_section_need(
    plan: Dict[str, Any], holes: Dict[str, Any], view_ranking: Dict[str, Any]
) -> Dict[str, Any]:
    hole_items = holes.get("holes", [])
    candidates = view_ranking.get("ranked_candidates", [])
    correspondence = max(
        (int(item.get("hole_corresponding_entity_count", 0)) for item in candidates), default=0
    )
    visible_circles = max(
        (int(item.get("visible_circular_edge_count", 0)) for item in candidates), default=0
    )
    base = next((item for item in plan.get("views", []) if item.get("id") == "BASE-1"), {})

    if not hole_items:
        status = "not_required_by_known_features"
        reason = "未发现结构化 Hole Wizard 特征，当前规则不触发剖视或局部视图。"
    elif correspondence > 0:
        status = "projection_evidence_available"
        reason = "至少一个 Hole Wizard 模型面与投影视图实体存在 API 对应关系；是否剖视仍取决于尺寸与内部结构表达。"
    else:
        status = "candidate_requires_human_cutting_basis"
        reason = "投影中可观察到圆边，但 Hole Wizard 模型面未与绘图实体建立 API 对应；不能自动放置剖切线或声称孔内部结构已表达。"

    return {
        "schema_version": "1.0",
        "mode": "section_detail_view_assessment",
        "created_at": datetime.now().isoformat(timespec="seconds"),
        "part_number": plan.get("part_number"),
        "status": status,
        "input_evidence": {
            "hole_feature_count": len(hole_items),
            "max_hole_corresponding_entity_count": correspondence,
            "max_visible_circular_edge_count": visible_circles,
            "geometry_preferred_base_view": base.get("orientation"),
        },
        "section_view": {
            "status": status,
            "automatic_cutting_line_allowed": False,
            "reason": reason,
        },
        "detail_view": {
            "status": "not_automatically_triggered",
            "automatic_detail_boundary_allowed": False,
            "reason": "未验证孔在图纸视图中的可定位实体与标注拥挤度，不能自动圈定局部视图边界。",
        },
        "policy": {
            "must_not_infer": ["cutting_line", "section_direction", "feature_priority", "functional_intent"],
            "human_input_needed_for_execution": ["section_cutting_basis", "section_direction", "detail_view_boundary"],
        },
    }


def apply_assessment(plan: Dict[str, Any], assessment: Dict[str, Any], source: str) -> Dict[str, Any]:
    plan["section_view_assessment"] = {
        "status": assessment.get("status"),
        "source": source,
        "automatic_cutting_line_allowed": False,
    }
    for view in plan.get("views", []):
        if view.get("id") == "SECTION-1":
            view["status"] = assessment.get("status")
            view["reason"] = assessment.get("section_view", {}).get("reason")
    return plan


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("plan_file")
    parser.add_argument("hole_analysis_file")
    parser.add_argument("view_ranking_file")
    parser.add_argument("output_file")
    parser.add_argument("--plan-output-file", default=None)
    args = parser.parse_args()
    plan = json.loads(Path(args.plan_file).read_text(encoding="utf-8"))
    holes = json.loads(Path(args.hole_analysis_file).read_text(encoding="utf-8"))
    ranking = json.loads(Path(args.view_ranking_file).read_text(encoding="utf-8"))
    assessment = assess_section_need(plan, holes, ranking)
    Path(args.output_file).write_text(json.dumps(assessment, ensure_ascii=False, indent=2), encoding="utf-8")
    if args.plan_output_file:
        updated = apply_assessment(plan, assessment, args.output_file)
        Path(args.plan_output_file).write_text(json.dumps(updated, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"status": assessment["status"], "output": args.output_file}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
