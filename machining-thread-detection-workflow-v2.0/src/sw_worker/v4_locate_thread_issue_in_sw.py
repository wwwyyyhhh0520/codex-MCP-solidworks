import argparse
import csv
import os
import sys
from pathlib import Path

import pythoncom
import win32com.client

from sw_com_session import connect_solidworks_session


def norm(s):
    return str(s or "").strip()


def value_or_call(obj, name, default=""):
    try:
        value = getattr(obj, name)
        if callable(value):
            value = value()
        return value
    except Exception:
        return default


def find_row(csv_path, issue_id):
    with open(csv_path, "r", encoding="utf-8-sig", newline="") as f:
        rows = list(csv.DictReader(f))

    issue_id = norm(issue_id)
    keys = ["hole_id", "??", "issue_id", "anomaly_id"]

    # 1) ????
    for row in rows:
        for k in keys:
            if norm(row.get(k)) == issue_id:
                return row

    # 2) ?? THR-xxxx / HOLE-xxxx??????????
    suffix = ""
    if "-" in issue_id:
        suffix = issue_id.rsplit("-", 1)[-1]
    if suffix and suffix.isdigit():
        candidates = []
        for row in rows:
            for k in keys:
                value = norm(row.get(k))
                if value.endswith(suffix):
                    candidates.append(row)
                    break
        if len(candidates) == 1:
            return candidates[0]
        if len(candidates) > 1:
            raise RuntimeError(f"ISSUE_AMBIGUOUS={issue_id}; MATCH_COUNT={len(candidates)}")

    sample = []
    for row in rows[:12]:
        sample.append("|".join(norm(row.get(k)) for k in keys))
    raise RuntimeError(f"ISSUE_NOT_FOUND={issue_id}; SAMPLE={sample}")


def get_sw():
    result = connect_solidworks_session(visible=True)
    return result.sw


def open_doc(sw, path, doc_type):
    errors = win32com.client.VARIANT(pythoncom.VT_BYREF | pythoncom.VT_I4, 0)
    warnings = win32com.client.VARIANT(pythoncom.VT_BYREF | pythoncom.VT_I4, 0)
    doc = sw.OpenDoc6(str(path), doc_type, 0, "", errors, warnings)
    if doc is None:
        raise RuntimeError(f"OPEN_DOC_FAILED={path}|ERRORS={errors.value}|WARNINGS={warnings.value}")
    sw.ActivateDoc3(Path(path).name, False, 0, errors)
    return doc


def iter_components(root):
    try:
        children = root.GetChildren()
    except Exception:
        children = None
    if not children:
        return
    for c in children:
        yield c
        yield from iter_components(c)


def get_all_components(asm_doc):
    comps = []
    try:
        raw = asm_doc.GetComponents(False)
        if raw:
            comps.extend(list(raw))
    except Exception:
        pass

    if not comps:
        try:
            cfg = asm_doc.ConfigurationManager.ActiveConfiguration
            root = cfg.GetRootComponent3(True)
            comps.extend(list(iter_components(root)))
        except Exception:
            pass

    # 去重
    seen = set()
    unique = []
    for c in comps:
        try:
            key = norm(value_or_call(c, "Name2", "")) + "|" + norm(value_or_call(c, "GetPathName", ""))
        except Exception:
            key = str(id(c))
        if key not in seen:
            seen.add(key)
            unique.append(c)
    return unique


def clean_comp_name(value):
    s = norm(value)
    for token in ["(固定)", "(轻化)", "(压缩)"]:
        s = s.replace(token, "")
    s = s.replace(" -> ", "/")
    s = s.replace("\\", "/")
    s = s.split("/")[-1]
    if "<" in s:
        s = s.split("<", 1)[0]
    if " " in s:
        s = s.split(" ", 1)[0]
    return s.strip()


def match_component(asm_doc, occurrence):
    try:
        asm_doc.ResolveAllLightWeightComponents(True)
    except Exception:
        pass

    candidates = get_all_components(asm_doc)
    occurrence = norm(occurrence)
    occ_leaf = clean_comp_name(occurrence)
    occ_stem = Path(occ_leaf).stem

    best = None
    best_score = -1
    debug = []

    for c in candidates:
        try:
            name2 = norm(c.Name2)
        except Exception:
            name2 = ""
        path = norm(value_or_call(c, "GetPathName", ""))

        name_leaf = clean_comp_name(name2)
        name_stem = Path(name_leaf).stem
        path_stem = Path(path).stem if path else ""

        score = 0
        if occurrence and occurrence == name2:
            score += 1200
        if occ_leaf and occ_leaf == name_leaf:
            score += 1000
        if occ_stem and occ_stem == name_stem:
            score += 900
        if path_stem and occ_stem and path_stem == occ_stem:
            score += 800
        if occ_stem and occ_stem in name_stem:
            score += 500
        if name_stem and name_stem in occ_stem:
            score += 450
        if path_stem and occ_stem and occ_stem in path_stem:
            score += 350

        debug.append((score, name2, path))

        if score > best_score:
            best = c
            best_score = score

    if best is None or best_score <= 0:
        debug_top = sorted(debug, reverse=True)[:12]
        sample = " | ".join([f"{s}:{n}" for s, n, p in debug_top])
        raise RuntimeError(f"COMPONENT_NOT_FOUND={occurrence}; SAMPLE={sample}")

    return best, best_score


