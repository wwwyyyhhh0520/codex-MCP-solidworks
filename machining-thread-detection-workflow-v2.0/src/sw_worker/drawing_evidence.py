from __future__ import annotations

import json
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Callable


MM_PER_M = 1000.0


@dataclass
class ViewEvidence:
    view_id: str | None
    view_name: str | None
    view_bbox_sheet_mm: list[float] | None
    evaluation_status: str
    get_display_dimensions_count: int = 0
    visible_dimension_count: int = 0
    get_first_annotation3_result: str = "UNAVAILABLE"
    visible_annotation_count: int = 0


@dataclass
class DimensionEvidence:
    dimension_id: str | None
    view_id: str | None
    semantic_type: str | None
    text_position_sheet_mm: list[float] | None
    bounding_box_sheet_mm: list[float] | None
    dimension_line: dict | None
    extension_refs: list[dict]
    evaluation_status: str
    diagnostics: list[str] = field(default_factory=list)


@dataclass
class AnnotationEvidence:
    annotation_id: str | None
    view_id: str | None
    annotation_type: str | None
    position_sheet_mm: list[float] | None
    bounding_box_sheet_mm: list[float] | None
    evaluation_status: str
    diagnostics: list[str] = field(default_factory=list)


@dataclass
class DrawingEvidence:
    schema_version: str
    views: list[ViewEvidence]
    dimensions: list[DimensionEvidence]
    annotations: list[AnnotationEvidence]
    capture_diagnostics: list[str] = field(default_factory=list)

    def to_dict(self) -> dict:
        return asdict(self)


def _value_or_call(target: object, name: str, default: Any = None) -> Any:
    try:
        value = getattr(target, name)
        return value() if callable(value) else value
    except Exception:
        return default


def _as_list(value: Any) -> list[Any]:
    if value is None:
        return []
    if isinstance(value, (list, tuple)):
        return list(value)
    try:
        return list(value)
    except TypeError:
        return [value]


def _point_mm(value: Any) -> list[float] | None:
    raw = _as_list(value)
    if len(raw) < 2:
        return None
    try:
        return [round(float(raw[0]) * MM_PER_M, 6), round(float(raw[1]) * MM_PER_M, 6)]
    except (TypeError, ValueError):
        return None


def _box_mm(value: Any) -> list[float] | None:
    raw = _as_list(value)
    try:
        # IAnnotation.GetBox supplies min xyz / max xyz. Some dispatch paths
        # expose an already flattened four-value rectangle.
        if len(raw) >= 6:
            values = (raw[0], raw[1], raw[3], raw[4])
        elif len(raw) >= 4:
            values = (raw[0], raw[1], raw[2], raw[3])
        else:
            return None
        left, bottom, right, top = (float(item) * MM_PER_M for item in values)
        return [round(min(left, right), 6), round(min(bottom, top), 6),
                round(max(left, right), 6), round(max(bottom, top), 6)]
    except (TypeError, ValueError):
        return None


def _annotation_text(annotation: object) -> str:
    for target in (annotation, _value_or_call(annotation, "GetSpecificAnnotation")):
        if target is None:
            continue
        for member in ("GetText", "GetText2", "Text"):
            value = _value_or_call(target, member)
            if isinstance(value, str) and value:
                return value
    return ""


def _annotation_position(annotation: object, diagnostics: list[str]) -> list[float] | None:
    for member in ("GetPosition", "GetPosition2"):
        try:
            value = getattr(annotation, member)
            position = value() if callable(value) else value
            result = _point_mm(position)
            if result is not None:
                return result
            diagnostics.append(f"{member}=unsupported_shape")
        except Exception as exc:
            diagnostics.append(f"{member}={type(exc).__name__}")
    return None


def _annotation_box(annotation: object, diagnostics: list[str]) -> list[float] | None:
    try:
        result = _box_mm(_value_or_call(annotation, "GetBox"))
        if result is None:
            diagnostics.append("GetBox=unavailable")
        return result
    except Exception as exc:
        diagnostics.append(f"GetBox={type(exc).__name__}")
        return None


