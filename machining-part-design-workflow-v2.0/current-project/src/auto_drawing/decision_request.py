"""Create a minimal human decision request from unresolved drawing blockers."""

from __future__ import annotations

import argparse
import json
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List


def _group_tolerance_decisions(candidates: Dict[str, Any] | None) -> List[Dict[str, Any]]:
    if not candidates:
        return []
    result = []
    for index, candidate in enumerate(candidates.get("callout_candidates", []), start=1):
        params = candidate.get("reliable_parameters", {})
        specification = params.get("thread_specification") or "孔特征"
        result.append({
            "id": f"DEC-HOLE-TOL-{index:03d}",
            "topic": "hole_tolerance",
            "question": f"{specification} 孔标注候选组是否有专用公差；若无，是否明确采用已选模板的一般线性公差？",
            "target": {
                "callout_group_id": candidate.get("id"),
                "feature_definition_count": candidate.get("feature_definition_count"),
                "physical_quantity": None,
                "reliable_parameters": params,
            },
            "allowed_sources": ["客户图纸", "检验规范", "PLM/ERP", "经授权的项目标准", "人工确认"],
            "value": None,
            "source": None,
        })
    return result


def create_request(
    plan: Dict[str, Any],
    holes: Dict[str, Any],
    template_policy: Dict[str, Any],
    callout_candidates: Dict[str, Any] | None = None,
    section_assessment: Dict[str, Any] | None = None,
) -> Dict[str, Any]:
    hole = next(iter(holes.get("holes", [])), {})
    parameters = hole.get("reliable_parameters", {})
    return {
        "schema_version": "1.0",
        "mode": "minimal_human_decision_request",
        "created_at": datetime.now().isoformat(timespec="seconds"),
        "part_number": plan.get("part_number"),
        "read_only_model_facts": {
            "hole_feature": hole.get("feature_name"),
            "diameter": parameters.get("diameter"),
            "axial_length": parameters.get("depth"),
            "material_note": next((item.get("text") for item in plan.get("notes", []) if item.get("kind") == "material"), None),
            "surface_treatment_note": next((item.get("text") for item in plan.get("notes", []) if item.get("kind") == "surface_treatment"), None),
            "geometry_preferred_view": next((item.get("orientation") for item in plan.get("views", []) if item.get("id") == "BASE-1"), None),
        },
        "decisions_required": [
            *(_group_tolerance_decisions(callout_candidates) or [{
                "id": "DEC-HOLE-TOL-001",
                "topic": "hole_tolerance",
                "question": "该 Hole Wizard 间隙孔是否有专用公差；若无，是否明确采用已选模板的一般线性公差？",
                "allowed_sources": ["客户图纸", "检验规范", "PLM/ERP", "经授权的项目标准", "人工确认"],
                "value": None,
                "source": None,
            }]),
            {
                "id": "DEC-VIEW-001",
                "topic": "primary_view",
                "question": "前视为几何投影优选候选，是否确认其为工程图主视图，或指定另一方向/剖视表达？",
                "allowed_sources": ["人工确认", "客户图纸", "工艺规范"],
                "value": None,
                "source": None,
            },
            *([{
                "id": "DEC-SECTION-001",
                "topic": "section_detail_view",
                "question": "剖视候选缺少可靠剖切依据；是否需要剖视或局部视图，并提供剖切方向/边界依据？",
                "allowed_sources": ["人工确认", "客户图纸", "工艺规范"],
                "value": None,
                "source": None,
            }] if (section_assessment or {}).get("status") == "candidate_requires_human_cutting_basis" else []),
            {
                "id": "DEC-TEMPLATE-NOTE-001",
                "topic": "template_technical_requirements",
                "question": "模板中的通用、焊件、钣金技术要求应采用哪一套，或是否全部不带入本零件？",
                "allowed_sources": ["人工确认", "项目标准", "客户图纸"],
                "value": None,
                "source": None,
            },
        ],
        "template_tolerance_candidate": template_policy.get("linear_general_tolerance_bands", []),
        "policy": {
            "must_not_edit": ["read_only_model_facts"],
            "must_provide_source_for_each_decision": True,
            "forbidden_sources": ["feature_name_text", "filename", "visual_guess", "guess", "inferred"],
        },
    }


def render_markdown(request: Dict[str, Any]) -> str:
    lines = [f"# 最小人工确认请求：{request.get('part_number')}", "", "## 已确认模型事实", ""]
    for key, value in request.get("read_only_model_facts", {}).items():
        lines.append(f"- `{key}`：{value}")
    lines.extend(["", "## 需确认项", ""])
    for item in request.get("decisions_required", []):
        lines.append(f"- `{item.get('id')}` {item.get('question')}")
        lines.append("  - 需要填写 value 与可追溯 source。")
    lines.append("")
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("plan_file")
    parser.add_argument("hole_analysis_file")
    parser.add_argument("template_policy_file")
    parser.add_argument("json_output")
    parser.add_argument("markdown_output")
    parser.add_argument("--callout-candidates-file", default=None)
    parser.add_argument("--section-assessment-file", default=None)
    args = parser.parse_args()
    plan = json.loads(Path(args.plan_file).read_text(encoding="utf-8"))
    holes = json.loads(Path(args.hole_analysis_file).read_text(encoding="utf-8"))
    policy = json.loads(Path(args.template_policy_file).read_text(encoding="utf-8"))
    candidates = json.loads(Path(args.callout_candidates_file).read_text(encoding="utf-8")) if args.callout_candidates_file else None
    section = json.loads(Path(args.section_assessment_file).read_text(encoding="utf-8")) if args.section_assessment_file else None
    request = create_request(plan, holes, policy, candidates, section)
    Path(args.json_output).write_text(json.dumps(request, ensure_ascii=False, indent=2), encoding="utf-8")
    Path(args.markdown_output).write_text(render_markdown(request), encoding="utf-8")
    print(json.dumps({"status": "needs_human_decision", "json_output": args.json_output}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
