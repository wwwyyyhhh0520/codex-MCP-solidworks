"""SolidWorks drawing execution probes.

This module is intentionally conservative. The default command validates that a
configured drawing template can create a temporary drawing and that SolidWorks
can insert one base view from the source part. It does not edit or save the
source SLDPRT.
"""

from __future__ import annotations

import argparse
import json
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List, Optional


class SolidWorksExecutionError(RuntimeError):
    pass


def _safe_call(obj: Any, name: str, default: Any = None, *args: Any) -> Any:
    try:
        value = getattr(obj, name)
        return value(*args) if callable(value) else value
    except Exception:
        return default


def _orientation_aliases(orientation: Optional[str]) -> List[str]:
    aliases = {
        "front": ["*Front", "*前视", "*前视图"],
        "top": ["*Top", "*上视", "*上视图"],
        "right": ["*Right", "*右视", "*右视图"],
        "left": ["*Left", "*左视", "*左视图"],
        "bottom": ["*Bottom", "*下视", "*下视图"],
        "back": ["*Back", "*后视", "*后视图"],
    }
    return aliases.get(str(orientation or "").lower(), ["*Front", "*前视", "*前视图"])


def _open_part_read_only(app: Any, source_file: str, steps: List[Dict[str, Any]]) -> Any:
    try:
        model = app.OpenDoc6(source_file, 1, 1, "", 0, 0)
        steps.append({"step": "open_part_read_only", "status": "ok", "api": "OpenDoc6"})
        return model
    except Exception as exc:
        steps.append({"step": "open_part_read_only", "status": "warning", "api": "OpenDoc6", "message": str(exc)})
    try:
        model = app.OpenDoc(source_file, 1)
        steps.append({"step": "open_part_read_only", "status": "ok", "api": "OpenDoc"})
        return model
    except Exception as exc:
        steps.append({"step": "open_part_read_only", "status": "error", "api": "OpenDoc", "message": str(exc)})
        return None


def validate_template_and_base_view(
    analysis: Dict[str, Any],
    plan: Dict[str, Any],
    output_report: str,
) -> Dict[str, Any]:
    steps: List[Dict[str, Any]] = []
    source_file = analysis.get("source_file")
    sheet = plan.get("sheet", {})
    template_path = sheet.get("template_path")
    result: Dict[str, Any] = {
        "schema_version": "1.0",
        "mode": "template_validation_only",
        "created_at": datetime.now().isoformat(timespec="seconds"),
        "part_number": plan.get("part_number") or analysis.get("part_number"),
        "source_file": source_file,
        "template_path": template_path,
        "drawing_saved": False,
        "source_model_modified": False,
        "status": "failed",
        "steps": steps,
    }
    if not source_file or not Path(source_file).is_file():
        steps.append({"step": "validate_inputs", "status": "error", "message": "源 SLDPRT 不存在。"})
        return result
    if not template_path or not Path(template_path).is_file():
        steps.append({"step": "validate_inputs", "status": "error", "message": "工程图模板不存在。"})
        return result

    try:
        import win32com.client  # type: ignore
    except ImportError:
        steps.append({"step": "connect_solidworks", "status": "error", "message": "缺少 pywin32。"})
        return result

    try:
        app = win32com.client.Dispatch("SldWorks.Application")
        app.Visible = True
        steps.append({"step": "connect_solidworks", "status": "ok"})
    except Exception as exc:
        steps.append({"step": "connect_solidworks", "status": "error", "message": str(exc)})
        return result

    part_model = _open_part_read_only(app, str(source_file), steps)
    if part_model is None:
        return result

    drawing = None
    try:
        drawing = app.NewDocument(str(template_path), 0, 0.0, 0.0)
        if drawing is None:
            steps.append({"step": "new_drawing_from_template", "status": "error", "message": "SolidWorks 返回空工程图对象。"})
            return result
        steps.append({"step": "new_drawing_from_template", "status": "ok"})
    except Exception as exc:
        steps.append({"step": "new_drawing_from_template", "status": "error", "message": str(exc)})
        return result

    base_view = next((view for view in plan.get("views", []) if view.get("id") == "BASE-1"), {})
    inserted = None
    tried = []
    for alias in _orientation_aliases(base_view.get("orientation")):
        tried.append(alias)
        inserted = _safe_call(drawing, "CreateDrawViewFromModelView3", None, str(source_file), alias, 0.18, 0.15, 0.0)
        if inserted is not None:
            steps.append({"step": "insert_base_view", "status": "ok", "orientation": alias})
            break
    if inserted is None:
        steps.append({
            "step": "insert_base_view",
            "status": "error",
            "message": "无法插入基础视图。",
            "tried_orientations": tried,
        })
        return result

    sheet_format = sheet.get("sheet_format_path")
    if sheet_format and Path(sheet_format).is_file():
        steps.append({
            "step": "sheet_format_available",
            "status": "ok",
            "sheet_format_path": sheet_format,
            "message": "已确认图纸格式文件存在；本预演暂不调用 SetupSheet 改写当前 Sheet。",
        })

    result["status"] = "passed"
    result["template_validation_status"] = "validated_in_solidworks_session"
    result["base_view_inserted"] = True
    Path(output_report).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("analysis_file")
    parser.add_argument("plan_file")
    parser.add_argument("output_report")
    parser.add_argument("--template-validation-only", action="store_true")
    args = parser.parse_args()
    if not args.template_validation_only:
        raise SolidWorksExecutionError("当前阶段只允许 --template-validation-only 安全预演模式。")
    analysis = json.loads(Path(args.analysis_file).read_text(encoding="utf-8"))
    plan = json.loads(Path(args.plan_file).read_text(encoding="utf-8"))
    result = validate_template_and_base_view(analysis, plan, args.output_report)
    if not Path(args.output_report).is_file():
        Path(args.output_report).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({
        "status": result.get("status"),
        "base_view_inserted": result.get("base_view_inserted", False),
        "output": args.output_report,
    }, ensure_ascii=False))
    return 0 if result.get("status") == "passed" else 2


if __name__ == "__main__":
    raise SystemExit(main())
