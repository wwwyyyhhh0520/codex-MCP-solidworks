# Client drawing policy runtime

This optional policy pipeline preserves the original creator and annotation executor. `build.py` composes its runner with the stable executor helpers; the only helper change is an opt-in native-only hole-callout guard. The ordinary drawing planner CLI retains its prior behavior unless `--client-policy` is supplied.

1. Build with `python policy_drawing_runtime/build.py`, then `dotnet build policy_drawing_runtime/policy_drawing_runtime.csproj`.
2. Probe: `policy_drawing_runtime.exe --probe <semantics.json> <template.drwdot> <evidence-directory>`.
3. Plan: `python general_drawing_planner/general_drawing_planner.py --semantics <semantics.json> --output <plan.json> --client-policy config/client_drawing_policy.json --projection-evidence <evidence-directory>/view_projection_evidence.json --template-profile config/template_profiles/company_landscape.json`.
4. Execute: `policy_drawing_runtime.exe --execute <plan.json> <semantics.json> <template.drwdot> <new-output-directory> <output-stem> [unannotated-base.SLDDRW]`.

The optional base is copied before modification. A file existing at that copied path does **not** establish successful V2 persistence. Require the successful validation artifact and saved PDF. Do not terminate SolidWorks to clear a blocked call: unsaved generated documents may remain open after the client timeout.

All geometry is in meters. Template zones are deliberately conservative, normalized template configuration rather than model coordinates. Collision candidates use estimated text boxes and idealized leader/extension segments, requiring saved-PDF visual QA; full native crossing is not validated. Datum, fit, GD&T and design symmetry are not inferred. Two placement points support a geometric linear center group but not inferred pattern pitch/count beyond the semantic evidence. Center groups must survive reopen before CENTERLINE may pass.

Tests: `python general_drawing_planner/test_client_policy_plan.py`. These test policy behavior with synthetic evidence transformations; they do not prove cross-model SolidWorks runtime success.
