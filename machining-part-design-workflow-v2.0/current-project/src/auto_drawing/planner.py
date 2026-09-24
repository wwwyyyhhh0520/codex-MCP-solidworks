"""从 PartAnalysis 生成保守的 DrawingPlan，不执行 SolidWorks 操作。"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any, Dict, Optional

from .models import DrawingPlan, PartAnalysis, UncertainItem


def _max_extent_mm(analysis: PartAnalysis) -> Optional[float]:
    bbox = analysis.bounding_box or {}
    axes = [
        ("min_x", "max_x"),
        ("min_y", "max_y"),
        ("min_z", "max_z"),
    ]
    extents = []
    for low_key, high_key in axes:
        if low_key in bbox and high_key in bbox:
            extents.append(abs(float(bbox[high_key]) - float(bbox[low_key])) * 1000.0)
    return max(extents) if extents else None


def _select_template(analysis: PartAnalysis, template_config: Optional[Dict[str, Any]]) -> Dict[str, Any]:
    if not template_config:
        return {
            "template": None,
            "template_path": None,
            "sheet_format_path": None,
            "selection_status": "pending_template_configuration",
            "selection_reason": "尚未提供可用 SolidWorks 工程图模板配置。",
        }

    templates = {item["id"]: item for item in template_config.get("drawing_templates", [])}
    rules = template_config.get("selection_rules", {})
    max_extent = _max_extent_mm(analysis)
    selected_id = None
    if max_extent is not None:
        if max_extent <= float(rules.get("small_part_max_extent_mm", 120)):
            selected_id = "A4"
        elif max_extent <= float(rules.get("medium_part_max_extent_mm", 260)):
            selected_id = "A3"
        else:
            selected_id = "A2"

    for candidate in rules.get("preferred_order", []):
        if selected_id == candidate and candidate in templates:
            selected = templates[candidate]
            return {
                "template": selected.get("id"),
                "sheet_size": selected.get("sheet_size"),
                "template_path": selected.get("template_path"),
                "sheet_format_path": selected.get("sheet_format_path"),
                "scale": "1:1",
                "selection_status": "rule_selected_pending_solidworks_open_validation",
                "selection_reason": f"零件最大包络约 {max_extent:.3f} mm，按 MVP 模板规则选择 {selected.get('id')}。" if max_extent is not None else "按模板优先级选择。",
            }

    return {
        "template": None,
        "template_path": None,
        "sheet_format_path": None,
        "selection_status": "no_matching_template",
        "selection_reason": "模板配置存在，但未找到与零件尺寸匹配的工程图模板。",
    }


def _resolved_custom_property(analysis: PartAnalysis, name: str) -> str | None:
    for scope in ("active_configuration", "document"):
        record = (analysis.properties or {}).get(scope, {}).get(name, {})
        value = str(record.get("resolved_value", "")).strip() if isinstance(record, dict) else ""
        if value:
            return value
    return None


def _sourced_property_notes(analysis: PartAnalysis) -> list[Dict[str, Any]]:
    rules = (
        ("材料", "material", "材料："),
        ("表面处理", "surface_treatment", "表面处理："),
        ("热处理", "heat_treatment", "热处理："),
    )
    notes = []
    for index, (property_name, kind, prefix) in enumerate(rules, start=1):
        value = _resolved_custom_property(analysis, property_name)
        if not value:
            continue
        notes.append({
            "id": f"NOTE-PROP-{index:03d}",
            "kind": kind,
            "text": prefix + value,
            "source": f"part_analysis.properties.*.{property_name}.resolved_value",
            "status": "planned_traceable_property",
            "note": "仅复制模型已解析自定义属性；不解释或补充缺失的工艺要求。",
        })
    return notes


def make_plan(analysis: PartAnalysis, template_config: Optional[Dict[str, Any]] = None) -> DrawingPlan:
    candidates = sorted(
        analysis.view_candidates,
        key=lambda item: item.get("score", 0),
        reverse=True,
    )
    best = candidates[0] if candidates else None
    plan = DrawingPlan(part_number=analysis.part_number)
    plan.sheet.update(_select_template(analysis, template_config))
    plan.notes.extend(_sourced_property_notes(analysis))
    if best:
        plan.views.append({
            "id": "BASE-1",
            "kind": "base_view_candidate",
            "orientation": best.get("orientation"),
            "score": best.get("score"),
            "confidence": best.get("confidence", 0.0),
            "status": "candidate_only",
            "reason": best.get("warning", "启发式候选"),
        })
    plan.views.append({
        "id": "SECTION-1",
        "kind": "section_view_candidate",
        "status": "candidate_only",
        "reason": "用于核验 Hole Wizard 孔的内部形状；剖切方向尚未由真实投影评估确定。",
    })
    for feature in analysis.features:
        if feature.get("type") == "HoleWzd":
            plan.hole_callouts.append({
                "feature_name": feature.get("name", ""),
                "source": "FeatureManager/HoleWzd",
                "status": "known_feature_pending_parameter_read",
                "known": ["存在结构化 Hole Wizard 特征"],
                "forbidden_inference": ["螺纹规格", "螺距", "有效深度", "公差"],
            })
    plan.uncertain_items.extend(analysis.uncertain_items)
    plan.uncertain_items.append(UncertainItem("PLAN-VIEW-001", "view_orientation", "主视方向尚未通过真实投影可见性验证。"))
    if plan.sheet.get("selection_status") != "rule_selected_pending_solidworks_open_validation":
        plan.uncertain_items.append(UncertainItem("PLAN-TEMPLATE-001", "drawing_template", "尚未提供可用 SolidWorks 工程图模板。"))
    else:
        plan.uncertain_items.append(UncertainItem(
            "PLAN-TEMPLATE-VERIFY-001",
            "drawing_template",
            "工程图模板已按规则选定，但尚未通过 SolidWorks NewDocument/SetupSheet 实际打开验证。",
            source=plan.sheet.get("template_path") or "not_available",
        ))
    return plan


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("analysis_file")
    parser.add_argument("output_file")
    parser.add_argument("--templates", default=None)
    args = parser.parse_args()
    data = json.loads(Path(args.analysis_file).read_text(encoding="utf-8"))
    analysis = PartAnalysis(**{key: value for key, value in data.items() if key in PartAnalysis.__dataclass_fields__})
    # JSON 中的 uncertain_items 重新转为 dataclass，保证序列化结构一致。
    analysis.uncertain_items = [UncertainItem(**item) for item in data.get("uncertain_items", [])]
    template_config = None
    if args.templates:
        template_config = json.loads(Path(args.templates).read_text(encoding="utf-8"))
    plan = make_plan(analysis, template_config)
    Path(args.output_file).write_text(plan.to_json(), encoding="utf-8")
    print(json.dumps({"status": "ok", "output": args.output_file}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
