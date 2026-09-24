"""SolidWorks 只读零件读取器。

依赖 Windows COM 与本机 SolidWorks 安装。该模块只读取模型，不调用保存、修改
特征或修改配置的 API。
"""

from __future__ import annotations

import argparse
import json
from datetime import datetime
from pathlib import Path
from typing import Any

from .models import PartAnalysis, UncertainItem


class SolidWorksReadError(RuntimeError):
    pass


def _safe_call(obj: Any, name: str, default: Any = None) -> Any:
    try:
        value = getattr(obj, name)
        return value() if callable(value) else value
    except Exception:
        return default


def _connect_solidworks() -> Any:
    try:
        import win32com.client  # type: ignore
    except ImportError as exc:
        raise SolidWorksReadError("缺少 pywin32，无法建立 SolidWorks COM 连接") from exc
    app = win32com.client.Dispatch("SldWorks.Application")
    app.Visible = True
    return app


def _byref_int(value: int = 0) -> Any:
    try:
        import pythoncom  # type: ignore
        import pywintypes  # type: ignore
        return pywintypes.VARIANT(pythoncom.VT_BYREF | pythoncom.VT_I4, value)
    except Exception:
        return value


def _open_part(
    app: Any,
    path: Path,
    configuration: str | None = None,
    open_strategy: str = "default",
) -> tuple[Any, list[str]]:
    risks: list[str] = []
    if open_strategy in {"default", "opendoc6_byref"}:
        try:
            errors = _byref_int(0)
            warnings = _byref_int(0)
            model = app.OpenDoc6(str(path), 1, 1, configuration or "", errors, warnings)
            if model is not None:
                risks.append("OpenDoc6 by-ref 参数调用成功。")
                return model, risks
            risks.append("OpenDoc6 by-ref 参数调用返回空文档对象。")
        except Exception as exc:
            risks.append(f"OpenDoc6 by-ref 参数调用失败：{exc}")
        if open_strategy == "opendoc6_byref":
            detail = "；".join(risks) if risks else "无详细错误。"
            raise SolidWorksReadError(f"OpenDoc6 by-ref 探测失败，未启用 OpenDoc 兼容回退：{detail}")
    try:
        model = app.OpenDoc6(str(path), 1, 1, configuration or "", 0, 0)
    except Exception as exc:
        risks.append(f"OpenDoc6 参数类型不匹配，已尝试 OpenDoc 兼容回退：{exc}")
        try:
            model = app.OpenDoc(str(path), 1)
        except Exception as fallback_exc:
            raise SolidWorksReadError(
                f"OpenDoc6 与兼容回退 OpenDoc 均失败；OpenDoc6={exc}; OpenDoc={fallback_exc}"
            ) from fallback_exc
    if model is None:
        raise SolidWorksReadError("SolidWorks 返回空文档对象")
    return model, risks


def _close_document(app: Any, model: Any, path: Path) -> str:
    title = str(_safe_call(model, "GetTitle", "") or path.name)
    for name in (title, path.name, str(path)):
        try:
            app.CloseDoc(name)
            return f"已请求关闭文档：{name}"
        except Exception:
            pass
    return "已尝试关闭文档，但 SolidWorks 未确认 CloseDoc 调用成功。"


def probe_part(source_file: str, configuration: str | None = None, open_strategy: str = "opendoc6_byref") -> dict[str, Any]:
    path = Path(source_file)
    result: dict[str, Any] = {
        "schema_version": "1.0",
        "mode": "solidworks_open_close_probe",
        "created_at": datetime.now().isoformat(timespec="seconds"),
        "source_file": str(path),
        "status": "failed",
        "source_model_modified": False,
        "open_strategy": open_strategy,
        "steps": [],
        "risks": [],
    }
    if not path.is_file() or path.suffix.upper() != ".SLDPRT":
        result["risks"].append(f"输入文件不是可读取的 SLDPRT：{source_file}")
        return result
    model = None
    app = None
    try:
        app = _connect_solidworks()
        result["steps"].append({"step": "connect_solidworks", "status": "ok"})
        model, risks = _open_part(app, path, configuration, open_strategy)
        result["risks"].extend(risks)
        result["steps"].append({"step": "open_part", "status": "ok"})
        result["document_title"] = str(_safe_call(model, "GetTitle", "") or "")
        result["active_configuration"] = str(_safe_call(model, "GetActiveConfiguration", "") or "")
        result["status"] = "passed"
    except Exception as exc:
        result["steps"].append({"step": "probe", "status": "error", "message": str(exc)})
    finally:
        if app is not None and model is not None:
            result["steps"].append({"step": "close_document", "status": "attempted", "message": _close_document(app, model, path)})
    return result


