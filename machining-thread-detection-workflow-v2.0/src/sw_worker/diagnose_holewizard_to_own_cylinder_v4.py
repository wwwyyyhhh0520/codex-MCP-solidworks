"""Evidence-only check: locate each HoleWizard point against its own part cylinders.

This avoids using a screw candidate while validating the hole side.  If an
exact axis is absent here, the cylinder harvest itself is incomplete or the
HoleWizard uses non-cylindrical geometry; no thread pairing is then permitted.
"""

from __future__ import annotations

import argparse
import json
import math
from collections import Counter, defaultdict
from datetime import datetime
from pathlib import Path


def point(value):
    try:
        if not isinstance(value, list) or len(value) < 3:
            return None
        return [float(value[0]), float(value[1]), float(value[2])]
    except (TypeError, ValueError):
        return None


def dot(left, right):
    return sum(a * b for a, b in zip(left, right))


def subtract(left, right):
    return [a - b for a, b in zip(left, right)]


def length(value):
    return math.sqrt(dot(value, value))


def axis_distance(value, origin, axis):
    delta = subtract(value, origin)
    axial = dot(delta, axis)
    return length([delta[i] - axis[i] * axial for i in range(3)])


def load(path_text, label):
    path = Path(path_text).expanduser().resolve()
    if not path.is_file():
        raise FileNotFoundError(f"{label}_NOT_FOUND={path}")
    return path, json.loads(path.read_text(encoding="utf-8-sig"))


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("cylinder_axes_json")
    parser.add_argument("holewizard_points_json")
    parser.add_argument("output_root")
    parser.add_argument("--exact-distance-mm", type=float, default=0.25)
    return parser.parse_args()


def main():
    args = parse_args()
    if args.exact_distance_mm <= 0:
        raise ValueError("EXACT_DISTANCE_MM_MUST_BE_POSITIVE")
    axis_path, axis_data = load(args.cylinder_axes_json, "CYLINDER_AXES_JSON")
    point_path, point_data = load(args.holewizard_points_json, "HOLEWIZARD_POINTS_JSON")
    axes_by_occurrence = defaultdict(list)
    for index, axis_row in enumerate(axis_data.get("cylinder_axes") or [], start=1):
        occurrence = str(axis_row.get("occurrence") or "")
        origin, direction = point(axis_row.get("axis_origin_m")), point(axis_row.get("axis_unit_vector"))
        if not occurrence or origin is None or direction is None:
            continue
        magnitude = length(direction)
        if magnitude <= 1e-12:
            continue
        axes_by_occurrence[occurrence].append({
            "axis_index": index,
            "origin_m": origin,
            "axis_unit_vector": [value / magnitude for value in direction],
            "radius_mm": float(axis_row.get("radius_m") or 0.0) * 1000.0,
            "surface_identity": axis_row.get("surface_identity"),
        })

    rows, states = [], Counter()
    exact_limit_m = args.exact_distance_mm / 1000.0
    for feature_index, feature in enumerate(point_data.get("features") or [], start=1):
        occurrence = str(feature.get("occurrence") or "")
        own_axes = axes_by_occurrence.get(occurrence, [])
        for point_index, raw in enumerate(feature.get("sketch_point_global_m") or [], start=1):
            location = point(raw)
            if location is None:
                states["point_invalid"] += 1
                continue
            if not own_axes:
                states["own_cylinder_axes_unavailable"] += 1
                continue
            best = min(own_axes, key=lambda item: axis_distance(location, item["origin_m"], item["axis_unit_vector"]))
            distance_mm = axis_distance(location, best["origin_m"], best["axis_unit_vector"]) * 1000.0
            state = "EXACT_OWN_CYLINDER_AXIS" if distance_mm <= args.exact_distance_mm else "OWN_CYLINDER_AXIS_NOT_FOUND"
            states[state] += 1
            rows.append({
                "physical_hole_key": f"{occurrence}|F{feature_index}|P{point_index}",
                "occurrence": occurrence,
                "feature_name": feature.get("feature_name") or "",
                "fastener_size": feature.get("fastener_size") or "",
                "thread_depth_m": feature.get("thread_depth_m"),
                "hole_point_m": location,
                "nearest_own_cylinder_axis_index": best["axis_index"],
                "nearest_own_cylinder_origin_m": best["origin_m"],
                "nearest_own_cylinder_axis": best["axis_unit_vector"],
                "nearest_own_cylinder_radius_mm": best["radius_mm"],
                "nearest_own_cylinder_surface_identity": best["surface_identity"],
                "point_to_own_axis_distance_mm": distance_mm,
                "state": state,
            })

    rows.sort(key=lambda item: item["point_to_own_axis_distance_mm"])
    output_root = Path(args.output_root).expanduser().resolve()
    run_dir = output_root / f"holewizard_own_cylinder_diagnostic_v4_{datetime.now():%Y%m%d_%H%M%S}"
    run_dir.mkdir(parents=True, exist_ok=False)
    result = {
        "version": "V4",
        "purpose": "hole_side_identity_diagnostic_only",
        "source_cylinder_axes_json": str(axis_path),
        "source_holewizard_points_json": str(point_path),
        "physical_hole_point_count": len(rows),
        "state_counts": dict(states),
        "exact_distance_mm_max": args.exact_distance_mm,
        "nearest_samples": rows[:50],
        "records": rows,
        "important_notice": "Only EXACT_OWN_CYLINDER_AXIS records may be used to identify a HoleWizard physical hole. All other records must remain unpaired.",
    }
    output_path = run_dir / "HoleWizard孔位与本体圆柱轴诊断_V4.json"
    output_path.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"PHYSICAL_HOLE_POINT_COUNT={result['physical_hole_point_count']}")
    print("STATE_COUNTS=" + json.dumps(result["state_counts"], ensure_ascii=False))
    print(f"RESULT_PATH={output_path}")
    print("HOLEWIZARD_OWN_CYLINDER_DIAGNOSTIC_V4_STATUS=SUCCESS")


if __name__ == "__main__":
    main()
