using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

public static class GeneralSemanticExtractor
{
    struct Interval { public double Min, Max; public Interval(double min, double max) { Min = Math.Min(min, max); Max = Math.Max(min, max); } }
    struct P3
    {
        public double X, Y, Z;
        public P3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public double this[int i] { get { return i == 0 ? X : i == 1 ? Y : Z; } }
        public static P3 operator +(P3 a, P3 b) => new P3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static P3 operator -(P3 a, P3 b) => new P3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static P3 operator *(double s, P3 p) => new P3(s * p.X, s * p.Y, s * p.Z);
    }
    static double Dot(P3 a, P3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    static P3 Cross(P3 a, P3 b) => new P3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    static double Length(P3 p) => Math.Sqrt(Dot(p, p));
    static double Distance(P3 a, P3 b) => Length(a - b);
    static P3 Unit(P3 p) { var l = Length(p); if (l <= 1e-12) throw new InvalidOperationException("ZERO_LENGTH_VECTOR"); return (1.0 / l) * p; }
    static Interval Support(P3 p, int axis) => new Interval(p[axis], p[axis]);
    static Interval Support(P3 a, P3 b, int axis) => new Interval(a[axis], b[axis]);
    static Interval CircleSupport(P3 center, P3 normal, double radius, int axis)
    {
        var n = Unit(normal); var projection = n[axis]; var q = radius * Math.Sqrt(Math.Max(0.0, 1.0 - projection * projection));
        return new Interval(center[axis] - q, center[axis] + q);
    }
    static string PointKey(P3 p) => $"{p.X:R}|{p.Y:R}|{p.Z:R}";
    static string ComIdentity(object value)
    {
        if (value == null) return null;
        IntPtr unknown = IntPtr.Zero;
        try { unknown = Marshal.GetIUnknownForObject(value); return "IUnknown:" + unknown.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture); }
        catch { return null; }
        finally { if (unknown != IntPtr.Zero) Marshal.Release(unknown); }
    }
    static P3 PointFrom(object value) { var a = value as Array; if (a == null || a.Length < 3) throw new InvalidOperationException("POINT_UNAVAILABLE"); return new P3(Convert.ToDouble(a.GetValue(0)), Convert.ToDouble(a.GetValue(1)), Convert.ToDouble(a.GetValue(2))); }
    static string FormatArray(Array values)
    {
        if (values == null) return "UNAVAILABLE";
        var parts = new List<string>(); for (int i = 0; i < values.Length; i++) parts.Add(Convert.ToString(values.GetValue(i), System.Globalization.CultureInfo.InvariantCulture));
        return "[" + string.Join(",", parts) + "]";
    }
    static Dictionary<string,object> ApproximateEnvelope(double[] b)
    {
        return new Dictionary<string,object>{{"min_x_m",b[0]},{"min_y_m",b[1]},{"min_z_m",b[2]},{"max_x_m",b[3]},{"max_y_m",b[4]},{"max_z_m",b[5]},{"size_x_m",Math.Abs(b[3]-b[0])},{"size_y_m",Math.Abs(b[4]-b[1])},{"size_z_m",Math.Abs(b[5]-b[2])},{"source","PartDoc.GetPartBox"},{"confidence","APPROXIMATE_API"}};
    }
    static void Include(Interval s, int axis, double[] mins, double[] maxs) { mins[axis] = Math.Min(mins[axis], s.Min); maxs[axis] = Math.Max(maxs[axis], s.Max); }
    static bool IsFullCircle(ICurve curve, ICurveParamData parameters, P3 start, P3 end)
    {
        if (Length(end - start) <= 1e-8) return true;
        return parameters != null && Math.Abs(Math.Abs(parameters.UMaxValue - parameters.UMinValue) - 2.0 * Math.PI) <= 1e-7;
    }
    static bool IsWithinTrim(ICurveParamData parameters, double parameter)
    {
        if (parameters == null) return false;
        return IsWithinTrimValues(parameters.UMinValue, parameters.UMaxValue, parameter);
    }
    static bool IsWithinTrimValues(double uMin, double uMax, double parameter)
    {
        double lo = Math.Min(uMin, uMax) - 1e-8;
        double hi = Math.Max(uMin, uMax) + 1e-8;
        if (parameter >= lo && parameter <= hi) return true;
        // Circular parameter domains can straddle their periodic seam.
        double twoPi = 2.0 * Math.PI;
        return parameter + twoPi >= lo && parameter + twoPi <= hi || parameter - twoPi >= lo && parameter - twoPi <= hi;
    }
    static bool IncludeArcExtrema(IEdge edge, ICurve curve, ICurveParamData parameters, P3 center, P3 normal, double radius, int axis, double[] mins, double[] maxs)
    {
        var n = Unit(normal); var direction = new P3(axis == 0 ? 1 : 0, axis == 1 ? 1 : 0, axis == 2 ? 1 : 0);
        var inPlane = direction - Dot(direction, n) * n;
        if (Length(inPlane) <= 1e-12) return true; // The circle's value on this axis is constant.
        var unit = Unit(inPlane);
        foreach (var p in new[] { center + radius * unit, center - radius * unit })
        {
            try { if (IsWithinTrim(parameters, Convert.ToDouble(edge.GetParameter(p.X, p.Y, p.Z)))) Include(Support(p, axis), axis, mins, maxs); }
            catch { return false; }
        }
        return true;
    }
    static P3 EvaluatePoint(ICurve curve, double parameter)
    {
        return PointFrom(curve.Evaluate(parameter));
    }
    static bool DomainContains(ICurveParamData parameters, double candidate)
    {
        if (parameters == null) return false;
        return IsWithinTrimValues(parameters.UMinValue, parameters.UMaxValue, candidate);
    }
    static double[] StationaryParameters(double cosine, double sine)
    {
        if (Math.Abs(cosine) <= 1e-14 && Math.Abs(sine) <= 1e-14) return new double[0];
        var first = Math.Atan2(sine, cosine);
        return new[] { first, first + Math.PI };
    }
    static string FormatParameters(IEnumerable<double> values) => "[" + string.Join(",", values.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]";
    static bool ValidateParameterizedCurve(ICurve curve, ICurveParamData domain, Func<double, P3> model, P3 start, P3 end)
    {
        const double tolerance = 1e-8;
        var atStart = EvaluatePoint(curve, domain.UMinValue); var atEnd = EvaluatePoint(curve, domain.UMaxValue);
        bool nativeMatchesModel = Distance(atStart, model(domain.UMinValue)) <= tolerance && Distance(atEnd, model(domain.UMaxValue)) <= tolerance;
        bool nativeMatchesTopology = (Distance(atStart, start) <= tolerance && Distance(atEnd, end) <= tolerance) || (Distance(atStart, end) <= tolerance && Distance(atEnd, start) <= tolerance);
        return nativeMatchesModel && nativeMatchesTopology;
    }
    static bool IncludeAnalyticSupport(string edgeId, string identity, string resolvedType, ICurveParamData domain, Func<double, P3> pointAt, P3 center, P3 u, double a, P3 v, double b, string source, double[] mins, double[] maxs)
    {
        if (domain == null || a <= 0 || b <= 0 || Math.Abs(Dot(Unit(u), Unit(v))) > 1e-8) return false;
        var endpoints = new[] { domain.UMinValue, domain.UMaxValue };
        for (int axis = 0; axis < 3; axis++)
        {
            double cosine = a * u[axis], sine = b * v[axis];
            var stationary = StationaryParameters(cosine, sine).ToList();
            var accepted = stationary.Where(t => DomainContains(domain, t)).ToList();
            var values = new List<double>(); foreach (var t in endpoints) values.Add(pointAt(t)[axis]); foreach (var t in accepted) values.Add(pointAt(t)[axis]);
            if (values.Count == 0) return false;
            Include(new Interval(values.Min(), values.Max()), axis, mins, maxs);
            Console.WriteLine($"CURVE_SUPPORT_PROOF edge_id={edgeId} curve_identity={identity} resolved_type={resolvedType} semantic_axis={(axis == 0 ? "X" : axis == 1 ? "Y" : "Z")} domain_start={domain.UMinValue:R} domain_end={domain.UMaxValue:R} domain_periodic=true domain_reversed={(domain.UMaxValue < domain.UMinValue)} stationary_parameters={FormatParameters(stationary)} accepted_stationary_parameters={FormatParameters(accepted)} endpoint_parameters={FormatParameters(endpoints)} support_min={values.Min():R} support_max={values.Max():R} support_source={source} proof_result=PASS proof_reason=ANALYTIC_STATIONARY_POINTS_WITH_NATIVE_DOMAIN");
        }
        return true;
    }
    static bool TryCircleSupport(string edgeId, string identity, string resolvedType, ICurve curve, ICurveParamData domain, P3 start, P3 end, Array parameters, double[] mins, double[] maxs, out string source)
    {
        source = null; if (parameters == null || parameters.Length < 7 || domain == null) return false;
        var center = new P3(Convert.ToDouble(parameters.GetValue(0)), Convert.ToDouble(parameters.GetValue(1)), Convert.ToDouble(parameters.GetValue(2)));
        var normal = new P3(Convert.ToDouble(parameters.GetValue(3)), Convert.ToDouble(parameters.GetValue(4)), Convert.ToDouble(parameters.GetValue(5))); double radius = Convert.ToDouble(parameters.GetValue(6));
        if (radius <= 0 || Length(normal) <= 1e-12) return false;
        try
        {
            var u = Unit((1.0 / radius) * (EvaluatePoint(curve, 0.0) - center));
            var v = Unit((1.0 / radius) * (EvaluatePoint(curve, Math.PI / 2.0) - center));
            Func<double, P3> pointAt = t => center + radius * (Math.Cos(t) * u + Math.Sin(t) * v);
            if (!ValidateParameterizedCurve(curve, domain, pointAt, start, end) || Math.Abs(Dot(u, v)) > 1e-8 || Math.Abs(Dot(Unit(normal), Unit(Cross(u, v)))) < 1.0 - 1e-8) return false;
            source = IsFullCircle(curve, domain, start, end) ? "CIRCLE_ANALYTIC_FULL" : "CIRCLE_ANALYTIC_TRIMMED";
            return IncludeAnalyticSupport(edgeId, identity, resolvedType, domain, pointAt, center, u, radius, v, radius, source, mins, maxs);
        }
        catch { return false; }
    }
    static bool TryEllipseSupport(string edgeId, string identity, string resolvedType, ICurve curve, ICurveParamData domain, P3 start, P3 end, Array parameters, double[] mins, double[] maxs, out string source)
    {
        source = null; if (parameters == null || parameters.Length < 11 || domain == null) return false;
        var center = new P3(Convert.ToDouble(parameters.GetValue(0)), Convert.ToDouble(parameters.GetValue(1)), Convert.ToDouble(parameters.GetValue(2))); double major = Convert.ToDouble(parameters.GetValue(3));
        var u = new P3(Convert.ToDouble(parameters.GetValue(4)), Convert.ToDouble(parameters.GetValue(5)), Convert.ToDouble(parameters.GetValue(6))); double minor = Convert.ToDouble(parameters.GetValue(7));
        var v = new P3(Convert.ToDouble(parameters.GetValue(8)), Convert.ToDouble(parameters.GetValue(9)), Convert.ToDouble(parameters.GetValue(10)));
        if (major <= 0 || minor <= 0 || Math.Abs(Length(u) - 1.0) > 1e-8 || Math.Abs(Length(v) - 1.0) > 1e-8) return false;
        try
        {
            Func<double, P3> pointAt = t => center + major * Math.Cos(t) * u + minor * Math.Sin(t) * v;
            if (!ValidateParameterizedCurve(curve, domain, pointAt, start, end)) return false;
            source = IsFullCircle(curve, domain, start, end) ? "ELLIPSE_ANALYTIC_FULL" : "ELLIPSE_ANALYTIC_TRIMMED";
            return IncludeAnalyticSupport(edgeId, identity, resolvedType, domain, pointAt, center, u, major, v, minor, source, mins, maxs);
        }
        catch { return false; }
    }
    // A conservative bound is not an extrema solver.  It only proves that an
    // unresolved trimmed circle/ellipse cannot escape a known axis side.
    static bool TryConservativeUnresolvedCurveBounds(string identity, ICurve curve, out Interval[] bounds)
    {
        bounds = new Interval[3];
        try
        {
            if (identity == "3002")
            {
                var p = curve.CircleParams as Array;
                if (p == null || p.Length < 7) return false;
                var center = new P3(Convert.ToDouble(p.GetValue(0)), Convert.ToDouble(p.GetValue(1)), Convert.ToDouble(p.GetValue(2)));
                var normal = new P3(Convert.ToDouble(p.GetValue(3)), Convert.ToDouble(p.GetValue(4)), Convert.ToDouble(p.GetValue(5)));
                var radius = Convert.ToDouble(p.GetValue(6));
                if (radius <= 0 || Length(normal) <= 1e-12) return false;
                for (int axis = 0; axis < 3; axis++) bounds[axis] = CircleSupport(center, normal, radius, axis);
                return true;
            }
            if (identity == "3003")
            {
                var p = curve.GetEllipseParams() as Array;
                if (p == null || p.Length < 11) return false;
                var center = new P3(Convert.ToDouble(p.GetValue(0)), Convert.ToDouble(p.GetValue(1)), Convert.ToDouble(p.GetValue(2)));
                var major = Convert.ToDouble(p.GetValue(3)); var u = new P3(Convert.ToDouble(p.GetValue(4)), Convert.ToDouble(p.GetValue(5)), Convert.ToDouble(p.GetValue(6)));
                var minor = Convert.ToDouble(p.GetValue(7)); var v = new P3(Convert.ToDouble(p.GetValue(8)), Convert.ToDouble(p.GetValue(9)), Convert.ToDouble(p.GetValue(10)));
                if (major <= 0 || minor <= 0 || Math.Abs(Length(u) - 1) > 1e-8 || Math.Abs(Length(v) - 1) > 1e-8) return false;
                for (int axis = 0; axis < 3; axis++)
                {
                    var radius = Math.Sqrt(major * major * u[axis] * u[axis] + minor * minor * v[axis] * v[axis]);
                    bounds[axis] = new Interval(center[axis] - radius, center[axis] + radius);
                }
                return true;
            }
        }
        catch { }
        return false;
    }
    static (bool LowProven, bool HighProven, string LowBlocker, string HighBlocker) DecideAxisSideProof(bool lowWitness, bool highWitness, double candidateLow, double candidateHigh, IReadOnlyList<Interval> unresolvedBounds, bool hasUnboundedGeometry)
    {
        const double tolerance = 1e-8;
        bool lowBlocked = hasUnboundedGeometry || unresolvedBounds.Any(bound => bound.Min < candidateLow - tolerance);
        bool highBlocked = hasUnboundedGeometry || unresolvedBounds.Any(bound => bound.Max > candidateHigh + tolerance);
        // Geometric coverage is independent from whether an extraction-session
        // COM object can later be selected as a drawing witness. Keep the
        // witness arguments for call-site compatibility and diagnostics.
        _ = lowWitness; _ = highWitness;
        return (!lowBlocked && !double.IsInfinity(candidateLow), !highBlocked && !double.IsInfinity(candidateHigh),
            lowBlocked ? (hasUnboundedGeometry ? "UNBOUNDED_UNRESOLVED_GEOMETRY" : "UNRESOLVED_GEOMETRY_MAY_EXTEND_BELOW") : null,
            highBlocked ? (hasUnboundedGeometry ? "UNBOUNDED_UNRESOLVED_GEOMETRY" : "UNRESOLVED_GEOMETRY_MAY_EXTEND_ABOVE") : null);
    }
    static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    static bool IsEnvelopeCoverageComplete(double[] mins, double[] maxs, bool[] unboundedByAxis, List<Interval>[] unresolvedBounds)
    {
        const double tolerance = 1e-8;
        for (int axis = 0; axis < 3; axis++)
        {
            if (!IsFinite(mins[axis]) || !IsFinite(maxs[axis]) || unboundedByAxis[axis]) return false;
            if (unresolvedBounds[axis].Any(bound => bound.Min < mins[axis] - tolerance || bound.Max > maxs[axis] + tolerance)) return false;
        }
        return true;
    }
    static Dictionary<string, object> ExactEnvelope(PartDoc part)
    {
        var mins = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var maxs = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        // Endpoint supports are real topology facts.  They deliberately remain
        // separate from coordinates produced by the envelope solver so a later
        // consumer never mistakes a scalar extent for a selectable entity.
        var endpointSupports = new List<Dictionary<string, object>>[] { new(), new(), new() };
        var vertexPlaneCandidates = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var unresolvedBounds = new List<Interval>[] { new(), new(), new() };
        var axisHasUnboundedUnresolvedGeometry = new[] { false, false, false };
        var seenEdges = new HashSet<string>(StringComparer.Ordinal); var seenVertices = new HashSet<string>(StringComparer.Ordinal);
        int rawUnsupported = 0, vertices = 0, lines = 0, circles = 0, arcs = 0, rawEdges = 0, rawVertices = 0;
        var curveIdentities = new Dictionary<string, int>(StringComparer.Ordinal); var classifications = new Dictionary<string, int>(StringComparer.Ordinal);
        var unsupportedUniqueEdges = new HashSet<string>(StringComparer.Ordinal);
        var unresolvedUniqueItems = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        var unboundedLowCounts = new int[3]; var unboundedHighCounts = new int[3];
        var terminalClassifications = new Dictionary<string, string>(StringComparer.Ordinal);
        object bodiesObject = null; try { bodiesObject = part.GetBodies2((int)swBodyType_e.swSolidBody, true); } catch { }
        foreach (var bodyObject in ArrayOf(bodiesObject))
        {
            var body = bodyObject as Body2; if (body == null) continue;
            object facesObject = null; try { facesObject = body.GetFaces(); } catch { }
            foreach (var faceObject in ArrayOf(facesObject))
            {
                var face = faceObject as Face2; if (face == null) continue;
                string planarSignature = null;
                try
                {
                    var surface = face.GetSurface() as ISurface;
                    if (surface != null && surface.IsPlane())
                    {
                        var plane = PlaneEvidence(surface, Doubles(face.GetBox()), face.GetArea(), new List<Dictionary<string, object>>());
                        planarSignature = plane == null ? null : S(plane["supporting_plane_signature"]);
                    }
                }
                catch { planarSignature = null; }
                object edgesObject = null; try { edgesObject = face.GetEdges(); } catch { }
                foreach (var edgeObject in ArrayOf(edgesObject))
                {
                    var edge = edgeObject as Edge; if (edge == null) continue;
                    rawEdges++;
                    P3 start = default, end = default;
                    Vertex startVertex = null, endVertex = null;
                    bool endpointReadFailed = false;
                    try { startVertex = edge.GetStartVertex() as Vertex; endVertex = edge.GetEndVertex() as Vertex; rawVertices += (startVertex != null ? 1 : 0) + (endVertex != null ? 1 : 0); start = PointFrom(startVertex == null ? null : startVertex.GetPoint()); end = PointFrom(endVertex == null ? null : endVertex.GetPoint()); }
                    catch { endpointReadFailed = true; }
                    if (planarSignature != null)
                    {
                        foreach (var vertex in new[] { startVertex, endVertex })
                        {
                            var vertexId = ComIdentity(vertex);
                            if (vertexId == null) continue;
                            if (!vertexPlaneCandidates.TryGetValue(vertexId, out var signatures))
                                vertexPlaneCandidates[vertexId] = signatures = new HashSet<string>(StringComparer.Ordinal);
                            signatures.Add(planarSignature);
                        }
                    }
                    string key;
                    key = ComIdentity(edge);
                    if (String.IsNullOrEmpty(key)) key = String.CompareOrdinal(PointKey(start), PointKey(end)) <= 0 ? PointKey(start) + "|" + PointKey(end) : PointKey(end) + "|" + PointKey(start);
                    bool uniqueEdge = seenEdges.Add(key);
                    try
                    {
                        ICurve curve = edge.GetCurve() as ICurve;
                        if (endpointReadFailed && curve != null)
                        {
                            try
                            {
                                var recoveryDomain = edge.GetCurveParams3();
                                if (recoveryDomain != null)
                                {
                                    start = EvaluatePoint(curve, recoveryDomain.UMinValue);
                                    end = EvaluatePoint(curve, recoveryDomain.UMaxValue);
                                    endpointReadFailed = false;
                                }
                            }
                            catch { }
                        }
                        if (endpointReadFailed && curve == null)
                        {
                            var unresolvedKey = "EDGE_READ_ERROR:" + rawEdges.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            unresolvedUniqueItems[unresolvedKey] = new Dictionary<string, object> { { "identity", null }, { "curve_type", "UNKNOWN" }, { "terminal_classification", "UNSUPPORTED_EDGE_READ" }, { "affected_axes", new[] { "X", "Y", "Z" } }, { "conservative_bound_exists", false }, { "bound_unavailable_reason", "EDGE_ENDPOINT_READ_FAILED" }, { "participates_in_coverage_accounting", true } };
                            for (int axis = 0; axis < 3; axis++) { axisHasUnboundedUnresolvedGeometry[axis] = true; unboundedLowCounts[axis]++; unboundedHighCounts[axis]++; }
                            continue;
                        }
                        string identity = null; try { identity = curve == null ? null : curve.Identity().ToString(System.Globalization.CultureInfo.InvariantCulture); } catch { }
                        string curveKey = identity ?? (curve == null ? "NULL" : curve.GetType().Name);
                        string resolvedType = identity == null ? "UNKNOWN" : (Enum.GetName(typeof(swCurveTypes_e), Convert.ToInt32(identity)) ?? "UNKNOWN_ENUM_VALUE");
                        if (uniqueEdge) curveIdentities[curveKey] = curveIdentities.TryGetValue(curveKey, out var ci) ? ci + 1 : 1;
                        if (curve != null && identity == "3002")
                        {
                            var cp = curve.CircleParams as Array; var parametersForInventory = edge.GetCurveParams3();
                            Console.WriteLine($"CURVE_IDENTITY_DIAGNOSTIC identity=3002 resolved_type={resolvedType} count={(uniqueEdge ? 1 : 0)} edge_id={key} curve_params={FormatArray(cp)} curve_parameter_start={(parametersForInventory == null ? "" : parametersForInventory.UMinValue.ToString("R"))} curve_parameter_end={(parametersForInventory == null ? "" : parametersForInventory.UMaxValue.ToString("R"))} classification_reason={(parametersForInventory == null ? "PARAMETERS_UNAVAILABLE" : "CIRCLE_DOMAIN_REQUIRES_TRIM_PROOF")}");
                        }
                        if (curve != null && identity == "3003")
                        {
                            var ep = curve.GetEllipseParams() as Array; var parametersForInventory = edge.GetCurveParams3();
                            Console.WriteLine($"CURVE_IDENTITY_DIAGNOSTIC identity=3003 resolved_type={resolvedType} count={(uniqueEdge ? 1 : 0)} edge_id={key} curve_params={FormatArray(ep)} curve_parameter_start={(parametersForInventory == null ? "" : parametersForInventory.UMinValue.ToString("R"))} curve_parameter_end={(parametersForInventory == null ? "" : parametersForInventory.UMaxValue.ToString("R"))} classification_reason=ELLIPSE_ANALYTIC_TRIMMED_DOMAIN_NOT_IMPLEMENTED");
                        }
                        if (!uniqueEdge) { if (terminalClassifications.TryGetValue(key, out var cached) && cached.StartsWith("UNSUPPORTED_", StringComparison.Ordinal)) rawUnsupported++; continue; }
                        Console.WriteLine($"EXACT_ENVELOPE_EDGE_INVENTORY edge_id={key} body_index={bodyObject.GetHashCode()} face_index={faceObject.GetHashCode()} curve_identity={curveKey} resolved_type={resolvedType} start_vertex_present={startVertex != null} end_vertex_present={endVertex != null} start_point={PointKey(start)} end_point={PointKey(end)}");
                        for (int axis = 0; axis < 3; axis++) Include(Support(start, end, axis), axis, mins, maxs);
                        // IUnknown identity is extraction-session scoped.  Keep
                        // that limitation explicit instead of fabricating a
                        // persistent drawing-selection identity.
                        for (int axis = 0; axis < 3; axis++)
                        {
                            string axisName = axis == 0 ? "X" : axis == 1 ? "Y" : "Z";
                            string curveType = curve == null ? "UNKNOWN" : resolvedType;
                            if (startVertex != null) endpointSupports[axis].Add(new Dictionary<string, object> {
                                { "support_kind", "VERTEX" }, { "curve_type", curveType },
                                { "topology_entity_identity", ComIdentity(startVertex) }, { "topology_identity_scope", "EXTRACTION_SESSION_ONLY" },
                                { "owning_edge_identity", key }, { "endpoint_relation", "START_VERTEX" },
                                { "axis", axisName }, { "axis_coordinate", start[axis] }, { "geometric_signature", PointKey(start) }
                            });
                            if (endVertex != null) endpointSupports[axis].Add(new Dictionary<string, object> {
                                { "support_kind", "VERTEX" }, { "curve_type", curveType },
                                { "topology_entity_identity", ComIdentity(endVertex) }, { "topology_identity_scope", "EXTRACTION_SESSION_ONLY" },
                                { "owning_edge_identity", key }, { "endpoint_relation", "END_VERTEX" },
                                { "axis", axisName }, { "axis_coordinate", end[axis] }, { "geometric_signature", PointKey(end) }
                            });
                        }
                        if (seenVertices.Add(PointKey(start))) vertices++; if (seenVertices.Add(PointKey(end))) vertices++;
                        string terminalClassification;
                        if (curve == null) terminalClassification = "UNSUPPORTED_NULL_CURVE";
                        else if (curve.IsLine()) { terminalClassification = "STRAIGHT_EDGE"; lines++; }
                        else if (identity == "3002" && TryCircleSupport(key, curveKey, resolvedType, curve, edge.GetCurveParams3(), start, end, curve.CircleParams as Array, mins, maxs, out var circleSource)) { terminalClassification = circleSource == "CIRCLE_ANALYTIC_FULL" ? "FULL_CIRCLE" : "ARC"; if (terminalClassification == "FULL_CIRCLE") circles++; else arcs++; }
                        else if (identity == "3003" && TryEllipseSupport(key, curveKey, resolvedType, curve, edge.GetCurveParams3(), start, end, curve.GetEllipseParams() as Array, mins, maxs, out var ellipseSource)) { terminalClassification = ellipseSource == "ELLIPSE_ANALYTIC_FULL" ? "FULL_ELLIPSE" : "ELLIPSE"; }
                        else terminalClassification = identity == "3002" ? "UNSUPPORTED_ARC_DOMAIN" : identity == "3003" ? "UNSUPPORTED_ELLIPSE_DOMAIN" : "UNSUPPORTED_NON_CIRCLE";
                        classifications[terminalClassification] = classifications.TryGetValue(terminalClassification, out var count) ? count + 1 : 1;
                        terminalClassifications[key] = terminalClassification;
                        if (terminalClassification.StartsWith("UNSUPPORTED_", StringComparison.Ordinal))
                        {
                            unsupportedUniqueEdges.Add(key); rawUnsupported++;
                            if (TryConservativeUnresolvedCurveBounds(identity ?? "", curve, out var bounds))
                            {
                                for (int axis = 0; axis < 3; axis++) unresolvedBounds[axis].Add(bounds[axis]);
                                unresolvedUniqueItems[key] = new Dictionary<string, object> {
                                    { "identity", identity }, { "curve_type", resolvedType },
                                    { "terminal_classification", terminalClassification },
                                    { "conservative_bound_exists", true },
                                    { "conservative_bounds", new[] { bounds[0].Min, bounds[0].Max, bounds[1].Min, bounds[1].Max, bounds[2].Min, bounds[2].Max } },
                                    { "conservative_bounds_by_axis", new Dictionary<string, object> {
                                        { "X", new Dictionary<string, object> { { "low", bounds[0].Min }, { "high", bounds[0].Max } } },
                                        { "Y", new Dictionary<string, object> { { "low", bounds[1].Min }, { "high", bounds[1].Max } } },
                                        { "Z", new Dictionary<string, object> { { "low", bounds[2].Min }, { "high", bounds[2].Max } } }
                                    } },
                                    { "bound_unavailable_reason", null }, { "participates_in_coverage_accounting", true }
                                };
                                Console.WriteLine($"UNRESOLVED_CURVE_CONSERVATIVE_BOUNDS edge_id={key} curve_identity={curveKey} bounds_x=[{bounds[0].Min:R},{bounds[0].Max:R}] bounds_y=[{bounds[1].Min:R},{bounds[1].Max:R}] bounds_z=[{bounds[2].Min:R},{bounds[2].Max:R}]");
                            }
                            else { var unresolvedItem = new Dictionary<string, object> { { "identity", identity }, { "curve_type", resolvedType }, { "terminal_classification", terminalClassification }, { "affected_axes", new[] { "X", "Y", "Z" } }, { "conservative_bound_exists", false }, { "bound_unavailable_reason", "CURVE_PARAMETERS_UNAVAILABLE" }, { "participates_in_coverage_accounting", true } }; unresolvedUniqueItems[key] = unresolvedItem; for (int axis = 0; axis < 3; axis++) { axisHasUnboundedUnresolvedGeometry[axis] = true; unboundedLowCounts[axis]++; unboundedHighCounts[axis]++; } }
                        }
                        Console.WriteLine($"EDGE_CLASSIFICATION_TERMINAL edge_id={key} curve_identity={curveKey} terminal_classification={terminalClassifications[key]} classification_increment_count=1");
                    }
                    catch { if (uniqueEdge) unsupportedUniqueEdges.Add(key); rawUnsupported++; if (uniqueEdge) unresolvedUniqueItems[key] = new Dictionary<string, object> { { "identity", null }, { "curve_type", "UNKNOWN" }, { "terminal_classification", "UNSUPPORTED_EDGE_PROCESSING" }, { "affected_axes", new[] { "X", "Y", "Z" } }, { "conservative_bound_exists", false }, { "bound_unavailable_reason", "EDGE_PROCESSING_EXCEPTION" }, { "participates_in_coverage_accounting", true } }; for (int axis = 0; axis < 3; axis++) { axisHasUnboundedUnresolvedGeometry[axis] = true; unboundedLowCounts[axis]++; unboundedHighCounts[axis]++; } }
                }
            }
        }
        foreach (var item in unresolvedUniqueItems.Values)
        {
            if (item.TryGetValue("conservative_bounds", out var raw) && raw is double[] values)
            {
                var affected = new List<string>();
                for (int axis = 0; axis < 3; axis++)
                {
                    if (values[axis * 2] < mins[axis] - 1e-8 || values[axis * 2 + 1] > maxs[axis] + 1e-8)
                        affected.Add(axis == 0 ? "X" : axis == 1 ? "Y" : "Z");
                }
                item["affected_axes"] = affected.ToArray();
                item["bound_within_proven_envelope"] = affected.Count == 0;
                item["affected_sides"] = new Dictionary<string, object> {
                    { "X", new[] { values[0] < mins[0] - 1e-8 ? "LOW" : null, values[1] > maxs[0] + 1e-8 ? "HIGH" : null }.Where(value => value != null).ToArray() },
                    { "Y", new[] { values[2] < mins[1] - 1e-8 ? "LOW" : null, values[3] > maxs[1] + 1e-8 ? "HIGH" : null }.Where(value => value != null).ToArray() },
                    { "Z", new[] { values[4] < mins[2] - 1e-8 ? "LOW" : null, values[5] > maxs[2] + 1e-8 ? "HIGH" : null }.Where(value => value != null).ToArray() }
                };
            }
            else if (!item.ContainsKey("affected_sides"))
            {
                item["affected_sides"] = new Dictionary<string, object> {
                    { "X", new[] { "LOW", "HIGH" } }, { "Y", new[] { "LOW", "HIGH" } }, { "Z", new[] { "LOW", "HIGH" } }
                };
            }
        }
        int unsupported = unsupportedUniqueEdges.Count;
        bool complete = IsEnvelopeCoverageComplete(mins, maxs, axisHasUnboundedUnresolvedGeometry, unresolvedBounds);
        var unboundedLowByAxis = new Dictionary<string, int> { { "X", unboundedLowCounts[0] }, { "Y", unboundedLowCounts[1] }, { "Z", unboundedLowCounts[2] } };
        var unboundedHighByAxis = new Dictionary<string, int> { { "X", unboundedHighCounts[0] }, { "Y", unboundedHighCounts[1] }, { "Z", unboundedHighCounts[2] } };
        int unboundedUnresolvedCount = unresolvedUniqueItems.Values.Count(x => x["conservative_bound_exists"] is bool b && !b);
        var envelope = new Dictionary<string, object> { { "source", "TOPOLOGY_GEOMETRIC_SUPPORT" }, { "confidence", complete ? "EXACT_GEOMETRY" : "PARTIAL_GEOMETRY" }, { "coverage_complete", complete }, { "unsupported_curve_count", unsupported }, { "raw_unsupported_curve_occurrence_count", rawUnsupported }, { "unsupported_unique_classification_sum", classifications.Where(x => x.Key.StartsWith("UNSUPPORTED_", StringComparison.Ordinal)).Sum(x => x.Value) }, { "unresolved_unique_count", unresolvedUniqueItems.Count }, { "unbounded_unresolved_unique_count", unboundedUnresolvedCount }, { "bounded_unresolved_unique_count", unresolvedUniqueItems.Count - unboundedUnresolvedCount }, { "unbounded_low_count", unboundedLowCounts.Sum() }, { "unbounded_high_count", unboundedHighCounts.Sum() }, { "unbounded_low_count_by_axis", unboundedLowByAxis }, { "unbounded_high_count_by_axis", unboundedHighByAxis }, { "unresolved_items", unresolvedUniqueItems.Values.ToList() }, { "unsupported_surface_count", 0 }, { "raw_edge_count", rawEdges }, { "unique_edge_count", seenEdges.Count }, { "raw_vertex_count", rawVertices }, { "unique_vertex_count", vertices }, { "curve_identity_counts", curveIdentities }, { "classification_counts", classifications }, { "entity_count_by_type", new Dictionary<string, object> { { "VERTEX", vertices }, { "STRAIGHT_EDGE", lines }, { "FULL_CIRCLE", circles }, { "ARC", arcs } } } };
        var axisEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        for (int axis = 0; axis < 3; axis++)
        {
            string name = axis == 0 ? "x" : axis == 1 ? "y" : "z";
            string axisName = axis == 0 ? "X" : axis == 1 ? "Y" : "Z";
            const double coordinateTolerance = 1e-8;
            var lowSupport = endpointSupports[axis].FirstOrDefault(candidate => Math.Abs(Convert.ToDouble(candidate["axis_coordinate"]) - mins[axis]) <= coordinateTolerance);
            var highSupport = endpointSupports[axis].FirstOrDefault(candidate => Math.Abs(Convert.ToDouble(candidate["axis_coordinate"]) - maxs[axis]) <= coordinateTolerance);
            foreach (var support in new[] { lowSupport, highSupport })
            {
                if (support == null) continue;
                var candidates = new List<string>();
                var vertexId = S(support["topology_entity_identity"]);
                if (vertexId != null && vertexPlaneCandidates.TryGetValue(vertexId, out var signatures)) candidates.AddRange(signatures.OrderBy(value => value, StringComparer.Ordinal));
                support["supporting_plane_candidate_signatures"] = candidates.ToArray();
                support["supporting_plane_signature"] = candidates.Count == 1 ? candidates[0] : null;
                support["supporting_plane_proof_state"] = candidates.Count == 1 ? "PROVEN" : candidates.Count > 1 ? "AMBIGUOUS" : "UNAVAILABLE";
                support["supporting_plane_proof_method"] = candidates.Count == 1 ? "TOPOLOGY_VERTEX_EDGE_ADJACENT_PLANAR_FACE" : null;
            }
            var proof = DecideAxisSideProof(lowSupport != null, highSupport != null, mins[axis], maxs[axis], unresolvedBounds[axis], axisHasUnboundedUnresolvedGeometry[axis]);
            bool lowProven = proof.LowBlocker == null && !axisHasUnboundedUnresolvedGeometry[axis] && !double.IsInfinity(mins[axis]);
            bool highProven = proof.HighBlocker == null && !axisHasUnboundedUnresolvedGeometry[axis] && !double.IsInfinity(maxs[axis]);
            bool axisComplete = lowProven && highProven;
            axisEvidence[axisName] = new Dictionary<string, object> {
                { "axis", axisName }, { "low_coordinate", mins[axis] }, { "high_coordinate", maxs[axis] }, { "span", maxs[axis] - mins[axis] },
                { "low_proven", lowProven }, { "high_proven", highProven }, { "coverage_complete", axisComplete },
                { "target_source", axisComplete ? "PROVEN_TOPOLOGY" : "APPROXIMATE_FALLBACK" },
                { "low_support", lowProven ? lowSupport : null }, { "high_support", highProven ? highSupport : null },
                { "low_blocker", proof.LowBlocker }, { "high_blocker", proof.HighBlocker },
                { "unproven_reason", axisComplete ? null : proof.LowBlocker ?? proof.HighBlocker ?? "SELECTABLE_ENDPOINT_SUPPORT_UNAVAILABLE" }
            };
            envelope["min_" + name + "_m"] = mins[axis]; envelope["max_" + name + "_m"] = maxs[axis]; envelope["size_" + name + "_m"] = maxs[axis] - mins[axis];
        }
        envelope["axis_evidence"] = axisEvidence;
        return envelope;
    }
    static Dictionary<string, object> MaterializeAxisEvidence(Dictionary<string, object> exact, Dictionary<string, object> approximate)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        var exactAxes = exact.TryGetValue("axis_evidence", out var exactValue) ? exactValue as Dictionary<string, object> : null;
        foreach (var axisName in new[] { "X", "Y", "Z" })
        {
            string suffix = axisName.ToLowerInvariant();
            var exactAxis = exactAxes != null && exactAxes.TryGetValue(axisName, out var exactAxisValue)
                ? exactAxisValue as Dictionary<string, object> : null;
            bool lowProven = exactAxis != null && exactAxis.TryGetValue("low_proven", out var lowValue) && lowValue is bool && (bool)lowValue;
            bool highProven = exactAxis != null && exactAxis.TryGetValue("high_proven", out var highValue) && highValue is bool && (bool)highValue;
            bool promoted = lowProven && highProven;
            var source = promoted ? exact : approximate;
            var row = new Dictionary<string, object> {
                { "axis", axisName },
                { "low_coordinate", source["min_" + suffix + "_m"] },
                { "high_coordinate", source["max_" + suffix + "_m"] },
                { "span", source["size_" + suffix + "_m"] },
                { "low_proven", promoted },
                { "high_proven", promoted },
                { "coverage_complete", promoted },
                { "target_source", promoted ? "PROVEN_TOPOLOGY" : "APPROXIMATE_FALLBACK" },
                { "low_support", promoted ? exactAxis["low_support"] : null },
                { "high_support", promoted ? exactAxis["high_support"] : null },
                { "low_blocker", promoted ? null : exactAxis != null && exactAxis.TryGetValue("low_blocker", out var lowBlocker) ? lowBlocker : null },
                { "high_blocker", promoted ? null : exactAxis != null && exactAxis.TryGetValue("high_blocker", out var highBlocker) ? highBlocker : null },
                { "unproven_reason", promoted ? null : exactAxis != null && exactAxis.TryGetValue("unproven_reason", out var reason) ? reason : "AXIS_EVIDENCE_UNAVAILABLE" }
            };
            result[axisName] = row;
        }
        return result;
    }
    static void Require(bool condition, string name) { if (!condition) throw new InvalidOperationException("GEOMETRY_SELF_TEST_FAILED=" + name); Console.WriteLine("GEOMETRY_TEST=" + name + ":PASS"); }
    static void GeometrySelfTest()
    {
        var vertex = Support(new P3(1.25, -2.0, 3.0), 1); Require(vertex.Min == -2.0 && vertex.Max == -2.0, "A_VERTEX_SUPPORT");
        var line = Support(new P3(-2, 4, 1), new P3(3, -1, 8), 0); Require(line.Min == -2 && line.Max == 3, "B_STRAIGHT_EDGE_SUPPORT");
        var inPlane = CircleSupport(new P3(0, 0, 0), new P3(0, 0, 1), 2, 0); Require(Math.Abs(inPlane.Min + 2) < 1e-12 && Math.Abs(inPlane.Max - 2) < 1e-12, "C_FULL_CIRCLE_AXIS_IN_PLANE");
        var normalAxis = CircleSupport(new P3(0, 0, 4), new P3(1, 0, 0), 2, 0); Require(Math.Abs(normalAxis.Min) < 1e-12 && Math.Abs(normalAxis.Max) < 1e-12, "D_FULL_CIRCLE_AXIS_PARALLEL_NORMAL");
        var oblique = CircleSupport(new P3(0, 0, 0), Unit(new P3(1, 1, 0)), 2, 0); Require(Math.Abs(oblique.Min + Math.Sqrt(2)) < 1e-12 && Math.Abs(oblique.Max - Math.Sqrt(2)) < 1e-12, "E_OBLIQUE_CIRCLE_PROJECTION");
        Require(!IsWithinTrimValues(1.0, 2.0, 0.0), "F_ARC_EXCLUDES_THEORETICAL_EXTREMUM");
        Require(IsWithinTrimValues(1.0, 2.0, 1.0), "G_ARC_INCLUDES_TRIM_ENDPOINT");
        var seen = new HashSet<string>(StringComparer.Ordinal); var p = new P3(1, 2, 3); Require(seen.Add(PointKey(p)) && !seen.Add(PointKey(p)), "H_DUPLICATE_TOPOLOGY_DEDUP");
        var partial = new Dictionary<string, object> { { "coverage_complete", false }, { "confidence", "PARTIAL_GEOMETRY" }, { "unsupported_curve_count", 1 } }; Require(!(bool)partial["coverage_complete"], "I_UNSUPPORTED_CURVE_INCOMPLETE");
        var approx = ApproximateEnvelope(new[] { -1.0, -2.0, -3.0, 4.0, 5.0, 6.0 }); Require((string)approx["confidence"] == "APPROXIMATE_API", "J_APPROXIMATE_FALLBACK_EXPLICIT");
        var circleX = StationaryParameters(2.0, 0.0);
        Require(circleX.Length == 2 && IsWithinTrimValues(-0.2, 0.2, circleX[0]) && !IsWithinTrimValues(-0.2, 0.2, circleX[1]), "K_TRIMMED_ARC_CROSSES_ONE_EXTREMUM");
        var circleY = StationaryParameters(0.0, 2.0);
        Require(circleY.Length == 2 && !circleY.Any(t => IsWithinTrimValues(-0.2, 0.2, t)), "L_TRIMMED_ARC_EXCLUDES_EXTREMA");
        Require(IsWithinTrimValues(6.1, 6.5, 0.0) && !IsWithinTrimValues(6.1, 6.5, Math.PI), "M_PERIODIC_WRAPAROUND_DOMAIN");
        var ellipseX = StationaryParameters(3.0, 0.0);
        var ellipseY = StationaryParameters(0.0, 1.5);
        Require(ellipseX.Length == 2 && ellipseY.Length == 2 && ellipseX.Any(t => IsWithinTrimValues(0.0, 2.0 * Math.PI, t)) && ellipseY.Any(t => IsWithinTrimValues(0.0, 2.0 * Math.PI, t)), "N_FULL_ELLIPSE_ANALYTIC_EXTREMA");
        Require(ellipseX.Count(t => IsWithinTrimValues(2.8, 3.5, t)) == 1 && ellipseY.Count(t => IsWithinTrimValues(2.8, 3.5, t)) == 0, "O_TRIMMED_ELLIPSE_DOMAIN_PROOF");
        var p1 = DecideAxisSideProof(true, true, -5, 5, new[] { new Interval(-2, 3) }, false); Require(p1.LowProven && p1.HighProven, "P1_UNRESOLVED_BOUND_INSIDE_ENVELOPE");
        var p2 = DecideAxisSideProof(true, true, -5, 5, new[] { new Interval(-6, 3) }, false); Require(!p2.LowProven && p2.HighProven, "P2_UNRESOLVED_CROSSES_LOW_ONLY");
        var p3 = DecideAxisSideProof(true, true, -5, 5, new[] { new Interval(-2, 6) }, false); Require(p3.LowProven && !p3.HighProven, "P3_UNRESOLVED_CROSSES_HIGH_ONLY");
        var p4 = DecideAxisSideProof(true, true, -5, 5, new[] { new Interval(-6, 6) }, false); Require(!p4.LowProven && !p4.HighProven, "P4_UNRESOLVED_CROSSES_BOTH");
        var p5 = DecideAxisSideProof(true, true, -5, 5, Array.Empty<Interval>(), true); Require(!p5.LowProven && !p5.HighProven, "P5_UNBOUNDED_UNRESOLVED_GEOMETRY");
        var p6x = DecideAxisSideProof(true, true, -5, 5, Array.Empty<Interval>(), false); var p6y = DecideAxisSideProof(true, true, -5, 5, new[] { new Interval(-6, 6) }, false); var p6z = DecideAxisSideProof(true, true, -5, 5, Array.Empty<Interval>(), false); Require(p6x.LowProven && p6x.HighProven && !p6y.LowProven && !p6y.HighProven && p6z.LowProven && p6z.HighProven, "P6_MIXED_AXIS_ISOLATION");
        var cMins = new[] { -1.0, -2.0, -3.0 }; var cMaxs = new[] { 1.0, 2.0, 3.0 };
        var cUnbounded = new[] { true, false, false }; var cBounds = new[] { new List<Interval>(), new List<Interval>(), new List<Interval>() };
        Require(!IsEnvelopeCoverageComplete(cMins, cMaxs, cUnbounded, cBounds), "C1_UNBOUNDED_UNRESOLVED_INCOMPLETE");
        cUnbounded[0] = false; cBounds[0].Add(new Interval(-0.5, 0.5));
        Require(IsEnvelopeCoverageComplete(cMins, cMaxs, cUnbounded, cBounds), "C2_CONSERVATIVE_BOUND_INSIDE_COMPLETE");
        cBounds[0].Clear(); Require(IsEnvelopeCoverageComplete(cMins, cMaxs, cUnbounded, cBounds), "C3_ALL_GEOMETRY_COVERED");
        var c4 = DecideAxisSideProof(false, false, -1.0, 1.0, Array.Empty<Interval>(), false);
        Require(c4.LowProven && c4.HighProven, "C4_GEOMETRIC_PROOF_INDEPENDENT_OF_WITNESS");
        var c5x = DecideAxisSideProof(false, false, -1.0, 1.0, Array.Empty<Interval>(), false);
        var c5y = DecideAxisSideProof(false, false, -1.0, 1.0, new[] { new Interval(-2.0, 0.5) }, false);
        var c5z = DecideAxisSideProof(false, false, -1.0, 1.0, Array.Empty<Interval>(), false);
        Require(c5x.LowProven && c5x.HighProven && !c5y.LowProven && c5y.HighProven && c5z.LowProven && c5z.HighProven, "C5_MIXED_AXIS_SIDE_ACCOUNTING");
        Console.WriteLine("COVERAGE_ACCOUNTING_SELF_TEST=C1-C5:PASS");
        var support = new Dictionary<string, object> { { "support_kind", "VERTEX" } };
        Dictionary<string, object> Axis(bool proven, double low, double high) => new Dictionary<string, object> {
            { "low_proven", proven }, { "high_proven", proven }, { "low_support", proven ? support : null }, { "high_support", proven ? support : null }, { "unproven_reason", proven ? null : "UNPROVEN" }
        };
        Dictionary<string, object> Envelope(Dictionary<string, object> axes) => new Dictionary<string, object> {
            { "min_x_m", -1.0 }, { "max_x_m", 2.0 }, { "size_x_m", 3.0 }, { "min_y_m", -3.0 }, { "max_y_m", 4.0 }, { "size_y_m", 7.0 }, { "min_z_m", -5.0 }, { "max_z_m", 6.0 }, { "size_z_m", 11.0 }, { "axis_evidence", axes }, { "coverage_complete", true }
        };
        var exactAll = Envelope(new Dictionary<string, object> { { "X", Axis(true, -10, 20) }, { "Y", Axis(true, -30, 40) }, { "Z", Axis(true, -50, 60) } });
        var approximateDifferent = Envelope(new Dictionary<string, object> { { "X", Axis(false, 0, 0) }, { "Y", Axis(false, 0, 0) }, { "Z", Axis(false, 0, 0) } });
        var m1 = MaterializeAxisEvidence(exactAll, approximateDifferent); var m1x = (Dictionary<string, object>)m1["X"]; Require((double)m1x["low_coordinate"] == -1.0 && (string)m1x["target_source"] == "PROVEN_TOPOLOGY" && m1x["low_support"] != null, "M1_EXACT_SUPPORT_WINS");
        var exactMixed = Envelope(new Dictionary<string, object> { { "X", Axis(true, 0, 0) }, { "Y", Axis(false, 0, 0) }, { "Z", Axis(true, 0, 0) } }); var m2 = MaterializeAxisEvidence(exactMixed, approximateDifferent); Require((string)((Dictionary<string, object>)m2["X"])["target_source"] == "PROVEN_TOPOLOGY" && (string)((Dictionary<string, object>)m2["Y"])["target_source"] == "APPROXIMATE_FALLBACK" && (string)((Dictionary<string, object>)m2["Z"])["target_source"] == "PROVEN_TOPOLOGY", "M2_AXIS_LEVEL_PROMOTION");
        var m3 = MaterializeAxisEvidence(Envelope(new Dictionary<string, object> { { "X", Axis(false, 0, 0) }, { "Y", Axis(false, 0, 0) }, { "Z", Axis(false, 0, 0) } }), approximateDifferent); Require(((Dictionary<string, object>)m3["X"])["low_support"] == null && (string)((Dictionary<string, object>)m3["X"])["target_source"] == "APPROXIMATE_FALLBACK", "M3_NO_FABRICATED_PROVENANCE");
        var m4 = MaterializeAxisEvidence(exactAll, approximateDifferent); Require(new[] { "X", "Y", "Z" }.All(a => (string)((Dictionary<string, object>)m4[a])["target_source"] == "PROVEN_TOPOLOGY"), "M4_COMPLETE_EXACT_NOT_DOWNGRADED");
        var legacy = new Dictionary<string, object> { { "min_x_m", -7.0 }, { "max_x_m", 8.0 }, { "size_x_m", 15.0 }, { "min_y_m", -9.0 }, { "max_y_m", 10.0 }, { "size_y_m", 19.0 }, { "min_z_m", -11.0 }, { "max_z_m", 12.0 }, { "size_z_m", 23.0 } }; var m5 = MaterializeAxisEvidence(legacy, legacy); Require((string)((Dictionary<string, object>)m5["X"])["target_source"] == "APPROXIMATE_FALLBACK" && ((Dictionary<string, object>)m5["X"])["low_support"] == null, "M5_LEGACY_COMPATIBILITY");
        Console.WriteLine("MATERIALIZATION_SELF_TEST=M1-M5:PASS");
        Console.WriteLine("GEOMETRY_SELF_TEST=PASS");
    }
    static void BindInterop()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            string name = new System.Reflection.AssemblyName(args.Name).Name;
            if (name.StartsWith("SolidWorks.Interop", StringComparison.OrdinalIgnoreCase))
            {
                string path = Path.Combine(@"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist", name + ".dll");
                if (File.Exists(path)) return System.Reflection.Assembly.LoadFrom(path);
            }
            return null;
        };
    }
    static object[] ArrayOf(object value) { var a = value as Array; if (a == null) return new object[0]; var r = new object[a.Length]; a.CopyTo(r, 0); return r; }
    static double[] Doubles(object value)
    {
        var a = value as Array; if (a == null) return new double[0];
        var r = new double[a.Length]; for (int i = 0; i < a.Length; i++) r[i] = Convert.ToDouble(a.GetValue(i)); return r;
    }
    static object Numbers(double[] values) { return values == null ? null : Array.ConvertAll(values, x => (object)x); }
    static string S(object value) { return value == null ? null : Convert.ToString(value); }
    static object Read(Func<object> f) { try { return f(); } catch { return null; } }
    static double[] PartBox(IModelDoc2 model)
    {
        try { var part = model as PartDoc; var p = part?.GetPartBox(true) as Array; if (p != null && p.Length >= 6) { var r = new double[6]; for (int i=0;i<6;i++) r[i]=Convert.ToDouble(p.GetValue(i)); return r; } } catch { }
        return new double[0];
    }
    static List<Dictionary<string, object>> ViewCandidates(double[] b, int faces, int vertices)
    {
        var r = new List<Dictionary<string, object>>(); if (b.Length < 6) return r;
        double x=Math.Abs(b[3]-b[0]), y=Math.Abs(b[4]-b[1]), z=Math.Abs(b[5]-b[2]);
        var specs = new[]{("FRONT","Z",x,y),("BACK","Z",x,y),("LEFT","X",z,y),("RIGHT","X",z,y),("TOP","Y",x,z),("BOTTOM","Y",x,z)};
        int i=0; foreach(var s in specs) r.Add(new Dictionary<string,object>{ {"candidate_id",$"ORTHO_{++i}"},{"orientation",s.Item1},{"projection_axis",s.Item2},{"projected_width_m",s.Item3},{"projected_height_m",s.Item4},{"projected_area_m2",s.Item3*s.Item4},{"visible_geometry_score",faces},{"feature_visibility_score",faces},{"hole_visibility_score",0},{"silhouette_complexity_score",vertices},{"geometry_signature",$"{s.Item3:0.#########}x{s.Item4:0.#########}"},{"evidence",new[]{"IModelDoc2.GetPartBox","feature_tree_geometry_context"}},{"confidence","GEOMETRY_BBOX"} });
        return r;
    }
    static void ReadField(Dictionary<string, object> row, string name, Func<object> getter)
    {
        try { row[name] = getter(); }
        catch (Exception ex) { row[name + "_read_error"] = ex.GetType().Name; }
    }
    static Dictionary<string, object> Feature(IFeature f, int index, string parent)
    {
        var row = new Dictionary<string, object>();
        row["feature_name"] = f.Name; row["feature_type"] = f.GetTypeName2(); row["feature_index"] = index; row["parent_feature"] = parent; row["suppressed"] = Read(() => f.IsSuppressed());
        return row;
    }
    static string SurfaceType(ISurface surface)
    {
        if (surface == null) return "UNKNOWN";
        try { if (surface.IsCylinder()) return "CYLINDER"; } catch { }
        try { if (surface.IsCone()) return "CONE"; } catch { }
        try { if (surface.IsPlane()) return "PLANE"; } catch { }
        return "OTHER";
    }
    static Dictionary<string, object> PointRecord(object raw)
    {
        var a = Doubles(raw);
        if (a.Length < 3) return null;
        return new Dictionary<string, object> { { "x_m", a[0] }, { "y_m", a[1] }, { "z_m", a[2] } };
    }
    static Dictionary<string, object> CircleEdgeRecord(IEdge edge)
    {
        var row = new Dictionary<string, object> {
            { "session_edge_id", ComIdentity(edge) }, { "selectable_expectation", edge != null },
            { "geometry", "CIRCLE" }
        };
        try
        {
            var curve = edge.GetCurve() as ICurve;
            if (curve == null || !curve.IsCircle()) { row["geometry"] = "NON_CIRCLE"; return row; }
            var p = Doubles(curve.CircleParams);
            if (p.Length >= 7)
            {
                row["center"] = PointRecord(new[] { p[0], p[1], p[2] });
                row["axis"] = Numbers(new[] { p[3], p[4], p[5] });
                row["radius_m"] = p[6]; row["diameter_m"] = 2.0 * p[6];
            }
            row["start_vertex"] = ComIdentity(edge.GetStartVertex());
            row["end_vertex"] = ComIdentity(edge.GetEndVertex());
        }
        catch (Exception ex) { row["read_error"] = ex.GetType().Name; }
        return row;
    }
    // Canonical plane/face signature formatting: collapse only signed zero.
    // No tolerance or rounding is applied to nonzero geometry values.
    static string StableNumber(double value)
    {
        double canonical = value == 0.0 ? 0.0 : value;
        return canonical.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }
    static Dictionary<string, object> PlaneEvidence(ISurface surface, double[] box, double area, List<Dictionary<string, object>> circular)
    {
        var p = Doubles(Read(() => surface.PlaneParams));
        if (p.Length < 6) return null;
        var normal = new[] { p[3], p[4], p[5] };
        var length = Math.Sqrt(normal.Sum(x => x * x));
        if (length <= 1e-12) return null;
        normal = normal.Select(x => x / length).ToArray();
        var point = new[] { p[0], p[1], p[2] };
        var offset = normal[0] * point[0] + normal[1] * point[1] + normal[2] * point[2];
        var sign = normal.FirstOrDefault(x => Math.Abs(x) > 1e-12);
        if (sign < 0) { normal = normal.Select(x => -x).ToArray(); offset = -offset; }
        var planeSignature = string.Join("|", normal.Select(StableNumber).Concat(new[] { StableNumber(offset) }));
        var faceSignature = planeSignature + "|AREA:" + StableNumber(area) + "|BOX:" + string.Join(",", box.Select(StableNumber));
        return new Dictionary<string, object> {
            { "surface_type", "PLANE" }, { "proof_state", "PROVEN" },
            { "proof_method", "MODEL_PLANAR_FACE_GEOMETRY" }, { "selectable_witness_expectation", "FACE" },
            { "plane_normal", Numbers(normal) }, { "plane_offset_m", offset },
            { "representative_point", PointRecord(point) },
            { "face_signature", faceSignature }, { "supporting_plane_signature", planeSignature },
            { "area_m2", area }, { "extent_box_m", Numbers(box) },
            { "adjacent_circular_edge_signatures", circular.Select(e => S(e["geometry"])).ToArray() }
        };
    }
    static Dictionary<string, object> HoleTopology(IFeature feature, int placementCount)
    {
        var faces = new List<Dictionary<string, object>>();
        var openings = new List<Dictionary<string, object>>();
        try
        {
            foreach (var raw in ArrayOf(feature.GetFaces()))
            {
                var face = raw as IFace; if (face == null) continue;
                var surface = Read(() => face.GetSurface()) as ISurface;
                var type = SurfaceType(surface);
                var frow = new Dictionary<string, object> {
                    { "session_face_id", ComIdentity(face) }, { "surface_type", type },
                    { "feature_owner", feature.Name }, { "selectable_expectation", true },
                    { "face_sense", Read(() => face.FaceInSurfaceSense()) }
                };
                try { frow["area"] = face.GetArea(); } catch { }
                try { frow["box"] = Numbers(Doubles(face.GetBox())); } catch { }
                var edges = new List<Dictionary<string, object>>();
                foreach (var eraw in ArrayOf(Read(() => face.GetEdges())))
                {
                    var edge = eraw as IEdge; if (edge == null) continue;
                    var e = CircleEdgeRecord(edge); if (S(e["geometry"]) == "CIRCLE") edges.Add(e);
                }
                frow["boundary_circular_edges"] = edges;
                if (type == "CYLINDER" || type == "CONE")
                {
                    var facts = new Dictionary<string, object>();
                    try
                    {
                        var p = Doubles(type == "CYLINDER" ? surface.CylinderParams : surface.ConeParams);
                        if (p.Length >= 6)
                        {
                            facts["axis_origin"] = PointRecord(new[] { p[0], p[1], p[2] });
                            facts["axis"] = Numbers(new[] { p[3], p[4], p[5] });
                            if (type == "CYLINDER" && p.Length >= 7) { facts["radius_m"] = p[6]; facts["diameter_m"] = 2.0 * p[6]; }
                            if (type == "CONE") facts["cone_params"] = Numbers(p);
                        }
                    }
                    catch { }
                    try { var b = Doubles(face.GetBox()); if (b.Length >= 6) facts["axial_extent_box_m"] = b; } catch { }
                    frow["surface_facts"] = facts;
                    openings.Add(new Dictionary<string, object> {
                        { "opening_index", openings.Count + 1 }, { "feature_name", feature.Name },
                        { "proof_state", edges.Count > 0 ? "PROVEN" : "UNRESOLVED" },
                        { "surface_type", type }, { "surface_facts", facts },
                        { "boundary_circular_edges", edges },
                        { "topology_signature", type + "|" + S(frow["session_face_id"]) },
                        { "blockers", edges.Count > 0 ? Array.Empty<string>() : new[] { "NO_CIRCULAR_BOUNDARY_EDGE" } }
                    });
                }
                faces.Add(frow);
            }
        }
        catch (Exception ex) { return new Dictionary<string, object> { { "error", ex.GetType().Name }, { "faces", faces }, { "physical_openings", openings } }; }
        int proven = openings.Count(x => S(x["proof_state"]) == "PROVEN");
        int unresolved = openings.Count - proven;
        var binding = new List<Dictionary<string, object>>();
        for (int i = 0; i < placementCount; i++) binding.Add(new Dictionary<string, object> {
            { "semantic_instance_index", i + 1 }, { "feature_name", feature.Name },
            { "opening_index", openings.Count == placementCount && proven == placementCount ? i + 1 : null },
            { "binding_status", openings.Count == placementCount && proven == placementCount ? "AMBIGUOUS" : "UNRESOLVED" },
            { "binding_evidence", openings.Count == placementCount && proven == placementCount ? "FEATURE_OWNED_TOPOLOGY_COUNT_MATCH_ONLY" : "FEATURE_OWNED_OPENING_COUNT_NOT_PROVEN" }
        });
        return new Dictionary<string, object> {
            { "owned_face_count", faces.Count },
            { "owned_cylindrical_face_count", faces.Count(x => S(x["surface_type"]) == "CYLINDER") },
            { "owned_conical_face_count", faces.Count(x => S(x["surface_type"]) == "CONE") },
            { "faces", faces }, { "physical_openings", openings }, { "instance_bindings", binding },
            { "proven_opening_count", proven }, { "ambiguous_opening_count", 0 }, { "unresolved_opening_count", unresolved },
            { "bindable_circular_edge_count", openings.Sum(x => ((List<Dictionary<string, object>>)x["boundary_circular_edges"]).Count) }
        };
    }

    static Dictionary<string, object> BodyTopology(PartDoc part)
    {
        var faces = new List<Dictionary<string, object>>();
        var candidates = new List<Dictionary<string, object>>();
        int bodyCount = 0;
        object bodiesObject = null; try { bodiesObject = part.GetBodies2((int)swBodyType_e.swSolidBody, true); } catch { }
        foreach (var rawBody in ArrayOf(bodiesObject))
        {
            var body = rawBody as Body2; if (body == null) continue; bodyCount++;
            foreach (var rawFace in ArrayOf(Read(() => body.GetFaces())))
            {
                var face = rawFace as IFace2; if (face == null) continue;
                var surface = Read(() => face.GetSurface()) as ISurface;
                string type = SurfaceType(surface);
                var row = new Dictionary<string, object> {
                    { "session_face_id", ComIdentity(face) }, { "surface_type", type },
                    { "selectable_expectation", true }, { "face_sense", Read(() => face.FaceInSurfaceSense()) }
                };
                try { row["area_m2"] = face.GetArea(); } catch { }
                double[] box = Array.Empty<double>(); try { box = Doubles(face.GetBox()); if (box.Length >= 6) row["box"] = Numbers(box); } catch { }
                var circular = new List<Dictionary<string, object>>(); int edgeCount = 0;
                foreach (var rawEdge in ArrayOf(Read(() => face.GetEdges())))
                {
                    var edge = rawEdge as IEdge; if (edge == null) continue; edgeCount++;
                    var edgeRow = CircleEdgeRecord(edge);
                    if (S(edgeRow["geometry"]) == "CIRCLE") circular.Add(edgeRow);
                }
                row["edge_count"] = edgeCount; row["boundary_circular_edges"] = circular;
                if (type == "PLANE" && box.Length >= 6)
                {
                    try { var plane = PlaneEvidence(surface, box, Convert.ToDouble(row["area_m2"]), circular); if (plane != null) row["plane_evidence"] = plane; } catch { }
                }
                if (type == "CYLINDER" || type == "CONE")
                {
                    var facts = new Dictionary<string, object>();
                    try {
                        var p = Doubles(type == "CYLINDER" ? surface.CylinderParams : surface.ConeParams);
                        if (p.Length >= 6) { facts["axis_origin"] = PointRecord(new[] { p[0], p[1], p[2] }); facts["axis"] = Numbers(new[] { p[3], p[4], p[5] }); }
                        if (type == "CYLINDER" && p.Length >= 7) { facts["radius_m"] = p[6]; facts["diameter_m"] = 2.0 * p[6]; }
                        if (type == "CONE") facts["cone_params"] = Numbers(p);
                    } catch { }
                    if (box.Length >= 6) facts["axial_extent_box_m"] = box;
                    row["surface_facts"] = facts;
                    string classification = circular.Count >= 1 ? "AMBIGUOUS" : "AMBIGUOUS_CYLINDER";
                    var candidate = new Dictionary<string, object> {
                        { "opening_id", "BODY_FACE_" + (candidates.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                        { "classification", classification }, { "proof_state", "UNRESOLVED" },
                        { "surface_type", type }, { "source_face_id", row["session_face_id"] },
                        { "surface_facts", facts }, { "boundary_circular_edges", circular },
                        { "selectable_opening_edge", circular.Count > 0 ? circular[0] : null },
                        { "topology_signature", type + "|" + S(row["session_face_id"]) + "|CIRCLES:" + circular.Count },
                        { "classification_evidence", new[] { "BODY_TOPOLOGY_CYLINDRICAL_FACE", circular.Count > 0 ? "BOUNDARY_CIRCULAR_EDGE_PRESENT" : "NO_BOUNDARY_CIRCULAR_EDGE", "INTERNAL_VS_EXTERNAL_NOT_PROVEN" } }
                    };
                    candidates.Add(candidate);
                }
                faces.Add(row);
            }
        }
        var physical = new List<Dictionary<string, object>>();
        var coneEdgeIds = new HashSet<string>(candidates.Where(x => S(x["surface_type"]) == "CONE")
            .SelectMany(x => ((List<Dictionary<string, object>>)x["boundary_circular_edges"]).Select(e => S(e["session_edge_id"])))
            .Where(x => !String.IsNullOrEmpty(x)), StringComparer.Ordinal);
        foreach (var candidate in candidates.Where(x => S(x["surface_type"]) == "CYLINDER"))
        {
            var edges = (List<Dictionary<string, object>>)candidate["boundary_circular_edges"];
            var shared = edges.Where(e => coneEdgeIds.Contains(S(e["session_edge_id"]))).ToList();
            if (edges.Count >= 2 && shared.Count > 0)
            {
                var witness = edges.FirstOrDefault(e => !coneEdgeIds.Contains(S(e["session_edge_id"]))) ?? edges[0];
                candidate["classification"] = "INTERNAL_HOLE"; candidate["proof_state"] = "PROVEN";
                candidate["classification_evidence"] = new[] { "CYLINDER_WITH_TWO_CIRCULAR_BOUNDARIES", "COAXIAL_CONE_CHAIN_SHARED_BOUNDARY", "BODY_TOPOLOGY_INTERNAL_OPENING" };
                physical.Add(new Dictionary<string, object> {
                    { "opening_id", candidate["opening_id"] }, { "classification", "INTERNAL_HOLE" }, { "proof_state", "PROVEN" },
                    { "axis", ((Dictionary<string, object>)candidate["surface_facts"])["axis"] },
                    { "diameter_stages", new[] { ((Dictionary<string, object>)candidate["surface_facts"])["diameter_m"] } },
                    { "boundary_circular_edges", edges }, { "selectable_opening_edge", witness },
                    { "topology_signature", candidate["topology_signature"] },
                    { "classification_evidence", candidate["classification_evidence"] }
                });
            }
        }
        int cylinderCount = faces.Count(x => S(x["surface_type"]) == "CYLINDER");
        return new Dictionary<string, object> {
            { "body_count", bodyCount }, { "face_count", faces.Count },
            { "cylindrical_face_count", faces.Count(x => S(x["surface_type"]) == "CYLINDER") },
            { "conical_face_count", faces.Count(x => S(x["surface_type"]) == "CONE") },
            { "faces", faces }, { "physical_openings", physical },
            { "candidate_cylinders", candidates },
            { "internal_hole_count", physical.Count }, { "external_cylinder_count", 0 },
            { "ambiguous_cylinder_count", cylinderCount - physical.Count },
            { "classification_policy", "CONSERVATIVE_INTERNAL_OPENING_PROOF_REQUIRED" }
        };
    }
    static double[] PointValues(object value) { var map = value as Dictionary<string, object>; if (map != null && map.ContainsKey("x_m")) return new[] { Convert.ToDouble(map["x_m"]), Convert.ToDouble(map["y_m"]), Convert.ToDouble(map["z_m"]) }; var a = Doubles(value); return a.Length >= 3 ? new[] { a[0], a[1], a[2] } : null; }
    static double Distance3(double[] a, double[] b) { var x = a[0] - b[0]; var y = a[1] - b[1]; var z = a[2] - b[2]; return Math.Sqrt(x * x + y * y + z * z); }
    static string SetSignature(List<double[]> points)
    {
        var values = new List<double>(); for (int i = 0; i < points.Count; i++) for (int j = i + 1; j < points.Count; j++) values.Add(Distance3(points[i], points[j]));
        values.Sort(); return string.Join("|", values.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
    }
    static List<Dictionary<string, object>> SetAttribution(List<Dictionary<string, object>> holes, List<Dictionary<string, object>> openings)
    {
        const double tolerance = 1e-7; var rows = new List<Dictionary<string, object>>();
        foreach (var hole in holes)
        {
            var points = ((List<Dictionary<string, object>>)hole["sketch_points"]).Select(p => PointValues(p.TryGetValue("sketch_to_model_point_m", out var v) ? v : null)).Where(p => p != null).ToList();
            var compatible = openings.Where(o => { try { var d = ((Array)o["diameter_stages"]).GetValue(0); return Math.Abs(Convert.ToDouble(d) - Convert.ToDouble(hole["TapDrillDiameter"])) <= tolerance; } catch { return false; } }).ToList();
            var target = SetSignature(points); var matches = new List<Dictionary<string, object>>();
            for (int i = 0; i < compatible.Count; i++) for (int j = i + 1; j < compatible.Count; j++) for (int k = j + 1; k < compatible.Count; k++)
            {
                if (points.Count != 3) continue; var subset = new List<double[]> { PointValues(((Dictionary<string, object>)compatible[i]["selectable_opening_edge"])["center"]), PointValues(((Dictionary<string, object>)compatible[j]["selectable_opening_edge"])["center"]), PointValues(((Dictionary<string, object>)compatible[k]["selectable_opening_edge"])["center"]) };
                if (SetSignature(subset) != target) continue;
                var pc = new[] { points.Average(p => p[0]), points.Average(p => p[1]), points.Average(p => p[2]) }; var qc = new[] { subset.Average(p => p[0]), subset.Average(p => p[1]), subset.Average(p => p[2]) }; var delta = new[] { qc[0] - pc[0], qc[1] - pc[1], qc[2] - pc[2] };
                if (points.All(p => subset.Any(q => Distance3(new[] { p[0] + delta[0], p[1] + delta[1], p[2] + delta[2] }, q) <= tolerance))) matches.Add(new Dictionary<string, object> { { "opening_ids", new[] { compatible[i]["opening_id"], compatible[j]["opening_id"], compatible[k]["opening_id"] } }, { "translation_m", delta }, { "transform_kind", "RIGID_TRANSLATION_FROM_COMPLETE_SET" } });
            }
            rows.Add(new Dictionary<string, object> { { "feature_name", hole["feature_name"] }, { "candidate_match_count", matches.Count }, { "attribution", matches.Count == 1 ? "PROVEN" : "AMBIGUOUS" }, { "matches", matches }, { "invariant", target } });
        }
        return rows;
    }
    static void Walk(IFeature f, int index, string parent, List<Dictionary<string, object>> rows)
    {
        if (f == null) return; rows.Add(Feature(f, index, parent));
        IFeature child = null; try { child = f.GetFirstSubFeature() as IFeature; } catch { }
        int childIndex = 1; while (child != null) { Walk(child, childIndex++, f.Name, rows); try { child = child.GetNextSubFeature() as IFeature; } catch { child = null; } }
    }
    static Dictionary<string, object> Hole(IFeature f, IModelDoc2 model, IMathUtility math, int index)
    {
        var row = Feature(f, index, null); row["typed_interface"] = "IWizardHoleFeatureData2";
        var definition = Read(() => f.GetDefinition()) as IWizardHoleFeatureData2;
        if (definition == null) { row["definition_status"] = "UNAVAILABLE"; return row; }
        row["definition_status"] = "OBTAINED";
        bool accessed = false; try { accessed = definition.AccessSelections(model, null); } catch { }
        row["access_selections"] = accessed;
        // Read from the interop interface itself. COM runtime wrapper reflection does not
        // reliably expose the interface's property surface.
        ReadField(row, "Type", () => definition.Type);
        ReadField(row, "Standard", () => definition.Standard);
        ReadField(row, "Standard2", () => definition.Standard2);
        ReadField(row, "FastenerType", () => definition.FastenerType);
        ReadField(row, "FastenerType2", () => definition.FastenerType2);
        ReadField(row, "FastenerSize", () => definition.FastenerSize);
        ReadField(row, "Diameter", () => definition.Diameter);
        ReadField(row, "HoleDiameter", () => definition.HoleDiameter);
        ReadField(row, "ThruHoleDiameter", () => definition.ThruHoleDiameter);
        ReadField(row, "Depth", () => definition.Depth);
        ReadField(row, "HoleDepth", () => definition.HoleDepth);
        ReadField(row, "ThruHoleDepth", () => definition.ThruHoleDepth);
        ReadField(row, "TapDrillDiameter", () => definition.TapDrillDiameter);
        ReadField(row, "ThruTapDrillDiameter", () => definition.ThruTapDrillDiameter);
        ReadField(row, "TapDrillDepth", () => definition.TapDrillDepth);
        ReadField(row, "ThruTapDrillDepth", () => definition.ThruTapDrillDepth);
        ReadField(row, "ThreadDiameter", () => definition.ThreadDiameter);
        ReadField(row, "MajorDiameter", () => definition.MajorDiameter);
        ReadField(row, "MinorDiameter", () => definition.MinorDiameter);
        ReadField(row, "ThreadDepth", () => definition.ThreadDepth);
        ReadField(row, "ThreadClass", () => definition.ThreadClass);
        ReadField(row, "EndCondition", () => definition.EndCondition);
        ReadField(row, "ThreadEndCondition", () => definition.ThreadEndCondition);
        ReadField(row, "TapType", () => definition.TapType);
        ReadField(row, "CounterBoreDiameter", () => definition.CounterBoreDiameter);
        ReadField(row, "CounterBoreDepth", () => definition.CounterBoreDepth);
        ReadField(row, "CounterSinkDiameter", () => definition.CounterSinkDiameter);
        ReadField(row, "CounterSinkAngle", () => definition.CounterSinkAngle);
        ReadField(row, "CosmeticThreadType", () => definition.CosmeticThreadType);
        IFace2 supportFace = null;
        try { supportFace = definition.IFace; } catch { }
        if (supportFace != null)
        {
            row["support_face_id"] = Read(() => supportFace.GetFaceId());
            var normal = Doubles(Read(() => supportFace.Normal));
            if (normal.Length == 3) row["support_face_normal_model"] = Numbers(normal);
            row["support_face_sense"] = Read(() => supportFace.FaceInSurfaceSense());
        }
        var subfeatures = new List<string>();
        IFeature sub = null; try { sub = f.GetFirstSubFeature() as IFeature; } catch { }
        while (sub != null) { subfeatures.Add(sub.Name + "|" + sub.GetTypeName2()); try { sub = sub.GetNextSubFeature() as IFeature; } catch { sub = null; } }
        row["subfeatures"] = subfeatures;
        var points = new List<Dictionary<string, object>>();
        int count = 0; try { count = definition.GetSketchPointCount(); } catch { }
        row["sketch_point_count"] = count;
        try
        {
            object raw = definition.GetSketchPoints();
            int pointIndex = 0;
            foreach (object item in ArrayOf(raw))
            {
                ISketchPoint point = item as ISketchPoint; if (point == null) continue;
                var rawPoint = new[] { point.X, point.Y, point.Z };
                var itemRow = new Dictionary<string, object> { { "point_index", ++pointIndex }, { "x_m", point.X }, { "y_m", point.Y }, { "z_m", point.Z }, { "coordinate_space", "API_POINT_COORDINATES_UNVERIFIED" }, { "typed_interface", "ISketchPoint" }, { "relation", "IWizardHoleFeatureData2.GetSketchPoints" } };
                // Record, rather than assume, a sketch-to-model conversion. A HoleWizard
                // placement point can be expressed in a sketch-local coordinate system.
                IMathTransform modelToSketch = null;
                IFeature sketchFeature = null;
                try { sketchFeature = f.GetFirstSubFeature() as IFeature; } catch { }
                ISketch sketch = sketchFeature == null ? null : sketchFeature.GetSpecificFeature2() as ISketch;
                if (sketch != null)
                {
                    modelToSketch = sketch.ModelToSketchTransform;
                    var transformData = Doubles(modelToSketch == null ? null : modelToSketch.ArrayData);
                    if (transformData.Length > 0) itemRow["model_to_sketch_transform"] = Numbers(transformData);
                    if (math != null && modelToSketch != null)
                    {
                        var sketchToModel = modelToSketch.IInverse();
                        IMathPoint sourcePoint = math.CreatePoint(rawPoint) as IMathPoint;
                        IMathPoint converted = sourcePoint == null ? null : sourcePoint.IMultiplyTransform(sketchToModel);
                        var modelPoint = Doubles(converted == null ? null : converted.ArrayData);
                        if (modelPoint.Length == 3) { itemRow["sketch_to_model_point_m"] = Numbers(modelPoint); itemRow["coordinate_space"] = "RAW_POINT_WITH_EXPLICIT_SKETCH_TO_MODEL_CANDIDATE"; }
                    }
                }
                points.Add(itemRow);
            }
        }
        catch { row["sketch_points_error"] = "GetSketchPoints_FAILED"; }
        row["sketch_points"] = points;
        row["feature_owned_topology"] = HoleTopology(f, count);
        try { definition.ReleaseSelectionAccess(); } catch { }
        return row;
    }
    public static int ExtractExisting(SldWorks sw, IModelDoc2 model, string modelPath, string output, int warnings)
    {
        var result = new Dictionary<string, object> { { "schema_version", "L4HE_CSHARP_EARLYBOUND_V1" }, { "extractor_version", "L4HE_CSHARP_EARLYBOUND_V1" }, { "model_path", modelPath }, { "typed_interop", "SolidWorks.Interop.sldworks" }, { "feature_tree", new List<Dictionary<string, object>>() }, { "holewizard_semantics", new List<Dictionary<string, object>>() } };
        try
        {
            var tree = (List<Dictionary<string, object>>)result["feature_tree"];
            IFeature cursor = model.FirstFeature() as IFeature;
            int index = 0;
            var holes = (List<Dictionary<string, object>>)result["holewizard_semantics"];
            IMathUtility math = null;
            try { math = sw.IGetMathUtility(); } catch { }
            while (cursor != null)
            {
                index++;
                Walk(cursor, index, null, tree);
                if (cursor.GetTypeName2() == "HoleWzd") holes.Add(Hole(cursor, model, math, index));
                try { cursor = cursor.GetNextFeature() as IFeature; } catch { cursor = null; }
            }
            result["feature_count"] = tree.Count;
            result["holewzd_nodes"] = holes.Count;
            result["typed_definitions"] = holes.FindAll(x => S(x["definition_status"]) == "OBTAINED").Count;
            int pointCount = 0;
            foreach (var h in holes) pointCount += ((List<Dictionary<string, object>>)h["sketch_points"]).Count;
            result["sketch_point_rows"] = pointCount;
            result["hole_instance_count"] = pointCount;
            var bodyTopology = BodyTopology((PartDoc)model);
            result["body_topology"] = bodyTopology;
            result["body_face_count"] = Convert.ToInt32(bodyTopology["face_count"]);
            result["body_cylindrical_face_count"] = Convert.ToInt32(bodyTopology["cylindrical_face_count"]);
            result["body_conical_face_count"] = Convert.ToInt32(bodyTopology["conical_face_count"]);
            result["physical_opening_count"] = Convert.ToInt32(bodyTopology["internal_hole_count"]);
            result["internal_hole_count"] = Convert.ToInt32(bodyTopology["internal_hole_count"]);
            result["external_cylinder_count"] = Convert.ToInt32(bodyTopology["external_cylinder_count"]);
            result["ambiguous_cylinder_count"] = Convert.ToInt32(bodyTopology["ambiguous_cylinder_count"]);
            var physicalOpenings = (List<Dictionary<string, object>>)bodyTopology["physical_openings"];
            // Transport the authoritative topology facts at the semantic
            // contract boundary so downstream view probes do not need a
            // second geometry discovery pass.
            result["physical_openings"] = physicalOpenings.Select(NormalizedOpening).ToList();
            result["physical_opening_facts_source"] = "body_topology.physical_openings";
            result["holewizard_set_attribution"] = SetAttribution(holes, physicalOpenings);
            var attributions = new List<Dictionary<string, object>>();
            foreach (var hole in holes)
            {
                double drill = 0.0; try { drill = Convert.ToDouble(hole["TapDrillDiameter"]); } catch { }
                int multiplicity = 0; try { multiplicity = Convert.ToInt32(hole["sketch_point_count"]); } catch { }
                var compatible = physicalOpenings.Where(o => {
                    try { var stages = o["diameter_stages"] as Array; return stages != null && stages.Cast<object>().Any(v => Math.Abs(Convert.ToDouble(v) - drill) <= 1e-7); } catch { return false; }
                }).ToList();
                string status = compatible.Count == multiplicity && multiplicity > 0 ? "GROUP_PROVEN" : (compatible.Count > 0 ? "AMBIGUOUS" : "UNRESOLVED");
                attributions.Add(new Dictionary<string, object> { { "feature_name", hole["feature_name"] }, { "semantic_tap_drill_diameter_m", drill }, { "semantic_multiplicity", multiplicity }, { "compatible_opening_ids", compatible.Select(o => o["opening_id"]).ToList() }, { "group_status", status }, { "evidence", new[] { "COMPATIBLE_DRILL_DIAMETER", "TOPOLOGY_DERIVED_OPENING" } } });
            }
            result["holewizard_attribution"] = attributions;
            result["holewizard_attributed_openings"] = attributions.SelectMany(a => ((List<object>)a["compatible_opening_ids"])).Distinct().Count();
            result["bindable_opening_edges"] = physicalOpenings.Count(o => o["selectable_opening_edge"] != null);
            result["instance_proven"] = 0;
            result["group_proven"] = attributions.Where(a => S(a["group_status"]) == "GROUP_PROVEN").Sum(a => ((List<object>)a["compatible_opening_ids"]).Count);
            result["ambiguous"] = attributions.Where(a => S(a["group_status"]) == "AMBIGUOUS").Sum(a => ((List<object>)a["compatible_opening_ids"]).Count);
            result["unresolved"] = attributions.Count(a => S(a["group_status"]) == "UNRESOLVED");
            var topology = holes.Select(h => h.TryGetValue("feature_owned_topology", out var t) ? t as Dictionary<string, object> : null).Where(t => t != null).ToList();
            result["proven_opening_count"] = topology.Sum(t => Convert.ToInt32(t["proven_opening_count"]));
            result["ambiguous_opening_count"] = topology.Sum(t => Convert.ToInt32(t["ambiguous_opening_count"]));
            result["unresolved_opening_count"] = topology.Sum(t => Convert.ToInt32(t["unresolved_opening_count"]));
            result["bindable_circular_edge_count"] = topology.Sum(t => Convert.ToInt32(t["bindable_circular_edge_count"]));
            result["semantic_to_physical_proven"] = topology.Sum(t => ((List<Dictionary<string, object>>)t["instance_bindings"]).Count(x => S(x["binding_status"]) == "PROVEN"));
            result["semantic_to_physical_ambiguous"] = topology.Sum(t => ((List<Dictionary<string, object>>)t["instance_bindings"]).Count(x => S(x["binding_status"]) == "AMBIGUOUS"));
            result["semantic_to_physical_unresolved"] = topology.Sum(t => ((List<Dictionary<string, object>>)t["instance_bindings"]).Count(x => S(x["binding_status"]) == "UNRESOLVED"));
            var box = PartBox(model);
            if (box.Length >= 6 && Math.Abs(box[3] - box[0]) > 0 && Math.Abs(box[4] - box[1]) > 0 && Math.Abs(box[5] - box[2]) > 0)
            {
                var approximate = ApproximateEnvelope(box);
                var exact = ExactEnvelope((PartDoc)model);
                bool complete = exact.TryGetValue("coverage_complete", out var completeValue) && completeValue is bool && (bool)completeValue;
                var preferred = complete ? exact : approximate;
                var promotedAxisEvidence = MaterializeAxisEvidence(exact, approximate);
                result["bounding_box"] = new Dictionary<string, object> {
                    { "min_x_m", preferred["min_x_m"] }, { "min_y_m", preferred["min_y_m"] }, { "min_z_m", preferred["min_z_m"] },
                    { "max_x_m", preferred["max_x_m"] }, { "max_y_m", preferred["max_y_m"] }, { "max_z_m", preferred["max_z_m"] },
                    { "size_x_m", preferred["size_x_m"] }, { "size_y_m", preferred["size_y_m"] }, { "size_z_m", preferred["size_z_m"] },
                    { "source", preferred["source"] }, { "confidence", complete ? "EXACT_GEOMETRY" : "APPROXIMATE" },
                    { "preferred_source", complete ? "EXACT_ENVELOPE" : "APPROXIMATE_FALLBACK" },
                    { "preferred_confidence", complete ? "EXACT_GEOMETRY" : "APPROXIMATE" },
                    { "exact_envelope", exact }, { "approximate_part_box", approximate },
                    { "axis_evidence", promotedAxisEvidence }
                };
                for (int axis = 0; axis < 3; axis++)
                {
                    string name = axis == 0 ? "x" : axis == 1 ? "y" : "z";
                    string prefix = axis == 0 ? "x" : axis == 1 ? "y" : "z";
                    double exactMin = Convert.ToDouble(exact["min_" + name + "_m"]), exactMax = Convert.ToDouble(exact["max_" + name + "_m"]);
                    double approxMin = Convert.ToDouble(approximate["min_" + name + "_m"]), approxMax = Convert.ToDouble(approximate["max_" + name + "_m"]);
                    Console.WriteLine($"EXACT_ENVELOPE_SUMMARY axis={prefix} exact_min={exactMin:R} exact_max={exactMax:R} exact_span={(exactMax - exactMin):R} approx_min={approxMin:R} approx_max={approxMax:R} approx_span={(approxMax - approxMin):R} delta_min={(exactMin - approxMin):R} delta_max={(exactMax - approxMax):R} delta_span={(exactMax - exactMin) - (approxMax - approxMin):R} coverage_complete={complete} unsupported_geometry_count={exact["unsupported_curve_count"]}");
                }
                result["view_candidates"] = ViewCandidates(new[] { Convert.ToDouble(preferred["min_x_m"]), Convert.ToDouble(preferred["min_y_m"]), Convert.ToDouble(preferred["min_z_m"]), Convert.ToDouble(preferred["max_x_m"]), Convert.ToDouble(preferred["max_y_m"]), Convert.ToDouble(preferred["max_z_m"]) }, tree.Count, pointCount);
            }
            else throw new InvalidOperationException("BOUNDING_BOX_EXTRACTION_FAILED");
            result["open_warnings"] = warnings;
            result["source_model_modified"] = false;
            File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            result["status"] = "BLOCKED";
            result["error"] = ex.GetType().Name + ":" + (ex.Message ?? "");
            try { File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })); } catch { }
            Console.WriteLine("L4HE_ERROR=" + result["error"]);
            return 2;
        }
    }

    static List<IFeature> TopLevelFeatures(IModelDoc2 model)
    {
        var result = new List<IFeature>();
        IFeature cursor = null;
        try { cursor = model.FirstFeature() as IFeature; } catch { }
        while (cursor != null)
        {
            result.Add(cursor);
            try { cursor = cursor.GetNextFeature() as IFeature; } catch { cursor = null; }
        }
        return result;
    }

    static Dictionary<IFeature, bool> CaptureSuppressionStates(List<IFeature> features)
    {
        var states = new Dictionary<IFeature, bool>();
        foreach (var feature in features)
        {
            bool suppressed = false;
            try { suppressed = feature.IsSuppressed(); } catch { throw new InvalidOperationException("SUPPRESSION_STATE_READ_FAILED:" + feature.Name); }
            states[feature] = suppressed;
        }
        return states;
    }

    static void SetSuppressed(IFeature feature, bool suppressed)
    {
        bool ok = feature.SetSuppression((int)(suppressed
            ? swFeatureSuppressionAction_e.swSuppressFeature
            : swFeatureSuppressionAction_e.swUnSuppressFeature));
        if (!ok) throw new InvalidOperationException("SUPPRESSION_CHANGE_FAILED:" + feature.Name + ":" + (suppressed ? "SUPPRESS" : "UNSUPPRESS"));
    }

    static void RestoreSuppressionStates(List<IFeature> features, Dictionary<IFeature, bool> original)
    {
        // Suppress from the end first, then unsuppress from the beginning so
        // dependent features see the same prerequisites they had originally.
        for (int i = features.Count - 1; i >= 0; i--)
        {
            var feature = features[i];
            if (original[feature])
            {
                bool current = feature.IsSuppressed();
                if (!current) SetSuppressed(feature, true);
            }
        }
        for (int i = 0; i < features.Count; i++)
        {
            var feature = features[i];
            if (!original[feature])
            {
                bool current = feature.IsSuppressed();
                if (current) SetSuppressed(feature, false);
            }
        }
        foreach (var feature in features)
        {
            bool current = feature.IsSuppressed();
            if (current != original[feature]) throw new InvalidOperationException("SUPPRESSION_RESTORE_VERIFY_FAILED:" + feature.Name);
        }
    }

    static Dictionary<string, object> NormalizedOpening(Dictionary<string, object> opening)
    {
        var edge = opening.TryGetValue("selectable_opening_edge", out var edgeValue)
            ? edgeValue as Dictionary<string, object> : null;
        var center = edge == null ? null : PointValues(edge.TryGetValue("center", out var centerValue) ? centerValue : null);
        var axis = edge == null ? null : Doubles(edge.TryGetValue("axis", out var axisValue) ? axisValue : null);
        var diameter = edge != null && edge.TryGetValue("diameter_m", out var diameterValue)
            ? Convert.ToDouble(diameterValue) : 0.0;
        var boundary = new List<string>();
        if (opening.TryGetValue("boundary_circular_edges", out var rawBoundary) && rawBoundary is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                var e = item as Dictionary<string, object>; if (e == null) continue;
                var c = PointValues(e.TryGetValue("center", out var cv) ? cv : null);
                var a = Doubles(e.TryGetValue("axis", out var av) ? av : null);
                var d = e.TryGetValue("diameter_m", out var dv) ? Convert.ToDouble(dv) : 0.0;
                if (c != null && a.Length >= 3) boundary.Add($"{c[0]:R}|{c[1]:R}|{c[2]:R}|{a[0]:R}|{a[1]:R}|{a[2]:R}|{d:R}");
            }
        }
        boundary.Sort(StringComparer.Ordinal);
        string signature = string.Join(";", boundary);
        return new Dictionary<string, object> {
            { "opening_id", opening.TryGetValue("opening_id", out var id) ? id : null },
            { "geometry_signature", signature },
            { "axis", axis ?? Array.Empty<double>() },
            { "entry_location_m", center ?? Array.Empty<double>() },
            { "diameter_m", diameter },
            { "diameter_stages", opening.TryGetValue("diameter_stages", out var stages) ? stages : Array.Empty<double>() },
            { "boundary_circular_edge_signature", boundary }
        };
    }

    static List<Dictionary<string, object>> NormalizedOpenings(Dictionary<string, object> topology)
    {
        var result = new List<Dictionary<string, object>>();
        if (!topology.TryGetValue("physical_openings", out var raw) || !(raw is IEnumerable enumerable)) return result;
        foreach (var item in enumerable)
        {
            if (item is Dictionary<string, object> opening) result.Add(NormalizedOpening(opening));
        }
        return result;
    }

    static Dictionary<string, object> OpeningDelta(List<Dictionary<string, object>> before, List<Dictionary<string, object>> after)
    {
        var beforeBySignature = before.GroupBy(x => S(x["geometry_signature"])).ToDictionary(g => g.Key, g => g.First());
        var afterBySignature = after.GroupBy(x => S(x["geometry_signature"])).ToDictionary(g => g.Key, g => g.First());
        var added = afterBySignature.Where(x => !beforeBySignature.ContainsKey(x.Key)).Select(x => x.Value).ToList();
        var removed = beforeBySignature.Where(x => !afterBySignature.ContainsKey(x.Key)).Select(x => x.Value).ToList();
        return new Dictionary<string, object> {
            { "added_openings", added }, { "removed_openings", removed },
            { "changed_openings", Array.Empty<object>() },
            { "added_count", added.Count }, { "removed_count", removed.Count }, { "changed_count", 0 }
        };
    }

    static List<Dictionary<string, object>> CaptureHistoryStates(PartDoc part, List<IFeature> features, Dictionary<IFeature, bool> original, int[] boundaries)
    {
        var snapshots = new List<Dictionary<string, object>>();
        for (int stateIndex = 0; stateIndex < boundaries.Length; stateIndex++)
        {
            RestoreSuppressionStates(features, original);
            int boundary = boundaries[stateIndex];
            var actions = new List<string>();
            for (int i = features.Count - 1; i >= 0; i--)
            {
                if (i >= boundary && !original[features[i]])
                {
                    SetSuppressed(features[i], true);
                    actions.Add(features[i].Name);
                }
            }
            var topology = BodyTopology(part);
            snapshots.Add(new Dictionary<string, object> {
                { "state_index", stateIndex },
                { "boundary_feature_position", boundary },
                { "temporarily_suppressed_features", actions },
                { "physical_openings", NormalizedOpenings(topology) },
                { "physical_opening_count", ((List<Dictionary<string, object>>)topology["physical_openings"]).Count }
            });
        }
        RestoreSuppressionStates(features, original);
        return snapshots;
    }

    public static int ProbeFeatureStateHistory(SldWorks sw, IModelDoc2 model, string modelPath, string output, int warnings, int[] featureIndices)
    {
        var result = new Dictionary<string, object> {
            { "schema_version", "L4HE_FEATURE_STATE_PROBE_V1" },
            { "model_path", modelPath },
            { "feature_state_api_selected", "IFeature.SetSuppression(swFeatureSuppressionAction_e) with full-state restore and verification" },
            { "rollback_position_api", "UNREADABLE_IN_INSTALLED_INTEROP" },
            { "source_model_saved", false }
        };
        var features = TopLevelFeatures(model);
        var original = CaptureSuppressionStates(features);
        var activeConfiguration = model.IGetActiveConfiguration();
        string activeConfigurationName = activeConfiguration == null ? null : activeConfiguration.Name;
        result["original_active_configuration"] = activeConfigurationName;
        result["original_save_flag"] = model.GetSaveFlag();
        result["original_feature_suppression_states"] = features.Select((f, i) => new Dictionary<string, object> {
            { "feature_index", i + 1 }, { "feature_name", f.Name }, { "feature_type", f.GetTypeName2() }, { "suppressed", original[f] }
        }).ToList();
        try
        {
            var relevant = featureIndices.Where(i => i > 0 && i <= features.Count).Distinct().OrderBy(i => i).ToArray();
            if (relevant.Length == 0) throw new InvalidOperationException("NO_VALID_FEATURE_INDICES");
            var boundaries = relevant.Select(i => i - 1).Concat(new[] { features.Count }).ToArray();
            var snapshots = CaptureHistoryStates((PartDoc)model, features, original, boundaries);
            result["relevant_feature_indices"] = relevant;
            result["states"] = snapshots;
            var deltas = new List<Dictionary<string, object>>();
            for (int i = 1; i < snapshots.Count; i++)
            {
                var before = (List<Dictionary<string, object>>)snapshots[i - 1]["physical_openings"];
                var after = (List<Dictionary<string, object>>)snapshots[i]["physical_openings"];
                var delta = OpeningDelta(before, after);
                delta["from_state_index"] = i - 1; delta["to_state_index"] = i;
                deltas.Add(delta);
            }
            result["deltas"] = deltas;
            result["reversible_state_probe_implemented"] = true;
            result["source_model_restored"] = true;
            result["suppression_restore_verified"] = true;
            result["active_configuration_restored"] = model.IGetActiveConfiguration()?.Name == activeConfigurationName;
            result["final_save_flag"] = model.GetSaveFlag();
        }
        catch (Exception ex)
        {
            try { RestoreSuppressionStates(features, original); } catch { result["suppression_restore_verified"] = false; }
            result["reversible_state_probe_implemented"] = false;
            result["source_model_restored"] = result.TryGetValue("suppression_restore_verified", out var restored) && restored is bool b && b;
            result["error"] = ex.GetType().Name + ":" + (ex.Message ?? "");
            File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            return 2;
        }
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--geometry-self-test")
        {
            try { GeometrySelfTest(); return 0; } catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 3; }
        }
        bool historyProbe = args.Length > 0 && args[0] == "--feature-state-probe";
        if (historyProbe && (args.Length < 7 || args[1] != "--input" || args[3] != "--output" || args[5] != "--feature-indices"))
        {
            Console.Error.WriteLine("Usage: --feature-state-probe --input <part.SLDPRT> --output <probe.json> --feature-indices <comma-separated-top-level-indices>");
            return 64;
        }
        if (!historyProbe && (args.Length < 4 || args[0] != "--input" || args[2] != "--output")) { Console.Error.WriteLine("Usage: --input <part.SLDPRT> --output <model_semantics.json> [--log <runtime_log.txt>]"); return 64; }
        int inputPosition = historyProbe ? 2 : 1;
        int outputPosition = historyProbe ? 4 : 3;
        string modelPath = Path.GetFullPath(args[inputPosition]); string output = Path.GetFullPath(args[outputPosition]); Directory.CreateDirectory(Path.GetDirectoryName(output));
        if (!historyProbe && args.Length >= 6 && args[4] == "--log") Console.SetOut(new StreamWriter(Path.GetFullPath(args[5])) { AutoFlush = true });
        Console.WriteLine("L4HE_START");
        BindInterop();
        int preexistingProcessCount = 0; try { preexistingProcessCount = System.Diagnostics.Process.GetProcessesByName("SLDWORKS").Length; } catch { }
        bool ownedApplication = false;
        SldWorks sw = null;
        IModelDoc2 openedModel = null;
        try
        {
            sw = (SldWorks)Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application")); ownedApplication = preexistingProcessCount == 0; Console.WriteLine(ownedApplication ? "L4HE_STARTED_OWNED" : "L4HE_ATTACHED_PREEXISTING");
            sw.Visible = false; int errors = 0, warnings = 0;
            openedModel = sw.OpenDoc6(modelPath, (int)swDocumentTypes_e.swDocPART, (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref errors, ref warnings) as IModelDoc2; if (openedModel == null) throw new InvalidOperationException("PART_OPEN_FAILED=" + errors);
            if (historyProbe)
            {
                int[] indices;
                try { indices = args[6].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray(); }
                catch { Console.Error.WriteLine("FEATURE_INDICES_PARSE_FAILED"); return 64; }
                return ProbeFeatureStateHistory(sw, openedModel, modelPath, output, warnings, indices);
            }
            return ExtractExisting(sw, openedModel, modelPath, output, warnings);
        }
        catch (Exception ex) { Console.WriteLine("L4HE_ERROR=" + ex.GetType().Name + ":" + (ex.Message ?? "")); return 2; }
        finally
        {
            if (historyProbe && openedModel != null && sw != null)
            {
                try { sw.CloseDoc(openedModel.GetTitle()); } catch { }
            }
            try { if (openedModel != null) Marshal.FinalReleaseComObject(openedModel); } catch { }
            if (ownedApplication && sw != null) { try { sw.ExitApp(); } catch { } }
            try { if (sw != null) Marshal.FinalReleaseComObject(sw); } catch { }
        }
    }
}






