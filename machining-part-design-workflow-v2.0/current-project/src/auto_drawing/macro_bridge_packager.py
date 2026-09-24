"""Create a per-case SolidWorks VBA bridge package."""

from __future__ import annotations

import argparse
import json
from datetime import datetime
from pathlib import Path
from typing import Dict


TEMPLATE_FILE = Path("tools/solidworks_macro_bridge.bas")


def _vba_string(value: str) -> str:
    return value.replace('"', '""')


def package_macro_bridge(source_file: str, case_dir: Path, template_file: Path = TEMPLATE_FILE) -> Dict[str, str]:
    template = template_file.read_text(encoding="utf-8")
    output_file = case_dir / "macro_bridge_probe.json"
    macro_file = case_dir / "solidworks_macro_bridge.case.bas"
    request_file = case_dir / "macro_bridge_request.json"
    instructions_file = case_dir / "macro_bridge_request.md"
    case_dir.mkdir(parents=True, exist_ok=True)

    lines = []
    for line in template.splitlines():
        if line.startswith("Private Const SOURCE_FILE As String"):
            lines.append(f'Private Const SOURCE_FILE As String = "{_vba_string(source_file)}"')
        elif line.startswith("Private Const OUTPUT_FILE As String"):
            lines.append(f'Private Const OUTPUT_FILE As String = "{_vba_string(str(output_file.resolve()))}"')
        else:
            lines.append(line)
    macro_file.write_text("\n".join(lines) + "\n", encoding="utf-8")

    request = {
        "schema_version": "1.0",
        "created_at": datetime.now().isoformat(timespec="seconds"),
        "status": "waiting_for_macro_bridge",
        "source_file": source_file,
        "macro_file": str(macro_file),
        "expected_output_file": str(output_file),
        "source_model_modified": False,
        "instructions": [
            "Open SolidWorks.",
            "Create or edit a VBA macro.",
            "Import or paste the generated .bas file.",
            "Run Main.",
            "Return to the pipeline after macro_bridge_probe.json is created.",
        ],
    }
    request_file.write_text(json.dumps(request, ensure_ascii=False, indent=2), encoding="utf-8")
    instructions_file.write_text(
        "\n".join([
            f"# Macro bridge request: {Path(source_file).name}",
            "",
            f"- Macro file: `{macro_file}`",
            f"- Expected output: `{output_file}`",
            "- Run `Main` inside SolidWorks VBA.",
            "- The macro is read-only for the source model and writes only the JSON output.",
            "- Do not use sample JSON as a real result.",
            "",
        ]),
        encoding="utf-8",
    )
    return {
        "status": "ok",
        "macro_file": str(macro_file),
        "request_file": str(request_file),
        "instructions_file": str(instructions_file),
        "expected_output_file": str(output_file),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("source_file")
    parser.add_argument("case_dir")
    parser.add_argument("--template-file", default=str(TEMPLATE_FILE))
    args = parser.parse_args()
    result = package_macro_bridge(args.source_file, Path(args.case_dir), Path(args.template_file))
    print(json.dumps(result, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
