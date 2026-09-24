import argparse
import csv
import json
from pathlib import Path


def norm(v):
    return str(v or "").strip()


def fnum(v):
    try:
        s = norm(v)
        if s in ["", "未知", "None", "nan"]:
            return None
        return float(s)
    except Exception:
        return None


def load_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def flatten_records(data):
    if isinstance(data, list):
        yield from data
    elif isinstance(data, dict):
        for key in ["records", "items", "faces", "cylinder_faces", "data", "results"]:
            value = data.get(key)
            if isinstance(value, list):
                yield from value
        for value in data.values():
            if isinstance(value, list) and value and isinstance(value[0], dict):
                yield from value


def read_csv(path):
    with open(path, "r", encoding="utf-8-sig", newline="") as f:
        reader = csv.DictReader(f)
        return list(reader), reader.fieldnames or []


def write_csv(path, rows, fields):
    with open(path, "w", encoding="utf-8-sig", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=fields, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(rows)


def expected_main_diameter(row):
    # 优先用实测孔径；没有则用推荐底孔直径
    return fnum(row.get("diameter_mm")) or fnum(row.get("expected_diameter_mm"))


def axis_score(vec, axis_name):
    if not vec or not axis_name:
        return 0.0
    axis_name = axis_name.upper()
    target = {"X": (1, 0, 0), "Y": (0, 1, 0), "Z": (0, 0, 1)}.get(axis_name)
    if not target:
        return 0.0
    try:
        return abs(sum(float(vec[i]) * target[i] for i in range(3)))
    except Exception:
        return 0.0


def main_axis_records(records, occurrence, diameter_mm, axis_name, dia_tol=0.35):
    matched = []
    for rec in records:
        if norm(rec.get("occurrence")) != occurrence:
            continue
        r = fnum(rec.get("radius_m"))
        if r is None:
            continue
        d = r * 2000.0
        dia_delta = abs(d - diameter_mm) if diameter_mm is not None else 999
        ax = axis_score(rec.get("axis_unit_vector"), axis_name)
        extent = fnum(rec.get("axial_extent_mm"))
        if diameter_mm is not None and dia_delta <= dia_tol and ax >= 0.95 and extent is not None:
            matched.append((extent, dia_delta, rec))
    matched.sort(key=lambda x: (x[0], -x[1]), reverse=True)
    return matched


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--csv", required=True)
    ap.add_argument("--cylinder-json", required=True)
    ap.add_argument("--output-root", required=True)
    ap.add_argument("--bottom-clearance-mm", type=float, default=0.5)
    args = ap.parse_args()

    rows, fields = read_csv(args.csv)
    cyl_records = list(flatten_records(load_json(args.cylinder_json)))

    extra_fields = [
        "corrected_status",
        "corrected_severity",
        "corrected_finding_code",
        "corrected_finding",
        "corrected_physical_depth_mm",
        "correction_basis",
    ]
    out_fields = fields + [f for f in extra_fields if f not in fields]

    changed = 0
    pass_by_main_bore = 0
    still_issue = 0

    for row in rows:
        row["corrected_status"] = row.get("status", "")
        row["corrected_severity"] = row.get("severity", "")
        row["corrected_finding_code"] = row.get("finding_code", "")
        row["corrected_finding"] = row.get("finding", "")
        row["corrected_physical_depth_mm"] = row.get("physical_depth_mm", "")
        row["correction_basis"] = ""

        if norm(row.get("hole_kind")) != "tapped":
            continue

        occurrence = norm(row.get("occurrence"))
        thread_depth = fnum(row.get("thread_depth_mm"))
        axis = norm(row.get("axis_alignment"))
        main_dia = expected_main_diameter(row)

        if thread_depth is None or main_dia is None:
            continue

        candidates = main_axis_records(cyl_records, occurrence, main_dia, axis)
        if not candidates:
            continue

        max_extent = candidates[0][0]
        row["corrected_physical_depth_mm"] = f"{max_extent:.3f}"
        row["correction_basis"] = (
            f"按主底孔直径筛选圆柱段：目标直径 {main_dia:.3f} mm，"
            f"轴向 {axis}，最大同径圆柱段 {max_extent:.3f} mm；"
            f"原 physical_depth_mm={row.get('physical_depth_mm')}"
        )

        # 如果主底孔圆柱段已经覆盖攻丝深度，则旧的“攻丝深度大于物理孔深”不成立
        if max_extent + 0.05 >= thread_depth:
            if "THREAD_DEPTH_EXCEEDS_PHYSICAL_HOLE_DEPTH" in norm(row.get("finding_code")):
                changed += 1
                pass_by_main_bore += 1
                row["corrected_status"] = "PASS"
                row["corrected_severity"] = "INFO"
                row["corrected_finding_code"] = "HOLE_SIDE_GEOMETRY_PASS_BY_MAIN_BORE_DEPTH"
                row["corrected_finding"] = (
                    f"按主底孔直径重新计算：同径主孔深 {max_extent:.3f} mm "
                    f">= 攻丝深度 {thread_depth:.3f} mm，原短圆柱段孔深不作为盲孔底判据"
                )
        else:
            if thread_depth - max_extent > args.bottom_clearance_mm:
                still_issue += 1
                row["corrected_status"] = "ISSUE"
                row["corrected_severity"] = "SEVERE"
                row["corrected_finding_code"] = "THREAD_DEPTH_EXCEEDS_MAIN_BORE_DEPTH"
                row["corrected_finding"] = (
                    f"按主底孔直径重新计算：攻丝深度 {thread_depth:.3f} mm "
                    f"> 主孔深 {max_extent:.3f} mm"
                )

    out_dir = Path(args.output_root) / ("hole_side_geometry_corrected_v4_" + __import__("datetime").datetime.now().strftime("%Y%m%d_%H%M%S"))
    out_dir.mkdir(parents=True, exist_ok=True)

    out_csv = out_dir / "孔侧几何检测明细_主孔深修正版_V4.csv"
    out_json = out_dir / "孔侧几何检测主孔深修正报告_V4.json"
    write_csv(out_csv, rows, out_fields)

    report = {
        "input_csv": args.csv,
        "cylinder_json": args.cylinder_json,
        "row_count": len(rows),
        "changed_count": changed,
        "pass_by_main_bore_count": pass_by_main_bore,
        "still_issue_count": still_issue,
        "csv_path": str(out_csv),
    }
    out_json.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

    print(f"ROW_COUNT={len(rows)}")
    print(f"CHANGED_COUNT={changed}")
    print(f"PASS_BY_MAIN_BORE_COUNT={pass_by_main_bore}")
    print(f"STILL_ISSUE_COUNT={still_issue}")
    print(f"CSV_PATH={out_csv}")
    print(f"REPORT_PATH={out_json}")
    print("HOLE_SIDE_MAIN_BORE_DEPTH_CORRECTION_V4_STATUS=SUCCESS")


if __name__ == "__main__":
    main()
