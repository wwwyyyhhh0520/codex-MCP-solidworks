"""Create a truthful V4 thread-audit report from available CAD evidence.

Mode is chosen by evidence, never by assumption:
* FULL_SCREW_HOLE_CHECK_READY requires at least one identified screw-hole link.
* HOLE_SIDE_ONLY is used when no screw occurrence/metadata is available.

The hole-side screen never claims to detect a missing screw, wrong screw,
engagement count, or bottoming.  Those all require screw-side inputs.
"""

from __future__ import annotations

import argparse
import csv
import json
import re
from collections import Counter
from datetime import datetime
from pathlib import Path


METRIC_RE = re.compile(r"\bM\s*(\d+(?:\.\d+)?)\b", re.I)
TAPPED_NAME_RE = re.compile(r"螺纹|攻丝|螺牙|\bthread\b", re.I)
CLEARANCE_NAME_RE = re.compile(r"间隙|槽口|打孔尺寸|直径孔|通孔|\bclearance\b", re.I)


def load(path_text, label):
    path = Path(path_text).expanduser().resolve()
    if not path.is_file():
        raise FileNotFoundError(f"{label}_NOT_FOUND={path}")
    return path, json.loads(path.read_text(encoding="utf-8-sig"))


def nominal_mm(value):
    match = METRIC_RE.search(str(value or ""))
    return None if match is None else float(match.group(1))


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("holewizard_parameters_json")
    parser.add_argument("partner_fastener_evidence_json")
    parser.add_argument("output_root")
    parser.add_argument("--depth-screen-factor", type=float, default=1.0)
    return parser.parse_args()


def write_csv(path, rows):
    fields = [
        "occurrence", "part_path", "feature_name", "hole_standard",
        "hole_thread_spec", "thread_depth_mm", "screening_depth_limit_mm",
        "check_status", "severity", "finding", "required_next_action",
    ]
    with path.open("w", newline="", encoding="utf-8-sig") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)


