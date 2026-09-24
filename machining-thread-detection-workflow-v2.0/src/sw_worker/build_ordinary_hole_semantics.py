from __future__ import annotations

import json
import sys
from collections import defaultdict
from pathlib import Path


def canonical_axis(vector: list[float]) -> tuple[str, int]:
    index = max(range(3), key=lambda item: abs(float(vector[item])))
    return "XYZ"[index], 1 if float(vector[index]) >= 0 else -1


def main() -> None:
    source = Path(sys.argv[1]).resolve()
    part_path = Path(sys.argv[2]).resolve()
    output = Path(sys.argv[3]).resolve()
    data = json.loads(source.read_text(encoding="utf-8-sig"))
    raw_diameters = [float(row.get("radius_m", 0.0)) * 2000.0 for row in data.get("cylinder_axes", [])]
    largest_diameter = max(raw_diameters, default=0.0)
    # A cylindrical surface close to the model's maximum round envelope is an
    # outside diameter/boss, not a drillable hole.  Suppress it before
    # grouping so topology-only analysis cannot create false hole callouts.
    exterior_threshold = largest_diameter * 0.90
    raw_groups: dict[tuple[float, str, int], list[dict]] = defaultdict(list)
    for row in data.get("cylinder_axes", []):
        radius = float(row["radius_m"])
        diameter = radius * 2000.0
        if largest_diameter > 0.0 and diameter >= exterior_threshold:
            continue
        point = [float(value) for value in row["axis_origin_m"][:3]]
        axis, sign = canonical_axis(row["axis_unit_vector"])
        raw_groups[(round(radius * 2000.0, 3), axis, sign)].append({"point": point})

    features = []
    for index, ((diameter, axis, sign), rows) in enumerate(sorted(raw_groups.items()), start=1):
        # Opposite-face cylinders with identical in-plane centres represent one
        # through-hole. Prefer the positive face as its single annotation owner.
        transverse = {"X": (1, 2), "Y": (0, 2), "Z": (0, 1)}[axis]
        centres = {(round(item["point"][transverse[0]] * 1000, 3), round(item["point"][transverse[1]] * 1000, 3)) for item in rows}
        duplicate_key = (diameter, axis, tuple(sorted(centres)))
        if any(feature.get("ordinary_through_key") == duplicate_key for feature in features):
            continue
        points = [item["point"] for item in rows]
        feature_name = f"ordinary_hole_d{diameter:g}_{axis}_{'plus' if sign > 0 else 'minus'}"
        features.append({
            "feature_name": feature_name,
            "source_feature": feature_name,
            "feature_type": "ordinary_hole",
            "hole_kind": "ordinary_hole",
            "hole_spec": f"Φ{diameter:g}",
            "part_path": str(part_path),
            "occurrence": f"{part_path.stem}-1",
            "points_part_local_m": points,
            "ordinary_through_key": duplicate_key,
        })

    for feature in features:
        feature.pop("ordinary_through_key", None)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps({
        "version": "ORDINARY_HOLE_GEOMETRY_SEMANTICS_V1",
        "part_path": str(part_path),
        "features": features,
    }, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"ORDINARY_HOLE_GROUP_COUNT={len(features)}")
    for feature in features:
        print(f"GROUP={feature['feature_name']}|SPEC={feature['hole_spec']}|COUNT={len(feature['points_part_local_m'])}")
    print(f"OUTPUT_PATH={output}")


if __name__ == "__main__":
    main()
