"""Validate candidate drawing views in a temporary SolidWorks drawing."""

from __future__ import annotations

import argparse
import json
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List

from .solidworks_executor import _open_part_read_only, _orientation_aliases, _safe_call


def _view_outline(view: Any) -> Dict[str, float] | None:
    outline = _safe_call(view, "GetOutline", None)
    if not outline or len(outline) < 4:
        return None
    return {
        "min_x": float(outline[0]),
        "min_y": float(outline[1]),
        "max_x": float(outline[2]),
        "max_y": float(outline[3]),
        "width": float(outline[2]) - float(outline[0]),
        "height": float(outline[3]) - float(outline[1]),
    }


def _candidate_orientations(analysis: Dict[str, Any]) -> List[str]:
    result: List[str] = []
    for item in analysis.get("view_candidates", []):
        orientation = item.get("orientation")
        if orientation and orientation not in result:
            result.append(str(orientation))
    for fallback in ("front", "top", "right"):
        if fallback not in result:
            result.append(fallback)
    return result


def _planned_base_orientation(plan: Dict[str, Any]) -> str:
    for view in plan.get("views", []):
        if view.get("id") == "BASE-1" and view.get("orientation"):
            return str(view["orientation"])
    return "front"


def validate_views(
    analysis: Dict[str, Any],
    plan: Dict[str, Any],
    output_file: str,
    orientations: List[str] | None = None,
) -> Dict[str, Any]:
    source_file = analysis.get("source_file")
    sheet = plan.get("sheet", {})
    template_path = sheet.get("template_path")
    report: Dict[str, Any] = {
        "schema_version": "1.0",
        "mode": "view_candidate_validation",
        "created_at": datetime.now().isoformat(timespec="seconds"),
        "part_number": plan.get("part_number") or analysis.get("part_number"),
        "source_file": source_file,
        "template_path": template_path,
        "drawing_saved": False,
        "source_model_modified": False,
        "status": "failed",
        "candidates": [],
        "risks": [],
    }
    if not source_file or not Path(source_file).is_file():
        report["risks"].append("源 SLDPRT 不存在。")
        return report
    if not template_path or not Path(template_path).is_file():
        report["risks"].append("工程图模板不存在。")
        return report
    try:
        import win32com.client  # type: ignore
    except ImportError:
        report["risks"].append("缺少 pywin32。")
        return report
    try:
        app = win32com.client.Dispatch("SldWorks.Application")
        app.Visible = True
    except Exception as exc:
        report["risks"].append(f"SolidWorks COM 连接失败：{exc}")
        return report
    steps: List[Dict[str, Any]] = []
    if _open_part_read_only(app, str(source_file), steps) is None:
        report["risks"].extend(step.get("message", "") for step in steps if step.get("status") == "error")
        return report
    drawing = None
    try:
        drawing = app.NewDocument(str(template_path), 0, 0.0, 0.0)
    except Exception as exc:
        report["risks"].append(f"新建临时工程图失败：{exc}")
        return report
    if drawing is None:
        report["risks"].append("SolidWorks 返回空工程图对象。")
        return report

    x = 0.12
    y = 0.18
    requested_orientations = orientations or [_planned_base_orientation(plan)]
    for orientation in requested_orientations:
        candidate: Dict[str, Any] = {
            "orientation": orientation,
            "inserted": False,
            "aliases_tried": [],
            "outline": None,
            "api_validation_status": "failed",
            "semantic_validation_status": "not_evaluated",
        }
        for alias in _orientation_aliases(orientation):
            candidate["aliases_tried"].append(alias)
            view = _safe_call(drawing, "CreateDrawViewFromModelView3", None, str(source_file), alias, x, y, 0.0)
            if view is not None:
                candidate["inserted"] = True
                candidate["used_alias"] = alias
                candidate["outline"] = _view_outline(view)
                candidate["api_validation_status"] = "inserted"
                break
        if not candidate["inserted"]:
            candidate["risk"] = "SolidWorks 未能插入该方向视图。"
        elif candidate["outline"] is None:
            candidate["risk"] = "视图已插入，但未能读取视图外框。"
        report["candidates"].append(candidate)
        x += 0.12

    inserted_count = sum(1 for item in report["candidates"] if item.get("inserted"))
    report["status"] = "passed" if inserted_count else "failed"
    report["summary"] = {
        "candidate_count": len(report["candidates"]),
        "inserted_count": inserted_count,
        "tested_orientations": requested_orientations,
        "available_candidate_orientations": _candidate_orientations(analysis),
        "semantic_limit": "本验证只确认 SolidWorks API 可插入视图，不判断主视图工程合理性。",
    }
    Path(output_file).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    return report


