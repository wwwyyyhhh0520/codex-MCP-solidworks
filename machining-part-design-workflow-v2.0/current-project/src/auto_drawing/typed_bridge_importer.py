"""Convert a strongly typed SolidWorks bridge report into PartAnalysis facts.

The bridge is deliberately narrow: absent geometry is recorded as absent instead
of manufacturing placeholder dimensions or view directions.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any, Dict


def _view_candidates(features: list[Dict[str, Any]], bbox: Dict[str, Any]) -> list[Dict[str, Any]]:
    axes = (("front", "min_x", "max_x"), ("top", "min_y", "max_y"), ("right", "min_z", "max_z"))
    extents = []
    for orientation, low, high in axes:
        if low in bbox and high in bbox:
            extents.append((orientation, abs(float(bbox[high]) - float(bbox[low]))))
    has_hole = any(item.get("type") == "HoleWzd" for item in features)
    return [
        {
            "orientation": orientation,
            "rank": rank,
            "score": round(1.0 / rank + (0.15 if has_hole else 0.0), 3),
            "basis": ["SolidWorks API 包围盒主尺寸优先", "存在 Hole Wizard 特征" if has_hole else "未发现结构化孔特征"],
            "confidence": 0.35,
            "status": "heuristic_candidate",
            "warning": "尚未进行真实投影可见性、隐藏线数量和基准可标注性分析。",
            "projected_extent": extent,
        }
        for rank, (orientation, extent) in enumerate(sorted(extents, key=lambda item: item[1], reverse=True), start=1)
    ]


def import_typed_bridge(report: Dict[str, Any]) -> Dict[str, Any]:
    source = str(report.get("source_file") or "")
    features = [dict(item) for item in (report.get("features") or [])]
    bounding_box = report.get("bounding_box") or {}
    bodies = report.get("bodies") or []
    properties = report.get("custom_properties") or {}
    pmi = report.get("pmi") or {}
    hole_geometry = report.get("hole_geometry") or {}
    hole_geometry_by_feature = report.get("hole_geometry_by_feature") or {}
    for feature in features:
        if feature.get("type") == "HoleWzd":
            feature["topology_geometry"] = hole_geometry_by_feature.get(feature.get("name"), hole_geometry)
    has_hole_wizard = any(item.get("type") == "HoleWzd" for item in features)
    has_geometry = len(bounding_box) == 6
    return {
        "schema_version": "1.0",
        "part_number": Path(source).stem if source else None,
        "source_file": source,
        "configuration": "",
        "units": "unknown",
        "category": "single_body_geometric_candidate" if len(bodies) == 1 else "unknown",
        "category_confidence": 0.35 if len(bodies) == 1 else 0.0,
        "properties": properties,
        "pmi": pmi,
        "bounding_box": bounding_box,
        "bodies": bodies,
        "features": features,
        "datum_candidates": [],
        "view_candidates": _view_candidates(features, bounding_box) if has_geometry else [],
        "source_items": [
            {"field": "features", "source": report.get("mode", "solidworks_typed_bridge")},
            {"field": "bounding_box", "source": report.get("mode", "solidworks_typed_bridge")},
            {"field": "bodies", "source": report.get("mode", "solidworks_typed_bridge")},
            {"field": "features[].topology_geometry", "source": "HoleWzd.GetFaces().GetSurface().CylinderParams"},
            {"field": "properties", "source": "ModelDocExtension.CustomPropertyManager/Get6"},
            {"field": "pmi", "source": "ModelDocExtension.GetAnnotations"},
        ],
        "risks": [
            *( [] if has_geometry else ["强类型桥接未返回有效包围盒，因此不生成总体尺寸、图幅比例或主视图候选。"] ),
            *( ["模型未返回 PMI/DimXpert 注释；不得自动生成公差、GD&T、粗糙度或功能基准。"] if pmi.get("annotation_count") == 0 else ["PMI/DimXpert 注释需由后续专用解析器逐项确认；不得自动补齐缺失要求。"] ),
        ],
        "uncertain_items": [
            *([] if has_geometry else [{
                "id": "TYPED-BRIDGE-001",
                "topic": "geometry_completeness",
                "description": "当前 PartAnalysis 只有经过 API 验证的特征树事实，几何读取尚未完成。",
                "source": report.get("mode", "solidworks_typed_bridge"),
                "severity": "warning",
                "requires_human_confirmation": True,
            }]),
            *([] if has_hole_wizard else [{
                "id": "TYPED-BRIDGE-002",
                "topic": "feature_reading",
                "description": "特征树中未发现 Hole Wizard；不据此推断模型没有普通孔。",
                "source": report.get("mode", "solidworks_typed_bridge"),
                "severity": "warning",
                "requires_human_confirmation": True,
            }]),
        ],
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("bridge_report_file")
    parser.add_argument("output_file")
    args = parser.parse_args()
    report = json.loads(Path(args.bridge_report_file).read_text(encoding="utf-8-sig"))
    if report.get("status") != "passed":
        raise SystemExit("Bridge report is not passed; refusing to create PartAnalysis.")
    analysis = import_typed_bridge(report)
    Path(args.output_file).write_text(json.dumps(analysis, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"status": "ok", "output": args.output_file}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