def _physical_view_id(created_views: list[object], plan: dict) -> dict[int, str]:
    planned = list(plan.get("views") or [])
    return {
        id(view): str(planned[index].get("view_id")) if index < len(planned) else None
        for index, view in enumerate(created_views or [])
    }


def _drawing_views(drawing: object, created_views: list[object]) -> list[object]:
    """Prefer post-save DrawingDoc view proxies; retain known created views as fallback."""
    result: list[object] = []
    seen_names: set[str] = set()
    current = _value_or_call(drawing, "GetFirstView")
    seen: set[int] = set()
    while current is not None and id(current) not in seen:
        seen.add(id(current))
        name = str(_value_or_call(current, "Name", "") or "")
        # The first drawing view is normally the sheet container. It has no
        # model view name and cannot own displayed dimensions.
        if name:
            result.append(current)
            seen_names.add(name)
        current = _value_or_call(current, "GetNextView")
    for view in created_views or []:
        name = str(_value_or_call(view, "Name", "") or "")
        if name not in seen_names:
            result.append(view)
            seen_names.add(name)
    return result


def _unique_dimension_ids(plan: dict, semantic_dimensions: dict, view_id: str | None) -> dict[str, str]:
    """Map a displayed text to a plan id only when it is unique in one view."""
    expected: dict[str, list[str]] = {}
    planned = {str(item.get("dimension_id")): item for item in plan.get("dimensions") or []}
    for item in semantic_dimensions.get("created") or []:
        dim_id = str(item.get("id") or "")
        text = str(item.get("text") or "")
        if not dim_id or not text or planned.get(dim_id, {}).get("owner_view_id") != view_id:
            continue
        expected.setdefault(text, []).append(dim_id)
    return {text: ids[0] for text, ids in expected.items() if len(ids) == 1}


def _display_dimension_annotation(display_dimension: object) -> object | None:
    return _value_or_call(display_dimension, "GetAnnotation")


def _iter_display_dimensions(view: object) -> tuple[list[object], str, int]:
    """Use the array API first; traversal is only a version fallback."""
    try:
        array_result = _as_list(getattr(view, "GetDisplayDimensions")())
        if array_result:
            return array_result, "IView.GetDisplayDimensions=available", len(array_result)
        array_status = "IView.GetDisplayDimensions=empty"
    except Exception as exc:
        array_status = f"IView.GetDisplayDimensions={type(exc).__name__}"
    result: list[object] = []
    current = _value_or_call(view, "GetFirstDisplayDimension6")
    first_status = "IView.GetFirstDisplayDimension6=none" if current is None else "IView.GetFirstDisplayDimension6=available"
    if current is None:
        current = _value_or_call(view, "GetFirstDisplayDimension")
        first_status = "IView.GetFirstDisplayDimension=none" if current is None else "IView.GetFirstDisplayDimension=available"
    seen: set[int] = set()
    while current is not None and id(current) not in seen:
        seen.add(id(current))
        result.append(current)
        current = _value_or_call(current, "GetNext6") or _value_or_call(current, "GetNext")
    return result, f"{array_status};{first_status}", 0


def _annotation_identity_map(native_hole_binding: dict, plan: dict) -> dict[tuple[str | None, str], str]:
    role_to_id = {str(item.get("role") or "").lower(): item.get("view_id") for item in plan.get("views") or []}
    result: dict[tuple[str | None, str], str] = {}
    for record in native_hole_binding.get("records") or []:
        text = str((record.get("native_callout_text") or {}).get("after") or "")
        annotation_id = record.get("annotation_id")
        view_id = role_to_id.get(str(record.get("view_role") or "").lower())
        if text and annotation_id:
            result[(view_id, text)] = str(annotation_id)
    return result


