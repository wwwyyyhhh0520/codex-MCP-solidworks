"""Import SolidWorks VBA bridge JSON into project artifacts."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any, Dict, List


def _is_meaningful_parameter(value: Any) -> bool:
    """Reject unset numeric defaults returned by the Hole Wizard API."""
    if value is None:
        return False
    text = str(value).strip()
    if not text or text in {"0", "0.0", "0.00", "-1", "-1.0"}:
        return False
    return True


def _has_positive_geometry(parameters: Dict[str, Any]) -> bool:
    for name in ("HoleDiameter", "Diameter", "ThreadDiameter", "MajorDiameter"):
        try:
            if float(parameters.get(name, 0)) > 0:
                return True
        except (TypeError, ValueError):
            continue
    return False


def import_macro_bridge(report: Dict[str, Any]) -> Dict[str, Any]:
    features = report.get("features", [])
    holes: List[Dict[str, Any]] = []
    for feature in features:
        if feature.get("type") != "HoleWzd":
            continue
        raw_parameters = feature.get("parameters") or {}
        parameters = {
            name: value for name, value in raw_parameters.items()
            if _is_meaningful_parameter(value)
        }
        has_geometry = _has_positive_geometry(raw_parameters)
        holes.append({
            "id": f"MACRO-HOLE-{len(holes) + 1:03d}",
            "feature_name": feature.get("name"),
            "feature_type": feature.get("type"),
            "reliable_parameters": parameters,
            "parameter_source": report.get("mode", "solidworks_macro_bridge"),
            "callout_allowed": has_geometry,
            "status": "parameters_read" if has_geometry else "parameters_incomplete",
            "missing_reliable_parameters": [] if has_geometry else [
                "diameter",
                "depth",
                "end_condition",
                "thread_specification",
                "thread_pitch",
                "thread_class",
                "thread_depth",
                "tolerance",
            ],
        })
    return {
        "schema_version": "1.0",
        "mode": "macro_bridge_import",
        "source_file": report.get("source_file"),
        "source_model_modified": bool(report.get("source_model_modified", False)),
        "macro_status": report.get("status"),
        "feature_count": len(features),
        "hole_count": len(holes),
        "holes": holes,
        "status": "passed" if report.get("status") == "passed" else "blocked",
        "policy": {
            "trusted_fields": ["features[].type", "features[].parameters"],
            "still_requires_review": ["hole callout text", "tolerance", "gdandt", "surface finish", "functional datums"],
        },
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("macro_report_file")
    parser.add_argument("output_file")
    args = parser.parse_args()
    report = json.loads(Path(args.macro_report_file).read_text(encoding="utf-8-sig"))
    imported = import_macro_bridge(report)
    Path(args.output_file).write_text(json.dumps(imported, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({
        "status": imported["status"],
        "feature_count": imported["feature_count"],
        "hole_count": imported["hole_count"],
        "output": args.output_file,
    }, ensure_ascii=False))
    return 0 if imported["status"] == "passed" else 2


if __name__ == "__main__":
    raise SystemExit(main())
