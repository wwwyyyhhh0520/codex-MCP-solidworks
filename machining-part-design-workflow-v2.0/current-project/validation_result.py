from __future__ import annotations

import json
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any


@dataclass
class ValidationIssue:
    issue_id: str
    rule_id: str
    severity: str
    feature_id: str | None
    view_id: str | None
    plan_item_id: str | None
    message: str
    evidence: dict = field(default_factory=dict)


@dataclass
class ValidationCheck:
    check_id: str
    rule_id: str
    category: str
    status: str
    weight: int
    earned: float
    message: str
    evidence: dict = field(default_factory=dict)


@dataclass
class ValidationResult:
    schema_version: str
    score: float
    status: str
    checks: list[ValidationCheck]
    issues: list[ValidationIssue]
    summary: dict
    quality_checks: dict = field(default_factory=lambda: {"chain_dimensions": [], "layout_issues": []})

    def to_dict(self) -> dict:
        return asdict(self)


def _issue(items: list[ValidationIssue], rule: str, severity: str, message: str,
           feature_id=None, view_id=None, plan_item_id=None, evidence=None) -> None:
    items.append(ValidationIssue(
        issue_id=f"ISSUE-{len(items) + 1:03d}", rule_id=rule, severity=severity,
        feature_id=feature_id, view_id=view_id, plan_item_id=plan_item_id,
        message=message, evidence=evidence or {},
    ))


def _check(checks: list[ValidationCheck], check_id: str, rule: str, category: str,
           status: str, weight: int, earned: float, message: str, evidence=None) -> None:
    checks.append(ValidationCheck(check_id, rule, category, status, weight,
                                  round(float(earned), 3), message, evidence or {}))


def _created_count(report: dict, *names: str) -> int | None:
    for name in names:
        value = report.get(name)
        if isinstance(value, dict) and isinstance(value.get("created_count"), (int, float)):
            return int(value["created_count"])
    return None


def _rect(value: Any) -> tuple[float, float, float, float] | None:
    if isinstance(value, (list, tuple)) and len(value) >= 4:
        try:
            left, bottom, right, top = (float(value[i]) for i in range(4))
            return (min(left, right), min(bottom, top), max(left, right), max(bottom, top))
        except (TypeError, ValueError):
            return None
    if isinstance(value, dict):
        for key in ("bbox", "bounding_box", "estimated_rect_m", "rect"):
            result = _rect(value.get(key))
            if result:
                return result
        keys = ("min_x", "min_y", "max_x", "max_y")
        if all(key in value for key in keys):
            return _rect([value[key] for key in keys])
    return None


def _quality_issue(rule: str, severity: str, message: str, view_id=None,
                   dimension_ids=None, annotation_id=None, evidence=None) -> dict:
    return {
        "rule_id": rule,
        "severity": severity,
        "view_id": view_id,
        "dimension_ids": list(dimension_ids or []),
        "annotation_id": annotation_id,
        "message": message,
        "evidence": evidence or {},
    }


