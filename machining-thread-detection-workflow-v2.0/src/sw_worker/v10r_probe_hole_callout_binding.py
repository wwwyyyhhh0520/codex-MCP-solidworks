from __future__ import annotations

import argparse
import json
import math
from datetime import datetime
from pathlib import Path

import pythoncom
import win32com.client
from win32com.client import VARIANT


EDGE_ENTITY_TYPE = 1


def norm(value) -> str:
    return str(value or "").strip()


def value_or_call(obj, name: str, default=None):
    try:
        value = getattr(obj, name)
        return value() if callable(value) else value
    except Exception:
        return default


def list_or_empty(value):
    if value is None:
        return []
    try:
        return list(value)
    except TypeError:
        return [value]


def get_sw():
    try:
        sw = win32com.client.GetActiveObject("SldWorks.Application")
        sw.Visible = True
        return sw, False
    except Exception:
        sw = win32com.client.Dispatch("SldWorks.Application")
        sw.Visible = True
        return sw, True


def open_drawing(sw, path: Path):
    errors = VARIANT(pythoncom.VT_BYREF | pythoncom.VT_I4, 0)
    warnings = VARIANT(pythoncom.VT_BYREF | pythoncom.VT_I4, 0)
    doc = sw.OpenDoc6(str(path), 3, 1, "", errors, warnings)
    if doc is None:
        raise RuntimeError(f"DRAWING_OPEN_FAILED={path}|ERRORS={errors.value}|WARNINGS={warnings.value}")
    try:
        sw.ActivateDoc3(path.name, False, 0, errors)
    except Exception:
        pass
    return doc, int(errors.value), int(warnings.value)


def view_name(view) -> str:
    for attr in ("Name", "GetName2", "GetName"):
        value = norm(value_or_call(view, attr, ""))
        if value:
            return value
    return ""


def view_outline(view) -> list[float]:
    try:
        raw = list_or_empty(value_or_call(view, "GetOutline", ()))
        if len(raw) >= 4:
            return [float(raw[0]), float(raw[1]), float(raw[2]), float(raw[3])]
    except Exception:
        pass
    x = float(value_or_call(view, "X", 0.0) or 0.0)
    y = float(value_or_call(view, "Y", 0.0) or 0.0)
    return [x - 0.03, y - 0.03, x + 0.03, y + 0.03]


def visible_edges(view):
    attempts = []
    for source in (VARIANT(pythoncom.VT_DISPATCH, None), None):
        try:
            edges = list_or_empty(view.GetVisibleEntities2(source, EDGE_ENTITY_TYPE))
            attempts.append(f"GetVisibleEntities2({type(source).__name__})={len(edges)}")
            if edges:
                return edges, attempts
        except Exception as exc:
            attempts.append(f"GetVisibleEntities2({type(source).__name__})={type(exc).__name__}:{exc}")
    return [], attempts


def drawing_views(drawing):
    views = []
    # Robust route: drawing.GetFirstView() returns the sheet-format view first,
    # then each model drawing view via GetNextView().
    try:
        view = drawing.GetFirstView()
        first = True
        while view is not None:
            if first:
                # The first drawing view is the sheet-format view, not a model view.
                first = False
            else:
                views.append(view)
            try:
                view = view.GetNextView()
            except Exception:
                break
    except Exception:
        pass
    if views:
        return views
    # Fallback for SW versions where the current sheet exposes views directly.
    sheet = value_or_call(drawing, "GetCurrentSheet", None)
    if sheet is not None:
        sheet_views = list_or_empty(value_or_call(sheet, "GetViews", ()))
        if len(sheet_views) > 1:
            views.extend(sheet_views[1:])
        else:
            views.extend(sheet_views)
    return views


def curve_probe(edge) -> dict:
    rec: dict = {}
    curve = None
    try:
        curve = getattr(edge, "GetCurve")
        rec["get_curve_property_type"] = type(curve).__name__
    except Exception as exc:
        rec["get_curve_property_error"] = f"{type(exc).__name__}:{exc}"
    if curve is not None:
        for attr in ("IsCircle", "IsEllipse", "IsLine", "IsPeriodic", "Identity"):
            try:
                rec[attr] = value_or_call(curve, attr, "")
            except Exception as exc:
                rec[attr] = f"ERR:{type(exc).__name__}:{exc}"
    try:
        rec["edge_length_m"] = value_or_call(edge, "GetLength", None)
    except Exception as exc:
        rec["edge_length_error"] = f"{type(exc).__name__}:{exc}"
    try:
        params = [float(x) for x in list_or_empty(value_or_call(edge, "GetCurveParams2", ()))]
    except Exception as exc:
        params = []
        rec["params_error"] = f"{type(exc).__name__}:{exc}"
    rec["params_len"] = len(params)
    rec["params"] = [round(x, 9) for x in params[:16]]
    if len(params) >= 6:
        chord = math.sqrt(sum((params[i + 3] - params[i]) ** 2 for i in range(3)))
        rec["chord_mm"] = round(chord * 1000.0, 4)
        rec["midpoint_m"] = [round((params[i] + params[i + 3]) / 2.0, 9) for i in range(3)]
    # Empirical SW drawing circle/arc candidates often expose 11 curve params.
    rec["circle_like_by_params"] = len(params) in (10, 11, 12) and float(rec.get("chord_mm") or 0.0) < 100.0
    return rec


