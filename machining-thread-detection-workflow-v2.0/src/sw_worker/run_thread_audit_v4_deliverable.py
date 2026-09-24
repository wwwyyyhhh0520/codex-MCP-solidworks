import argparse
import csv
import json
import os
import re
import subprocess
import sys
from collections import Counter, defaultdict
from datetime import datetime
from pathlib import Path


def run(cmd, cwd=None):
    print("RUN=" + " ".join(str(x) for x in cmd), flush=True)
    p = subprocess.run(cmd, text=True, capture_output=True, cwd=cwd)
    if p.stdout:
        print(p.stdout, flush=True)
    if p.stderr:
        print(p.stderr, file=sys.stderr, flush=True)
    if p.returncode != 0:
        raise RuntimeError(f"COMMAND_FAILED={cmd[0]}|EXIT={p.returncode}")
    return p.stdout


def latest_file(root: Path, pattern: str) -> Path | None:
    files = list(root.rglob(pattern))
    if not files:
        return None
    return max(files, key=lambda p: p.stat().st_mtime)


def read_csv(path: Path):
    with path.open("r", encoding="utf-8-sig", newline="") as f:
        return list(csv.DictReader(f))


def norm_part(s):
    s = str(s or "").strip().split("/")[-1]
    s = re.sub(r"-\d+$", "", s)
    return s


def spec(s):
    s = str(s or "").upper()
    m = re.search(r"M\d+", s)
    return m.group(0) if m else ""


def is_tapped_strict(row):
    txt = " ".join(str(row.get(k, "")) for k in ["feature_name", "hole_kind", "finding", "finding_code"])
    up = txt.upper()
    return ("螺纹" in txt) or ("TAPPED" in up) or ("TAP" in up)


def build_semantic_diff(no_thread_csv: Path, with_thread_csv: Path, out_dir: Path):
    out_dir.mkdir(parents=True, exist_ok=True)

    no_rows = read_csv(no_thread_csv)
    yes_rows = read_csv(with_thread_csv)

    no_by_part_spec = defaultdict(list)
    yes_thread_by_part_spec = defaultdict(list)

    for r in no_rows:
        p = norm_part(r.get("occurrence"))
        sp = spec(r.get("hole_spec") or r.get("feature_name"))
        if p and sp:
            no_by_part_spec[(p, sp)].append(r)

    for r in yes_rows:
        p = norm_part(r.get("occurrence"))
        sp = spec(r.get("hole_spec") or r.get("feature_name"))
        if p and sp and is_tapped_strict(r):
            yes_thread_by_part_spec[(p, sp)].append(r)

    diffs = []
    for key, yrows in sorted(yes_thread_by_part_spec.items()):
        nrows = no_by_part_spec.get(key, [])
        if not nrows:
            diffs.append({
                "diff_type": "THREAD_FEATURE_MISSING_IN_TARGET_MODEL",
                "part": key[0],
                "thread_spec": key[1],
                "baseline_thread_count": len(yrows),
                "target_candidate_count": 0,
                "baseline_features": "; ".join(sorted(set(str(r.get("feature_name", "")) for r in yrows))),
                "target_features": "",
                "explanation": "基准模型存在该规格螺纹孔；目标模型未找到同零件同规格孔侧记录，疑似螺纹/孔语义缺失或特征未对齐。",
            })
        else:
            no_tapped = [r for r in nrows if is_tapped_strict(r)]
            if not no_tapped:
                diffs.append({
                    "diff_type": "THREAD_SEMANTIC_MISSING_IN_TARGET_MODEL",
                    "part": key[0],
                    "thread_spec": key[1],
                    "baseline_thread_count": len(yrows),
                    "target_candidate_count": len(nrows),
                    "baseline_features": "; ".join(sorted(set(str(r.get("feature_name", "")) for r in yrows))),
                    "target_features": "; ".join(sorted(set(str(r.get("feature_name", "")) for r in nrows))),
                    "explanation": "基准模型为螺纹孔；目标模型同零件同规格仅找到非螺纹/普通孔候选，属于目标模型螺纹语义缺失差异。",
                })

    csv_path = out_dir / "螺纹语义差异对比表_V4.csv"
    json_path = out_dir / "螺纹语义差异对比表_V4.json"

    fields = [
        "diff_type",
        "part",
        "thread_spec",
        "baseline_thread_count",
        "target_candidate_count",
        "baseline_features",
        "target_features",
        "explanation",
    ]

    with csv_path.open("w", encoding="utf-8-sig", newline="") as f:
        w = csv.DictWriter(f, fieldnames=fields)
        w.writeheader()
        w.writerows(diffs)

    json_path.write_text(json.dumps(diffs, ensure_ascii=False, indent=2), encoding="utf-8")

    return {
        "semantic_diff_count": len(diffs),
        "semantic_diff_type_counts": dict(Counter(d["diff_type"] for d in diffs)),
        "semantic_diff_csv": str(csv_path),
        "semantic_diff_json": str(json_path),
    }


