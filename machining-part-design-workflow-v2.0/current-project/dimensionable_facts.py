"""Read-only dimensionable engineering facts from semantic JSON artifacts.

This module deliberately has no SolidWorks or drawing API dependency.  It
turns stable topology measurements into facts that a planner may consume.
"""

from __future__ import annotations

import json
import math
from pathlib import Path
from typing import Any


def _num(value: Any) -> float | None:
    try:
        value = float(value)
    except (TypeError, ValueError):
        return None
    return value if math.isfinite(value) else None


def _key(values: list[Any], digits: int = 9) -> tuple[Any, ...]:
    result: list[Any] = []
    for value in values:
        number = _num(value)
        result.append(round(number, digits) if number is not None else str(value))
    return tuple(result)


def _fact(fact_id: str, fact_type: str, value: float, unit: str,
          source_geometry: str, source_signatures: list[str],
          axis_or_direction: list[float] | None, reference_a: Any,
          reference_b: Any, proof_method: str,
          selectable_witness_expectation: bool = True) -> dict[str, Any]:
    return {
        "fact_id": fact_id,
        "fact_type": fact_type,
        "value": value,
        "unit": unit,
        "source_geometry": source_geometry,
        "source_signatures": source_signatures,
        "axis_or_direction": axis_or_direction,
        "reference_a": reference_a,
        "reference_b": reference_b,
        "proof_state": "PROVEN",
        "proof_method": proof_method,
        "selectable_witness_expectation": selectable_witness_expectation,
        "confidence": "EXACT_GEOMETRY",
    }


