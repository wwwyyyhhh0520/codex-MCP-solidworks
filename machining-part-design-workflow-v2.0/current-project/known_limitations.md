# Known Limitations

ordinary-hole semantic groups can aggregate multiple independent parallel
holes.

Native IFace2 provenance is now preserved, but per-hole-instance
semantic-to-native binding has not yet been implemented.

Therefore automatic baseline/ordinate dimension execution remains disabled.

Additional boundaries:

- A model bounding-box coordinate is not a native SolidWorks dimension anchor.
- Native cylindrical face candidates may have persistent-reference bytes, but
  the installed Interop does not expose a usable persistent-reference resolve
  API for round-trip verification.
- G01 `ordinary_hole_d3.3_X_minus` currently retains three face candidates and
  is classified `MULTI_FACE`; the candidates are parallel and equal-radius but
  lie on different axis lines.
- Face role classification remains `UNKNOWN` where available topology does not
  distinguish hole wall, counterbore, countersink, through, or blind roles.
- Chain-dimension and detailed layout checks remain human-review items when
  verified SolidWorks geometry evidence is unavailable.
- The Phase 7E pilot does not consume `drawing_plan_quality.json` in the normal
  path and does not alter generated drawings.