def _definition_parameters(feature: Any, model: Any) -> dict[str, Any]:
    """读取特征定义中常见的只读字段；不同 SW 版本字段可能不同。"""
    result: dict[str, Any] = {}
    try:
        definition = feature.GetDefinition()
        try:
            definition.AccessSelections(model, None)
        except Exception:
            pass
        fields = (
            "Type", "HoleType", "Diameter", "Depth", "Depth2", "EndCondition",
            "EndCondition2", "ThreadType", "ThreadClass", "ThreadDepth",
            "ThreadPitch", "MajorDiameter", "MinorDiameter", "CosmeticThread",
        )
        for field in fields:
            value = _safe_call(definition, field, None)
            if value is not None and not callable(value):
                try:
                    result[field] = value
                except Exception:
                    pass
        try:
            definition.ReleaseSelectionAccess()
        except Exception:
            pass
    except Exception:
        pass
    return result


def _property_manager(model: Any) -> dict[str, str]:
    result: dict[str, str] = {}
    try:
        manager = model.Extension.CustomPropertyManager("")
        names = manager.GetNames() or []
        for name in names:
            try:
                value = manager.Get(name)[1]
            except Exception:
                value = ""
            result[str(name)] = str(value or "")
    except Exception:
        pass
    return result


def _geometry_summary(model: Any) -> tuple[list[dict[str, Any]], dict[str, float], list[str]]:
    bodies: list[dict[str, Any]] = []
    bounding_box: dict[str, float] = {}
    risks: list[str] = []
    try:
        for body in model.GetBodies2(0, True) or []:
            surface_counts: dict[str, int] = {}
            for face in body.GetFaces() or []:
                surface = _safe_call(face, "GetSurface", None)
                surface_type = str(_safe_call(surface, "Identity", "unknown"))
                surface_counts[surface_type] = surface_counts.get(surface_type, 0) + 1
            bodies.append({
                "name": str(_safe_call(body, "Name", "")),
                "type": str(_safe_call(body, "Type", "solid")),
                "is_sheet_body": bool(_safe_call(body, "IsSheetBody", False)),
                "face_count": len(body.GetFaces() or []),
                "surface_counts": surface_counts,
            })
        values = model.GetPartBox(True)
        if values and len(values) >= 6:
            bounding_box = {key: float(value) for key, value in zip(
                ("min_x", "min_y", "min_z", "max_x", "max_y", "max_z"), values
            )}
    except Exception as exc:
        risks.append(f"实体或包围盒读取失败：{exc}")
    try:
        mass = model.Extension.CreateMassProperty()
        if mass is not None:
            bodies.append({
                "mass": float(_safe_call(mass, "Mass", 0.0) or 0.0),
                "volume": float(_safe_call(mass, "Volume", 0.0) or 0.0),
                "surface_area": float(_safe_call(mass, "SurfaceArea", 0.0) or 0.0),
            })
    except Exception as exc:
        risks.append(f"质量属性读取失败：{exc}")
    return bodies, bounding_box, risks


def _classify(features: list[dict[str, Any]], bodies: list[dict[str, Any]], box: dict[str, float]) -> tuple[str, float]:
    has_holewizard = any(item.get("type") == "HoleWzd" for item in features)
    solid_count = sum(1 for item in bodies if item.get("type") == "solid")
    if solid_count == 1 and has_holewizard and box:
        dimensions = [box.get("max_x", 0) - box.get("min_x", 0),
                      box.get("max_y", 0) - box.get("min_y", 0),
                      box.get("max_z", 0) - box.get("min_z", 0)]
        if max(dimensions) / max(min(dimensions), 1e-12) < 2.0:
            return "machined_nut_or_prismatic_single_body_candidate", 0.62
    return "unknown", 0.0


def _view_candidates(features: list[dict[str, Any]], box: dict[str, float]) -> list[dict[str, Any]]:
    if not box:
        return []
    dims = {
        "front": box.get("max_x", 0) - box.get("min_x", 0),
        "top": box.get("max_y", 0) - box.get("min_y", 0),
        "right": box.get("max_z", 0) - box.get("min_z", 0),
    }
    has_hole = any(item.get("type") == "HoleWzd" for item in features)
    ordered = sorted(dims.items(), key=lambda item: item[1], reverse=True)
    candidates = []
    for rank, (orientation, size) in enumerate(ordered, start=1):
        candidates.append({
            "orientation": orientation,
            "rank": rank,
            "score": round(1.0 / rank + (0.15 if has_hole else 0.0), 3),
            "basis": [
                "包围盒主尺寸优先",
                "存在 Hole Wizard 特征" if has_hole else "未发现结构化孔特征",
            ],
            "confidence": 0.35,
            "status": "heuristic_candidate",
            "warning": "尚未进行真实投影可见性、隐藏线数量和基准可标注性分析。",
            "projected_extent": size,
        })
    return candidates


