"""Read-only evidence scan for external geometry around exact metric HoleWizard holes.

No screw identity is inferred here.  The report answers a narrower question:
for every metric-thread HoleWizard point whose own hole axis is verified, is
there any different-component cylinder co-axial enough to be a physical
partner?  Absence is NOT_VERIFIABLE, never a thread-failure verdict.
"""

from __future__ import annotations

import argparse
import json
import math
import re
from collections import Counter, defaultdict
from datetime import datetime
from pathlib import Path


THREAD_RE = re.compile(r"^\s*M\s*\d+(?:[xX×]\s*[\d.]+)?\b", re.IGNORECASE)


def vector(value):
    try:
        if not isinstance(value, list) or len(value) < 3:
            return None
        return [float(value[0]), float(value[1]), float(value[2])]
    except (TypeError, ValueError):
        return None


def dot(left, right):
    return sum(a * b for a, b in zip(left, right))


def sub(left, right):
    return [a - b for a, b in zip(left, right)]


def norm(value):
    return math.sqrt(dot(value, value))


def unit(value):
    size = norm(value)
    return None if size <= 1e-12 else [item / size for item in value]


def axis_distance(point, origin, axis):
    delta = sub(point, origin)
    axial = dot(delta, axis)
    return norm([delta[index] - axis[index] * axial for index in range(3)])


def load(path_text, label):
    path = Path(path_text).expanduser().resolve()
    if not path.is_file():
        raise FileNotFoundError(f"{label}_NOT_FOUND={path}")
    return path, json.loads(path.read_text(encoding="utf-8-sig"))


def clean_face(raw):
    origin, direction = vector(raw.get("axis_origin_m")), unit(vector(raw.get("axis_unit_vector")) or [])
    occurrence = str(raw.get("occurrence") or "")
    if not occurrence or origin is None or direction is None:
        return None
    try:
        radius = float(raw.get("radius_m"))
    except (TypeError, ValueError):
        radius = None
    return {"occurrence": occurrence, "part_path": str(raw.get("part_path") or ""), "origin_m": origin, "axis": direction, "radius_m": radius, "surface_identity": raw.get("surface_identity")}


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("own_hole_diagnostic_json")
    parser.add_argument("cylinder_faces_json")
    parser.add_argument("output_root")
    parser.add_argument("--axis-distance-mm", type=float, default=0.25)
    parser.add_argument("--parallel-cosine-min", type=float, default=0.999)
    return parser.parse_args()


def main():
    args = parse_args()
    if args.axis_distance_mm <= 0 or not 0 < args.parallel_cosine_min <= 1:
        raise ValueError("INVALID_THRESHOLD")
    own_path, own_data = load(args.own_hole_diagnostic_json, "OWN_HOLE_DIAGNOSTIC_JSON")
    faces_path, faces_data = load(args.cylinder_faces_json, "CYLINDER_FACES_JSON")
    faces = [face for raw in (faces_data.get("cylinder_faces") or []) if (face := clean_face(raw))]
    limit_m = args.axis_distance_mm / 1000.0
    rows, states = [], Counter()
    for hole in own_data.get("records") or []:
        spec = str(hole.get("fastener_size") or "")
        if not THREAD_RE.search(spec):
            continue
        if hole.get("state") != "EXACT_OWN_CYLINDER_AXIS":
            states["TAPPED_HOLE_AXIS_NOT_VERIFIED"] += 1
            continue
        occurrence = str(hole.get("occurrence") or "")
        origin, direction = vector(hole.get("nearest_own_cylinder_origin_m")), unit(vector(hole.get("nearest_own_cylinder_axis")) or [])
        if not occurrence or origin is None or direction is None:
            states["TAPPED_HOLE_AXIS_DATA_INVALID"] += 1
            continue
        nearest, coaxes = None, []
        for face in faces:
            if face["occurrence"] == occurrence:
                continue
            cosine = abs(dot(direction, face["axis"]))
            if cosine < args.parallel_cosine_min:
                continue
            distance = axis_distance(face["origin_m"], origin, direction)
            probe = (distance, -float(face["radius_m"] or 0.0), face, cosine)
            if nearest is None or probe[:2] < nearest[:2]:
                nearest = probe
            if distance <= limit_m:
                coaxes.append(probe)
        coaxes.sort(key=lambda item: (item[0], item[1]))
        status = "TAPPED_HOLE_EXTERNAL_COAXIAL_GEOMETRY_FOUND" if coaxes else "TAPPED_HOLE_EXTERNAL_PARTNER_NOT_VISIBLE"
        states[status] += 1
        rows.append({
            "physical_hole_key": hole.get("physical_hole_key") or "",
            "hole": {"occurrence": occurrence, "feature_name": hole.get("feature_name") or "", "thread_spec": spec, "thread_depth_m": hole.get("thread_depth_m"), "point_m": hole.get("hole_point_m")},
            "own_axis": {"origin_m": origin, "axis": direction, "point_to_axis_mm": hole.get("point_to_own_axis_distance_mm")},
            "external_coaxial_geometry_count": len(coaxes),
            "external_coaxial_geometry": [{"occurrence": item[2]["occurrence"], "part_path": item[2]["part_path"], "radius_mm": (item[2]["radius_m"] or 0) * 1000.0, "surface_identity": item[2]["surface_identity"], "axis_distance_mm": item[0] * 1000.0, "axis_alignment_cosine": item[3]} for item in coaxes[:8]],
            "nearest_parallel_external_geometry": None if nearest is None else {"occurrence": nearest[2]["occurrence"], "part_path": nearest[2]["part_path"], "axis_distance_mm": nearest[0] * 1000.0, "radius_mm": (nearest[2]["radius_m"] or 0) * 1000.0},
            "status": status,
            "verdict": "NOT_ISSUED",
            "notice": "No physical screw, thread-spec, engagement or bottoming conclusion is implied by this geometry scan.",
        })
    output_root = Path(args.output_root).expanduser().resolve()
    run_dir = output_root / f"tapped_hole_partner_visibility_v4_{datetime.now():%Y%m%d_%H%M%S}"
    run_dir.mkdir(parents=True, exist_ok=False)
    result = {"version": "V4", "status": "SUCCESS", "generated_at": datetime.now().isoformat(timespec="seconds"), "source_own_hole_diagnostic_json": str(own_path), "source_cylinder_faces_json": str(faces_path), "tapped_hole_record_count": len(rows), "status_counts": dict(states), "thresholds": {"axis_distance_mm_max": args.axis_distance_mm, "parallel_cosine_min": args.parallel_cosine_min}, "records": rows}
    output_path = run_dir / "攻丝孔外部伙伴几何可见性_V4.json"
    output_path.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"TAPPED_HOLE_RECORD_COUNT={len(rows)}")
    print("STATUS_COUNTS=" + json.dumps(result["status_counts"], ensure_ascii=False))
    print(f"RESULT_PATH={output_path}")
    print("TAPPED_HOLE_PARTNER_VISIBILITY_V4_STATUS=SUCCESS")


if __name__ == "__main__":
    main()
