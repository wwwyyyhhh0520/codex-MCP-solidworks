from __future__ import annotations

import json
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any


def _json_value(value: Any) -> Any:
    if isinstance(value, dict):
        return {str(key): _json_value(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [_json_value(item) for item in value]
    if isinstance(value, (str, int, float, bool)) or value is None:
        return value
    return str(value)


@dataclass
class SheetPlan:
    unit: str
    width_mm: float
    height_mm: float
    scale: float
    scale_label: str


@dataclass
class ViewPlan:
    view_id: str
    role: str
    required: bool
    purpose: list[str] = field(default_factory=list)
    purpose_status: str = "UNRESOLVED"


@dataclass
class DimensionPlan:
    dimension_id: str
    semantic_type: str
    source_feature_id: str | None
    owner_view_id: str | None
    required: bool
    status: str = "PLANNED"


@dataclass
class AnnotationPlan:
    annotation_id: str
    semantic_type: str
    source_feature_id: str | None
    owner_view_id: str | None
    required: bool
    status: str = "PLANNED"


@dataclass
class DrawingPlan:
    schema_version: str
    model_ref: dict
    sheet: SheetPlan
    views: list[ViewPlan]
    dimensions: list[DimensionPlan]
    annotations: list[AnnotationPlan]
    unresolved: list[dict]

    def to_dict(self) -> dict:
        return _json_value(asdict(self))


def _role(value: Any) -> str:
    text = str(value or "")
    return text.lstrip("*").lower()


def _view_ids(view_specs: list[dict], view_results: list[dict]) -> dict[str, str]:
    result: dict[str, str] = {}
    for index, item in enumerate(view_specs or [], start=1):
        name = str(item.get("view_name") or "")
        result[_role(name)] = f"V{index:03d}"
    for index, item in enumerate(view_results or [], start=1):
        name = str(item.get("view_name") or "")
        if name and _role(name) not in result:
            result[_role(name)] = f"V{index:03d}"
    return result


def _owner_id(role: Any, ids: dict[str, str]) -> str | None:
    value = _role(role)
    aliases = {"primary": "front", "top_view": "top", "isometric": "isometric"}
    return ids.get(aliases.get(value, value))


def build_drawing_plan(
    model_path: Path,
    sheet_w: float,
    sheet_h: float,
    scale: float,
    scale_label: str,
    view_specs: list[dict],
    view_results: list[dict],
    dimension_definitions: list[dict],
    semantic_dimensions: dict,
    feature_dimension_candidates: list[dict],
    feature_dimensions: dict,
    native_hole_binding: dict,
    unresolved: list[Any] | None = None,
) -> DrawingPlan:
    ids = _view_ids(view_specs, view_results)
    views: list[ViewPlan] = []
    for index, spec in enumerate(view_specs or [], start=1):
        name = str(spec.get("view_name") or "")
        role = _role(name)
        result = next((item for item in view_results or [] if str(item.get("view_name") or "") == name), {})
        required = bool(result.get("created", True))
        purpose: list[str] = []
        for definition in dimension_definitions or []:
            if _role(definition.get("preferred_view_role")) == role:
                purpose.append(f"semantic_dimension:{definition.get('id')}")
        for record in native_hole_binding.get("records") or []:
            if _role(record.get("view_role")) == role:
                purpose.append(f"hole_group:{record.get('feature_name') or record.get('annotation_id')}")
        purpose = list(dict.fromkeys(item for item in purpose if not item.endswith(":None")))
        views.append(ViewPlan(
            view_id=f"V{index:03d}", role=role, required=required,
            purpose=purpose, purpose_status="RESOLVED" if purpose else "UNRESOLVED",
        ))

    dimensions: list[DimensionPlan] = []
    for definition in dimension_definitions or []:
        dimensions.append(DimensionPlan(
            dimension_id=str(definition.get("id") or f"D{len(dimensions)+1:03d}"),
            semantic_type=str(definition.get("category") or definition.get("id") or "dimension"),
            source_feature_id=None,
            owner_view_id=_owner_id(definition.get("preferred_view_role"), ids),
            required=True,
            status="CREATED" if str(definition.get("id")) in {
                str(item.get("definition_id")) for item in semantic_dimensions.get("created") or []
            } else "PLANNED",
        ))
    for index, candidate in enumerate(feature_dimension_candidates or [], start=1):
        dimensions.append(DimensionPlan(
            dimension_id=f"FD{index:03d}",
            semantic_type=str(candidate.get("dimension_role") or "feature_distance"),
            source_feature_id=str(candidate.get("feature_name") or "") or None,
            owner_view_id=_owner_id(candidate.get("preferred_view_role"), ids),
            required=True,
            status="CREATED" if candidate in (feature_dimensions.get("created") or []) else "CANDIDATE",
        ))

    annotations: list[AnnotationPlan] = []
    for index, record in enumerate(native_hole_binding.get("records") or [], start=1):
        annotations.append(AnnotationPlan(
            annotation_id=str(record.get("annotation_id") or f"A{index:03d}"),
            semantic_type="hole_callout",
            source_feature_id=str(record.get("feature_name") or "") or None,
            owner_view_id=_owner_id(record.get("view_role"), ids),
            required=True,
            status="BOUND" if record.get("hole_callout_bound") else str(record.get("status") or "UNRESOLVED"),
        ))

    pending: list[dict] = []
    for item in unresolved or []:
        pending.append({"source": "v10r_release_gate", "detail": str(item)})
    for candidate in feature_dimension_candidates or []:
        if candidate.get("binding_status") not in {None, "BOUND"}:
            pending.append({"source": "feature_dimension", "feature_id": candidate.get("feature_name"), "detail": candidate.get("binding_note", "unresolved")})
    for index, result in enumerate(view_results or [], start=1):
        if not result.get("created"):
            pending.append({"source": "view", "view_id": f"V{index:03d}", "detail": result.get("error") or "view_not_created"})
    return DrawingPlan(
        schema_version="DRAWING_PLAN_V1",
        model_ref={"model_path": str(model_path.expanduser().resolve())},
        # V10R keeps sheet dimensions in metres internally; the shadow
        # contract exposes them as millimetres without changing execution.
        sheet=SheetPlan("MM", float(sheet_w) * 1000.0, float(sheet_h) * 1000.0, float(scale), str(scale_label)),
        views=views, dimensions=dimensions, annotations=annotations,
        unresolved=pending,
    )


def write_drawing_plan(path: Path, plan: DrawingPlan) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(plan.to_dict(), ensure_ascii=False, indent=2), encoding="utf-8")
    return path
