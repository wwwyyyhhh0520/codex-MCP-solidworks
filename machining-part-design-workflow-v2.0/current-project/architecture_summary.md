# Architecture Summary

The current prototype keeps V10R drawing execution as the frozen execution path.
Model facts and drawing decisions are separate contracts; quality planning and
validation remain shadow/read-only layers.

```text
SLDPRT
  -> model_semantics.json
  -> drawing_plan.json
  -> drawing_plan_quality.json
  -> V10R executor
  -> SolidWorks drawing/PDF
  -> drawing_evidence.json
  -> validation_result.json
  -> Golden regression
```

## Components

- `model_semantics.json`: factual model identity, units, bounding box, feature facts, hole geometry, feature-tree facts, and optional native provenance.
- `drawing_plan.json`: existing V10R drawing decisions, including views, dimensions, annotations, and unresolved items.
- `drawing_plan_quality.json`: shadow dimension groups, datum candidates, strategies, and layout lanes. It is not consumed by V10R.
- V10R executor: creates the SolidWorks drawing and exports SLDDRW/PDF. Its normal behavior is frozen.
- `drawing_evidence.json`: read-only post-generation evidence where SolidWorks exposes it.
- `validation_result.json`: deterministic validation and quality checks.
- Golden regression: compares generated artifacts with high-confidence expectations for exactly G01, G02, and G03.

## Safety Boundary

The Phase 7E quality executor pilot remains disabled by default and is blocked
for baseline execution. No automatic baseline/ordinate dimension execution is
enabled by the frozen delivery prototype.