def capture_drawing_evidence(
    drawing: object,
    created_views: list[object],
    plan: dict,
    semantic_dimensions: dict,
    native_hole_binding: dict,
    outline: Callable[[object], list[float]],
) -> DrawingEvidence:
    """Read actual drawing state only; this function never mutates SolidWorks."""
    view_ids = _physical_view_id(created_views, plan)
    created_name_to_id = {
        str(_value_or_call(view, "Name", "") or ""): view_id
        for view, view_id in ((view, view_ids.get(id(view))) for view in created_views or [])
    }
    views: list[ViewEvidence] = []
    dimensions: list[DimensionEvidence] = []
    annotations: list[AnnotationEvidence] = []
    capture_diagnostics: list[str] = []
    callout_ids = _annotation_identity_map(native_hole_binding, plan)
    seen_annotation_ids: set[int] = set()

    for view in _drawing_views(drawing, created_views):
        view_id = view_ids.get(id(view)) or created_name_to_id.get(str(_value_or_call(view, "Name", "") or ""))
        view_name = str(_value_or_call(view, "Name", "") or "")
        try:
            raw_outline = outline(view)
            bbox = [round(float(item) * MM_PER_M, 6) for item in raw_outline[:4]] if len(raw_outline) >= 4 else None
        except Exception:
            bbox = None
        view_evidence = ViewEvidence(view_id, view_name, bbox, "AVAILABLE" if bbox else "PARTIAL")
        views.append(view_evidence)

        dim_text_to_id = _unique_dimension_ids(plan, semantic_dimensions, view_id)
        display_dimensions, dimension_status, array_count = _iter_display_dimensions(view)
        view_evidence.get_display_dimensions_count = array_count
        view_evidence.visible_dimension_count = len(display_dimensions)
        capture_diagnostics.append(f"{view_name}:{dimension_status}|count={len(display_dimensions)}")
        for display_dimension in display_dimensions:
            diagnostics: list[str] = []
            annotation = _display_dimension_annotation(display_dimension)
            if annotation is None:
                dimensions.append(DimensionEvidence(None, view_id, None, None, None, None, [], "PARTIAL", ["DisplayDimension.GetAnnotation=unavailable"]))
                continue
            seen_annotation_ids.add(id(annotation))
            text = _annotation_text(annotation)
            dimension_id = dim_text_to_id.get(text)
            planned = next((item for item in plan.get("dimensions") or [] if item.get("dimension_id") == dimension_id), {})
            position = _annotation_position(annotation, diagnostics)
            box = _annotation_box(annotation, diagnostics)
            status = "AVAILABLE" if position is not None and box is not None else "PARTIAL"
            dimensions.append(DimensionEvidence(
                dimension_id, view_id, planned.get("semantic_type"), position, box,
                None, [], status, diagnostics,
            ))

        # Hole callouts and notes are annotations too. Their identities are
        # only attached when actual native text matches one unique bound record.
        current = _value_or_call(view, "GetFirstAnnotation3")
        annotation_status = "AVAILABLE" if current is not None else "NONE"
        view_evidence.get_first_annotation3_result = annotation_status
        local_seen: set[int] = set()
        while current is not None and id(current) not in local_seen:
            local_seen.add(id(current))
            annotation = current
            current = _value_or_call(annotation, "GetNext3")
            if id(annotation) in seen_annotation_ids:
                continue
            diagnostics: list[str] = []
            text = _annotation_text(annotation)
            annotations.append(AnnotationEvidence(
                callout_ids.get((view_id, text)), view_id,
                str(_value_or_call(annotation, "Type", "") or "") or None,
                _annotation_position(annotation, diagnostics), _annotation_box(annotation, diagnostics),
                "AVAILABLE" if not diagnostics else "PARTIAL", diagnostics,
            ))
        view_evidence.visible_annotation_count = len(local_seen)
        capture_diagnostics.append(f"{view_name}:IView.GetFirstAnnotation3={annotation_status}|count={len(local_seen)}")
    return DrawingEvidence("DRAWING_EVIDENCE_V1", views, dimensions, annotations, capture_diagnostics)


def write_drawing_evidence(path: Path, evidence: DrawingEvidence) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(evidence.to_dict(), ensure_ascii=False, indent=2), encoding="utf-8")
    return path
