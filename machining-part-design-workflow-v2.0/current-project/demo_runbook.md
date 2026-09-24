# Demo Runbook

This runbook demonstrates the frozen prototype in its normal mode. Keep the
experimental dimension-quality executor flag disabled.

## 1. Generate Semantics

Run the existing V10R wrapper for a selected SLDPRT. The run produces
`model_semantics.json` from the model, HoleWizard evidence, sketch geometry,
and feature-tree results.

## 2. Generate Drawing Plan

The same normal V10R run writes `drawing_plan.json`. It describes selected
views, dimension intent, annotation intent, and unresolved items.

## 3. Generate Quality Plan

Run the existing shadow quality adapter against `model_semantics.json`,
`drawing_plan.json`, and the V10R report. It writes
`drawing_plan_quality.json`; this file is not consumed by the executor.

## 4. Run V10R

Use the normal wrapper without `--dimension-quality-executor-pilot`:

```powershell
& C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe `
  -NoProfile -ExecutionPolicy Bypass `
  -File C:\Users\18015\Desktop\sw_worker\run_generic_auto_drawing.ps1 `
  -ModelPath "C:\path\to\model.SLDPRT" `
  -OutputRoot "C:\path\to\output"
```

Confirm that the run creates the SLDDRW and PDF and writes the semantic,
planning, evidence, and validation artifacts.

## 5. Inspect Validation Result

Open the run's `validation_result.json`. Treat chain dimensions and detailed
dimension layout as human-review items when their geometry evidence is not
available.

## 6. Run Golden Regression

From the project directory:

```powershell
& C:\Users\18015\AppData\Local\Programs\Python\Python313\python.exe `
  golden_regression_runner.py `
  --output-root "C:\path\to\golden-output" `
  --run
```

Expected frozen baseline summary:

```text
G01 PASS / REVIEW / 85.000
G02 PASS / REVIEW / 81.667
G03 PASS / REVIEW / 76.081
```

Do not modify Golden expectations or enable the experimental pilot as part of
the normal delivery demonstration.
