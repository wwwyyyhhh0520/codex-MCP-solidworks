"""Preflight reviewer for part_analysis and drawing_plan artifacts."""

from __future__ import annotations

import argparse
import json
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Dict, List


@dataclass
class ReviewItem:
    id: str
    severity: str
    topic: str
    message: str
    source: str
    blocks_execution: bool = False


@dataclass
class ReviewReport:
    schema_version: str = "1.0"
    status: str = "blocked"
    part_number: str | None = None
    summary: Dict[str, Any] = field(default_factory=dict)
    items: List[ReviewItem] = field(default_factory=list)
    execution_allowed: bool = False

    def to_dict(self) -> Dict[str, Any]:
        data = asdict(self)
        data["summary"] = {
            **self.summary,
            "error_count": sum(1 for item in self.items if item.severity == "error"),
            "warning_count": sum(1 for item in self.items if item.severity == "warning"),
            "info_count": sum(1 for item in self.items if item.severity == "info"),
            "blocking_count": sum(1 for item in self.items if item.blocks_execution),
        }
        return data

    def to_json(self) -> str:
        return json.dumps(self.to_dict(), ensure_ascii=False, indent=2)


def _add(report: ReviewReport, item: ReviewItem) -> None:
    report.items.append(item)


def _feature_types(analysis: Dict[str, Any]) -> set[str]:
    return {str(feature.get("type", "")) for feature in analysis.get("features", [])}


def _review_template(plan: Dict[str, Any], report: ReviewReport) -> None:
    sheet = plan.get("sheet", {})
    if not sheet.get("template_path"):
        _add(report, ReviewItem(
            "REV-TEMPLATE-001",
            "error",
            "drawing_template",
            "未指定工程图模板路径，不能进入 SolidWorks 出图执行。",
            "drawing_plan.sheet.template_path",
            True,
        ))
        return
    if sheet.get("selection_status") != "validated_in_solidworks":
        _add(report, ReviewItem(
            "REV-TEMPLATE-002",
            "warning",
            "drawing_template",
            "模板已按规则选择，但尚未通过 SolidWorks 新建工程图实际验证。",
            "drawing_plan.sheet.selection_status",
            True,
        ))


def _review_views(plan: Dict[str, Any], report: ReviewReport) -> None:
    base_views = [view for view in plan.get("views", []) if view.get("id") == "BASE-1"]
    if not base_views:
        _add(report, ReviewItem(
            "REV-VIEW-001",
            "error",
            "view_orientation",
            "缺少主视图计划。",
            "drawing_plan.views",
            True,
        ))
        return
    base = base_views[0]
    if base.get("status") != "validated":
        _add(report, ReviewItem(
            "REV-VIEW-002",
            "warning",
            "view_orientation",
            "主视图仍是候选方向，尚未通过真实投影可见性、隐藏线数量和基准可标注性验证。",
            "drawing_plan.views.BASE-1.status",
            True,
        ))
    if float(base.get("confidence", 0.0)) < 0.7:
        _add(report, ReviewItem(
            "REV-VIEW-003",
            "warning",
            "view_orientation",
            f"主视图置信度为 {base.get('confidence', 0.0)}，低于自动执行阈值 0.7。",
            "drawing_plan.views.BASE-1.confidence",
            True,
        ))


def _review_dimensions(plan: Dict[str, Any], report: ReviewReport) -> None:
    dimensions = plan.get("dimensions", [])
    if not dimensions:
        _add(report, ReviewItem(
            "REV-DIM-001",
            "error",
            "dimensions",
            "尚未生成可追溯的尺寸计划；不能生成完整工程图。",
            "drawing_plan.dimensions",
            True,
        ))
        return
    untraceable = [
        item for item in dimensions
        if not item.get("source") or item.get("status") not in {"planned_traceable_geometry", "validated"}
    ]
    if untraceable:
        _add(report, ReviewItem(
            "REV-DIM-002",
            "error",
            "dimensions",
            "存在缺少可靠来源或状态未确认的尺寸计划。",
            "drawing_plan.dimensions",
            True,
        ))
    if not any(item.get("annotation_role") == "overall_size" for item in dimensions):
        _add(report, ReviewItem(
            "REV-DIM-003",
            "warning",
            "dimensions",
            "尺寸计划中缺少总体尺寸。",
            "drawing_plan.dimensions",
            True,
        ))
    _add(report, ReviewItem(
        "REV-DIM-004",
        "warning",
        "dimensions",
        "已存在可追溯尺寸计划，但当前 MVP 只覆盖总体包络尺寸；功能尺寸、定位尺寸、孔/槽尺寸链仍待规划。",
        "drawing_plan.dimensions",
        False,
    ))


