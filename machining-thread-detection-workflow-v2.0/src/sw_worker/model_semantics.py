from __future__ import annotations

import json
import math
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Any


_FORBIDDEN_KEYS = {
    "preferred_view", "annotation_position", "annotation_positions",
    "dimension_placement", "drawing_layout", "drawing_decision",
    "view_role", "callout_point_m", "placement_plan",
    "projection_policy", "annotation_status", "annotation_reason",
    "drawing", "view", "views", "annotation", "annotations",
    "callout_point", "leader_position", "dimension_position",
}


def _clean(value: Any) -> Any:
    if isinstance(value, dict):
        return {str(k): _clean(v) for k, v in value.items()
                if str(k).casefold() not in _FORBIDDEN_KEYS}
    if isinstance(value, (list, tuple)):
        return [_clean(item) for item in value]
    if isinstance(value, (str, int, float, bool)) or value is None:
        return value
    return str(value)


def _points_mm(row: dict) -> list[list[float]] | None:
    """Convert V10R's model-local meter points at the contract boundary."""
    raw = row.get("points_part_local_m") or row.get("sketch_point_part_local_m")
    if not isinstance(raw, (list, tuple)):
        return None
    points = []
    for point in raw:
        if isinstance(point, (list, tuple)) and len(point) >= 3:
            points.append([round(float(point[i]) * 1000.0, 6) for i in range(3)])
    return points or None


def _number_mm(row: dict, keys: tuple[str, ...]) -> float | None:
    for key in keys:
        value = row.get(key)
        try:
            if value is not None and value != "":
                number = float(value)
                return round(number * (1000.0 if key.endswith("_m") else 1.0), 6)
        except (TypeError, ValueError):
            continue
    return None


def _hole_facts(row: dict) -> dict:
    """Expose factual hole fields without making a drawing recommendation."""
    facts: dict[str, Any] = {}
    diameter = _number_mm(row, ("diameter_mm", "hole_diameter_mm", "hole_diameter_m", "thread_diameter_m"))
    if diameter is None:
        match = re.search(r"(?:Φ|Ø)\s*([0-9]+(?:\.[0-9]+)?)", str(row.get("hole_spec") or ""))
        if match:
            diameter = float(match.group(1))
    if diameter is not None:
        facts["diameter_mm"] = diameter
    thread = row.get("thread") or row.get("thread_spec") or row.get("fastener_size")
    if thread:
        facts["thread"] = str(thread)
    kind = row.get("hole_kind") or row.get("feature_type")
    if kind:
        facts["hole_kind"] = str(kind)
    axis = row.get("axis_unit_vector")
    if isinstance(axis, (list, tuple)) and len(axis) >= 3:
        values = [float(axis[i]) for i in range(3)]
        norm = math.sqrt(sum(value * value for value in values))
        if norm > 1e-12:
            facts["axis_unit_vector"] = [round(value / norm, 9) for value in values]
            facts["axis_alignment_xyz"] = [round(abs(value) / norm, 9) for value in values]
    return facts


def _feature(row: dict, index: int, source: str) -> dict:
    # This fallback ID is retained for compatibility; it is not a long-term
    # stable identity and is intentionally not redesigned in Phase 2.
    feature_id = str(row.get("feature_id") or row.get("feature_name")
                     or row.get("source_feature") or f"{source}-{index:03d}")
    feature_type = str(row.get("feature_type") or row.get("hole_kind")
                       or row.get("type") or "unknown")
    payload = _clean(dict(row))
    payload.pop("feature_id", None)
    payload.pop("feature_type", None)
    payload.pop("points_part_local_m", None)
    payload.pop("sketch_point_part_local_m", None)
    points = _points_mm(row)
    if points:
        payload["points_part_local_mm"] = points
    payload.update(_hole_facts(row))
    return {
        "feature_id": feature_id,
        "feature_type": feature_type,
        "source": {"adapter": source, "provenance": "V10R_existing_semantic_result"},
        "data": payload,
    }


@dataclass(frozen=True)
class ModelSemantics:
    schema_version: str
    model_path: str
    model_identity: dict
    unit: str
    bounding_box: dict
    features: list[dict]

    def to_dict(self) -> dict:
        return _clean({
            "schema_version": self.schema_version,
            "model_path": self.model_path,
            "model_identity": self.model_identity,
            "unit": self.unit,
            "bounding_box": self.bounding_box,
            "features": self.features,
        })


def build_model_semantics(model_path: Path, box: dict, holewizard_rows: list[dict],
                          sketch_rows: list[dict], feature_tree: dict) -> ModelSemantics:
    features: list[dict] = []
    for source, rows in (("holewizard", holewizard_rows), ("sketch_geometry", sketch_rows)):
        for index, row in enumerate(rows or [], start=1):
            if isinstance(row, dict):
                features.append(_feature(row, index, source))
    for index, row in enumerate(feature_tree.get("records") or [], start=1):
        if isinstance(row, dict):
            features.append(_feature(row, index, "feature_tree"))
    resolved = model_path.expanduser().resolve()
    return ModelSemantics(
        schema_version="MODEL_SEMANTICS_V2",
        model_path=str(resolved),
        model_identity={"file_name": resolved.name, "file_stem": resolved.stem,
                        "extension": resolved.suffix.casefold()},
        unit="MM", bounding_box=_clean(box), features=features,
    )


def write_model_semantics(path: Path, semantics: ModelSemantics) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(semantics.to_dict(), ensure_ascii=False, indent=2), encoding="utf-8")
    return path