def component_debug_info(comp):
    info = {}
    for attr in [
        "Name2",
        "ReferencedConfiguration",
        "GetPathName",
        "GetSuppression",
        "IsSuppressed",
        "IsLightWeight",
        "IsSpeedPak",
        "GetSelectByIDString",
        "GetSelectByIDString2",
        "GetBox",
    ]:
        try:
            value = value_or_call(comp, attr, "")
            if attr == "GetBox" and value:
                try:
                    value = list(value)
                except Exception:
                    pass
            info[attr] = value
        except Exception as e:
            info[attr] = "ERR:" + str(e)

    try:
        model_doc = value_or_call(comp, "GetModelDoc2", None)
        info["GetModelDoc2_is_none"] = model_doc is None
    except Exception as e:
        info["GetModelDoc2"] = "ERR:" + str(e)

    return info



def select_component(sw, asm_doc, comp):
    """
    ????????????????
    ???? SolidWorks ???????????????????
    """
    debug = []

    def add_debug(msg):
        try:
            debug.append(str(msg))
        except Exception:
            pass

    try:
        asm_doc.ClearSelection2(True)
    except Exception as e:
        add_debug(f"ClearSelection2 failed: {e}")

    name2 = norm(value_or_call(comp, "Name2", ""))
    asm_title = norm(value_or_call(asm_doc, "GetTitle", ""))
    asm_stem = Path(asm_title).stem if asm_title else ""

    sw_empty_callout = win32com.client.VARIANT(pythoncom.VT_DISPATCH, None)

    select_names = []

    # 1) SolidWorks ?????????????
    for attr in ["GetSelectByIDString", "GetSelectByIDString2"]:
        try:
            v = value_or_call(comp, attr, "")
            v = norm(v)
            if v:
                select_names.append(v)
                add_debug(f"{attr}={v}")
        except Exception as e:
            add_debug(f"{attr} failed: {e}")

    # 2) ?????????
    if name2:
        select_names.append(name2)
        if asm_stem:
            select_names.append(f"{name2}@{asm_stem}")
        if asm_title:
            select_names.append(f"{name2}@{asm_title}")

    # ??
    seen = set()
    select_names = [x for x in select_names if x and not (x in seen or seen.add(x))]

    # 3) Select4??????
    try:
        sel_mgr = value_or_call(asm_doc, "SelectionManager", None)
        sel_data = None
        if sel_mgr is not None:
            try:
                sel_data = sel_mgr.CreateSelectData()
            except Exception:
                sel_data = None
        ok = bool(comp.Select4(False, sel_data, False))
        add_debug(f"Select4={ok}")
        if ok:
            try:
                asm_doc.ViewZoomtoSelection()
            except Exception:
                try:
                    sw.ActiveDoc.ViewZoomtoSelection()
                except Exception:
                    pass
            return True
    except Exception as e:
        add_debug(f"Select4 failed: {e}")

    # 4) SelectByID2????/?????
    for sel_name in select_names:
        try:
            ok = bool(asm_doc.Extension.SelectByID2(sel_name, "COMPONENT", 0.0, 0.0, 0.0, False, 0, sw_empty_callout, 0))
            add_debug(f"SelectByID2 COMPONENT {sel_name} => {ok}")
            if ok:
                try:
                    asm_doc.ViewZoomtoSelection()
                except Exception:
                    try:
                        sw.ActiveDoc.ViewZoomtoSelection()
                    except Exception:
                        pass
                return True
        except Exception as e:
            add_debug(f"SelectByID2 COMPONENT {sel_name} failed: {e}")

    # 5) ???????????????
    try:
        box = value_or_call(comp, "GetBox", None)
        if box and len(box) >= 6:
            cx = (float(box[0]) + float(box[3])) / 2
            cy = (float(box[1]) + float(box[4])) / 2
            cz = (float(box[2]) + float(box[5])) / 2

            for obj_type in ["COMPONENT", "FACE", "BODYFEATURE"]:
                try:
                    ok = bool(asm_doc.Extension.SelectByID2("", obj_type, float(cx), float(cy), float(cz), False, 0, sw_empty_callout, 0))
                    add_debug(f"SelectByID2 {obj_type} at box center => {ok}")
                    if ok:
                        try:
                            asm_doc.ViewZoomtoSelection()
                        except Exception:
                            try:
                                sw.ActiveDoc.ViewZoomtoSelection()
                            except Exception:
                                pass
                        return True
                except Exception as e:
                    add_debug(f"box center select {obj_type} failed: {e}")
    except Exception as e:
        add_debug(f"box fallback failed: {e}")

    try:
        asm_doc.ViewZoomtofit2()
    except Exception:
        pass

    # ????????
    try:
        comp._select_debug = " | ".join(debug)
    except Exception:
        pass

    return False