def _run_quality_checks(plan: dict, report: dict, issues: list[ValidationIssue], drawing_evidence: dict | None = None) -> dict:
    """Read-only quality checks; no geometry or layout is synthesized."""
    chain_dimensions: list[dict] = []
    dimension_edges: list[dict] = []
    planned = {str(item.get("dimension_id")): item for item in plan.get("dimensions", [])}
    for item in (report.get("semantic_feature_dimensions") or {}).get("created") or []:
        if not item.get("native_dimension_bound"):
            continue
        a, b = item.get("point_a_mm"), item.get("point_b_mm")
        if not (isinstance(a, (list, tuple)) and isinstance(b, (list, tuple)) and len(a) >= 3 and len(b) >= 3):
            continue
        dimension_id = str(item.get("dimension_id") or "")
        if not dimension_id:
            dimension_id = next((key for key, value in planned.items() if value.get("source_feature_id") == item.get("feature_name") and key not in {edge.get("dimension_id") for edge in dimension_edges}), "")
        if not dimension_id:
            continue
        axis = str(item.get("axis") or "").upper()
        owner = next((value.get("owner_view_id") for key, value in planned.items() if key == dimension_id), None)
        dimension_edges.append({"dimension_id": dimension_id, "axis": axis, "view_id": owner, "a": tuple(round(float(v), 6) for v in a[:3]), "b": tuple(round(float(v), 6) for v in b[:3])})
    by_scope: dict[tuple[str | None, str], list[dict]] = {}
    for edge in dimension_edges:
        by_scope.setdefault((edge.get("view_id"), edge.get("axis")), []).append(edge)
    for (view_id, axis), edges in by_scope.items():
        adjacency: dict[tuple, list[dict]] = {}
        for edge in edges:
            adjacency.setdefault(edge["a"], []).append(edge)
            adjacency.setdefault(edge["b"], []).append(edge)
        visited: set[str] = set()
        for edge in edges:
            if edge["dimension_id"] in visited:
                continue
            stack = [edge]
            component: list[dict] = []
            while stack:
                current = stack.pop()
                if current["dimension_id"] in visited:
                    continue
                visited.add(current["dimension_id"])
                component.append(current)
                for node in (current["a"], current["b"]):
                    stack.extend(adjacency.get(node, []))
            if len(component) < 3:
                continue
            endpoints = {node for item in component for node in (item["a"], item["b"])}
            shared = {node for node in endpoints if all(node in (item["a"], item["b"]) for item in component)}
            if not shared:
                ids = [item["dimension_id"] for item in component]
                evidence = {"dimension_ids": ids, "axis": axis, "chain_length": len(component), "shared_datum_found": False, "suggested_future_strategy": "BASELINE_OR_ORDINATE"}
                chain_dimensions.append(_quality_issue("CHAIN_DIMENSION_DETECTED", "WARNING", "Consecutive dimensions form a chain without one stable datum.", view_id, ids, evidence=evidence))
                _issue(issues, "CHAIN_DIMENSION_DETECTED", "WARNING", "Consecutive dimensions form a chain without one stable datum.", view_id=view_id, evidence=evidence)
    if not dimension_edges:
        chain_dimensions.append({"rule_id": "CHAIN_DIMENSION_DETECTED", "status": "NOT_EVALUATED", "reason": "Exact dimension endpoint binding is unavailable."})

    layout_issues: list[dict] = []
    dimensions_with_boxes: list[dict] = []
    annotations_with_boxes: list[dict] = []
    evidence_dimensions = (drawing_evidence or {}).get("dimensions") or []
    evidence_annotations = (drawing_evidence or {}).get("annotations") or []
    for item in evidence_dimensions:
        box = _rect(item.get("bounding_box_sheet_mm"))
        if box:
            dimensions_with_boxes.append({"id": item.get("dimension_id"), "view_id": item.get("view_id"), "box": box, "category": item.get("semantic_type"), "source": "drawing_evidence"})
    for item in evidence_annotations:
        box = _rect(item.get("bounding_box_sheet_mm"))
        if box:
            annotations_with_boxes.append({"id": item.get("annotation_id"), "view_id": item.get("view_id"), "box": box, "source": "drawing_evidence"})
    # Existing report values remain a compatibility fallback for pre-5C runs.
    if not dimensions_with_boxes:
        for item in (report.get("semantic_dimensions") or {}).get("created") or []:
            box = _rect(item)
            if box:
                dimensions_with_boxes.append({"id": item.get("id"), "view_id": item.get("view_id"), "box": box, "category": item.get("category"), "source": "legacy_report"})
        for item in (report.get("semantic_feature_dimensions") or {}).get("created") or []:
            box = _rect(item)
            if box:
                dimensions_with_boxes.append({"id": item.get("dimension_id"), "view_id": item.get("view_id"), "box": box, "category": "feature", "source": "legacy_report"})
    if not annotations_with_boxes:
        for item in (report.get("native_hole_callout_binding") or {}).get("records") or []:
            box = _rect(item)
            if box:
                annotations_with_boxes.append({"id": item.get("annotation_id"), "view_id": item.get("view_id"), "box": box, "source": "legacy_report"})
    evaluated_layout_rules: set[str] = set()
    if dimensions_with_boxes:
        for index, left in enumerate(dimensions_with_boxes):
            for right in dimensions_with_boxes[index + 1:]:
                if left["view_id"] == right["view_id"] and _overlap(left["box"], right["box"]):
                    detail = _quality_issue("DIMENSION_OVERLAP", "WARNING", "Dimension bounding boxes overlap.", left["view_id"], [left["id"], right["id"]], evidence={"boxes": [left["box"], right["box"]]})
                    detail["status"] = "DETECTED"
                    layout_issues.append(detail)
                    _issue(issues, "DIMENSION_OVERLAP", "WARNING", detail["message"], view_id=left["view_id"], plan_item_id=left["id"], evidence=detail["evidence"])
                    evaluated_layout_rules.add("DIMENSION_OVERLAP")
        if "DIMENSION_OVERLAP" not in evaluated_layout_rules:
            layout_issues.append({"rule_id": "DIMENSION_OVERLAP", "status": "PASS", "reason": "Actual SolidWorks dimension bounding boxes were compared without overlap."})
            evaluated_layout_rules.add("DIMENSION_OVERLAP")
    if dimensions_with_boxes and annotations_with_boxes:
        for dimension in dimensions_with_boxes:
            for annotation in annotations_with_boxes:
                if dimension["view_id"] == annotation["view_id"] and _overlap(dimension["box"], annotation["box"]):
                    detail = _quality_issue("DIMENSION_TEXT_GEOMETRY_COLLISION", "WARNING", "Dimension and annotation bounding boxes overlap.", dimension["view_id"], [dimension["id"]], annotation.get("id"), {"dimension_box": dimension["box"], "annotation_box": annotation["box"]})
                    detail["status"] = "DETECTED"
                    layout_issues.append(detail)
                    _issue(issues, "DIMENSION_TEXT_GEOMETRY_COLLISION", "WARNING", detail["message"], view_id=dimension["view_id"], plan_item_id=dimension["id"], evidence=detail["evidence"])
                    evaluated_layout_rules.add("DIMENSION_TEXT_GEOMETRY_COLLISION")
        if "DIMENSION_TEXT_GEOMETRY_COLLISION" not in evaluated_layout_rules:
            layout_issues.append({"rule_id": "DIMENSION_TEXT_GEOMETRY_COLLISION", "status": "PASS", "reason": "Actual SolidWorks dimension and annotation bounding boxes were compared without overlap."})
            evaluated_layout_rules.add("DIMENSION_TEXT_GEOMETRY_COLLISION")
    for rule in ("DIMENSION_OVERLAP", "DIMENSION_TEXT_GEOMETRY_COLLISION", "DIMENSION_LINE_CROSSING", "DIMENSION_SPACING_INCONSISTENT", "DIMENSION_ORDER_NONCOMPLIANT"):
        if rule not in evaluated_layout_rules:
            layout_issues.append({"rule_id": rule, "status": "NOT_EVALUATED", "reason": "Current V10R report does not expose sufficient dimension/annotation geometry for this rule."})
    return {
        "chain_dimension_count": sum(1 for item in chain_dimensions if item.get("rule_id") == "CHAIN_DIMENSION_DETECTED" and item.get("status") != "NOT_EVALUATED"),
        "layout_issue_count": sum(1 for item in layout_issues if item.get("status") != "NOT_EVALUATED"),
        "chain_dimensions": chain_dimensions,
        "layout_issues": layout_issues,
    }


