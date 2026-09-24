from __future__ import annotations

import argparse
import json
import subprocess
import sys
from datetime import datetime
from pathlib import Path


try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


WORKER = Path(__file__).resolve().parent


def now_stamp() -> str:
    return datetime.now().strftime("%Y%m%d_%H%M%S")


def require_file(path_text: str, label: str) -> Path:
    path = Path(path_text).expanduser().resolve()
    if not path.is_file():
        raise FileNotFoundError(f"{label}_NOT_FOUND={path}")
    return path


def ensure_dir(path_text: str | Path) -> Path:
    path = Path(path_text).expanduser().resolve()
    path.mkdir(parents=True, exist_ok=True)
    return path


def run_command(command: list[str], log_path: Path) -> list[str]:
    log_path.parent.mkdir(parents=True, exist_ok=True)
    completed = subprocess.run(
        command,
        cwd=str(WORKER),
        text=True,
        encoding="utf-8",
        errors="replace",
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
    )
    lines = completed.stdout.splitlines()
    log_path.write_text(completed.stdout, encoding="utf-8")
    for line in lines:
        print(line)
    if completed.returncode != 0:
        raise RuntimeError(
            f"STEP_FAILED={Path(command[0]).name if command else 'UNKNOWN'}; "
            f"EXIT_CODE={completed.returncode}; LOG={log_path}"
        )
    return lines


def run_python(script_name: str, args: list[str], step_name: str, log_dir: Path) -> list[str]:
    script = WORKER / script_name
    if not script.is_file():
        raise FileNotFoundError(f"SCRIPT_NOT_FOUND={script}")
    print(f"\n=== THREAD_AUDIT_V4_STAGE={step_name} ===")
    return run_command(
        [sys.executable, str(script), *args],
        log_dir / f"{step_name}.log",
    )


def run_powershell(script_name: str, args: list[str], step_name: str, log_dir: Path) -> list[str]:
    script = WORKER / script_name
    if not script.is_file():
        raise FileNotFoundError(f"SCRIPT_NOT_FOUND={script}")
    print(f"\n=== THREAD_AUDIT_V4_STAGE={step_name} ===")
    return run_command(
        [
            "powershell.exe",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(script),
            *args,
        ],
        log_dir / f"{step_name}.log",
    )


def find_output_path(lines: list[str], key: str) -> Path:
    prefix = key + "="
    for line in reversed(lines):
        if line.startswith(prefix):
            return Path(line[len(prefix) :].strip()).expanduser().resolve()
    raise RuntimeError(f"{key}_NOT_FOUND_IN_STAGE_OUTPUT")


def find_output_path_with_fallback(lines: list[str], key: str, output_root: Path, fallback_pattern: str) -> Path:
    try:
        parsed = find_output_path(lines, key)
        if parsed.is_file():
            return parsed
    except Exception:
        pass
    fallback = latest_file(output_root, fallback_pattern)
    if fallback is not None:
        print(f"PATH_FALLBACK_USED={key}|PATTERN={fallback_pattern}|PATH={fallback}")
        return fallback
    raise RuntimeError(f"{key}_NOT_FOUND_AND_FALLBACK_EMPTY={fallback_pattern}")


def parse_counts(lines: list[str], key: str) -> str:
    prefix = key + "="
    for line in reversed(lines):
        if line.startswith(prefix):
            return line[len(prefix) :].strip()
    return ""


def latest_file(root: Path, pattern: str) -> Path | None:
    candidates = [
        path
        for path in root.rglob(pattern)
        if path.is_file() and path.stat().st_size > 0
    ]
    if not candidates:
        return None
    return max(candidates, key=lambda item: item.stat().st_mtime)


def usable(path: Path) -> bool:
    return path.is_file() and path.stat().st_size > 0


