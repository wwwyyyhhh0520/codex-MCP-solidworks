from __future__ import annotations

import os
import subprocess
import time
from dataclasses import dataclass

import pythoncom
import win32com.client


TRUE_VALUES = {"1", "true", "TRUE", "yes", "YES", "y", "Y"}


@dataclass
class SolidWorksAttachResult:
    sw: object
    created_new: bool
    attempts: int
    message: str


def _allow_create_from_env() -> bool:
    return os.environ.get("SW_AUTOMATION_REQUIRE_EXISTING", "").strip() not in TRUE_VALUES


def _set_foreground_flags(sw: object, visible: bool = True) -> None:
    for attr, value in [
        ("Visible", bool(visible)),
        ("UserControl", True),
        ("FrameState", 1),
    ]:
        try:
            setattr(sw, attr, value)
        except Exception:
            pass


def _active_doc_probe(sw: object) -> str:
    try:
        doc = sw.ActiveDoc
        if doc is None:
            return "ACTIVE_DOC=None"
        title = getattr(doc, "GetTitle", None)
        if callable(title):
            return f"ACTIVE_DOC={title()}"
        return "ACTIVE_DOC=available"
    except Exception as exc:
        return f"ACTIVE_DOC_PROBE_ERROR={type(exc).__name__}:{exc}"


def _solidworks_process_snapshot() -> str:
    ps = (
        "Get-Process SLDWORKS -ErrorAction SilentlyContinue | "
        "Select-Object Id,CPU,StartTime,MainWindowHandle,MainWindowTitle | ConvertTo-Csv -NoTypeInformation"
    )
    try:
        completed = subprocess.run(
            ["powershell.exe", "-NoLogo", "-NoProfile", "-Command", ps],
            text=True,
            capture_output=True,
            timeout=10,
        )
        return (completed.stdout or completed.stderr or "").strip()
    except Exception as exc:
        return f"PROCESS_SNAPSHOT_ERROR={type(exc).__name__}:{exc}"


def connect_solidworks_session(
    *,
    visible: bool = True,
    allow_create: bool | None = None,
    wait_seconds: float | None = None,
    poll_seconds: float = 1.0,
) -> SolidWorksAttachResult:
    """Attach to a usable SolidWorks COM session.

    Default behavior is conservative for automation:
    - if SW_AUTOMATION_REQUIRE_EXISTING=1, never create a hidden duplicate;
    - wait a little for a just-opened SolidWorks UI to register in the COM ROT;
    - if attach still fails, return an actionable diagnostic instead of spawning ghosts.
    """
    pythoncom.CoInitialize()

    if allow_create is None:
        allow_create = _allow_create_from_env()
    if wait_seconds is None:
        wait_seconds = float(os.environ.get("SW_AUTOMATION_ATTACH_WAIT_SEC", "45"))

    deadline = time.time() + max(0.0, wait_seconds)
    attempts = 0
    last_error = ""
    while True:
        attempts += 1
        try:
            sw = win32com.client.GetActiveObject("SldWorks.Application")
            _set_foreground_flags(sw, visible=visible)
            return SolidWorksAttachResult(
                sw=sw,
                created_new=False,
                attempts=attempts,
                message=f"ATTACHED_EXISTING; {_active_doc_probe(sw)}",
            )
        except Exception as exc:
            last_error = f"{type(exc).__name__}:{exc}"
            if time.time() >= deadline:
                break
            time.sleep(max(0.2, poll_seconds))

    dispatch_recovery_allowed = os.environ.get("SW_AUTOMATION_DISABLE_DISPATCH_RECOVERY", "").strip() not in TRUE_VALUES
    if not allow_create and dispatch_recovery_allowed:
        try:
            sw = win32com.client.Dispatch("SldWorks.Application")
            _set_foreground_flags(sw, visible=visible)
            return SolidWorksAttachResult(
                sw=sw,
                created_new=False,
                attempts=attempts,
                message=f"RECOVERED_BY_DISPATCH_FOREGROUND; {_active_doc_probe(sw)}",
            )
        except Exception as exc:
            last_error = f"{last_error}; DISPATCH_RECOVERY_ERROR={type(exc).__name__}:{exc}"

    if not allow_create:
        snapshot = _solidworks_process_snapshot()
        raise RuntimeError(
            "SOLIDWORKS_ACTIVE_INSTANCE_NOT_FOUND; "
            "SolidWorks process may exist but is not registered as a usable COM session. "
            "Close orphan/no-window SLDWORKS processes, then open SolidWorks normally and wait until the UI is fully loaded. "
            f"ATTEMPTS={attempts}; LAST_ERROR={last_error}; PROCESS_SNAPSHOT={snapshot}"
        )

    sw = win32com.client.Dispatch("SldWorks.Application")
    _set_foreground_flags(sw, visible=visible)
    return SolidWorksAttachResult(
        sw=sw,
        created_new=True,
        attempts=attempts,
        message=f"CREATED_NEW_FOREGROUND; {_active_doc_probe(sw)}",
    )


def get_solidworks(visible: bool = True, allow_create: bool | None = None):
    result = connect_solidworks_session(visible=visible, allow_create=allow_create)
    return result.sw, result.created_new
