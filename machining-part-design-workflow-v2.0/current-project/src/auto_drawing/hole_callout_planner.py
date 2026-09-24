"""Build conservative hole-callout candidates without inferring pattern quantity."""

from __future__ import annotations

import argparse
import json
from collections import defaultdict
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List, Tuple


def _value(value: Any) -> Any:
    return value.get("value_mm") if isinstance(value, dict) else value


def _signature(hole: Dict[str, Any]) -> Tuple[Any, ...]:
    params = hole.get("reliable_parameters", {})
    return (
        params.get("thread_specification"),
        params.get("thread_depth"),
        params.get("end_condition"),
        _value(params.get("diameter")),
    )


def plan_hole_callouts(holes: Dict[str, Any]) -> Dict[str, Any]:
    grouped: Dict[Tuple[Any, ...], List[Dict[str, Any]]] = defaultdict(list)
    for hole in holes.get("holes", []):
        grouped[_signature(hole)].append(hole)

    candidates = []
    for index, members in enumerate(grouped.values(), start=1):
        representative = members[0]
        params = representative.get("reliable_parameters", {})
        needs_tolerance = any("tolerance" in item.get("missing_reliable_parameters", []) for item in members)
        candidates.append({
            "id": f"CALLOUT-GROUP-{index:03d}",
            "kind": "threaded_hole" if params.get("thread_specification") else "hole",
            "member_feature_ids": [item.get("id") for item in members],
            "member_feature_names": [item.get("feature_name") for item in members],
            "feature_definition_count": len(members),
            "physical_quantity": None,
            "physical_quantity_status": "not_inferred_from_feature_tree",
            "reliable_parameters": {
                key: params[key]
                for key in ("thread_specification", "thread_depth", "end_condition", "thread_class", "diameter", "depth")
                if key in params
            },
            "parameter_sources": {
                "thread_specification": "IWizardHoleFeatureData2.FastenerSize",
                "thread_depth": "IWizardHoleFeatureData2.ThreadDepth",
                "end_condition": "IWizardHoleFeatureData2.EndCondition",
                "diameter": "HoleWzd.GetFaces().GetSurface().CylinderParams[6]",
            },
            "status": "pending_tolerance_and_drawing_anchor" if needs_tolerance else "pending_drawing_anchor",
            "rendering_allowed": False,
            "blocking_reasons": [
                *(["缺少可追溯公差来源。"] if needs_tolerance else []),
                "未验证模型孔特征与工程图实体的标注锚点对应关系。",
                "镜向或阵列特征尚未展开为实际孔数量，禁止生成 nX 数量前缀。",
            ],
        })

    return {
        "schema_version": "1.0",
        "mode": "hole_callout_candidate_planning",
        "created_at": datetime.now().isoformat(timespec="seconds"),
        "part_number": holes.get("part_number"),
        "status": "blocked" if any(not item["rendering_allowed"] for item in candidates) else "ready",
        "callout_candidates": candidates,
        "policy": {
            "allowed_sources": ["Hole Wizard definition", "hole topology", "PMI", "approved human metadata"],
            "must_not_infer": ["physical_quantity", "thread_pitch", "tolerance", "fit", "drawing_anchor"],
        },
    }


def apply_candidates(plan: Dict[str, Any], report: Dict[str, Any], source: str) -> Dict[str, Any]:
    plan["hole_callout_candidates"] = report.get("callout_candidates", [])
    plan["hole_callout_candidate_source"] = source
    return plan


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("hole_analysis_file")
    parser.add_argument("output_file")
    parser.add_argument("--plan-file", default=None)
    parser.add_argument("--plan-output-file", default=None)
    args = parser.parse_args()
    holes = json.loads(Path(args.hole_analysis_file).read_text(encoding="utf-8"))
    report = plan_hole_callouts(holes)
    Path(args.output_file).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    if args.plan_file and args.plan_output_file:
        plan = json.loads(Path(args.plan_file).read_text(encoding="utf-8"))
        Path(args.plan_output_file).write_text(
            json.dumps(apply_candidates(plan, report, args.output_file), ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
    print(json.dumps({"status": report["status"], "candidate_count": len(report["callout_candidates"]), "output": args.output_file}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
