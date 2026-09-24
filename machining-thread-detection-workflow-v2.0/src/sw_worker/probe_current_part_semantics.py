from __future__ import annotations

import json
import os
import sys
from pathlib import Path

import pythoncom
import win32com.client
from win32com.client import VARIANT, gencache, makepy


def call(obj, name, default=None):
    try:
        value = getattr(obj, name)
        return value() if callable(value) else value
    except Exception:
        return default


def feature_snapshot(item, parent_name=""):
    """Return a COM-safe feature record without assuming one traversal API."""
    return {
        "feature_name": str(call(item, "Name", "") or ""),
        "feature_type": str(call(item, "GetTypeName2", "") or ""),
        "parent_feature": parent_name,
        "suppressed": call(item, "IsSuppressed"),
    }


def prepare_type_library():
    roots = [
        Path(os.environ.get("ProgramFiles", r"C:\\Program Files")) / "SOLIDWORKS Corp" / "SOLIDWORKS" / "sldworks.tlb",
        Path(r"C:\\Program Files\\SOLIDWORKS Corp\\SOLIDWORKS\\sldworks.tlb"),
    ]
    for tlb in roots:
        if not tlb.is_file():
            continue
        try:
            makepy.GenerateFromTypeLibSpec(str(tlb))
            return f"MAKEPY_READY:{tlb}"
        except Exception as exc:
            return f"MAKEPY_ERROR:{type(exc).__name__}:{exc}"
    return "MAKEPY_TLB_NOT_FOUND"


def wrap_feature_interface(feature):
    """Wrap a raw IFeature pointer with the generated Feature class."""
    if feature is None:
        return None
    tlb = pythoncom.LoadTypeLib(r"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\sldworks.tlb")
    attr = tlb.GetLibAttr()
    module = gencache.GetModuleForTypelib(attr[0], attr[1], attr[3], attr[4])
    return module.Feature(feature._oleobj_)


def enrich_pattern_snapshot(item, snapshot):
    type_name = str(snapshot.get("feature_type") or "")
    if type_name not in {"LPattern", "MirrorPattern", "CirPattern", "Extrusion"}:
        return snapshot
    definition = call(item, "GetDefinition")
    if definition is None:
        try:
            definition = item.GetSpecificFeature2()
        except Exception as exc:
            snapshot["specific_feature_error"] = f"{type(exc).__name__}:{exc}"
    snapshot["definition_type"] = str(type(definition).__name__)
    for attr in ("D1Spacing", "D1TotalInstances", "D2Spacing", "D2TotalInstances", "Angle", "TotalInstances"):
        snapshot[attr] = call(definition, attr)
    if definition is not None:
        for attr in ("FeatureArray", "SeedFeature", "PatternFeature", "Direction", "Direction2", "GetSeedFeature", "GetFeatureArray"):
            try:
                value = getattr(definition, attr)
                value = value() if callable(value) else value
                if isinstance(value, (str, int, float, bool)) or value is None:
                    snapshot[attr] = value
                else:
                    snapshot[attr] = str(value)
            except Exception as exc:
                snapshot[f"{attr}_error"] = f"{type(exc).__name__}:{exc}"
    try:
        dependencies = item.GetDependencies(False, False, False, False, False)
        snapshot["dependencies"] = [str(value) for value in (dependencies or [])]
    except Exception as exc:
        snapshot["dependencies_error"] = f"{type(exc).__name__}:{exc}"
    child_names = []
    try:
        child = item.GetFirstSubFeature()
        while child is not None:
            child_names.append({
                "name": str(call(child, "Name", "") or ""),
                "type": str(call(child, "GetTypeName2", "") or ""),
            })
            child = child.GetNextSubFeature()
    except Exception as exc:
        snapshot["subfeature_error"] = f"{type(exc).__name__}:{exc}"
    snapshot["subfeatures"] = child_names
    return snapshot