def _feature_summary(model: Any) -> list[dict[str, Any]]:
    features: list[dict[str, Any]] = []
    try:
        feature = model.FirstFeature()
        while feature is not None:
            features.append({
                "name": str(_safe_call(feature, "Name", "")),
                "type": str(_safe_call(feature, "GetTypeName2", "")),
                "suppressed": bool(_safe_call(feature, "IsSuppressed", False)),
                "parameters": _definition_parameters(feature, model),
            })
            feature = feature.GetNextFeature()
    except Exception:
        pass
    if features:
        return features
    try:
        manager = model.FeatureManager
        raw_features = manager.GetFeatures(True) or []
        for feature in raw_features:
            features.append({
                "name": str(_safe_call(feature, "Name", "")),
                "type": str(_safe_call(feature, "GetTypeName2", "")),
                "suppressed": bool(_safe_call(feature, "IsSuppressed", False)),
                "parameters": _definition_parameters(feature, model),
            })
    except Exception:
        # Feature tree 读取失败不能伪造结果；调用方会记录风险。
        pass
    return features


def read_part(source_file: str, configuration: str | None = None, open_strategy: str = "default") -> PartAnalysis:
    path = Path(source_file)
    if not path.is_file() or path.suffix.upper() != ".SLDPRT":
        raise SolidWorksReadError(f"输入文件不是可读取的 SLDPRT：{source_file}")

    app = None
    model = None
    analysis = PartAnalysis(source_file=str(path), part_number=path.stem)
    open_risks: list[str] = []
    try:
        app = _connect_solidworks()
        model, open_risks = _open_part(app, path, configuration, open_strategy)

        bodies, bounding_box, geometry_risks = _geometry_summary(model)
        features = _feature_summary(model)
        category, category_confidence = _classify(features, bodies, bounding_box)
        view_candidates = _view_candidates(features, bounding_box)
        analysis = PartAnalysis(
            source_file=str(path),
            configuration=configuration or str(_safe_call(model, "GetActiveConfiguration", "")),
            units="unknown",
            properties=_property_manager(model),
            bodies=bodies,
            bounding_box=bounding_box,
            features=features,
            category=category,
            category_confidence=category_confidence,
            view_candidates=view_candidates,
            source_items=[
                {"field": "properties", "source": "CustomPropertyManager"},
                {"field": "features", "source": "FeatureManager"},
            ],
        )
        analysis.risks.extend(open_risks)
        analysis.risks.extend(geometry_risks)
        analysis.part_number = analysis.properties.get("PartNumber") or path.stem
        analysis.risks.append("尚未读取 PMI/DimXpert、拓扑尺寸和质量属性；本结果不能作为工程图完成依据。")
        if not analysis.features:
            analysis.risks.append("Feature Tree 未返回特征摘要，需要检查 SolidWorks API 版本或模型状态。")
        analysis.uncertain_items.append(UncertainItem(
            id="MODEL-001",
            topic="design_intent",
            description="尚未确认螺纹是否由结构化螺纹特征或 Hole Wizard 定义。",
            source="not_yet_inspected",
        ))
        return analysis
    finally:
        if app is not None and model is not None:
            try:
                analysis.risks.append(_close_document(app, model, path))
            except Exception:
                pass


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("source_file")
    parser.add_argument("output_file")
    parser.add_argument("--probe-only", action="store_true")
    parser.add_argument("--open-strategy", choices=["default", "opendoc6_byref", "legacy"], default="default")
    args = parser.parse_args()
    if args.probe_only:
        output = Path(args.output_file)
        output.parent.mkdir(parents=True, exist_ok=True)
        strategy = "opendoc6_byref" if args.open_strategy == "default" else args.open_strategy
        result = probe_part(args.source_file, open_strategy=strategy)
        output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps({"status": result["status"], "output": str(output)}, ensure_ascii=False))
        return 0 if result["status"] == "passed" else 2
    analysis = read_part(args.source_file, open_strategy=args.open_strategy)
    output = Path(args.output_file)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(analysis.to_json(), encoding="utf-8")
    print(json.dumps({"status": "ok", "output": str(output)}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