def validate_from_execution_probe(probe: Dict[str, Any], output_file: str) -> Dict[str, Any]:
    inserted_steps = [
        step for step in probe.get("steps", [])
        if step.get("step") == "insert_base_view" and step.get("status") == "ok"
    ]
    report: Dict[str, Any] = {
        "schema_version": "1.0",
        "mode": "view_candidate_validation_from_execution_probe",
        "created_at": datetime.now().isoformat(timespec="seconds"),
        "part_number": probe.get("part_number"),
        "source_file": probe.get("source_file"),
        "template_path": probe.get("template_path"),
        "drawing_saved": bool(probe.get("drawing_saved", False)),
        "source_model_modified": bool(probe.get("source_model_modified", False)),
        "status": "passed" if inserted_steps else "failed",
        "candidates": [],
        "summary": {
            "candidate_count": len(inserted_steps),
            "inserted_count": len(inserted_steps),
            "semantic_limit": "来自 execution_probe 的事实只证明基础视图 API 插入成功，不证明主视图工程合理性。",
        },
        "risks": [],
    }
    for step in inserted_steps:
        report["candidates"].append({
            "orientation": "top" if step.get("orientation") in ("*Top", "*上视", "*上视图") else "unknown",
            "inserted": True,
            "used_alias": step.get("orientation"),
            "outline": None,
            "api_validation_status": "inserted_from_execution_probe",
            "semantic_validation_status": "not_evaluated",
            "risk": "未读取可见边、隐藏线、孔表达能力或视图外框；不能据此确认最佳主视图。",
        })
    if probe.get("status") != "passed":
        report["risks"].append("execution_probe 未通过，不能转化为视图验证事实。")
    Path(output_file).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    return report


def validate_from_typed_bridge_probe(probe: Dict[str, Any], output_file: str) -> Dict[str, Any]:
    """Turn a typed bridge's unsaved drawing preflight into limited API evidence."""
    drawing = probe.get("drawing_probe") or {}
    inserted = bool(drawing.get("base_view_inserted"))
    orientation = drawing.get("used_orientation") or drawing.get("requested_orientation") or "unknown"
    report: Dict[str, Any] = {
        "schema_version": "1.0",
        "mode": "view_candidate_validation_from_typed_bridge_probe",
        "created_at": datetime.now().isoformat(timespec="seconds"),
        "part_number": Path(str(probe.get("source_file") or "")).stem,
        "source_file": probe.get("source_file"),
        "template_path": drawing.get("template_path"),
        "drawing_saved": bool(probe.get("drawing_saved", False)),
        "source_model_modified": bool(probe.get("source_model_modified", False)),
        "status": "passed" if inserted else "failed",
        "candidates": [{
            "orientation": "top" if orientation in {"*Top", "*上视", "*上视图"} else "unknown",
            "inserted": inserted,
            "used_alias": orientation,
            "outline": None,
            "api_validation_status": "inserted_from_typed_bridge_probe" if inserted else "not_inserted",
            "semantic_validation_status": "not_evaluated",
            "risk": "只证明基础视图 API 插入成功；未验证可见边、隐藏线、孔表达能力或工程意义上的最佳主视图。",
        }],
        "summary": {
            "candidate_count": 1,
            "inserted_count": int(inserted),
            "semantic_limit": "来自强类型 execution_probe 的事实只证明基础视图 API 插入成功，不证明主视图工程合理性。",
        },
        "risks": [] if inserted else ["强类型桥接未能插入基础视图。"],
    }
    Path(output_file).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    return report


def apply_api_view_evidence(plan: Dict[str, Any], validation: Dict[str, Any], validation_source: str) -> Dict[str, Any]:
    candidate = next((item for item in validation.get("candidates", []) if item.get("inserted")), None)
    if not candidate:
        return plan
    sheet = plan.setdefault("sheet", {})
    if validation.get("template_path") and validation.get("drawing_saved") is False:
        sheet["selection_status"] = "validated_in_solidworks"
        sheet["validation_source"] = validation_source
    for view in plan.get("views", []):
        if view.get("id") != "BASE-1":
            continue
        view["status"] = "api_inserted_pending_semantic_validation"
        view["confidence"] = max(float(view.get("confidence", 0.0)), 0.5)
        view["api_orientation"] = candidate.get("orientation")
        view["validation_source"] = validation_source
        view["reason"] = "已通过临时工程图 API 插入验证；尚未完成投影语义和可标注性验证。"
    return plan


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("analysis_file")
    parser.add_argument("plan_file")
    parser.add_argument("output_file")
    parser.add_argument("--orientation", action="append", default=None)
    parser.add_argument("--from-execution-probe", default=None)
    parser.add_argument("--from-typed-bridge-probe", default=None)
    parser.add_argument("--plan-output-file", default=None)
    args = parser.parse_args()
    if args.from_execution_probe:
        probe = json.loads(Path(args.from_execution_probe).read_text(encoding="utf-8"))
        report = validate_from_execution_probe(probe, args.output_file)
    elif args.from_typed_bridge_probe:
        probe = json.loads(Path(args.from_typed_bridge_probe).read_text(encoding="utf-8"))
        report = validate_from_typed_bridge_probe(probe, args.output_file)
        if args.plan_output_file:
            plan = json.loads(Path(args.plan_file).read_text(encoding="utf-8"))
            plan = apply_api_view_evidence(plan, report, args.output_file)
            Path(args.plan_output_file).write_text(json.dumps(plan, ensure_ascii=False, indent=2), encoding="utf-8")
    else:
        analysis = json.loads(Path(args.analysis_file).read_text(encoding="utf-8"))
        plan = json.loads(Path(args.plan_file).read_text(encoding="utf-8"))
        report = validate_views(analysis, plan, args.output_file, args.orientation)
    if not Path(args.output_file).is_file():
        Path(args.output_file).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({
        "status": report.get("status"),
        "inserted_count": report.get("summary", {}).get("inserted_count", 0),
        "output": args.output_file,
    }, ensure_ascii=False))
    return 0 if report.get("status") == "passed" else 2


if __name__ == "__main__":
    raise SystemExit(main())