def choose_or_run_powershell(
    *,
    output_root: Path,
    preferred: Path,
    reuse_pattern: str,
    reuse: bool,
    script_name: str,
    args: list[str],
    step_name: str,
    log_dir: Path,
) -> Path:
    if reuse:
        cached = latest_file(output_root, reuse_pattern)
        if cached is not None:
            print(f"\n=== THREAD_AUDIT_V4_STAGE={step_name} ===")
            print(f"REUSE_CACHED_{step_name.upper()}={cached}")
            return cached
    if usable(preferred):
        print(f"\n=== THREAD_AUDIT_V4_STAGE={step_name} ===")
        print(f"REUSE_EXISTING_{step_name.upper()}={preferred}")
        return preferred
    run_powershell(script_name, args, step_name, log_dir)
    return preferred


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Run SolidWorks thread audit V4 in dual-mode. Current automatic path runs hole-side audit when screw metadata is absent."
    )
    parser.add_argument("assembly_path")
    parser.add_argument("output_root")
    parser.add_argument(
        "--background-image",
        default="",
        help="Optional real-view PNG used by the HTML locator page. Defaults to output_root/thread_audit_real_view.png when present.",
    )
    parser.add_argument(
        "--skip-visual",
        action="store_true",
        help="Skip HTML locator generation.",
    )
    parser.add_argument(
        "--no-reuse",
        action="store_true",
        help="Force rerun all expensive SolidWorks probe stages instead of reusing latest intermediate JSON files.",
    )
    parser.add_argument(
        "--open-cavity-hole-regex",
        action="append",
        default=[],
        help=(
            "额外指定开放到镂空/空腔的孔识别规则，可重复传入；匹配 occurrence + feature_name 后，"
            "孔侧检测会跳过盲孔底部余量规则。"
        ),
    )
    args = parser.parse_args()

    assembly = require_file(args.assembly_path, "ASSEMBLY")
    output_root = ensure_dir(args.output_root)
    run_dir = ensure_dir(output_root / f"thread_audit_v4_run_{now_stamp()}")
    log_dir = ensure_dir(run_dir / "logs")

    print(f"THREAD_AUDIT_V4_RUN_DIR={run_dir}")
    print(f"ASSEMBLY_PATH={assembly}")
    reuse = not args.no_reuse
    print(f"AUDIT_STRATEGY=DUAL_MODE_AUTO|REUSE_INTERMEDIATES={reuse}")

    hole_params = run_dir / "hole_wizard_thread_parameters_earlybound_v4.json"
    cylinder_faces = run_dir / "assembly_cylinder_face_extents_earlybound_v4.json"
    cylinder_axes = run_dir / "assembly_cylinder_axes_earlybound_v4.json"
    hole_points = run_dir / "holewizard_sketch_points_earlybound_v4.json"

    hole_params = choose_or_run_powershell(
        output_root=output_root,
        preferred=hole_params,
        reuse_pattern="hole_wizard_thread_parameters_earlybound_v4*.json",
        reuse=reuse,
        script_name="probe_hole_wizard_thread_parameters_earlybound_v4.ps1",
        args=[str(assembly), str(hole_params)],
        step_name="hole_wizard_parameters",
        log_dir=log_dir,
    )
    cylinder_faces = choose_or_run_powershell(
        output_root=output_root,
        preferred=cylinder_faces,
        reuse_pattern="assembly_cylinder_face_extents_earlybound_v4*.json",
        reuse=reuse,
        script_name="probe_assembly_cylinder_face_extents_earlybound_v4.ps1",
        args=[str(assembly), str(cylinder_faces)],
        step_name="cylinder_face_extents",
        log_dir=log_dir,
    )
    cylinder_axes = choose_or_run_powershell(
        output_root=output_root,
        preferred=cylinder_axes,
        reuse_pattern="assembly_cylinder_axes_earlybound_v4*.json",
        reuse=reuse,
        script_name="probe_assembly_cylinder_axes_earlybound_v4.ps1",
        args=[str(assembly), str(cylinder_axes)],
        step_name="cylinder_axes",
        log_dir=log_dir,
    )
    hole_points = choose_or_run_powershell(
        output_root=output_root,
        preferred=hole_points,
        reuse_pattern="holewizard_sketch_points_earlybound_v4*.json",
        reuse=reuse,
        script_name="probe_holewizard_sketch_points_earlybound_v4.ps1",
        args=[str(assembly), str(hole_points)],
        step_name="holewizard_sketch_points",
        log_dir=log_dir,
    )

    own_diag_lines = run_python(
        "diagnose_holewizard_to_own_cylinder_v4.py",
        [str(cylinder_axes), str(hole_points), str(output_root)],
        "own_cylinder_diagnostic",
        log_dir,
    )
    own_diag = find_output_path_with_fallback(
        own_diag_lines,
        "RESULT_PATH",
        output_root,
        "holewizard_own_cylinder_diagnostic_v4_*\\*.json",
    )

    partner_lines = run_python(
        "diagnose_tapped_hole_partner_visibility_v4.py",
        [str(own_diag), str(cylinder_faces), str(output_root)],
        "partner_visibility",
        log_dir,
    )
    partner_visibility = find_output_path_with_fallback(
        partner_lines,
        "RESULT_PATH",
        output_root,
        "tapped_hole_partner_visibility_v4_*\\*.json",
    )

    fastener_lines = run_python(
        "v4_probe_external_partner_fastener_evidence.py",
        [str(partner_visibility), str(output_root)],
        "external_partner_fastener_evidence",
        log_dir,
    )
    fastener_evidence = find_output_path_with_fallback(
        fastener_lines,
        "RESULT_PATH",
        output_root,
        "external_partner_fastener_evidence_v4_*\\*.json",
    )
    eligible_count = parse_counts(fastener_lines, "SCREW_HOLE_RULE_ELIGIBLE_LINK_COUNT")

    mode = "FULL_FASTENER_MODE_READY" if eligible_count and int(eligible_count) > 0 else "HOLE_SIDE_ONLY"
    print(f"THREAD_AUDIT_MODE_SELECTED={mode}")

    # Keep the original coverage report because it gives a clean boundary statement.
    coverage_lines = run_python(
        "build_thread_audit_coverage_and_hole_side_v4.py",
        [str(hole_params), str(fastener_evidence), str(output_root)],
        "coverage_and_hole_side_boundary",
        log_dir,
    )
    coverage_json = find_output_path_with_fallback(
        coverage_lines,
        "RESULT_PATH",
        output_root,
        "thread_audit_coverage_v4_*\\*.json",
    )
    coverage_csv = find_output_path_with_fallback(
        coverage_lines,
        "DETAIL_CSV_PATH",
        output_root,
        "thread_audit_coverage_v4_*\\*.csv",
    )

    geometry_args = [str(hole_params), str(own_diag), str(cylinder_faces), str(partner_visibility), str(output_root)]
    for pattern in args.open_cavity_hole_regex:
        geometry_args.extend(["--open-cavity-hole-regex", pattern])

    geometry_lines = run_python(
        "v4_build_hole_side_geometry_audit.py",
        geometry_args,
        "hole_side_geometry_audit",
        log_dir,
    )
    geometry_csv = find_output_path_with_fallback(
        geometry_lines,
        "CSV_PATH",
        output_root,
        "hole_side_geometry_audit_v4_*\\*.csv",
    )
    geometry_json = find_output_path_with_fallback(
        geometry_lines,
        "REPORT_PATH",
        output_root,
        "hole_side_geometry_audit_v4_*\\*.json",
    )
    geometry_summary = find_output_path_with_fallback(
        geometry_lines,
        "SUMMARY_PATH",
        output_root,
        "hole_side_geometry_audit_v4_*\\*.md",
    )

    html_path = ""
    visual_report = ""
    background = Path(args.background_image).expanduser().resolve() if args.background_image else output_root / "thread_audit_real_view.png"
    if not args.skip_visual and background.is_file():
        visual_lines = run_python(
            "v4_build_thread_audit_image_locator_page.py",
            [str(geometry_csv), str(background), str(output_root), "--assembly-path", str(assembly), "--embed-image"],
            "image_locator_page",
            log_dir,
        )
        html_path = str(
            find_output_path_with_fallback(
                visual_lines,
                "HTML_PATH",
                output_root,
                "thread_audit_image_locator_v*\\*.html",
            )
        )
        visual_report = str(
            find_output_path_with_fallback(
                visual_lines,
                "REPORT_PATH",
                output_root,
                "thread_audit_image_locator_v*\\*.json",
            )
        )
    elif not args.skip_visual:
        print(f"VISUAL_SKIPPED=BACKGROUND_IMAGE_NOT_FOUND={background}")

    manifest = {
        "workflow": "THREAD_AUDIT_V4_DUAL_MODE_AUTO",
        "started_at": datetime.now().isoformat(timespec="seconds"),
        "assembly_path": str(assembly),
        "run_dir": str(run_dir),
        "mode_selected": mode,
        "eligible_screw_hole_link_count": int(eligible_count or 0),
        "open_cavity_hole_regex": list(args.open_cavity_hole_regex),
        "outputs": {
            "hole_parameters_json": str(hole_params),
            "cylinder_faces_json": str(cylinder_faces),
            "cylinder_axes_json": str(cylinder_axes),
            "hole_points_json": str(hole_points),
            "own_cylinder_diagnostic_json": str(own_diag),
            "partner_visibility_json": str(partner_visibility),
            "fastener_evidence_json": str(fastener_evidence),
            "coverage_json": str(coverage_json),
            "coverage_csv": str(coverage_csv),
            "hole_side_geometry_csv": str(geometry_csv),
            "hole_side_geometry_json": str(geometry_json),
            "hole_side_geometry_summary": str(geometry_summary),
            "visual_html": html_path,
            "visual_report": visual_report,
        },
    }
    manifest_path = run_dir / "thread_audit_v4_workflow_manifest.json"
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")

    print(f"FINAL_MODE={mode}")
    print(f"FINAL_GEOMETRY_CSV={geometry_csv}")
    print(f"FINAL_GEOMETRY_REPORT={geometry_json}")
    print(f"FINAL_SUMMARY={geometry_summary}")
    if html_path:
        print(f"FINAL_VISUAL_HTML={html_path}")
    print(f"FINAL_MANIFEST_PATH={manifest_path}")
    print("THREAD_AUDIT_V4_WORKFLOW_STATUS=SUCCESS")


if __name__ == "__main__":
    main()
