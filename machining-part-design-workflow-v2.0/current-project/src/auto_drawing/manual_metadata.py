"""Manual metadata validation and merge helpers."""

from __future__ import annotations

import argparse
import json
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List


FORBIDDEN_SOURCES = {"", "feature_name_text", "filename", "visual_guess", "guess", "inferred"}


def _valid_source(value: Any) -> bool:
    return isinstance(value, str) and value.strip() not in FORBIDDEN_SOURCES


def _has_hole_callout_basis(parameters: Dict[str, Any]) -> bool:
    return bool(parameters.get("diameter") or parameters.get("thread_specification"))


def validate_metadata(metadata: Dict[str, Any]) -> Dict[str, Any]:
    items: List[Dict[str, Any]] = []
    accepted_holes: List[Dict[str, Any]] = []
    for index, hole in enumerate(metadata.get("hole_callouts", [])):
        source = hole.get("source") or metadata.get("source_document")
        parameters = hole.get("parameters") or {}
        if not _valid_source(source):
            items.append({
                "id": f"META-HOLE-{index + 1:03d}",
                "severity": "error",
                "topic": "hole_callouts",
                "message": "孔人工元数据缺少可靠 source，不能合并到出图计划。",
                "feature_name": hole.get("feature_name"),
            })
            continue
        if not _has_hole_callout_basis(parameters) and not hole.get("callout_text"):
            items.append({
                "id": f"META-HOLE-{index + 1:03d}",
                "severity": "error",
                "topic": "hole_callouts",
                "message": "孔人工元数据缺少 diameter、thread_specification 或 callout_text，不能生成孔标注。",
                "feature_name": hole.get("feature_name"),
            })
            continue
        accepted = dict(hole)
        accepted["source"] = source
        accepted_holes.append(accepted)
        items.append({
            "id": f"META-HOLE-{index + 1:03d}",
            "severity": "info",
            "topic": "hole_callouts",
            "message": "孔人工元数据通过最小校验，可合并为计划来源。",
            "feature_name": hole.get("feature_name"),
        })
    return {
        "schema_version": "1.0",
        "mode": "manual_metadata_validation",
        "created_at": datetime.now().isoformat(timespec="seconds"),
        "part_number": metadata.get("part_number"),
        "status": "passed" if not any(item["severity"] == "error" for item in items) else "blocked",
        "accepted_hole_callouts": accepted_holes,
        "items": items,
    }


def merge_metadata_into_plan(plan: Dict[str, Any], validation: Dict[str, Any], metadata_source: str) -> Dict[str, Any]:
    accepted_by_name = {item.get("feature_name"): item for item in validation.get("accepted_hole_callouts", [])}
    for callout in plan.get("hole_callouts", []):
        accepted = accepted_by_name.get(callout.get("feature_name"))
        if not accepted:
            continue
        callout["status"] = "planned_from_human_metadata"
        callout["callout_allowed"] = True
        callout["manual_metadata_source"] = metadata_source
        callout["source"] = accepted.get("source")
        callout["parameters"] = accepted.get("parameters", {})
        if accepted.get("callout_text"):
            callout["callout_text"] = accepted["callout_text"]
    return plan


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("metadata_file")
    parser.add_argument("validation_output_file")
    parser.add_argument("--plan-file", default=None)
    parser.add_argument("--plan-output-file", default=None)
    args = parser.parse_args()
    metadata = json.loads(Path(args.metadata_file).read_text(encoding="utf-8"))
    validation = validate_metadata(metadata)
    Path(args.validation_output_file).write_text(json.dumps(validation, ensure_ascii=False, indent=2), encoding="utf-8")
    if args.plan_file and args.plan_output_file and validation["status"] == "passed":
        plan = json.loads(Path(args.plan_file).read_text(encoding="utf-8"))
        plan = merge_metadata_into_plan(plan, validation, args.metadata_file)
        Path(args.plan_output_file).write_text(json.dumps(plan, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({
        "status": validation["status"],
        "accepted_hole_callout_count": len(validation["accepted_hole_callouts"]),
        "output": args.validation_output_file,
    }, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
