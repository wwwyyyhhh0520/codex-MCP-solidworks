"""Minimal stdio MCP facade for the existing SolidWorks workers."""
from __future__ import annotations

import json
import os
import subprocess
from pathlib import Path
from typing import Any

from mcp.server.fastmcp import FastMCP

ROOT = Path(__file__).resolve().parents[1]
SERVER_NAME = "solidworks-mcp"
mcp = FastMCP(SERVER_NAME)


def _result(**kwargs: Any) -> dict[str, Any]:
    return kwargs


def _run(cmd: list[str], timeout: int = 600) -> tuple[int, str, str]:
    try:
        p = subprocess.run(cmd, cwd=str(ROOT), capture_output=True, text=True,
                           timeout=timeout, check=False)
        return p.returncode, p.stdout or "", p.stderr or ""
    except subprocess.TimeoutExpired as exc:
        return 124, exc.stdout or "", f"worker timeout after {timeout}s"
    except OSError as exc:
        return 127, "", str(exc)


def _read_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def _find_extractor() -> Path:
    candidates = [
        ROOT / "general_semantic_extractor" / "bin" / "Debug" / "net10.0-windows" / "general_semantic_extractor.exe",
        ROOT / "general_semantic_extractor" / "bin" / "Release" / "net10.0-windows" / "general_semantic_extractor.exe",
    ]
    for p in candidates:
        if p.exists():
            return p
    raise FileNotFoundError("general_semantic_extractor executable is not built")


def _find_runtime() -> Path:
    candidates = [
        ROOT / "policy_drawing_runtime" / "bin" / "Debug" / "net10.0-windows" / "policy_drawing_runtime.exe",
        ROOT / "policy_drawing_runtime" / "bin" / "Release" / "net10.0-windows" / "policy_drawing_runtime.exe",
    ]
    for p in candidates:
        if p.exists():
            return p
    raise FileNotFoundError("policy_drawing_runtime executable is not built")


@mcp.tool()
def get_capabilities() -> dict[str, Any]:
    """Return available pipeline capabilities without launching SolidWorks."""
    return _result(success=True, server=SERVER_NAME, capabilities={
        "semantic_extraction": True,
        "drawing_planning": True,
        "drawing_creation": True,
        "native_annotation_execution": True,
        "validation_export": True,
        "feature_state_probe": True,
    }, solidworks_launched=False)


@mcp.tool()
def inspect_model(model_path: str, output_directory: str) -> dict[str, Any]:
    """Invoke the existing semantic extractor for one model."""
    model = Path(model_path).expanduser().resolve()
    out_dir = Path(output_directory).expanduser().resolve()
    if not model.is_file() or model.suffix.lower() != ".sldprt":
        return _result(success=False, error="invalid_model_path", message="model_path must be an existing .SLDPRT file")
    out_dir.mkdir(parents=True, exist_ok=True)
    semantic = out_dir / f"{model.stem}_model_semantics.json"
    log = out_dir / f"{model.stem}_extractor.log"
    try:
        exe = _find_extractor()
        rc, stdout, stderr = _run([str(exe), "--input", str(model), "--output", str(semantic), "--log", str(log)])
    except Exception as exc:
        return _result(success=False, error="worker_start_failure", message=str(exc))
    payload: dict[str, Any] = {"success": rc == 0, "semantic_output_path": str(semantic),
                               "return_code": rc, "stdout": stdout[-4000:], "stderr": stderr[-4000:]}
    if semantic.exists():
        try:
            data = _read_json(semantic)
            payload["feature_count"] = len(data.get("feature_tree", []))
            payload["holewizard_count"] = len(data.get("holewizard_semantics", []))
            payload["hole_feature_count"] = data.get("holewzd_nodes", len(data.get("holewizard_semantics", [])))
            payload["hole_instance_count"] = data.get("hole_instance_count", data.get("sketch_point_rows", 0))
            payload["physical_opening_count"] = data.get("physical_opening_count", 0)
            payload["proven_opening_count"] = data.get("proven_opening_count", 0)
            payload["ambiguous_opening_count"] = data.get("ambiguous_opening_count", 0)
            payload["unresolved_opening_count"] = data.get("unresolved_opening_count", 0)
            payload["bindable_circular_edge_count"] = data.get("bindable_circular_edge_count", 0)
            payload["semantic_to_physical_proven"] = data.get("semantic_to_physical_proven", 0)
            payload["semantic_to_physical_ambiguous"] = data.get("semantic_to_physical_ambiguous", 0)
            payload["semantic_to_physical_unresolved"] = data.get("semantic_to_physical_unresolved", data.get("hole_instance_count", 0))
            payload["body_face_count"] = data.get("body_face_count", data.get("body_topology", {}).get("face_count", 0))
            payload["body_cylindrical_face_count"] = data.get("body_cylindrical_face_count", data.get("body_topology", {}).get("cylindrical_face_count", 0))
            payload["body_conical_face_count"] = data.get("body_conical_face_count", data.get("body_topology", {}).get("conical_face_count", 0))
            payload["physical_opening_count"] = data.get("physical_opening_count", data.get("body_topology", {}).get("internal_hole_count", 0))
            payload["internal_hole_count"] = data.get("internal_hole_count", data.get("body_topology", {}).get("internal_hole_count", 0))
            payload["external_cylinder_count"] = data.get("external_cylinder_count", data.get("body_topology", {}).get("external_cylinder_count", 0))
            payload["ambiguous_cylinder_count"] = data.get("ambiguous_cylinder_count", data.get("body_topology", {}).get("ambiguous_cylinder_count", 0))
            payload["holewizard_attributed_openings"] = data.get("holewizard_attributed_openings", 0)
            payload["instance_proven"] = data.get("instance_proven", 0)
            payload["group_proven"] = data.get("group_proven", 0)
            payload["ambiguous"] = data.get("ambiguous", 0)
            payload["unresolved"] = data.get("unresolved", 0)
            payload["bindable_opening_edges"] = data.get("bindable_opening_edges", 0)
            payload["bounding_envelope"] = data.get("bounding_box", data.get("bounding_envelope"))
            payload["warnings"] = data.get("warnings", [])
            payload["unresolved"] = data.get("unresolved", data.get("errors", []))
        except Exception as exc:
            payload["parse_error"] = str(exc)
    return payload