def main() -> int:
    if len(sys.argv) != 3:
        raise SystemExit("usage: probe_current_part_semantics.py MODEL OUTPUT")
    model_path = Path(sys.argv[1]).resolve()
    output = Path(sys.argv[2]).resolve()
    pythoncom.CoInitialize()
    try:
        typelib_status = prepare_type_library()
        tlb = pythoncom.LoadTypeLib(r"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\sldworks.tlb")
        typelib_attr = tlb.GetLibAttr()
        generated_module = gencache.GetModuleForTypelib(typelib_attr[0], typelib_attr[1], typelib_attr[3], typelib_attr[4])
        sw = win32com.client.dynamic.Dispatch("SldWorks.Application")
        errors = VARIANT(pythoncom.VT_BYREF | pythoncom.VT_I4, 0)
        warnings = VARIANT(pythoncom.VT_BYREF | pythoncom.VT_I4, 0)
        try:
            doc = sw.GetOpenDocumentByName(str(model_path))
        except Exception:
            doc = None
        open_route = "GET_OPEN_DOCUMENT"
        if doc is None:
            open_route = "OPEN_DOC6"
            doc = sw.OpenDoc6(str(model_path), 1, 1, "", errors, warnings)
        if doc is None:
            raise RuntimeError(f"PART_OPEN_FAILED={errors.value}")
        try:
            typed_doc = win32com.client.Dispatch(doc._oleobj_)
        except Exception:
            typed_doc = doc
        try:
            raw_title = str(doc.GetTitle())
        except Exception as exc:
            raw_title = f"<ERROR:{type(exc).__name__}:{exc}>"
        try:
            raw_path = str(doc.GetPathName())
        except Exception as exc:
            raw_path = f"<ERROR:{type(exc).__name__}:{exc}>"
        try:
            raw_type = doc.GetType()
        except Exception as exc:
            raw_type = f"<ERROR:{type(exc).__name__}:{exc}>"
        # FeatureManager enumeration is only complete on the active document.
        try:
            sw.ActivateDoc3(str(model_path.name), True, 0, errors)
        except Exception:
            pass
        rows = []
        feature_types = {}
        all_features = []
        traversal_errors = []
        try:
            feature = doc.FirstFeature()
        except Exception as exc:
            traversal_errors.append(f"FirstFeature={type(exc).__name__}:{exc}")
            feature = None
        while feature is not None:
            stack = [feature]
            while stack:
                item = stack.pop()
                type_name = str(call(item, "GetTypeName2", "") or "")
                feature_types[type_name] = feature_types.get(type_name, 0) + 1
                all_features.append(enrich_pattern_snapshot(item, feature_snapshot(item)))
                if type_name.lower() == "holewzd":
                    definition = call(item, "GetDefinition")
                    rows.append({
                        "feature_name": str(call(item, "Name", "") or ""),
                        "feature_type": type_name,
                        "fastener_size": str(call(definition, "FastenerSize", "") or ""),
                        "standard": str(call(definition, "Standard", "") or ""),
                        "thread_class": str(call(definition, "ThreadClass", "") or ""),
                        "thread_depth_m": call(definition, "ThreadDepth"),
                        "tap_drill_depth_m": call(definition, "TapDrillDepth"),
                        "tap_drill_diameter_m": call(definition, "TapDrillDiameter"),
                        "thread_diameter_m": call(definition, "ThreadDiameter"),
                        "occurrence": model_path.stem + "-1",
                        "part_path": str(model_path),
                    })
                child = call(item, "GetFirstSubFeature")
                children = []
                while child is not None:
                    children.append(child)
                    child = call(child, "GetNextSubFeature")
                stack.extend(reversed(children))
            try:
                feature = feature.GetNextFeature()
            except Exception as exc:
                traversal_errors.append(f"GetNextFeature={type(exc).__name__}:{exc}")
                feature = None
        # Some SOLIDWORKS documents do not expose FirstFeature through the
        # late-bound wrapper, while IFeatureManager.GetFeatures still does.
        manager = getattr(typed_doc, "FeatureManager", None)
        manager_features = ()
        manager_errors = []
        if manager is not None:
            for traverse_subfeatures in (True, False):
                try:
                    values = manager.GetFeatures(bool(traverse_subfeatures))
                    if values:
                        manager_features = values
                        break
                except Exception as exc:
                    manager_errors.append(f"GetFeatures({traverse_subfeatures})={type(exc).__name__}:{exc}")
        manager_features = list(manager_features or [])
        manager_snapshots = [enrich_pattern_snapshot(item, feature_snapshot(item)) for item in manager_features]
        # Reacquire key features by name from ModelDoc2.  The objects returned
        # by FeatureManager.GetFeatures are sufficient for tree display but
        # can lose typed feature-definition methods through late binding.
        named_feature_details = []
        for snapshot in manager_snapshots:
            name = str(snapshot.get("feature_name") or "")
            if not name or str(snapshot.get("feature_type") or "") not in {"LPattern", "MirrorPattern", "CirPattern", "Extrusion", "HoleWzd"}:
                continue
            detail = {"feature_name": name, "feature_type": snapshot.get("feature_type")}
            try:
                named = typed_doc.FeatureByName(name)
                detail["resolved"] = named is not None
                typed_feature = named
                if named is not None:
                    try:
                        typed_feature = wrap_feature_interface(named)
                        detail["feature_cast"] = "GENERATED_Feature"
                    except Exception as exc:
                        detail["feature_cast_error"] = f"{type(exc).__name__}:{exc}"
                definition = typed_feature.GetDefinition() if typed_feature is not None else None
                detail["definition_type"] = str(type(definition).__name__)
                if str(snapshot.get("feature_type") or "") == "LPattern" and definition is not None:
                    try:
                        definition = generated_module.ILinearPatternFeatureData(definition._oleobj_)
                        detail["definition_cast"] = "ILinearPatternFeatureData"
                    except Exception as exc:
                        detail["definition_cast_error"] = f"{type(exc).__name__}:{exc}"
                if definition is not None:
                    accessed = None
                    try:
                        accessed = definition.AccessSelections(doc, None)
                    except Exception as exc:
                        detail["access_error"] = f"{type(exc).__name__}:{exc}"
                    detail["accessed"] = accessed
                    for attr in ("D1Spacing", "D1TotalInstances", "D2Spacing", "D2TotalInstances", "PatternSpacing", "PatternInstanceCount", "ThreadDepth", "FastenerSize"):
                        detail[attr] = call(definition, attr)
                    if detail.get("definition_cast") == "ILinearPatternFeatureData":
                        try:
                            count = int(definition.GetPatternFeatureCount())
                            raw = definition.PatternFeatureArray
                            detail["pattern_feature_count"] = count
                            detail["pattern_feature_raw_type"] = str(type(raw).__name__)
                            # COM may return a SAFEARRAY, tuple, or a single
                            # dispatch pointer for one seeded feature.
                            values = raw if isinstance(raw, (list, tuple)) else [raw]
                            detail["pattern_feature_array"] = [{
                                "name": str(call(item, "Name", "") or ""),
                                "type": str(call(item, "GetTypeName2", "") or ""),
                            } for item in values if item is not None]
                        except Exception as exc:
                            detail["pattern_feature_array_error"] = f"{type(exc).__name__}:{exc}"
                    try:
                        definition.ReleaseSelectionAccess()
                    except Exception:
                        pass
                if str(snapshot.get("feature_type") or "") in {"LPattern", "MirrorPattern", "CirPattern"}:
                    dependency_tests = []
                    for args in ((False, False, False), (True, False, False), (False, False), ()):
                        try:
                            values = typed_feature.GetDependencies(*args)
                            dependency_tests.append({"args": list(args), "values": [str(value) for value in (values or [])]})
                        except Exception as exc:
                            dependency_tests.append({"args": list(args), "error": f"{type(exc).__name__}:{exc}"})
                    detail["dependency_tests"] = dependency_tests
            except Exception as exc:
                detail["resolve_error"] = f"{type(exc).__name__}:{exc}"
            named_feature_details.append(detail)
        if not all_features:
            all_features = manager_snapshots
            for record in all_features:
                key = str(record.get("feature_type") or "")
                feature_types[key] = feature_types.get(key, 0) + 1
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps({
            "version": "PART_DIRECT_PYTHON_COM_V1",
            "api_route": "IModelDoc2.FeatureManager",
            "typelib_status": typelib_status,
            "part_path": str(model_path),
            "hole_wizard_features": rows,
            "hole_wizard_feature_occurrence_count": len(rows),
            "feature_type_counts": feature_types,
            "feature_count": len(all_features),
            "features": all_features,
            "feature_manager_feature_count": len(manager_snapshots),
            "feature_manager_features": manager_snapshots,
            "named_feature_details": named_feature_details,
            "feature_manager_errors": manager_errors,
            "feature_traversal_errors": traversal_errors,
            "document_title": str(call(doc, "GetTitle", "") or ""),
            "document_path": str(call(doc, "GetPathName", "") or ""),
            "document_type": call(doc, "GetType"),
            "raw_document_title": raw_title,
            "raw_document_path": raw_path,
            "raw_document_type": raw_type,
            "open_route": open_route,
            "open_error": getattr(errors, "value", None),
            "open_warning": getattr(warnings, "value", None),
        }, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"HOLE_WIZARD_FEATURE_OCCURRENCE_COUNT={len(rows)}")
        print(f"OUTPUT_PATH={output}")
        return 0
    finally:
        pythoncom.CoUninitialize()


if __name__ == "__main__":
    raise SystemExit(main())
