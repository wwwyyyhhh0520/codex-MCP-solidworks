"""Validate sourced human decisions without mutating model-derived facts."""

from __future__ import annotations

import argparse
import json
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List


FORBIDDEN_SOURCES = {"", "feature_name_text", "filename", "visual_guess", "guess", "inferred"}


def _valid_source(value: Any) -> bool:
    return isinstance(value, str) and value.strip() not in FORBIDDEN_SOURCES


def validate_decision_request(request: Dict[str, Any]) -> Dict[str, Any]:
    items: List[Dict[str, Any]] = []
    accepted: List[Dict[str, Any]] = []
    for decision in request.get("decisions_required", []):
        decision_id = str(decision.get("id", "UNKNOWN"))
        value = decision.get("value")
        source = decision.get("source")
        if value is None or (isinstance(value, str) and not value.strip()):
            items.append({
                "id": f"META-{decision_id}",
                "severity": "error",
                "topic": decision.get("topic", "unknown"),
                "message": "人工决策未填写，不能解除对应出图阻塞项。",
            })
            continue
        if not _valid_source(source):
            items.append({
                "id": f"META-{decision_id}",
                "severity": "error",
                "topic": decision.get("topic", "unknown"),
                "message": "人工决策缺少可追溯 source，不能解除对应出图阻塞项。",
            })
            continue
        allowed_sources = set(decision.get("allowed_sources", []))
        if allowed_sources and source not in allowed_sources:
            items.append({
                "id": f"META-{decision_id}",
                "severity": "error",
                "topic": decision.get("topic", "unknown"),
                "message": "source 不在该决策允许的来源范围内。",
                "source": source,
            })
            continue
        accepted.append({
            "id": decision_id,
            "topic": decision.get("topic"),
            "value": value,
            "source": source,
            "target": decision.get("target"),
        })
        items.append({
            "id": f"META-{decision_id}",
            "severity": "info",
            "topic": decision.get("topic", "unknown"),
            "message": "人工决策与来源通过最小校验；仍须由相应计划模块消费。",
            "source": source,
        })
    passed = bool(request.get("decisions_required")) and not any(item["severity"] == "error" for item in items)
    return {
        "schema_version": "1.0",
        "mode": "manual_decision_validation",
        "created_at": datetime.now().isoformat(timespec="seconds"),
        "part_number": request.get("part_number"),
        "status": "passed" if passed else "blocked",
        "accepted_decisions": accepted,
        "items": items,
        "policy": "Validation does not modify read_only_model_facts or the drawing plan.",
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("decision_request_file")
    parser.add_argument("validation_output_file")
    args = parser.parse_args()
    request = json.loads(Path(args.decision_request_file).read_text(encoding="utf-8"))
    validation = validate_decision_request(request)
    Path(args.validation_output_file).write_text(
        json.dumps(validation, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(json.dumps({"status": validation["status"], "output": args.validation_output_file}, ensure_ascii=False))
    return 0 if validation["status"] == "passed" else 2


if __name__ == "__main__":
    raise SystemExit(main())