def _overlap(left: tuple[float, float, float, float], right: tuple[float, float, float, float]) -> bool:
    return left[0] < right[2] and right[0] < left[2] and left[1] < right[3] and right[1] < left[3]


def validate_drawing_plan(plan: dict, report: dict, drawing_path: Path | None = None,
                         pdf_path: Path | None = None, drawing_evidence: dict | None = None) -> ValidationResult:
    checks: list[ValidationCheck] = []
    issues: list[ValidationIssue] = []

    drawing_ok = bool(report.get("drawing_saved")) and bool(drawing_path and drawing_path.is_file())
    _check(checks, "C001", "DRAWING_GENERATED", "drawing", "PASS" if drawing_ok else "FAIL", 20,
           20 if drawing_ok else 0, "SLDDRW generated successfully" if drawing_ok else "SLDDRW was not generated",
           {"drawing_saved": report.get("drawing_saved"), "path_exists": bool(drawing_path and drawing_path.is_file())})
    if not drawing_ok:
        _issue(issues, "DRAWING_GENERATION_FAILED", "ERROR", "Required SLDDRW output is missing.", evidence=checks[-1].evidence)

    pdf_ok = bool(report.get("pdf_export_ok")) and bool(pdf_path and pdf_path.is_file())
    _check(checks, "C002", "PDF_EXPORTED", "pdf", "PASS" if pdf_ok else "FAIL", 10,
           10 if pdf_ok else 0, "PDF export succeeded" if pdf_ok else "PDF export failed",
           {"pdf_export_ok": report.get("pdf_export_ok"), "path_exists": bool(pdf_path and pdf_path.is_file())})
    if not pdf_ok:
        _issue(issues, "PDF_EXPORT_FAILED", "ERROR", "Required PDF output is missing.", evidence=checks[-1].evidence)

    planned_views = [item for item in plan.get("views", []) if item.get("required")]
    actual_views = {str(item.get("view_name") or "").lstrip("*").lower(): item for item in report.get("view_results", [])}
    missing_views = []
    failed_views = []
    for item in planned_views:
        actual = actual_views.get(str(item.get("role") or "").lower())
        if actual is None or not actual.get("created"):
            missing_views.append(item)
            if actual is not None:
                failed_views.append(item)
    view_earned = 20.0 * (len(planned_views) - len(missing_views)) / len(planned_views) if planned_views else 0.0
    view_status = "PASS" if planned_views and not missing_views else ("PARTIAL" if planned_views and view_earned else "NOT_EVALUATED")
    _check(checks, "C003", "REQUIRED_VIEW_PRESENT", "views", view_status, 20, view_earned,
           "Required planned views match created view results" if not missing_views else "Required planned views are missing or failed",
           {"planned_required": len(planned_views), "missing": [x.get("view_id") for x in missing_views], "failed": [x.get("view_id") for x in failed_views]})
    for item in missing_views:
        _issue(issues, "REQUIRED_VIEW_MISSING" if item not in failed_views else "VIEW_CREATION_FAILED", "ERROR",
               f"Required view {item.get('role')} was not created.", view_id=item.get("view_id"), evidence={"role": item.get("role")})

    planned_dims = [item for item in plan.get("dimensions", []) if item.get("required")]
    dim_total = len(planned_dims)
    dim_report = report.get("semantic_dimensions") or {}
    feature_report = report.get("semantic_feature_dimensions") or {}
    dim_created = (_created_count(report, "semantic_dimensions") or 0) + (_created_count(report, "semantic_feature_dimensions") or 0) + int((report.get("step_profile_dimensions") or {}).get("created_count") or 0)
    dim_known = bool(dim_report or feature_report or report.get("step_profile_dimensions"))
    dim_earned = min(20.0, 20.0 * dim_created / dim_total) if dim_total and dim_known else 0.0
    dim_status = "PASS" if dim_total and dim_known and dim_created >= dim_total else ("PARTIAL" if dim_known else "NOT_EVALUATED")
    _check(checks, "C004", "REQUIRED_DIMENSION_RELEASED", "dimensions", dim_status, 20, dim_earned,
           "Dimension execution counts are available" if dim_known else "Dimension release cannot be fully evaluated from current report",
           {"planned_required": dim_total, "created_count": dim_created, "semantic_bound_count": dim_report.get("bound_count"), "feature_bound_count": feature_report.get("bound_count")})
    if dim_total and dim_known and dim_created < dim_total:
        for item in planned_dims[dim_created:]:
            _issue(issues, "REQUIRED_DIMENSION_MISSING", "ERROR", "Required dimension was not released.",
                   plan_item_id=item.get("dimension_id"), view_id=item.get("owner_view_id"), evidence={"created_count": dim_created, "planned_required": dim_total})
    elif dim_total and not dim_known:
        _issue(issues, "REQUIRED_DIMENSION_UNRESOLVED", "WARNING", "Dimension release could not be evaluated without reliable mapping.")

    planned_ann = [item for item in plan.get("annotations", []) if item.get("required")]
    binding = report.get("native_hole_callout_binding") or {}
    records = binding.get("records") or []
    bound = sum(1 for item in records if item.get("hole_callout_bound"))
    ann_total = len(planned_ann)
    ann_earned = min(15.0, 15.0 * bound / ann_total) if ann_total else 0.0
    ann_status = "PASS" if ann_total and bound >= ann_total else ("PARTIAL" if ann_total else "NOT_EVALUATED")
    _check(checks, "C005", "REQUIRED_HOLE_CALLOUT_BOUND", "hole_callouts", ann_status, 15, ann_earned,
           "Required hole callouts are bound" if ann_total and bound >= ann_total else "Some required hole callouts are unbound",
           {"planned_required": ann_total, "bound_count": bound, "reported_unbound_count": binding.get("unbound_count")})
    for item in planned_ann:
        record = next((x for x in records if x.get("annotation_id") == item.get("annotation_id")), None)
        if record is None or not record.get("hole_callout_bound"):
            _issue(issues, "REQUIRED_CALLOUT_MISSING" if record is None else "CALLOUT_UNBOUND", "ERROR",
                   "Required hole callout is missing or unbound.", feature_id=item.get("source_feature_id"),
                   view_id=item.get("owner_view_id"), plan_item_id=item.get("annotation_id"), evidence=record or {})

    annotation_ids = [x.get("annotation_id") for x in records if x.get("annotation_id")]
    suppression_known = bool(annotation_ids) and len(annotation_ids) == len(set(annotation_ids)) and binding.get("bound_annotation_ids") is not None
    _check(checks, "C006", "DUPLICATE_SUPPRESSION_REUSED", "duplicate_suppression",
           "PASS" if suppression_known else "NOT_EVALUATED", 10, 10 if suppression_known else 0,
           "Existing V10R unique binding result was reused" if suppression_known else "V10R does not expose a reliable suppression result",
           {"annotation_count": len(annotation_ids), "unique_annotation_count": len(set(annotation_ids)), "source": "native_hole_callout_binding"})

    quality_checks = _run_quality_checks(plan, report, issues, drawing_evidence)

    unresolved = plan.get("unresolved") or []
    unresolved_status = "PASS" if not unresolved else "WARNING"
    unresolved_earned = 5 if not unresolved else 0
    _check(checks, "C007", "UNRESOLVED_ITEMS_REVIEWED", "unresolved", unresolved_status, 5, unresolved_earned,
           "No unresolved plan items" if not unresolved else "Plan contains unresolved items",
           {"count": len(unresolved)})
    for item in unresolved:
        _issue(issues, "UNRESOLVED_PLAN_ITEM", "WARNING", str(item.get("detail") or "Unresolved drawing-plan item."),
               feature_id=item.get("feature_id"), view_id=item.get("view_id"), plan_item_id=item.get("plan_item_id"), evidence=item)

    score = round(sum(item.earned for item in checks), 3)
    critical = any(item.severity == "ERROR" and item.rule_id in {"DRAWING_GENERATION_FAILED", "PDF_EXPORT_FAILED"} for item in issues)
    status = "FAIL" if critical or score < 70 else ("PASS" if score >= 90 and not any(item.severity == "ERROR" for item in issues) else "REVIEW")
    return ValidationResult("VALIDATION_RESULT_V1", score, status, checks, issues, {
        "check_count": len(checks), "issue_count": len(issues),
        "error_count": sum(item.severity == "ERROR" for item in issues),
        "warning_count": sum(item.severity == "WARNING" for item in issues),
        "not_evaluated_count": sum(item.status == "NOT_EVALUATED" for item in checks),
        "critical_output_integrity_failure": critical,
    }, quality_checks)


def write_validation_result(path: Path, result: ValidationResult) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(result.to_dict(), ensure_ascii=False, indent=2), encoding="utf-8")
    return path