def try_native_callout(drawing, view, edge, x: float, y: float) -> dict:
    rec = {"attempted": True}
    name = view_name(view)
    try:
        rec["activate_view"] = bool(drawing.ActivateView(name))
    except Exception as exc:
        rec["activate_view_error"] = f"{type(exc).__name__}:{exc}"
    try:
        drawing.ClearSelection2(True)
    except Exception:
        pass
    selected = False
    try:
        selected = bool(view.SelectEntity(edge, False))
        rec["select_entity"] = selected
    except Exception as exc:
        rec["select_entity_error"] = f"{type(exc).__name__}:{exc}"
    if selected:
        try:
            created = drawing.AddHoleCallout2(float(x), float(y), 0.0)
            rec["add_hole_callout2_returned"] = created is not None
        except Exception as exc:
            rec["add_hole_callout2_error"] = f"{type(exc).__name__}:{exc}"
    return rec


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("drawing_path")
    parser.add_argument("output_root")
    parser.add_argument("--max-edges-per-view", type=int, default=120)
    parser.add_argument("--try-native", action="store_true")
    args = parser.parse_args()

    drawing_path = Path(args.drawing_path).expanduser().resolve()
    output_root = Path(args.output_root).expanduser().resolve()
    out_dir = output_root / f"v10r_hole_callout_probe_{datetime.now():%Y%m%d_%H%M%S}"
    out_dir.mkdir(parents=True, exist_ok=True)
    result_path = out_dir / "孔标注绑定探针_V10R.json"

    sw, created_sw = get_sw()
    drawing, errors, warnings = open_drawing(sw, drawing_path)
    try:
        try:
            drawing.ForceRebuild3(False)
        except Exception:
            pass
        model_views = drawing_views(drawing)
        records = []
        for view_index, view in enumerate(model_views[:3], start=1):
            name = view_name(view) or f"view_{view_index}"
            outline = view_outline(view)
            edges, attempts = visible_edges(view)
            edge_records = []
            circle_candidates = []
            for edge_index, edge in enumerate(edges[: args.max_edges_per_view], start=1):
                probe = curve_probe(edge)
                probe["edge_index"] = edge_index
                if probe.get("circle_like_by_params"):
                    circle_candidates.append((edge_index, edge, probe))
                if len(edge_records) < 30 or probe.get("circle_like_by_params"):
                    edge_records.append(probe)
            native_results = []
            if args.try_native:
                callout_x = min(max(outline[2] + 0.025, 0.02), 0.39)
                callout_y = max(outline[3] - 0.02, 0.02)
                for local_idx, (edge_index, edge, probe) in enumerate(circle_candidates[:8], start=1):
                    nr = try_native_callout(drawing, view, edge, callout_x, callout_y - local_idx * 0.008)
                    nr["edge_index"] = edge_index
                    nr["probe"] = probe
                    native_results.append(nr)
            records.append({
                "view_index": view_index,
                "view_name": name,
                "outline_m": outline,
                "edge_count": len(edges),
                "attempts": attempts,
                "circle_candidate_count": len(circle_candidates),
                "native_try_count": len(native_results),
                "native_success_count": sum(1 for x in native_results if x.get("add_hole_callout2_returned")),
                "native_results": native_results,
                "edge_samples": edge_records,
            })
        result = {
            "status": "SUCCESS",
            "drawing_path": str(drawing_path),
            "created_sw": created_sw,
            "open_errors": errors,
            "open_warnings": warnings,
            "try_native": bool(args.try_native),
            "view_count": len(records),
            "views": records,
        }
        result_path.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"RESULT_PATH={result_path}")
        print("V10R_HOLE_CALLOUT_BINDING_PROBE_STATUS=SUCCESS")
    finally:
        try:
            drawing.ClearSelection2(True)
        except Exception:
            pass


if __name__ == "__main__":
    main()
