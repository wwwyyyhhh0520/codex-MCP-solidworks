"""与 SolidWorks API 解耦的中间数据结构。"""

from dataclasses import dataclass, field, asdict
from typing import Any, Dict, List, Optional
import json


@dataclass
class UncertainItem:
    id: str
    topic: str
    description: str
    source: str = "not_available"
    severity: str = "warning"
    requires_human_confirmation: bool = True


@dataclass
class PartAnalysis:
    schema_version: str = "1.0"
    part_number: Optional[str] = None
    source_file: str = ""
    configuration: str = ""
    units: str = ""
    category: str = "unknown"
    category_confidence: float = 0.0
    properties: Dict[str, Any] = field(default_factory=dict)
    pmi: Dict[str, Any] = field(default_factory=dict)
    bounding_box: Dict[str, float] = field(default_factory=dict)
    bodies: List[Dict[str, Any]] = field(default_factory=list)
    features: List[Dict[str, Any]] = field(default_factory=list)
    datum_candidates: List[Dict[str, Any]] = field(default_factory=list)
    view_candidates: List[Dict[str, Any]] = field(default_factory=list)
    source_items: List[Dict[str, Any]] = field(default_factory=list)
    risks: List[str] = field(default_factory=list)
    uncertain_items: List[UncertainItem] = field(default_factory=list)

    def to_dict(self) -> Dict[str, Any]:
        return asdict(self)

    def to_json(self) -> str:
        return json.dumps(self.to_dict(), ensure_ascii=False, indent=2)


@dataclass
class DrawingPlan:
    schema_version: str = "1.0"
    status: str = "needs_human_confirmation"
    part_number: Optional[str] = None
    sheet: Dict[str, Any] = field(default_factory=lambda: {"template": None, "scale": None, "projection": "first_angle"})
    views: List[Dict[str, Any]] = field(default_factory=list)
    datums: List[Dict[str, Any]] = field(default_factory=list)
    dimensions: List[Dict[str, Any]] = field(default_factory=list)
    hole_callouts: List[Dict[str, Any]] = field(default_factory=list)
    tolerance_rules: List[Dict[str, Any]] = field(default_factory=list)
    gdandt: List[Dict[str, Any]] = field(default_factory=list)
    surface_finish: List[Dict[str, Any]] = field(default_factory=list)
    notes: List[Dict[str, Any]] = field(default_factory=list)
    layout_constraints: List[Dict[str, Any]] = field(default_factory=list)
    uncertain_items: List[UncertainItem] = field(default_factory=list)

    def to_dict(self) -> Dict[str, Any]:
        return asdict(self)

    def to_json(self) -> str:
        return json.dumps(self.to_dict(), ensure_ascii=False, indent=2)
