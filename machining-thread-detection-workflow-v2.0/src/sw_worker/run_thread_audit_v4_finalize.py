import argparse
import html
import re
import subprocess
import sys
from pathlib import Path


def latest_file(root: Path, pattern: str) -> Path:
    files = list(root.rglob(pattern))
    if not files:
        raise FileNotFoundError(f"LATEST_FILE_NOT_FOUND={pattern}")
    return max(files, key=lambda p: p.stat().st_mtime)


def run(cmd):
    print("RUN=" + " ".join(str(x) for x in cmd))
    completed = subprocess.run(cmd, text=True, capture_output=True)
    if completed.stdout:
        print(completed.stdout)
    if completed.stderr:
        print(completed.stderr, file=sys.stderr)
    if completed.returncode != 0:
        raise RuntimeError(f"COMMAND_FAILED={cmd[0]}|EXIT={completed.returncode}")
    return completed.stdout


def patch_html_links(html_path: Path, host: str, port: int) -> Path:
    text = html_path.read_text(encoding="utf-8", errors="ignore")

    style = """
<style>
.issue-link-sw {
  color: #0b63ce !important;
  text-decoration: underline !important;
  text-decoration-thickness: 2px !important;
  text-underline-offset: 3px !important;
  cursor: pointer !important;
  font-weight: 800 !important;
}
.locate-hint {
  margin: 10px 0 14px 0;
  padding: 10px 14px;
  border-left: 6px solid #2563eb;
  background: #eff6ff;
  color: #0f172a;
  border-radius: 10px;
  font-size: 15px;
}
</style>
"""

    script = f"""
<script>
async function locateInSW(issueId) {{
  const url = "http://{host}:{port}/locate?issue_id=" + encodeURIComponent(issueId);
  try {{
    const r = await fetch(url, {{ mode: "cors" }});
    const t = await r.text();
    alert(r.ok ? ("已请求 SolidWorks 定位：" + issueId) : ("定位失败：" + issueId + "\\n" + t.slice(0, 500)));
  }} catch (e) {{
    window.open(url, "_blank");
  }}
  return false;
}}
</script>
"""

    if "</head>" in text:
        text = text.replace("</head>", style + script + "\n</head>", 1)
    else:
        text = style + script + text

    hint = (
        '<div class="locate-hint">'
        '提示：点击带下划线的问题编号，可直接请求 SolidWorks 在装配体中定位/高亮对应零件。'
        f' 若无响应，请确认定位服务已启动：<code>http://{host}:{port}/health</code>'
        '</div>'
    )
    if "locate-hint" not in text:
        text = text.replace("<body>", "<body>\n" + hint, 1) if "<body>" in text else hint + text

    def repl(m):
        issue = m.group(0)
        return (
            f'<a class="issue-link-sw" href="http://{host}:{port}/locate?issue_id={html.escape(issue)}" '
            f'onclick="return locateInSW(\'{html.escape(issue)}\')">{html.escape(issue)}</a>'
        )

    # 避免重复包链接：先只处理裸文本中常见 HOLE/THR 编号
    text = re.sub(r'(?<![A-Za-z0-9_-])(HOLE-\d{4}|THR-\d{4})(?![A-Za-z0-9_-])', repl, text)

    out = html_path.with_name(html_path.stem + "_click_to_sw.html")
    out.write_text(text, encoding="utf-8")
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("assembly_path")
    parser.add_argument("output_root")
    parser.add_argument("--background-image", required=True)
    parser.add_argument("--locator-host", default="127.0.0.1")
    parser.add_argument("--locator-port", type=int, default=8765)
    parser.add_argument("--reuse", action="store_true")
    args = parser.parse_args()

    worker = Path(r"C:\Users\admin\Desktop\sw_worker")
    out_root = Path(args.output_root)
    out_root.mkdir(parents=True, exist_ok=True)

    base_runner = worker / "run_thread_audit_v4.py"
    corrector = worker / "v4_correct_hole_side_by_main_bore_depth.py"
    html_builder = worker / "v4_build_thread_audit_image_locator_page.py"

    if not args.reuse:
        run([
            sys.executable,
            str(base_runner),
            args.assembly_path,
            args.output_root,
            "--no-reuse",
        ])

    raw_csv = latest_file(out_root, "孔侧几何检测明细_V4.csv")
    print("RAW_GEOMETRY_CSV=" + str(raw_csv))

    cylinder_json = latest_file(out_root, "assembly_cylinder_face_extents_earlybound_v4*.json")
    print("CYLINDER_JSON=" + str(cylinder_json))

    if corrector.is_file():
        run([
            sys.executable,
            str(corrector),
            "--csv", str(raw_csv),
            "--cylinder-json", str(cylinder_json),
            "--output-root", str(out_root),
        ])
        final_csv = latest_file(out_root, "孔侧几何检测明细_主孔深修正版_V4.csv")
    else:
        final_csv = raw_csv

    print("FINAL_GEOMETRY_CSV=" + str(final_csv))

    before = set(out_root.rglob("*.html"))

    run([
        sys.executable,
        str(html_builder),
        str(final_csv),
        args.background_image,
        str(out_root),
        "--assembly-path",
        args.assembly_path,
        "--embed-image",
    ])

    after = set(out_root.rglob("*.html"))
    new_htmls = list(after - before)
    html_path = max(new_htmls, key=lambda p: p.stat().st_mtime) if new_htmls else latest_file(out_root, "*.html")

    clickable = patch_html_links(html_path, args.locator_host, args.locator_port)

    manifest = out_root / "thread_audit_v4_finalize_manifest.txt"
    manifest.write_text(
        "\n".join([
            "THREAD_AUDIT_V4_FINALIZE_STATUS=SUCCESS",
            "ASSEMBLY_PATH=" + args.assembly_path,
            "RAW_GEOMETRY_CSV=" + str(raw_csv),
            "CYLINDER_JSON=" + str(cylinder_json),
            "FINAL_GEOMETRY_CSV=" + str(final_csv),
            "FINAL_HTML=" + str(clickable),
        ]),
        encoding="utf-8",
    )

    print("FINAL_HTML=" + str(clickable))
    print("FINAL_MANIFEST=" + str(manifest))
    print("THREAD_AUDIT_V4_FINALIZE_STATUS=SUCCESS")


if __name__ == "__main__":
    main()