@mcp.tool()
def probe_feature_history(model_path: str, output_directory: str, feature_indices: list[int]) -> dict[str, Any]:
    """Measure reversible topology deltas at generic top-level feature boundaries."""
    model = Path(model_path).expanduser().resolve()
    out_dir = Path(output_directory).expanduser().resolve()
    if not model.is_file() or model.suffix.lower() != ".sldprt":
        return _result(success=False, error="invalid_model_path", message="model_path must be an existing .SLDPRT file")
    if not feature_indices:
        return _result(success=False, error="missing_feature_indices", message="feature_indices must contain one or more top-level feature indices")
    out_dir.mkdir(parents=True, exist_ok=True)
    probe = out_dir / f"{model.stem}_feature_state_probe.json"
    log = out_dir / f"{model.stem}_feature_state_probe.log"
    try:
        exe = _find_extractor()
        encoded = ",".join(str(int(i)) for i in feature_indices)
        rc, stdout, stderr = _run([
            str(exe), "--feature-state-probe", "--input", str(model),
            "--output", str(probe), "--feature-indices", encoded
        ], timeout=600)
    except Exception as exc:
        return _result(success=False, error="worker_start_failure", message=str(exc))
    payload: dict[str, Any] = {
        "success": rc == 0,
        "probe_output_path": str(probe),
        "return_code": rc,
        "stdout": stdout[-4000:],
        "stderr": stderr[-4000:],
        "feature_indices": feature_indices,
    }
    if probe.exists():
        try:
            data = _read_json(probe)
            payload.update({
                "feature_state_api_selected": data.get("feature_state_api_selected"),
                "reversible_state_probe_implemented": data.get("reversible_state_probe_implemented", False),
                "source_model_saved": data.get("source_model_saved", False),
                "source_model_restored": data.get("source_model_restored", False),
                "suppression_restore_verified": data.get("suppression_restore_verified", False),
                "states": data.get("states", []),
                "deltas": data.get("deltas", []),
                "error": data.get("error"),
            })
        except Exception as exc:
            payload["parse_error"] = str(exc)
    return payload


@mcp.tool()
def plan_drawing(semantic_output_path: str, output_directory: str) -> dict[str, Any]:
    """Invoke the existing drawing planner without launching SolidWorks."""
    semantics = Path(semantic_output_path).expanduser().resolve()
    out_dir = Path(output_directory).expanduser().resolve()
    if not semantics.is_file():
        return _result(success=False, error="invalid_semantic_path", message="semantic_output_path does not exist")
    out_dir.mkdir(parents=True, exist_ok=True)
    plan = out_dir / "drawing_plan.json"
    cmd = ["python", str(ROOT / "general_drawing_planner" / "general_drawing_planner.py"),
           "--semantics", str(semantics), "--output", str(plan),
           "--client-policy", str(ROOT / "config" / "client_drawing_policy.json"),
           "--template-profile", str(ROOT / "config" / "template_profiles" / "company_landscape.json")]
    rc, stdout, stderr = _run(cmd, timeout=120)
    payload: dict[str, Any] = {"success": rc == 0, "drawing_plan_path": str(plan),
                               "return_code": rc, "stdout": stdout[-4000:], "stderr": stderr[-4000:]}
    if plan.exists():
        try:
            data = _read_json(plan)
            payload["view_count"] = len(data.get("views", []))
            intents = data.get("intents", [])
            payload["annotation_intent_counts"] = {
                str(k): sum(1 for i in intents if i.get("kind") == k)
                for k in sorted({i.get("kind") for i in intents if i.get("kind")})
            }
            payload["warnings"] = data.get("warnings", [])
            payload["unresolved"] = data.get("unresolved_intents", data.get("unresolved", []))
        except Exception as exc:
            payload["parse_error"] = str(exc)
    return payload


