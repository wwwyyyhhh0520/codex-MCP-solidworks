"""Run the MVP pipeline with per-step subprocess timeouts."""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List


def _run_step(name: str, args: List[str], timeout_seconds: int, cwd: Path) -> Dict[str, Any]:
    started = datetime.now()
    try:
        completed = subprocess.run(
            [sys.executable, *args],
            cwd=str(cwd),
            text=True,
            capture_output=True,
            timeout=timeout_seconds,
            check=False,
        )
        return {
            "name": name,
            "status": "passed" if completed.returncode == 0 else "failed",
            "returncode": completed.returncode,
            "started_at": started.isoformat(timespec="seconds"),
            "finished_at": datetime.now().isoformat(timespec="seconds"),
            "timeout_seconds": timeout_seconds,
            "stdout": completed.stdout.strip(),
            "stderr": completed.stderr.strip(),
        }
    except subprocess.TimeoutExpired as exc:
        return {
            "name": name,
            "status": "timeout",
            "returncode": None,
            "started_at": started.isoformat(timespec="seconds"),
            "finished_at": datetime.now().isoformat(timespec="seconds"),
            "timeout_seconds": timeout_seconds,
            "stdout": (exc.stdout or "").strip() if isinstance(exc.stdout, str) else "",
            "stderr": (exc.stderr or "").strip() if isinstance(exc.stderr, str) else "",
        }


def run_pipeline(
    source_file: str,
    case_dir: Path,
    template_file: str,
    cwd: Path,
    timeout_seconds: int,
    probe_first: bool = False,
    use_macro_bridge: bool = False,
    macro_report_file: Path | None = None,
) -> Dict[str, Any]:
    case_dir.mkdir(parents=True, exist_ok=True)
    steps = [
        (
            "read_part",
            [
                "-m", "src.auto_drawing.solidworks_reader",
                source_file,
                str(case_dir / "part_analysis.json"),
            ],
        ),
        (
            "make_plan",
            [
                "-m", "src.auto_drawing.planner",
                str(case_dir / "part_analysis.json"),
                str(case_dir / "drawing_plan.json"),
                "--templates", template_file,
            ],
        ),
        (
            "dimension_plan",
            [
                "-m", "src.auto_drawing.dimension_planner",
                str(case_dir / "part_analysis.json"),
                str(case_dir / "drawing_plan.json"),
                str(case_dir / "drawing_plan.json"),
            ],
        ),
        (
            "hole_analysis",
            [
                "-m", "src.auto_drawing.hole_analyzer",
                str(case_dir / "part_analysis.json"),
                str(case_dir / "hole_analysis.json"),
                "--plan-file", str(case_dir / "drawing_plan.json"),
                "--plan-output-file", str(case_dir / "drawing_plan.json"),
            ],
        ),
        (
            "review",
            [
                "-m", "src.auto_drawing.reviewer",
                str(case_dir / "part_analysis.json"),
                str(case_dir / "drawing_plan.json"),
                str(case_dir / "review_report.json"),
            ],
        ),
        (
            "case_status",
            [
                "-m", "src.auto_drawing.case_status",
                str(case_dir),
                str(case_dir / "case_status.json"),
                str(case_dir / "case_status.md"),
            ],
        ),
    ]
    if probe_first:
        steps.insert(0, (
            "open_close_probe",
            [
                "-m", "src.auto_drawing.solidworks_reader",
                source_file,
                str(case_dir / "open_probe.json"),
                "--probe-only",
            ],
        ))
    report: Dict[str, Any] = {
        "schema_version": "1.0",
        "created_at": datetime.now().isoformat(timespec="seconds"),
        "source_file": source_file,
        "case_dir": str(case_dir),
        "status": "running",
        "steps": [],
    }
    if use_macro_bridge:
        macro_report = macro_report_file or (case_dir / "macro_bridge_probe.json")
        report["macro_bridge"] = {
            "expected_report": str(macro_report),
            "import_output": str(case_dir / "macro_bridge_import.json"),
        }
    for name, command in steps:
        step = _run_step(name, command, timeout_seconds, cwd)
        report["steps"].append(step)
        Path(case_dir / "pipeline_report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        if step["status"] != "passed":
            if use_macro_bridge:
                macro_report = macro_report_file or (case_dir / "macro_bridge_probe.json")
                if macro_report.is_file():
                    import_step = _run_step(
                        "macro_bridge_import",
                        [
                            "-m", "src.auto_drawing.macro_bridge_importer",
                            str(macro_report),
                            str(case_dir / "macro_bridge_import.json"),
                        ],
                        timeout_seconds,
                        cwd,
                    )
                    report["steps"].append(import_step)
                    report["status"] = "macro_bridge_imported" if import_step["status"] == "passed" else "blocked"
                    report["blocked_at"] = None if import_step["status"] == "passed" else "macro_bridge_import"
                    Path(case_dir / "pipeline_report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
                    return report
                report["status"] = "waiting_for_macro_bridge"
                report["blocked_at"] = name
                report["waiting_for"] = str(macro_report)
                Path(case_dir / "pipeline_report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
                return report
            report["status"] = "blocked"
            report["blocked_at"] = name
            Path(case_dir / "pipeline_report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
            return report
    report["status"] = "completed"
    Path(case_dir / "pipeline_report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    return report


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("source_file")
    parser.add_argument("case_dir")
    parser.add_argument("--templates", default="config/templates.json")
    parser.add_argument("--timeout-seconds", type=int, default=45)
    parser.add_argument("--probe-first", action="store_true")
    parser.add_argument("--use-macro-bridge", action="store_true")
    parser.add_argument("--macro-report-file", default=None)
    args = parser.parse_args()
    report = run_pipeline(
        args.source_file,
        Path(args.case_dir),
        args.templates,
        Path.cwd(),
        args.timeout_seconds,
        args.probe_first,
        args.use_macro_bridge,
        Path(args.macro_report_file) if args.macro_report_file else None,
    )
    print(json.dumps({
        "status": report["status"],
        "blocked_at": report.get("blocked_at"),
        "case_dir": report["case_dir"],
    }, ensure_ascii=False))
    return 0 if report["status"] in {"completed", "macro_bridge_imported", "waiting_for_macro_bridge"} else 2


if __name__ == "__main__":
    raise SystemExit(main())
