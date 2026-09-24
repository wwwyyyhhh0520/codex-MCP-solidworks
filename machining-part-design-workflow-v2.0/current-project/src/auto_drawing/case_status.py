"""Aggregate one part case into machine and human readable status reports."""

from __future__ import annotations

import argparse
import json
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List


ARTIFACTS = {
    "part_analysis": "part_analysis.json",
    "drawing_plan": "drawing_plan.json",
    "review_report": "review_report.json",
    "execution_probe": "execution_probe.json",
    "view_validation": "view_validation.json",
    "hole_analysis": "hole_analysis.json",
    "manual_metadata_validation": "manual_metadata_validation.json",
    "typed_bridge_report": "macro_bridge_probe.json",
    "typed_bridge_import": "macro_bridge_import.json",
}


def _read_json(path: Path) -> Dict[str, Any] | None:
    if not path.is_file():
        return None
    return json.loads(path.read_text(encoding="utf-8"))


def _artifact_status(case_dir: Path) -> Dict[str, Dict[str, Any]]:
    result: Dict[str, Dict[str, Any]] = {}
    for key, filename in ARTIFACTS.items():
        path = case_dir / filename
        data = _read_json(path)
        result[key] = {
            "path": str(path),
            "exists": data is not None,
            "status": (data.get("status") or "available") if data else "missing",
        }
    return result


def _blocking_items(review_report: Dict[str, Any] | None) -> List[Dict[str, Any]]:
    if not review_report:
        return []
    return [
        {
            "id": item.get("id"),
            "topic": item.get("topic"),
            "severity": item.get("severity"),
            "message": item.get("message"),
            "source": item.get("source"),
        }
        for item in review_report.get("items", [])
        if item.get("blocks_execution")
    ]


def _confirmed_facts(
    analysis: Dict[str, Any] | None,
    plan: Dict[str, Any] | None,
    execution_probe: Dict[str, Any] | None,
    view_validation: Dict[str, Any] | None,
    hole_analysis: Dict[str, Any] | None,
) -> List[str]:
    facts: List[str] = []
    if analysis:
        facts.append(f"源零件已读取：{analysis.get('source_file')}")
        if analysis.get("bounding_box"):
            facts.append("已读取模型包络尺寸，并可生成总体尺寸计划。")
        hole_count = sum(1 for feature in analysis.get("features", []) if feature.get("type") == "HoleWzd")
        if hole_count:
            facts.append(f"Feature Tree 中发现 {hole_count} 个 Hole Wizard 特征。")
    if plan and plan.get("dimensions"):
        dims = ", ".join(f"{item.get('id')}={item.get('value')}{item.get('unit')}" for item in plan["dimensions"])
        facts.append(f"已规划可追溯总体尺寸：{dims}。")
    if execution_probe and execution_probe.get("status") == "passed":
        facts.append("A4 工程图模板可新建临时工程图，并已插入基础视图。")
    if view_validation and view_validation.get("status") == "passed":
        facts.append("上视基础视图已有 API 插入成功依据，但未完成语义验证。")
    if hole_analysis:
        count = hole_analysis.get("summary", {}).get("hole_count", 0)
        validated = hole_analysis.get("summary", {}).get("validated_callout_count", 0)
        facts.append(f"孔分析已完成：发现 {count} 个孔特征，可靠可标注孔数 {validated}。")
    return facts


def _next_actions(blocking: List[Dict[str, Any]], manual_validation: Dict[str, Any] | None) -> List[str]:
    actions: List[str] = []
    topics = {item.get("topic") for item in blocking}
    if "view_orientation" in topics:
        actions.append("实现更稳定的视图语义验证：可见边、隐藏线、孔表达能力和基准可标注性。")
    if "hole_callouts" in topics:
        actions.append("继续攻 Hole Wizard/HoleCallout/显示尺寸读取，或填写带可靠 source 的人工孔元数据。")
    if manual_validation and manual_validation.get("status") == "blocked":
        actions.append("若要解除孔标注阻塞，需要补充带来源的人工元数据，而不是填写无来源数值。")
    actions.append("在当前样件稳定后，选择第二个简单零件做泛化回归测试。")
    return actions


def build_status(case_dir: Path) -> Dict[str, Any]:
    analysis = _read_json(case_dir / "part_analysis.json")
    plan = _read_json(case_dir / "drawing_plan.json")
    review = _read_json(case_dir / "review_report.json")
    probe = _read_json(case_dir / "execution_probe.json")
    view = _read_json(case_dir / "view_validation.json")
    hole = _read_json(case_dir / "hole_analysis.json")
    manual = _read_json(case_dir / "manual_metadata_validation.json")
    blocking = _blocking_items(review)
    return {
        "schema_version": "1.0",
        "created_at": datetime.now().isoformat(timespec="seconds"),
        "case_dir": str(case_dir),
        "part_number": (plan or analysis or {}).get("part_number"),
        "overall_status": "blocked" if blocking else "ready_for_next_stage",
        "execution_allowed": bool(review.get("execution_allowed")) if review else False,
        "artifact_status": _artifact_status(case_dir),
        "confirmed_facts": _confirmed_facts(analysis, plan, probe, view, hole),
        "blocking_items": blocking,
        "manual_metadata_status": manual.get("status") if manual else "missing",
        "next_actions": _next_actions(blocking, manual),
    }


def render_markdown(status: Dict[str, Any]) -> str:
    lines = [
        f"# 样件状态总览：{status.get('part_number')}",
        "",
        f"- 总状态：`{status.get('overall_status')}`",
        f"- 是否允许正式执行：`{str(status.get('execution_allowed')).lower()}`",
        f"- 人工元数据状态：`{status.get('manual_metadata_status')}`",
        "",
        "## 已确认事实",
        "",
    ]
    lines.extend(f"- {fact}" for fact in status.get("confirmed_facts", []))
    lines.extend(["", "## 阻塞项", ""])
    if status.get("blocking_items"):
        lines.extend(f"- `{item.get('id')}` {item.get('message')}" for item in status["blocking_items"])
    else:
        lines.append("- 无")
    lines.extend(["", "## 产物状态", ""])
    for key, item in status.get("artifact_status", {}).items():
        lines.append(f"- `{key}`：exists=`{str(item.get('exists')).lower()}` status=`{item.get('status')}`")
    lines.extend(["", "## 建议下一步", ""])
    lines.extend(f"- {action}" for action in status.get("next_actions", []))
    lines.append("")
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("case_dir")
    parser.add_argument("json_output")
    parser.add_argument("markdown_output")
    args = parser.parse_args()
    status = build_status(Path(args.case_dir))
    Path(args.json_output).write_text(json.dumps(status, ensure_ascii=False, indent=2), encoding="utf-8")
    Path(args.markdown_output).write_text(render_markdown(status), encoding="utf-8")
    print(json.dumps({
        "status": status["overall_status"],
        "execution_allowed": status["execution_allowed"],
        "blocking_count": len(status["blocking_items"]),
        "json_output": args.json_output,
        "markdown_output": args.markdown_output,
    }, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
