import argparse
import json
import subprocess
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse, parse_qs

class LocateHandler(BaseHTTPRequestHandler):
    assembly = ""
    csv = ""
    worker = Path(r"C:\Users\admin\Desktop\sw_worker")
    locate_script = worker / "v4_locate_thread_issue_in_sw.py"

    def _send(self, code, body, content_type="text/html; charset=utf-8"):
        data = body.encode("utf-8", errors="replace")
        try:
            self.send_response(code)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Access-Control-Allow-Origin", "*")
            self.end_headers()
            self.wfile.write(data)
        except (ConnectionAbortedError, BrokenPipeError, ConnectionResetError):
            # ???/HTML ??????????????????????????????
            pass

    def do_GET(self):
        parsed = urlparse(self.path)

        if parsed.path in ["/", "/health"]:
            self._send(200, "THREAD_LOCATOR_SERVER_OK")
            return

        if parsed.path != "/locate":
            self._send(404, "NOT_FOUND")
            return

        qs = parse_qs(parsed.query)
        issue_id = (qs.get("issue_id") or qs.get("id") or [""])[0].strip()

        if not issue_id:
            self._send(400, "MISSING_issue_id")
            return

        if not self.locate_script.is_file():
            self._send(500, f"LOCATE_SCRIPT_NOT_FOUND={self.locate_script}")
            return

        cmd = [
            sys.executable,
            str(self.locate_script),
            "--assembly",
            self.assembly,
            "--csv",
            self.csv,
            "--issue-id",
            issue_id,
        ]

        try:
            completed = subprocess.run(
                cmd,
                cwd=str(self.worker),
                text=True,
                capture_output=True,
                timeout=120,
            )

            ok = completed.returncode == 0 and "COMPONENT_SELECTED=True" in completed.stdout

            html = f"""
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<title>SW定位结果 {issue_id}</title>
<style>
body {{
  font-family: "Microsoft YaHei", Arial, sans-serif;
  margin: 24px;
  line-height: 1.5;
}}
.ok {{ color: #0a7a31; font-weight: 700; }}
.err {{ color: #b00020; font-weight: 700; }}
pre {{
  background: #f6f8fa;
  border: 1px solid #d0d7de;
  border-radius: 8px;
  padding: 12px;
  white-space: pre-wrap;
}}
</style>
</head>
<body>
<h2>SolidWorks 定位结果</h2>
<p>问题编号：<b>{issue_id}</b></p>
<p class="{ "ok" if ok else "err" }">{ "已在装配体中选中/缩放对应组件" if ok else "定位命令已执行，但未确认选中组件" }</p>
<h3>stdout</h3>
<pre>{completed.stdout}</pre>
<h3>stderr</h3>
<pre>{completed.stderr}</pre>
</body>
</html>
"""
            self._send(200 if completed.returncode == 0 else 500, html)

        except subprocess.TimeoutExpired as e:
            self._send(504, f"LOCATE_TIMEOUT={issue_id}\n{e}")
        except Exception as e:
            self._send(500, f"LOCATE_SERVER_ERROR={issue_id}\n{e}")

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--assembly", required=True)
    parser.add_argument("--csv", required=True)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    args = parser.parse_args()

    LocateHandler.assembly = str(Path(args.assembly))
    LocateHandler.csv = str(Path(args.csv))

    server = ThreadingHTTPServer((args.host, args.port), LocateHandler)
    print(f"THREAD_LOCATOR_SERVER_STATUS=LISTENING")
    print(f"URL=http://{args.host}:{args.port}/")
    print(f"ASSEMBLY={LocateHandler.assembly}")
    print(f"CSV={LocateHandler.csv}")
    server.serve_forever()

if __name__ == "__main__":
    main()