@mcp.tool()
def create_drawing(model_path: str, drawing_plan_path: str, output_directory: str) -> dict[str, Any]:
    """Invoke the existing accepted drawing runtime with its safety policy."""
    model = Path(model_path).expanduser().resolve()
    plan = Path(drawing_plan_path).expanduser().resolve()
    out_dir = Path(output_directory).expanduser().resolve()
    if not model.is_file() or not plan.is_file():
        return _result(success=False, error="invalid_input_path", message="model_path and drawing_plan_path must exist")
    return _result(success=False, error="not_enabled_in_phase_1", message="create_drawing is exposed but intentionally not executed in Phase 1")


@mcp.tool()
def create_hole_callout_canary(model_path: str, template_path: str, output_directory: str,
                               output_stem: str, facts_path: str) -> dict[str, Any]:
    """Run the existing hole-only native canary through the policy runtime."""
    model = Path(model_path).expanduser().resolve()
    template = Path(template_path).expanduser().resolve()
    out_dir = Path(output_directory).expanduser().resolve()
    facts = Path(facts_path).expanduser().resolve()
    if not model.is_file() or model.suffix.lower() != ".sldprt":
        return _result(success=False, error="invalid_model_path", message="model_path must be an existing .SLDPRT file")
    if not template.is_file() or template.suffix.lower() != ".drwdot":
        return _result(success=False, error="invalid_template_path", message="template_path must be an existing .DRWDOT file")
    if not facts.is_file() or facts.suffix.lower() != ".json":
        return _result(success=False, error="invalid_facts_path", message="facts_path must be an existing JSON file")
    out_dir.mkdir(parents=True, exist_ok=True)
    result_path = out_dir / f"{output_stem}_hole_callout_canary_result.json"
    log_path = out_dir / f"{output_stem}_hole_callout_canary.log"
    try:
        runtime = _find_runtime()
        rc, stdout, stderr = _run([
            str(runtime), "--hole-callout-canary", str(model), str(template),
            str(out_dir), output_stem, str(facts)
        ], timeout=900)
    except Exception as exc:
        return _result(success=False, error="worker_start_failure", message=str(exc), result_path=str(result_path), log_path=str(log_path))
    log_path.write_text(stdout + ("\nSTDERR:\n" + stderr if stderr else ""), encoding="utf-8")
    payload: dict[str, Any] = {
        "success": rc == 0,
        "return_code": rc,
        "stdout": stdout[-8000:],
        "stderr": stderr[-8000:],
        "result_path": str(result_path),
        "log_path": str(log_path),
        "model_path": str(model),
        "template_path": str(template),
        "facts_path": str(facts),
    }
    if result_path.exists():
        try:
            payload["result"] = _read_json(result_path)
        except Exception as exc:
            payload["result_parse_error"] = str(exc)
    return payload


@mcp.tool()
def probe_hole_view_representability(model_path: str, template_path: str, drawing_plan_path: str,
                                     semantic_output_path: str, output_directory: str) -> dict[str, Any]:
    """Probe drawing-view circular entities through the existing runtime."""
    model = Path(model_path).expanduser().resolve(); template = Path(template_path).expanduser().resolve()
    plan = Path(drawing_plan_path).expanduser().resolve(); semantics = Path(semantic_output_path).expanduser().resolve(); out_dir = Path(output_directory).expanduser().resolve()
    if not model.is_file() or model.suffix.lower() != ".sldprt": return _result(success=False, error="invalid_model_path")
    if not template.is_file() or template.suffix.lower() != ".drwdot": return _result(success=False, error="invalid_template_path")
    if not plan.is_file() or not semantics.is_file(): return _result(success=False, error="invalid_plan_or_semantic_path")
    out_dir.mkdir(parents=True, exist_ok=True); artifact = out_dir / "hole_view_representability.json"; log = out_dir / "hole_view_representability.log"
    try: runtime = _find_runtime(); rc, stdout, stderr = _run([str(runtime), "--probe-hole-view-representability", str(model), str(template), str(plan), str(semantics), str(artifact)], timeout=900)
    except Exception as exc: return _result(success=False, error="worker_start_failure", message=str(exc))
    stdout_text = stdout or ""; stderr_text = stderr or ""
    log.write_text(stdout_text + ("\nSTDERR:\n" + stderr_text if stderr_text else ""), encoding="utf-8"); payload = {"success": rc == 0, "return_code": rc, "stdout": stdout_text[-8000:], "stderr": stderr_text[-8000:], "artifact_path": str(artifact), "result_json_path": str(artifact), "log_path": str(log), "result_json_exists": artifact.exists()}
    if artifact.exists():
        try: payload["artifact"] = _read_json(artifact)
        except Exception as exc: payload["parse_error"] = str(exc)
    return payload


if __name__ == "__main__":
    mcp.run(transport="stdio")