def discover_dimensionable_facts(semantic: dict[str, Any]) -> dict[str, Any]:
    """Return proven facts and raw/deduplicated counts without COM access."""
    openings = semantic.get("physical_openings") or semantic.get("body_topology", {}).get("physical_openings", [])
    faces = semantic.get("body_topology", {}).get("faces", [])
    raw: list[dict[str, Any]] = []

    for opening in openings:
        diameter = _num(opening.get("diameter_m"))
        axis = opening.get("axis")
        if diameter is None or diameter <= 0 or not isinstance(axis, list) or len(axis) != 3:
            continue
        opening_id = str(opening.get("opening_id", "UNKNOWN"))
        signatures = [str(s) for s in opening.get("boundary_circular_edge_signature", [])]
        raw.append(_fact(
            f"CYLINDER_DIAMETER:{opening_id}", "CYLINDER_DIAMETER", diameter, "m",
            "physical_opening", signatures, [float(x) for x in axis], opening_id, None,
            "PROVEN_CIRCULAR_BOUNDARY_DIAMETER"))

    # Plane separation is emitted only when both faces carry explicit plane
    # normals and measurable geometric boxes.  No thickness upgrade is made.
    planes: list[dict[str, Any]] = []
    for face in faces:
        evidence = face.get("plane_evidence")
        if isinstance(evidence, dict):
            if str(evidence.get("surface_type", "")).upper() != "PLANE" or evidence.get("proof_state") != "PROVEN":
                continue
            normal = evidence.get("plane_normal")
            offset = _num(evidence.get("plane_offset_m"))
            supporting = evidence.get("supporting_plane_signature")
            if (not isinstance(normal, list) or len(normal) != 3
                    or any(_num(value) is None for value in normal)
                    or offset is None or not supporting):
                continue
            planes.append({"face": face, "normal": [float(x) for x in normal],
                           "offset": offset, "supporting": str(supporting)})
            continue
        if str(face.get("surface_type", "")).upper() == "PLANE" and isinstance(face.get("box"), list) and len(face["box"]) == 6:
            normal = face.get("normal") or face.get("normal_vector")
            offset = _num(face.get("plane_offset"))
            if isinstance(normal, list) and len(normal) == 3 and all(_num(value) is not None for value in normal) and offset is not None:
                planes.append({"face": face, "normal": [float(x) for x in normal],
                               "offset": offset, "supporting": str(face.get("supporting_plane_signature", ""))})
    for index, first in enumerate(planes):
        n1 = first["normal"]
        for second in planes[index + 1:]:
            n2 = second["normal"]
            cross = [n1[1] * n2[2] - n1[2] * n2[1], n1[2] * n2[0] - n1[0] * n2[2], n1[0] * n2[1] - n1[1] * n2[0]]
            if math.sqrt(sum(x * x for x in cross)) > 1e-7:
                continue
            # A generic plane distance needs a plane offset; without one the
            # evidence is insufficient and no fact is emitted.
            if first["supporting"] and first["supporting"] == second["supporting"]:
                continue
            separation = abs(second["offset"] - first["offset"])
            if separation <= 0:
                continue
            direction = [float(x) for x in n1]
            source_signatures = sorted([first["supporting"], second["supporting"]])
            raw.append(_fact(
                f"PARALLEL_PLANE_SEPARATION:{index}:{len(raw)}", "PARALLEL_PLANE_SEPARATION",
                separation, "m", "planar_face_pair", source_signatures, direction,
                first["face"].get("session_face_id"), second["face"].get("session_face_id"),
                "PROVEN_PARALLEL_PLANES_WITH_OFFSETS"))

    unique: dict[tuple[Any, ...], dict[str, Any]] = {}
    for item in raw:
        signature = (item["fact_type"], round(float(item["value"]), 9),
                     tuple(item.get("axis_or_direction") or []),
                     tuple(sorted(item.get("source_signatures") or [])))
        unique.setdefault(signature, item)

    # Overall extent is authoritative from the proven exact envelope.  It is
    # intentionally independent of planar separation facts: envelope extrema
    # may touch multiple planar faces without requiring a unique plane ID.
    overall_extent_facts: list[dict[str, Any]] = []
    overall_size_candidates: list[dict[str, Any]] = []
    envelope = semantic.get("bounding_box", {}).get("exact_envelope", {})
    axis_evidence = envelope.get("axis_evidence", {}) if isinstance(envelope, dict) else {}
    model_path = str(semantic.get("model_path", ""))
    body_stable_id = Path(model_path).stem if model_path else ""
    for axis in ("X", "Y", "Z"):
        row = axis_evidence.get(axis)
        if not isinstance(row, dict):
            continue
        low = row.get("low_coordinate")
        high = row.get("high_coordinate")
        low_support = row.get("low_support")
        high_support = row.get("high_support")
        low_sig = low_support.get("geometric_signature") if isinstance(low_support, dict) else None
        high_sig = high_support.get("geometric_signature") if isinstance(high_support, dict) else None
        valid = (row.get("low_proven") is True and row.get("high_proven") is True
                 and row.get("coverage_complete") is True
                 and row.get("target_source") == "PROVEN_TOPOLOGY"
                 and _num(low) is not None and _num(high) is not None
                 and _num(high) > _num(low) and bool(body_stable_id)
                 and bool(low_sig) and bool(high_sig)
                 and isinstance(low_support, dict) and isinstance(high_support, dict)
                 and bool(low_support.get("support_kind")) and bool(high_support.get("support_kind")))
        if not valid:
            continue
        value = _num(high) - _num(low)
        direction = {"X": [1.0, 0.0, 0.0], "Y": [0.0, 1.0, 0.0], "Z": [0.0, 0.0, 1.0]}[axis]
        stable = f"OVERALL_EXTENT:{body_stable_id}:{axis}:{low_sig}:{high_sig}"
        fact = {
            "fact_id": stable, "fact_type": "OVERALL_EXTENT", "value_m": value,
            "axis": axis, "axis_or_direction": direction,
            "low_coordinate_m": _num(low), "high_coordinate_m": _num(high),
            "low_topology_signature": str(low_sig), "high_topology_signature": str(high_sig),
            "body_stable_id": body_stable_id,
            "proof_state": "PROVEN", "proof_method": "PROVEN_EXACT_BODY_ENVELOPE",
            "source": "exact_envelope.axis_evidence", "stable_identity": stable,
            "dedup_key": stable,
        }
        overall_extent_facts.append(fact)
        candidate_id = f"OVERALL_SIZE:{body_stable_id}:{axis}:{low_sig}:{high_sig}"
        overall_size_candidates.append({
            "candidate_id": candidate_id, "classification": "OVERALL_SIZE",
            "source_fact_id": stable, "body_stable_id": body_stable_id,
            "axis": axis, "axis_or_direction": direction, "value_m": value,
            "low_topology_signature": str(low_sig), "high_topology_signature": str(high_sig),
            "semantic_basis": "EXACT_BODY_ENVELOPE", "proof_state": "PROVEN",
            "stable_identity": candidate_id, "dedup_key": candidate_id,
        })

    by_type: dict[str, dict[str, int]] = {}
    for fact_type in ("CYLINDER_DIAMETER", "PARALLEL_PLANE_SEPARATION", "PLATE_THICKNESS", "FEATURE_THICKNESS", "FILLET_RADIUS", "CHAMFER_SIZE_ANGLE"):
        raw_count = sum(x["fact_type"] == fact_type for x in raw)
        dedup_count = sum(x["fact_type"] == fact_type for x in unique.values())
        by_type[fact_type] = {"raw": raw_count, "deduplicated": dedup_count}

    return {
        "schema_version": "dimensionable-facts-v1",
        "facts": list(unique.values()),
        "overall_extent_facts": overall_extent_facts,
        "overall_size_candidates": overall_size_candidates,
        "counts": by_type,
        "total_proven_fact_count": len(unique),
        "unproven_fact_count": 0,
        "session_independent_signatures": True,
        "com_id_persistence_used": False,
    }


def discover_dimensionable_facts_file(path: str | Path) -> dict[str, Any]:
    return discover_dimensionable_facts(json.loads(Path(path).read_text(encoding="utf-8")))