def _review_holes(analysis: Dict[str, Any], plan: Dict[str, Any], report: ReviewReport) -> None:
    has_hole_wizard = "HoleWzd" in _feature_types(analysis)
    if has_hole_wizard and not plan.get("hole_callouts"):
        _add(report, ReviewItem(
            "REV-HOLE-001",
            "error",
            "hole_callouts",
            "模型含 Hole Wizard 特征，但计划中没有孔标注。",
            "part_analysis.features",
            True,
        ))
    accepted_statuses = {"validated", "geometry_complete_tolerance_confirmed"}
    for index, callout in enumerate(plan.get("hole_callouts", [])):
        if callout.get("status") not in accepted_statuses or not callout.get("callout_allowed"):
            missing = callout.get("missing_reliable_parameters", [])
            labels = {
                "diameter": "孔径", "depth": "孔深", "end_condition": "端部条件",
                "thread_specification": "螺纹规格", "thread_pitch": "螺距",
                "thread_class": "螺纹等级", "thread_depth": "螺纹深度", "tolerance": "公差",
            }
            missing_text = "、".join(labels.get(str(item), str(item)) for item in missing) or "完整孔标注条件"
            _add(report, ReviewItem(
                "REV-HOLE-002",
                "warning",
                "hole_callouts",
                f"孔特征已识别，但{missing_text}尚未从可靠参数源确认。",
                f"drawing_plan.hole_callouts[{index}]",
                True,
            ))
        if callout.get("forbidden_inference"):
            _add(report, ReviewItem(
                "REV-HOLE-003",
                "info",
                "hole_callouts",
                "计划明确禁止从特征名称推断螺纹规格、螺距、有效深度或公差，符合不猜测设计意图的约束。",
                f"drawing_plan.hole_callouts[{index}].forbidden_inference",
                False,
            ))


def _review_datums_and_tolerances(plan: Dict[str, Any], report: ReviewReport) -> None:
    if not plan.get("datums"):
        _add(report, ReviewItem(
            "REV-DATUM-001",
            "warning",
            "datums",
            "没有已确认的功能基准；不得自动生成 A/B/C 基准符号。",
            "drawing_plan.datums",
            False,
        ))
    if not plan.get("gdandt"):
        _add(report, ReviewItem(
            "REV-GDT-001",
            "info",
            "gdandt",
            "未发现可追溯 GD&T 来源，因此计划不生成形位公差。",
            "drawing_plan.gdandt",
            False,
        ))
    if not plan.get("surface_finish"):
        _add(report, ReviewItem(
            "REV-SURF-001",
            "info",
            "surface_finish",
            "未发现可追溯粗糙度来源，因此计划不生成粗糙度要求。",
            "drawing_plan.surface_finish",
            False,
        ))


def _review_uncertainties(analysis: Dict[str, Any], plan: Dict[str, Any], report: ReviewReport) -> None:
    items = analysis.get("uncertain_items", []) + plan.get("uncertain_items", [])
    seen = set()
    for item in items:
        item_id = item.get("id", "UNKNOWN")
        if item.get("requires_human_confirmation") and item_id not in seen:
            seen.add(item_id)
            _add(report, ReviewItem(
                f"REV-UNCERTAIN-{item_id}",
                item.get("severity", "warning"),
                item.get("topic", "unknown"),
                item.get("description", "存在需人工确认事项。"),
                item.get("source", "uncertain_items"),
                item.get("severity") == "error",
            ))


def review(analysis: Dict[str, Any], plan: Dict[str, Any]) -> ReviewReport:
    report = ReviewReport(part_number=plan.get("part_number") or analysis.get("part_number"))
    report.summary.update({
        "analysis_schema": analysis.get("schema_version"),
        "plan_schema": plan.get("schema_version"),
        "plan_status": plan.get("status"),
        "source_file": analysis.get("source_file"),
    })
    _review_template(plan, report)
    _review_views(plan, report)
    _review_dimensions(plan, report)
    _review_holes(analysis, plan, report)
    _review_datums_and_tolerances(plan, report)
    _review_uncertainties(analysis, plan, report)

    report.execution_allowed = not any(item.blocks_execution for item in report.items)
    report.status = "passed" if report.execution_allowed else "blocked"
    return report


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("analysis_file")
    parser.add_argument("plan_file")
    parser.add_argument("output_file")
    args = parser.parse_args()
    analysis = json.loads(Path(args.analysis_file).read_text(encoding="utf-8"))
    plan = json.loads(Path(args.plan_file).read_text(encoding="utf-8"))
    report = review(analysis, plan)
    Path(args.output_file).write_text(report.to_json(), encoding="utf-8")
    print(json.dumps({
        "status": report.status,
        "execution_allowed": report.execution_allowed,
        "output": args.output_file,
    }, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
