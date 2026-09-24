from __future__ import annotations

import json
from collections import defaultdict
from pathlib import Path


LOCATION_PREFIXES = ("UPPER_END", "LOWER_END")


def _feature_types(semantics: dict) -> dict[str, str]:
    result: dict[str, str] = {}
    for feature in semantics.get("features") or []:
        feature_id = str(feature.get("feature_id") or "")
        if feature_id:
            result.setdefault(feature_id, str(feature.get("feature_type") or ""))
    return result


def _dimension_axis(dimension: dict, report: dict) -> str | None:
    dim_id = str(dimension.get("dimension_id") or "")
    for axis in ("X", "Y", "Z"):
        if dim_id.endswith(f"_{axis}"):
            return axis
    source = dimension.get("source_feature_id")
    role = dimension.get("semantic_type")
    matches = [
        item for item in (report.get("semantic_feature_dimensions") or {}).get("created") or []
        if item.get("feature_name") == source and item.get("dimension_role") == role
    ]
    axes = {str(item.get("axis") or "").upper() for item in matches}
    return next(iter(axes)) if len(axes) == 1 and next(iter(axes)) in {"X", "Y", "Z"} else None


def _intent(dimension: dict, feature_types: dict[str, str]) -> str:
    if str(dimension.get("semantic_type") or "").lower() == "overall":
        return "OVERALL_SIZE"
    feature_type = feature_types.get(str(dimension.get("source_feature_id") or ""), "")
    if feature_type in {"ordinary_hole", "HoleWzd"}:
        return "HOLE_LOCATION"
    return "FEATURE_LOCATION"


def _reference_side(dimension: dict) -> str | None:
    semantic_type = str(dimension.get("semantic_type") or "")
    if semantic_type.startswith("UPPER_END"):
        return "MAX"
    if semantic_type.startswith("LOWER_END"):
        return "MIN"
    return None


def _purpose_family(dimension: dict) -> str:
    semantic_type = str(dimension.get("semantic_type") or "")
    if semantic_type.startswith("UPPER_END"):
        return "UPPER_END"
    if semantic_type.startswith("LOWER_END"):
        return "LOWER_END"
    return semantic_type


def _layout(intent: str, axis: str | None, has_owner: bool) -> dict | None:
    if not has_owner or axis is None:
        return None
    rank = {
        "FEATURE_SIZE": 1, "FEATURE_LOCATION": 2, "HOLE_LOCATION": 2,
        "PATTERN_SPACING": 3, "OVERALL_SIZE": 4, "STEP_LOCATION": 2,
    }.get(intent, 2)
    side = {"X": "BOTTOM", "Y": "LEFT", "Z": "RIGHT"}[axis]
    return {"side": side, "lane": rank, "distance_rank": rank, "outside_view": True}


def _datum(axis: str | None, side: str | None, semantics: dict) -> dict | None:
    bbox = semantics.get("bounding_box") or {}
    if axis is None or side is None or not bbox.get("available"):
        return None
    bounds = {"X": ("min_x_mm", "max_x_mm"), "Y": ("min_y_mm", "max_y_mm"), "Z": ("min_z_mm", "max_z_mm")}
    key = bounds[axis][0 if side == "MIN" else 1]
    if bbox.get(key) is None:
        return None
    return {"type": "MODEL_EXTENT", "axis": axis, "side": side, "source": "model_semantics.bounding_box"}


def build_drawing_plan_quality(plan: dict, semantics: dict, report: dict) -> dict:
    """Create a shadow-only quality plan from existing contract and release facts."""
    feature_types = _feature_types(semantics)
    buckets: dict[tuple, list[dict]] = defaultdict(list)
    unresolved: list[dict] = []
    for dimension in plan.get("dimensions") or []:
        intent = _intent(dimension, feature_types)
        axis = _dimension_axis(dimension, report)
        owner = dimension.get("owner_view_id")
        family = _purpose_family(dimension)
        if not owner or not axis:
            unresolved.append({
                "dimension_id": dimension.get("dimension_id"), "reason": "OWNER_VIEW_OR_MODEL_AXIS_UNAVAILABLE",
                "owner_view_id": owner, "axis": axis,
            })
        buckets[(owner, axis, intent, family)].append(dimension)

    groups = []
    for index, ((owner, axis, intent, family), members) in enumerate(sorted(buckets.items(), key=lambda item: str(item[0])), start=1):
        ids = [str(item.get("dimension_id")) for item in members]
        side = _reference_side(members[0]) if intent in {"FEATURE_LOCATION", "HOLE_LOCATION"} else None
        datum_ref = _datum(axis, side, semantics)
        if intent == "OVERALL_SIZE":
            strategy, reason = "DIRECT", "overall envelope dimension"
        elif not owner or not axis:
            strategy, reason = "UNRESOLVED", "owner view or model axis is not available in existing contracts"
        elif len(members) >= 2 and datum_ref:
            strategy, reason = "BASELINE", "multiple location dimensions share an existing model-extent datum"
        elif len(members) == 1 and datum_ref:
            strategy, reason = "DIRECT", "single location dimension from an existing model-extent datum"
        else:
            strategy, reason = "UNRESOLVED", "no stable existing datum candidate"
        if strategy == "UNRESOLVED":
            unresolved.append({"group_id": f"DG{index:03d}", "reason": reason, "member_dimension_ids": ids})
        groups.append({
            "group_id": f"DG{index:03d}", "owner_view_id": owner, "axis": axis,
            "intent": intent, "semantic_purpose": family,
            "related_feature_ids": sorted({str(item.get("source_feature_id")) for item in members if item.get("source_feature_id")}),
            "datum_ref": datum_ref, "strategy": strategy, "strategy_reason": reason,
            "member_dimension_ids": ids, "layout": _layout(intent, axis, bool(owner)),
        })
    return {
        "schema_version": "DRAWING_PLAN_QUALITY_V1",
        "source_plan_schema": plan.get("schema_version"),
        "dimension_groups": groups,
        "legacy_chain_candidates": [],
        "unresolved": unresolved,
        "summary": {
            "dimension_group_count": len(groups),
            "strategy_counts": {strategy: sum(item["strategy"] == strategy for item in groups) for strategy in ("BASELINE", "ORDINATE", "DIRECT", "CHAIN_ALLOWED", "UNRESOLVED")},
            "layout_lane_assignments": sum(item.get("layout") is not None for item in groups),
            "legacy_chain_candidate_note": "No legacy chain candidate is asserted without verified endpoint binding from the current evidence path.",
        },
    }


def write_drawing_plan_quality(path: Path, quality_plan: dict) -> Path:
    path.write_text(json.dumps(quality_plan, ensure_ascii=False, indent=2), encoding="utf-8")
    return path
