"""Read-only fastener-identity evidence for co-axial tapped-hole partners.

This stage does not issue thread conclusions.  It separates a physically
co-axial external cylinder from a verified screw, so ordinary cylindrical
geometry, nuts, washers and pins cannot be mistaken for a screw-hole pair.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter
from datetime import datetime
from pathlib import Path

REPO = Path(
    r"C:\Users\admin\Desktop\solidworks-automation-skill-src"
    r"\solidworks-automation-skill-main"
)
sys.path.insert(0, str(REPO / "scripts"))

from sw_assembly import iter_feature_tree, open_document  # noqa: E402
from sw_connect import connect_solidworks  # noqa: E402


THREAD_KEYS = ("ThreadSpec", "ThreadLen", "Pitch", "FastenerType")
THREAD_TYPES = {"CosmeticThread", "Thread"}
SCREW_RE = re.compile(r"螺钉|螺栓|螺杆|\b(?:screw|bolt)\b", re.I)
NON_SCREW_RE = re.compile(r"螺母|垫圈|垫片|销|\b(?:nut|washer|shim|pin)\b", re.I)
METRIC_RE = re.compile(r"\bM\s*(\d+(?:\.\d+)?)(?:\s*[xX×]\s*([\d.]+))?\b", re.I)


def value_or_call(obj, name, default=None):
    try:
        value = getattr(obj, name)
        return value() if callable(value) else value
    except Exception:
        return default


def call_member(obj, name, *args, default=None):
    try:
        return getattr(obj, name)(*args)
    except Exception:
        return default


def property_value(manager, name):
    for method in ("Get2", "Get3", "Get4", "Get5", "Get6"):
        try:
            value = getattr(manager, method)(name)
            if isinstance(value, tuple):
                texts = [str(item).strip() for item in value if isinstance(item, str) and item.strip()]
                if texts:
                    return texts[-1]
            if isinstance(value, str) and value.strip():
                return value.strip()
        except Exception:
            pass
    return ""


def metadata(model, configuration):
    extension = value_or_call(model, "Extension")
    values = {}
    if extension is None:
        return values
    for scope in (str(configuration or ""), ""):
        manager = call_member(extension, "CustomPropertyManager", scope)
        if manager is None:
            continue
        for key in THREAD_KEYS:
            values.setdefault(key, property_value(manager, key))
    return values


def feature_rows(model):
    rows = []
    for feature, _depth in iter_feature_tree(model):
        kind = str(value_or_call(feature, "GetTypeName2", "") or "")
        if kind not in THREAD_TYPES:
            continue
        name = str(value_or_call(feature, "Name", "") or "")
        match = METRIC_RE.search(name)
        rows.append({
            "feature_type": kind,
            "feature_name": name,
            "metric_spec_from_name": "" if match is None else f"M{match.group(1)}",
        })
    return rows


def classify(occurrence, props, features):
    text = str(occurrence or "")
    if NON_SCREW_RE.search(text):
        return "NON_SCREW_PART_EXCLUDED"
    # In this data set the feature names explicitly say "孔螺纹线".  A
    # cosmetic thread on a hole is hole-side evidence, never screw identity.
    # Keep it out of the screw candidate set even though its feature type is
    # CosmeticThread.
    if any("孔螺" in str(item.get("feature_name") or "") for item in features):
        return "THREADED_HOLE_PART_EXCLUDED"
    if props.get("ThreadSpec"):
        return "SCREW_METADATA_CONFIRMED"
    if SCREW_RE.search(text) and features:
        return "SCREW_THREAD_FEATURE_CONFIRMED"
    if SCREW_RE.search(text):
        return "SCREW_NAME_CANDIDATE_METADATA_MISSING"
    if features:
        return "THREADED_PART_BUT_SCREW_IDENTITY_UNVERIFIED"
    return "PARTNER_FASTENER_IDENTITY_NOT_VERIFIABLE"


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("partner_visibility_json")
    parser.add_argument("output_root")
    return parser.parse_args()


def main():
    args = parse_args()
    source = Path(args.partner_visibility_json).expanduser().resolve()
    if not source.is_file():
        raise FileNotFoundError(f"PARTNER_VISIBILITY_JSON_NOT_FOUND={source}")
    data = json.loads(source.read_text(encoding="utf-8-sig"))
    partners = {}
    links = []
    for record in data.get("records") or []:
        if record.get("status") != "TAPPED_HOLE_EXTERNAL_COAXIAL_GEOMETRY_FOUND":
            continue
        for geometry in record.get("external_coaxial_geometry") or []:
            occurrence = str(geometry.get("occurrence") or "")
            path = str(geometry.get("part_path") or "")
            if not occurrence or not path.lower().endswith(".sldprt"):
                continue
            partners.setdefault(occurrence, {"occurrence": occurrence, "part_path": path})
            links.append({
                "physical_hole_key": record.get("physical_hole_key") or "",
                "hole_occurrence": (record.get("hole") or {}).get("occurrence") or "",
                "hole_thread_spec": (record.get("hole") or {}).get("thread_spec") or "",
                "partner_occurrence": occurrence,
                "axis_distance_mm": geometry.get("axis_distance_mm"),
                "radius_mm": geometry.get("radius_mm"),
            })
    sw, _ = connect_solidworks()
    cache, owned_titles, rows = {}, set(), []
    try:
        for partner in partners.values():
            path = partner["part_path"]
            model = cache.get(path.casefold())
            if model is None:
                model = open_document(sw, path, read_only=True, silent=True, raise_on_error=False)
                cache[path.casefold()] = model
                title = value_or_call(model, "GetTitle") if model is not None else None
                if title:
                    owned_titles.add(str(title))
            configuration = ""
            props = metadata(model, configuration) if model is not None else {}
            features = feature_rows(model) if model is not None else []
            state = "PART_FILE_OPEN_FAILED" if model is None else classify(partner["occurrence"], props, features)
            rows.append({
                **partner,
                "thread_spec_property": props.get("ThreadSpec", ""),
                "thread_length_property": props.get("ThreadLen", ""),
                "pitch_property": props.get("Pitch", ""),
                "fastener_type_property": props.get("FastenerType", ""),
                "thread_features": features,
                "fastener_identity_state": state,
                "notice": "Only SCREW_* states may proceed to a screw-hole rule. Other states receive no thread verdict.",
            })
    finally:
        for title in owned_titles:
            try:
                sw.CloseDoc(title)
            except Exception:
                pass
    by_occurrence = {row["occurrence"]: row for row in rows}
    enriched_links = [{**link, "partner_evidence": by_occurrence.get(link["partner_occurrence"], {})} for link in links]
    states = Counter(row["fastener_identity_state"] for row in rows)
    eligible = [link for link in enriched_links if str((link.get("partner_evidence") or {}).get("fastener_identity_state", "")).startswith("SCREW_")]
    output_dir = Path(args.output_root).expanduser().resolve() / f"external_partner_fastener_evidence_v4_{datetime.now():%Y%m%d_%H%M%S}"
    output_dir.mkdir(parents=True, exist_ok=False)
    output = {
        "version": "V4_EVIDENCE_ONLY",
        "source_partner_visibility_json": str(source),
        "generated_at": datetime.now().isoformat(timespec="seconds"),
        "unique_external_partner_count": len(rows),
        "fastener_identity_state_counts": dict(states),
        "coaxial_link_count": len(enriched_links),
        "screw_hole_rule_eligible_link_count": len(eligible),
        "partners": rows,
        "coaxial_links": enriched_links,
        "eligible_screw_hole_links": eligible,
        "important_notice": "This output is identity evidence, not a spec-mismatch, engagement or bottoming verdict.",
    }
    path = output_dir / "攻丝孔外部伙伴_紧固件身份证据_V4.json"
    path.write_text(json.dumps(output, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"UNIQUE_EXTERNAL_PARTNER_COUNT={len(rows)}")
    print("FASTENER_IDENTITY_STATE_COUNTS=" + json.dumps(dict(states), ensure_ascii=False))
    print(f"COAXIAL_LINK_COUNT={len(enriched_links)}")
    print(f"SCREW_HOLE_RULE_ELIGIBLE_LINK_COUNT={len(eligible)}")
    print(f"RESULT_PATH={path}")
    print("EXTERNAL_PARTNER_FASTENER_EVIDENCE_V4_STATUS=SUCCESS")


if __name__ == "__main__":
    main()
