"""Apply verified human decisions to a drawing plan without inventing intent."""

from __future__ import annotations

import argparse
import copy
import json
from pathlib import Path
from typing import Any, Dict


VALID_ORIENTATIONS = {"front", "top", "right", "left", "bottom", "back"}


def _accepted_by_id(validation: Dict[str, Any]) -> Dict[str, Dict[str, Any]]:
    return {item["id"]: item for item in validation.get("accepted_decisions", []) if item.get("id")}


def apply_decisions(plan: Dict[str, Any], validation: Dict[str, Any]) -> Dict[str, Any]:
    if validation.get("status") != "passed":
        raise ValueError("Only a passed manual decision validation may be applied.")

    result = copy.deepcopy(plan)
    decisions = _accepted_by_id(validation)
    receipt = []

    view = decisions.get("DEC-VIEW-001")
    if view:
        value = view.get("value")
        orientation = value.get("orientation") if isinstance(value, dict) else None
        if orientation not in VALID_ORIENTATIONS:
            raise ValueError("DEC-VIEW-001.value.orientation must be a supported orientation.")
        base = next((item for item in result.get("views", []) if item.get("id") == "BASE-1"), None)
        if base is None:
            raise ValueError("Drawing plan has no BASE-1 view.")
        base.update({
            "orientation": orientation,
            "status": "validated",
            "confidence": 1.0,
            "reason": "人工确认的主视方向。",
            "manual_decision_source": view["source"],
        })
        receipt.append({"id": view["id"], "source": view["source"], "applied_to": "views.BASE-1"})

    section = decisions.get("DEC-SECTION-001")
    if section:
        result.setdefault("section_view_assessment", {})["manual_decision"] = {
            "value": section["value"],
            "source": section["source"],
            "status": "recorded_pending_section_execution_validation",
        }
        receipt.append({"id": section["id"], "source": section["source"], "applied_to": "section_view_assessment.manual_decision"})

    hole_decisions = [item for item in decisions.values() if str(item.get("id", "")).startswith("DEC-HOLE-TOL-")]
    for hole in hole_decisions:
        value = hole.get("value")
        if not isinstance(value, dict) or value.get("mode") not in {"specific", "general_linear"}:
            raise ValueError("DEC-HOLE-TOL-001.value.mode must be specific or general_linear.")
        if value["mode"] == "specific" and not value.get("tolerance"):
            raise ValueError("A specific hole tolerance requires value.tolerance.")
        if value["mode"] == "general_linear" and value.get("explicitly_applied") is not True:
            raise ValueError("General tolerance requires explicitly_applied=true.")
        target_group_id = (hole.get("target") or {}).get("callout_group_id")
        matching_candidates = [
            candidate for candidate in result.get("hole_callout_candidates", [])
            if target_group_id is None or candidate.get("id") == target_group_id
        ]
        for candidate in matching_candidates:
            candidate["tolerance_decision"] = value
            candidate["tolerance_source"] = hole["source"]
            candidate["status"] = "tolerance_confirmed_pending_drawing_anchor"
            candidate["rendering_allowed"] = False
            candidate["blocking_reasons"] = [
                reason for reason in candidate.get("blocking_reasons", []) if "公差来源" not in reason
            ]
        for callout in result.get("hole_callouts", []):
            if target_group_id is not None:
                continue
            parameters = callout.get("reliable_parameters", {})
            if not parameters.get("diameter"):
                continue
            callout["tolerance_decision"] = value
            callout["tolerance_source"] = hole["source"]
            callout["status"] = "geometry_complete_tolerance_confirmed"
            callout["callout_allowed"] = True
            callout["missing_reliable_parameters"] = [
                item for item in callout.get("missing_reliable_parameters", []) if item != "tolerance"
            ]
        applied_to = f"hole_callout_candidates.{target_group_id}" if target_group_id else "hole_callouts.*.tolerance_decision"
        receipt.append({"id": hole["id"], "source": hole["source"], "applied_to": applied_to})

    template = decisions.get("DEC-TEMPLATE-NOTE-001")
    if template:
        value = template.get("value")
        if not isinstance(value, dict) or value.get("scope") not in {"none", "named_requirements"}:
            raise ValueError("DEC-TEMPLATE-NOTE-001.value.scope must be none or named_requirements.")
        if value["scope"] == "named_requirements" and not value.get("requirement_ids"):
            raise ValueError("named_requirements requires non-empty requirement_ids.")
        result.setdefault("template_technical_requirements", {
            "selection": value,
            "source": template["source"],
            "status": "scope_confirmed_not_copied",
            "note": "本阶段仅记录适用范围；具体条文须由模板解析结果逐项匹配后再写入。",
        })
        receipt.append({"id": template["id"], "source": template["source"], "applied_to": "template_technical_requirements"})

    result["manual_decision_receipt"] = receipt
    result["status"] = "needs_review_after_manual_decisions"
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("plan_file")
    parser.add_argument("validation_file")
    parser.add_argument("output_plan_file")
    args = parser.parse_args()
    plan = json.loads(Path(args.plan_file).read_text(encoding="utf-8"))
    validation = json.loads(Path(args.validation_file).read_text(encoding="utf-8"))
    updated = apply_decisions(plan, validation)
    Path(args.output_plan_file).write_text(json.dumps(updated, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"status": updated["status"], "output": args.output_plan_file}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