def run_one_audit(python: str, worker: Path, assembly: str, output_root: Path, no_reuse: bool):
    cmd = [
        python,
        str(worker / "run_thread_audit_v4.py"),
        assembly,
        str(output_root),
    ]
    if no_reuse:
        cmd.append("--no-reuse")

    run(cmd, cwd=str(worker))

    raw_csv = latest_file(output_root, "孔侧几何检测明细_V4.csv")
    if raw_csv is None:
        raise FileNotFoundError("RAW_GEOMETRY_CSV_NOT_FOUND")

    corrected_csv = raw_csv
    corrector = worker / "v4_correct_hole_side_by_main_bore_depth.py"
    cylinder_json = latest_file(output_root, "assembly_cylinder_face_extents_earlybound_v4.json")

    if corrector.is_file() and cylinder_json is not None:
        try:
            run([
                python,
                str(corrector),
                "--csv",
                str(raw_csv),
                "--cylinder-json",
                str(cylinder_json),
                "--output-root",
                str(output_root),
            ], cwd=str(worker))
            fixed = latest_file(output_root, "孔侧几何检测明细_主孔深修正版_V4.csv")
            if fixed is not None:
                corrected_csv = fixed
        except Exception as e:
            print("MAIN_BORE_CORRECTION_SKIPPED=" + repr(e), flush=True)

    return {
        "raw_geometry_csv": str(raw_csv),
        "final_geometry_csv": str(corrected_csv),
        "geometry_report": str(latest_file(output_root, "孔侧几何检测结论_V4.json") or ""),
        "summary": str(latest_file(output_root, "孔侧几何检测摘要_V4.md") or ""),
    }


def build_visual(python: str, worker: Path, final_csv: str, background_image: str, output_root: Path, assembly: str, host: str, port: int):
    html_builder = worker / "v4_build_thread_audit_image_locator_page.py"
    if not html_builder.is_file():
        return {"visual_status": "SKIPPED", "reason": "HTML_BUILDER_NOT_FOUND"}

    if not background_image or not Path(background_image).is_file():
        return {"visual_status": "SKIPPED", "reason": f"BACKGROUND_IMAGE_NOT_FOUND={background_image}"}

    cmd = [
        python,
        str(html_builder),
        str(final_csv),
        str(background_image),
        str(output_root),
        "--assembly-path",
        str(assembly),
        "--embed-image",
    ]

    # 兼容旧版 html_builder：不强塞 locator 参数，后续再统一升级
    run(cmd, cwd=str(worker))

    html = latest_file(output_root, "*.html")
    return {
        "visual_status": "SUCCESS" if html else "HTML_NOT_FOUND_AFTER_BUILD",
        "visual_html": str(html or ""),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--target-assembly", required=True)
    parser.add_argument("--baseline-assembly")
    parser.add_argument("--output-root", required=True)
    parser.add_argument("--background-image", default="")
    parser.add_argument("--locator-host", default="127.0.0.1")
    parser.add_argument("--locator-port", type=int, default=8765)
    parser.add_argument("--no-reuse", action="store_true")
    args = parser.parse_args()

    worker = Path(__file__).resolve().parent
    python = sys.executable

    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    output_root = Path(args.output_root).resolve()
    run_root = output_root / f"thread_audit_deliverable_v4_{stamp}"
    target_root = run_root / "target_audit"
    baseline_root = run_root / "baseline_audit"
    diff_root = run_root / "semantic_diff"
    visual_root = run_root / "visual_html"

    for p in [run_root, target_root, baseline_root, diff_root, visual_root]:
        p.mkdir(parents=True, exist_ok=True)

    manifest = {
        "version": "THREAD_AUDIT_DELIVERABLE_V4",
        "created_at": datetime.now().isoformat(timespec="seconds"),
        "target_assembly": args.target_assembly,
        "baseline_assembly": args.baseline_assembly or "",
        "output_root": str(run_root),
        "locator_host": args.locator_host,
        "locator_port": args.locator_port,
    }

    print("THREAD_AUDIT_DELIVERABLE_STAGE=target_audit", flush=True)
    target = run_one_audit(python, worker, args.target_assembly, target_root, args.no_reuse)
    manifest["target"] = target

    if args.baseline_assembly:
        print("THREAD_AUDIT_DELIVERABLE_STAGE=baseline_audit", flush=True)
        baseline = run_one_audit(python, worker, args.baseline_assembly, baseline_root, args.no_reuse)
        manifest["baseline"] = baseline

        print("THREAD_AUDIT_DELIVERABLE_STAGE=semantic_diff", flush=True)
        manifest["semantic_diff"] = build_semantic_diff(
            Path(target["final_geometry_csv"]),
            Path(baseline["final_geometry_csv"]),
            diff_root,
        )

    print("THREAD_AUDIT_DELIVERABLE_STAGE=visual_html", flush=True)
    manifest["visual"] = build_visual(
        python,
        worker,
        target["final_geometry_csv"],
        args.background_image,
        visual_root,
        args.target_assembly,
        args.locator_host,
        args.locator_port,
    )

    manifest_path = run_root / "thread_audit_deliverable_manifest_v4.json"
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")

    print("DELIVERABLE_RUN_ROOT=" + str(run_root), flush=True)
    print("FINAL_TARGET_CSV=" + target["final_geometry_csv"], flush=True)
    if "semantic_diff" in manifest:
        print("FINAL_SEMANTIC_DIFF_CSV=" + manifest["semantic_diff"]["semantic_diff_csv"], flush=True)
    if manifest["visual"].get("visual_html"):
        print("FINAL_VISUAL_HTML=" + manifest["visual"]["visual_html"], flush=True)
    print("FINAL_MANIFEST_PATH=" + str(manifest_path), flush=True)
    print("THREAD_AUDIT_DELIVERABLE_V4_STATUS=SUCCESS", flush=True)


if __name__ == "__main__":
    main()
