import subprocess
import sys
import os
from pathlib import Path

from fastmcp import FastMCP

mcp = FastMCP("solidworks_audit_v2")

WORKER = Path(r"C:\Users\18015\Desktop\sw_worker")
PYTHON = Path(r"C:\Users\18015\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe")


def run_worker_script(args: list[str], timeout: int) -> dict:
    env = os.environ.copy()
    env.setdefault("SW_AUTOMATION_REQUIRE_EXISTING", "1")
    completed = subprocess.run(
        [str(PYTHON), *args],
        cwd=str(WORKER),
        text=True,
        capture_output=True,
        timeout=timeout,
        env=env,
    )
    return {
        "returncode": completed.returncode,
        "stdout": completed.stdout,
        "stderr": completed.stderr,
    }


def parse_key_values(text: str) -> dict:
    result = {}
    for line in (text or "").splitlines():
        if "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip()
        if key and key.replace("_", "").isalnum():
            result[key] = value.strip()
    return result


@mcp.tool()
def run_local_thread_audit_v4(
    assembly_path: str,
    output_root: str,
    no_reuse: bool = True,
    timeout_seconds: int = 3600,
) -> dict:
    """
    Run the local SolidWorks thread/hole-side audit on this PC.

    This writes reports under output_root and skips HTML generation.
    """
    script = WORKER / "run_thread_audit_v4.py"
    if not script.is_file():
        return {"status": "ERROR", "error": f"AUDIT_SCRIPT_NOT_FOUND={script}"}

    command = [str(script), assembly_path, output_root, "--skip-visual"]
    if no_reuse:
        command.append("--no-reuse")

    result = run_worker_script(command, timeout_seconds)
    values = parse_key_values(result["stdout"])
    ok = result["returncode"] == 0 and values.get("THREAD_AUDIT_V4_WORKFLOW_STATUS") == "SUCCESS"
    return {
        "status": "SUCCESS" if ok else "ERROR",
        **result,
        "paths": values,
        "assembly_path": assembly_path,
        "output_root": output_root,
    }


@mcp.tool()
def build_thread_compare_xlsx_v4(
    target_csv: str,
    baseline_csv: str,
    output_root: str,
    timeout_seconds: int = 300,
) -> dict:
    """
    Build the no-thread vs with-thread Excel comparison table from two audit CSV files.
    """
    script = WORKER / "v4_build_thread_compare_xlsx.py"
    if not script.is_file():
        return {"status": "ERROR", "error": f"COMPARE_SCRIPT_NOT_FOUND={script}"}

    result = run_worker_script(
        [
            str(script),
            "--target-csv",
            target_csv,
            "--baseline-csv",
            baseline_csv,
            "--output-root",
            output_root,
        ],
        timeout_seconds,
    )
    values = parse_key_values(result["stdout"])
    ok = result["returncode"] == 0 and values.get("THREAD_COMPARE_XLSX_V4_STATUS") == "SUCCESS"
    return {
        "status": "SUCCESS" if ok else "ERROR",
        **result,
        "paths": values,
        "target_csv": target_csv,
        "baseline_csv": baseline_csv,
        "output_root": output_root,
    }


@mcp.tool()
def check_thread_holes_against_checklist_v4(
    checklist_path: str,
    model_csv: str,
    output_root: str,
    include_model_extra: bool = True,
    timeout_seconds: int = 300,
) -> dict:
    """
    Compare a structured customer checklist against a SolidWorks thread audit CSV.

    checklist_path supports .xlsx/.csv with columns such as:
    part_key / feature_key / hole_spec / expected_count
    or Chinese aliases: 零件 / 孔特征 / 孔规格 / 数量.
    """
    script = WORKER / "v4_check_thread_holes_against_checklist.py"
    if not script.is_file():
        return {"status": "ERROR", "error": f"CHECKLIST_COMPARE_SCRIPT_NOT_FOUND={script}"}

    command = [
        str(script),
        "--checklist",
        checklist_path,
        "--model-csv",
        model_csv,
        "--output-root",
        output_root,
    ]
    if not include_model_extra:
        command.append("--no-extra")

    result = run_worker_script(command, timeout_seconds)
    values = parse_key_values(result["stdout"])
    ok = result["returncode"] == 0 and values.get("THREAD_CHECKLIST_COMPARE_V4_STATUS") == "SUCCESS"
    return {
        "status": "SUCCESS" if ok else "ERROR",
        **result,
        "paths": values,
        "checklist_path": checklist_path,
        "model_csv": model_csv,
        "output_root": output_root,
    }


@mcp.tool()
def locate_thread_audit_issue_v4(
    assembly_path: str,
    audit_csv_path: str,
    issue_id: str,
) -> dict:
    """
    在 SolidWorks 中定位螺纹/孔侧审核问题项。
    issue_id 支持 HOLE-xxxx 或 THR-xxxx。
    当前定位到装配体组件级。
    """
    script = WORKER / "v4_locate_thread_issue_in_sw.py"

    if not script.is_file():
        return {
            "status": "ERROR",
            "error": f"LOCATOR_SCRIPT_NOT_FOUND={script}",
        }

    env = os.environ.copy()
    env.setdefault("SW_AUTOMATION_REQUIRE_EXISTING", "1")
    completed = subprocess.run(
        [
            str(PYTHON),
            str(script),
            "--assembly",
            assembly_path,
            "--csv",
            audit_csv_path,
            "--issue-id",
            issue_id,
        ],
        cwd=str(WORKER),
        text=True,
        capture_output=True,
        timeout=120,
        env=env,
    )

    ok = completed.returncode == 0 and "COMPONENT_SELECTED=True" in completed.stdout

    return {
        "status": "SUCCESS" if ok else "ERROR",
        "returncode": completed.returncode,
        "component_selected": ok,
        "stdout": completed.stdout,
        "stderr": completed.stderr,
        "assembly_path": assembly_path,
        "audit_csv_path": audit_csv_path,
        "issue_id": issue_id,
    }


if __name__ == "__main__":
    mcp.run(transport="stdio", show_banner=False)




