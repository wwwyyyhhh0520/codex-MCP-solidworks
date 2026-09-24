"""执行流程接口；实现层不得绕过只读与计划校验边界。"""

from typing import Any, Protocol
from .models import DrawingPlan, PartAnalysis


class PartReader(Protocol):
    def read(self, source_file: str, configuration: str | None = None) -> PartAnalysis: ...


class SolidWorksBridgeReader(PartReader, Protocol):
    """Read through a typed .NET bridge when dynamic COM is not reliable."""
    def read_bridge_report(self, report_file: str) -> PartAnalysis: ...


class Analyzer(Protocol):
    def analyze(self, part: PartAnalysis) -> PartAnalysis: ...


class Planner(Protocol):
    def plan(self, part: PartAnalysis) -> DrawingPlan: ...


class DrawingExecutor(Protocol):
    def execute(self, plan: DrawingPlan, output_file: str) -> Any: ...


class Reviewer(Protocol):
    def review(self, part: PartAnalysis, plan: DrawingPlan, drawing_file: str | None = None) -> dict: ...


class Exporter(Protocol):
    def export_pdf(self, drawing_file: str, output_file: str) -> Any: ...
    def export_dwg(self, drawing_file: str, output_file: str) -> Any: ...
