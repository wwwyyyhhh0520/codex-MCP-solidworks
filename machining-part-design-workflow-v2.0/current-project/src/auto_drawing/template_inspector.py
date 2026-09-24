"""Extract explicit template tolerance text as candidates, never auto-apply it."""

from __future__ import annotations

import argparse
import json
import re
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List


BAND_RE = re.compile(
    r"(?P<low>(?:＞|>)?\s*\d+(?:\.\d+)?)\s*-\s*(?P<high>\d+(?:\.\d+)?)\s*mm\s*±\s*(?P<tolerance>\d+(?:\.\d+)?)",
    re.IGNORECASE,
)


def _number(text: str) -> float:
    return float(text.replace("＞", "").replace(">", "").strip())


def inspect_template(scan: Dict[str, Any]) -> Dict[str, Any]:
    drawing = scan.get("drawing_probe") or {}
    notes = (drawing.get("template_notes") or {}).get("notes") or []
    bands: List[Dict[str, Any]] = []
    source_texts: List[str] = []
    for note in notes:
        text = str(note.get("text") or "")
        if "未注" in text or "±" in text:
            source_texts.append(text)
        for match in BAND_RE.finditer(text):
            bands.append({
                "lower_mm": _number(match.group("low")),
                "lower_inclusive": not match.group("low").lstrip().startswith(("＞", ">")),
                "upper_mm": _number(match.group("high")),
                "tolerance_plus_minus_mm": float(match.group("tolerance")),
                "source_text": match.group(0),
            })
    return {
        "schema_version": "1.0",
        "mode": "drawing_template_tolerance_inspection",
        "created_at": datetime.now().isoformat(timespec="seconds"),
        "template_path": drawing.get("template_path"),
        "status": "candidate_requires_scope_confirmation" if bands else "no_linear_tolerance_rule_found",
        "linear_general_tolerance_bands": bands,
        "source_notes": source_texts,
        "policy": {
            "may_apply_to": "明确属于未注线性尺寸、且项目规则已确认采用该模板一般公差的尺寸。",
            "must_not_auto_apply_to": ["Hole Wizard 标准孔", "螺纹", "配合尺寸", "已有专用公差的尺寸", "GD&T", "粗糙度"],
            "reason": "模板一般公差的适用范围不能从几何或特征名称单独确认。",
        },
    }


def enrich_plan(plan: Dict[str, Any], inspection: Dict[str, Any], source: str) -> Dict[str, Any]:
    if inspection.get("status") != "candidate_requires_scope_confirmation":
        return plan
    plan.setdefault("tolerance_rules", [])
    plan["tolerance_rules"] = [{
        "id": "TOL-TEMPLATE-001",
        "kind": "general_linear_tolerance_candidate",
        "status": "requires_scope_confirmation",
        "source": source,
        "bands": inspection.get("linear_general_tolerance_bands", []),
        "must_not_auto_apply_to": inspection.get("policy", {}).get("must_not_auto_apply_to", []),
    }]
    return plan


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("template_scan_file")
    parser.add_argument("output_file")
    parser.add_argument("--plan-file", default=None)
    parser.add_argument("--plan-output-file", default=None)
    args = parser.parse_args()
    scan = json.loads(Path(args.template_scan_file).read_text(encoding="utf-8"))
    inspection = inspect_template(scan)
    Path(args.output_file).write_text(json.dumps(inspection, ensure_ascii=False, indent=2), encoding="utf-8")
    if args.plan_file and args.plan_output_file:
        plan = json.loads(Path(args.plan_file).read_text(encoding="utf-8"))
        Path(args.plan_output_file).write_text(
            json.dumps(enrich_plan(plan, inspection, args.output_file), ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
    print(json.dumps({"status": inspection["status"], "output": args.output_file}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