def iter_features(model):
    feat = value_or_call(model, "FirstFeature", None)
    while feat is not None:
        yield feat

        sub = value_or_call(feat, "GetFirstSubFeature", None)
        while sub is not None:
            yield sub
            sub = value_or_call(sub, "GetNextSubFeature", None)

        feat = value_or_call(feat, "GetNextFeature", None)


def select_feature_in_part(sw, part_path, feature_name):
    if not part_path:
        return False, "COMPONENT_PATH_EMPTY"

    part_doc = open_doc(sw, part_path, 1)  # swDocPART
    target = norm(feature_name)

    best = None
    for feat in iter_features(part_doc):
        try:
            fname = norm(feat.Name)
        except Exception:
            fname = ""
        if fname == target:
            best = feat
            break

    if best is None:
        for feat in iter_features(part_doc):
            try:
                fname = norm(feat.Name)
            except Exception:
                fname = ""
            if target and (target in fname or fname in target):
                best = feat
                break

    if best is None:
        names = []
        for feat in iter_features(part_doc):
            try:
                fname = norm(value_or_call(feat, "Name", ""))
            except Exception:
                fname = ""
            if fname:
                names.append(fname)
        sample = " | ".join(names[:40])

        # 特征树不可读时，至少把问题零件打开并缩放，作为可用定位入口。
        try:
            part_doc.ClearSelection2(True)
        except Exception:
            pass
        try:
            part_doc.ViewZoomtofit2()
        except Exception:
            pass
        try:
            sw.ActivateDoc3(Path(part_path).name, False, 0, pythoncom.VARIANT(pythoncom.VT_BYREF | pythoncom.VT_I4, 0))
        except Exception:
            pass

        return False, f"FEATURE_NOT_FOUND={target}; PART_OPENED_FOR_REVIEW=True; FEATURE_SAMPLE={sample}"

    try:
        part_doc.ClearSelection2(True)
    except Exception:
        pass

    ok = False
    try:
        ok = bool(best.Select2(False, 0))
    except Exception as e:
        return False, f"FEATURE_SELECT_FAILED={e}"

    try:
        part_doc.ViewZoomtofit2()
    except Exception:
        pass

    return ok, "FEATURE_SELECTED"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--assembly", required=True)
    ap.add_argument("--csv", required=True)
    ap.add_argument("--issue-id", required=True)
    args = ap.parse_args()

    row = find_row(args.csv, args.issue_id)

    occurrence = norm(row.get("occurrence") or row.get("孔零件/实例") or row.get("孔零件") or row.get("孔零件/组件"))
    feature_name = norm(row.get("feature_name") or row.get("孔特征"))
    hole_spec = norm(row.get("hole_spec") or row.get("孔规格"))
    status = norm(row.get("status") or row.get("等级"))
    finding = norm(row.get("finding") or row.get("问题说明"))

    sw = get_sw()
    sw.Visible = True

    asm_doc = open_doc(sw, args.assembly, 2)  # swDocASSEMBLY

    comp, score = match_component(asm_doc, occurrence)
    component_selected = select_component(sw, asm_doc, comp)

    comp_path = norm(value_or_call(comp, "GetPathName", ""))

    if not comp_path:
        try:
            model_doc = value_or_call(comp, "GetModelDoc2", None)
            if model_doc is not None:
                comp_path = norm(value_or_call(model_doc, "GetPathName", ""))
        except Exception:
            pass

    feature_selected = False
    feature_note = "ASSEMBLY_COMPONENT_LOCATE_MODE; part feature selection skipped by user preference"

    print("LOCATE_THREAD_ISSUE_STATUS=SUCCESS")
    print(f"ISSUE_ID={args.issue_id}")
    print(f"OCCURRENCE={occurrence}")
    print(f"FEATURE_NAME={feature_name}")
    print(f"HOLE_SPEC={hole_spec}")
    print(f"STATUS={status}")
    print(f"FINDING={finding}")
    print(f"MATCHED_COMPONENT={norm(comp.Name2)}")
    print(f"COMPONENT_PATH={comp_path}")
    print(f"MATCH_SCORE={score}")
    try:
        active_title = norm(value_or_call(sw.ActiveDoc, "GetTitle", ""))
    except Exception:
        active_title = ""
    print(f"ACTIVE_DOC_TITLE={active_title}")
    print(f"COMPONENT_SELECTED={component_selected}")
    print(f"COMPONENT_DEBUG={component_debug_info(comp)}")
    print(f"FEATURE_SELECTED={feature_selected}")
    print(f"FEATURE_NOTE={feature_note}")


if __name__ == "__main__":
    main()









