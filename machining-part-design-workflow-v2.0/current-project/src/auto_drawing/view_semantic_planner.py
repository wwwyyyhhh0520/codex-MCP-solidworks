"""Rank actual SolidWorks drawing projections without inferring design intent."""

from __future__ import annotations

import argparse
import json
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List


def _area(candidate: Dict[str, Any]) -> float:
    outline = candidate.get("outline") or {}
    return max(0.0, float(outline.get("width", 0.0))) * max(0.0, float(outline.get("height", 0.0)))


def analyze_view_comparison(probe: Dict[str, Any]) -> Dict[str, Any]:
    candidates = [item for item in (probe.get("drawing_probe") or {}).get("view_candidates", []) if item.get("inserted")]
    has_structured_hole = any(item.get("type") == "HoleWzd" for item in probe.get("features", []))
    largest_area = max((_area(item) for item in candidates), default=0.0)
    ranked: List[Dict[str, Any]] = []
    for candidate in candidates:
        area = _area(candidate)
        hidden_edges = int(candidate.get("hidden_edge_count", -1))
        visible_edges = max(int(candidate.get("visible_edge_count", 0)), 0)
        circular_edges = max(int(candidate.get("visible_circular_edge_count", 0)), 0)
        hole_correspondence = max(int(candidate.get("hole_corresponding_entity_count", 0)), 0)
        # This only resolves equal-area projections; it is not a functional-view inference.
        score = (area / largest_area if largest_area else 0.0) - (0.15 * max(hidden_edges, 0)) + (0.001 * visible_edges)
        if has_structured_hole and circular_edges and hole_correspondence:
            score += 0.1
        ranked.append({
            **candidate,
            "projected_area": area,
            "score": round(score, 6),
            "basis": [
                "SolidWorks 临时工程图真实视图外框",
                "SolidWorks API 隐藏边计数",
                "SolidWorks API 可见边计数（仅用于几何平局决胜）",
                *(["Hole Wizard 模型面与投影视图存在 API 对应实体"] if has_structured_hole and hole_correspondence else []),
            ],
            "semantic_validation_status": "geometry_ranked_only",
        })
    ranked.sort(key=lambda item: item["score"], reverse=True)
    return {
        "schema_version": "1.0",
        "mode": "view_geometry_ranking_from_typed_bridge",
        "created_at": datetime.now().isoformat(timespec="seconds"),
        "source_file": probe.get("source_file"),
        "status": "passed" if ranked else "failed",
        "ranked_candidates": ranked,
        "selected_candidate": ranked[0] if ranked else None,
        "template_validation": {
            "template_path": (probe.get("drawing_probe") or {}).get("template_path"),
            "drawing_created": bool((probe.get("drawing_probe") or {}).get("drawing_created")),
            "drawing_saved": bool(probe.get("drawing_saved", False)),
        },
        "limits": [
            "评分仅使用投影外框、隐藏边数和已验证的实体对应关系，不代表功能结构表达完整性。",
            "圆弧边若未与 Hole Wizard 特征建立 API 对应关系，只能作为观察记录，不能参与孔表达加分。",
            "未读取尺寸可标注性或功能基准，因此不得自动确认主视图。",
        ],
    }


def apply_geometry_ranking(plan: Dict[str, Any], report: Dict[str, Any], report_path: str) -> Dict[str, Any]:
    selected = report.get("selected_candidate")
    if not selected:
        return plan
    template_validation = report.get("template_validation") or {}
    if template_validation.get("drawing_created") and template_validation.get("drawing_saved") is False:
        sheet = plan.setdefault("sheet", {})
        sheet["selection_status"] = "validated_in_solidworks"
        sheet["validation_source"] = report_path
    for view in plan.get("views", []):
        if view.get("id") != "BASE-1":
            continue
        view["orientation"] = selected.get("orientation")
        view["status"] = "geometry_ranked_pending_feature_visibility"
        view["confidence"] = max(float(view.get("confidence", 0.0)), 0.6)
        view["geometry_score"] = selected.get("score")
        view["validation_source"] = report_path
        view["reason"] = "三方向均已完成 API 插入；按真实投影外框与隐藏边数选择当前几何优选候选，仍待特征可见性验证。"
    return plan


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("probe_file")
    parser.add_argument("output_file")
    parser.add_argument("--plan-file", default=None)
    parser.add_argument("--plan-output-file", default=None)
    args = parser.parse_args()
    probe = json.loads(Path(args.probe_file).read_text(encoding="utf-8"))
    report = analyze_view_comparison(probe)
    Path(args.output_file).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    if args.plan_file and args.plan_output_file:
        plan = json.loads(Path(args.plan_file).read_text(encoding="utf-8"))
        Path(args.plan_output_file).write_text(
            json.dumps(apply_geometry_ranking(plan, report, args.output_file), ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
    print(json.dumps({"status": report["status"], "output": args.output_file}, ensure_ascii=False))
    return 0 if report["status"] == "passed" else 2


if __name__ == "__main__":
    raise SystemExit(main())