def main():
    args = parse_args()
    if args.depth_screen_factor <= 0:
        raise ValueError("DEPTH_SCREEN_FACTOR_MUST_BE_POSITIVE")
    holes_path, holes_data = load(args.holewizard_parameters_json, "HOLEWIZARD_PARAMETERS_JSON")
    evidence_path, evidence_data = load(args.partner_fastener_evidence_json, "PARTNER_FASTENER_EVIDENCE_JSON")
    eligible = evidence_data.get("eligible_screw_hole_links") or []
    mode = "FULL_SCREW_HOLE_CHECK_READY" if eligible else "HOLE_SIDE_ONLY"
    rows, checks = [], Counter()
    for feature in holes_data.get("hole_wizard_features") or []:
        feature_name = str(feature.get("feature_name") or "")
        spec = str(feature.get("fastener_size") or "").strip()
        nominal = nominal_mm(spec)
        named_nominal = nominal_mm(feature_name)
        depth_m = feature.get("thread_depth_m")
        try:
            depth_mm = float(depth_m) * 1000.0 if depth_m is not None else None
        except (TypeError, ValueError):
            depth_mm = None
        limit_mm = None if nominal is None else nominal * args.depth_screen_factor
        # Hole Wizard's FastenerSize is also used for clearance holes and
        # slots.  ThreadDepth=0 on those features is normal, not an error.
        # A feature enters the tapped-hole screen only with positive readable
        # ThreadDepth or an explicit tapped-thread name.
        named_tapped = bool(TAPPED_NAME_RE.search(feature_name))
        named_clearance = bool(CLEARANCE_NAME_RE.search(feature_name))
        is_tapped = (depth_mm is not None and depth_mm > 0) or (named_tapped and not named_clearance)
        status, severity, finding, action = "PASS", "INFO", "孔侧规格与深度数据可读", "无需处理"
        if not is_tapped:
            status, severity = "NOT_APPLICABLE", "INFO"
            finding = "非攻丝孔或攻丝证据不足；不进入螺纹孔深度检查"
            action = "无需处理"
        elif nominal is None:
            status, severity = "REVIEW_REQUIRED", "WARNING"
            finding = "HoleWizard 螺纹规格不可解析，无法确认孔侧公称规格"
            action = "由机械工程师确认 HoleWizard 的螺纹规格"
        elif named_nominal is not None and abs(named_nominal - nominal) > 1e-9:
            status, severity = "ISSUE", "SEVERE"
            finding = f"特征名规格 M{named_nominal:g} 与 HoleWizard 规格 M{nominal:g} 不一致"
            action = "核对孔特征定义与命名，修正后重跑"
        elif depth_mm is None:
            status, severity = "REVIEW_REQUIRED", "WARNING"
            finding = "HoleWizard 有规格但有效攻丝深度不可读取"
            action = "由机械工程师确认 ThreadDepth"
        elif depth_mm <= 0:
            status, severity = "ISSUE", "SEVERE"
            finding = f"有效攻丝深度 {depth_mm:g} mm，不应小于等于零"
            action = "修正 HoleWizard 攻丝深度后重跑"
        elif limit_mm is not None and depth_mm < limit_mm:
            status, severity = "REVIEW_REQUIRED", "WARNING"
            finding = f"有效攻丝深度 {depth_mm:g} mm 小于孔侧筛查阈值 {limit_mm:g} mm（{args.depth_screen_factor:g}D）"
            action = "确认该深度是否满足连接设计；此项不是啮合圈数结论"
        checks[status] += 1
        rows.append({
            "occurrence": feature.get("occurrence") or "",
            "part_path": feature.get("part_path") or "",
            "feature_name": feature_name,
            "hole_standard": feature.get("standard") or "",
            "hole_thread_spec": spec,
            "thread_depth_mm": "" if depth_mm is None else round(depth_mm, 6),
            "screening_depth_limit_mm": "" if limit_mm is None else round(limit_mm, 6),
            "check_status": status,
            "severity": severity,
            "finding": finding,
            "required_next_action": action,
        })
    run_dir = Path(args.output_root).expanduser().resolve() / f"thread_audit_coverage_v4_{datetime.now():%Y%m%d_%H%M%S}"
    run_dir.mkdir(parents=True, exist_ok=False)
    csv_path = run_dir / "孔侧螺纹检查明细_V4.csv"
    write_csv(csv_path, rows)
    no_screw_notice = (
        "缺少可识别的螺钉实例或 BOM 映射；无法校验螺丝-孔装配匹配，"
        "漏螺丝、螺丝用错、啮合圈数不足和螺钉顶底均无法检出。"
    )
    result = {
        "version": "V4",
        "generated_at": datetime.now().isoformat(timespec="seconds"),
        "audit_mode": mode,
        "source_holewizard_parameters_json": str(holes_path),
        "source_partner_fastener_evidence_json": str(evidence_path),
        "identified_screw_hole_link_count": len(eligible),
        "hole_feature_count": len(rows),
        "hole_side_check_counts": dict(checks),
        "hole_side_depth_screen_factor_D": args.depth_screen_factor,
        "full_check_capabilities": {
            "thread_spec_mismatch": bool(eligible),
            "engagement_turns": bool(eligible),
            "screw_bottoming": bool(eligible),
        },
        "hole_side_capabilities": [
            "HoleWizard 规格可解析性", "特征名与 HoleWizard 规格一致性",
            "有效攻丝深度可读性与可配置深度筛查",
        ],
        "mandatory_notice": "" if eligible else no_screw_notice,
        "results_csv": str(csv_path),
    }
    json_path = run_dir / "螺纹审查覆盖与孔侧结论_V4.json"
    json_path.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    md_path = run_dir / "螺纹审查结论_V4.md"
    md_path.write_text(
        "# 螺纹审查结论 V4\n\n"
        f"- 审查模式：`{mode}`\n"
        f"- 可识别螺钉—孔配对：{len(eligible)}\n"
        f"- HoleWizard 孔特征：{len(rows)}\n"
        f"- 孔侧结果：{json.dumps(dict(checks), ensure_ascii=False)}\n\n"
        + (f"> {no_screw_notice}\n" if not eligible else "")
        + "\n本结果不把缺失的螺钉侧数据替换为推测值。\n",
        encoding="utf-8",
    )
    print(f"AUDIT_MODE={mode}")
    print(f"IDENTIFIED_SCREW_HOLE_LINK_COUNT={len(eligible)}")
    print(f"HOLE_FEATURE_COUNT={len(rows)}")
    print("HOLE_SIDE_CHECK_COUNTS=" + json.dumps(dict(checks), ensure_ascii=False))
    print(f"RESULT_PATH={json_path}")
    print(f"DETAIL_CSV_PATH={csv_path}")
    print("THREAD_AUDIT_COVERAGE_AND_HOLE_SIDE_V4_STATUS=SUCCESS")


if __name__ == "__main__":
    main()
