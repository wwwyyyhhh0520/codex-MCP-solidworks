param(
    [Parameter(Mandatory = $true)] [string]$AssemblyPath,
    [Parameter(Mandatory = $true)] [string]$OutputPath,
    [int]$MaxCylindersPerOccurrence = 160
)

$ErrorActionPreference = 'Stop'
function Find-SolidWorksInterop {
    foreach ($root in @('C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS', 'C:\Program Files\SOLIDWORKS Corp')) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        $found = Get-ChildItem -LiteralPath $root -Filter 'SolidWorks.Interop.sldworks.dll' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    throw 'SOLIDWORKS_INTEROP_ASSEMBLY_NOT_FOUND'
}
$assemblyFile = [System.IO.Path]::GetFullPath($AssemblyPath)
if (-not (Test-Path -LiteralPath $assemblyFile -PathType Leaf)) { throw "ASSEMBLY_NOT_FOUND=$assemblyFile" }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
$interop = Find-SolidWorksInterop
# Load from the same location SolidWorks exposes in the successful axis probe.
# Passing a compile-time reference alone is insufficient for runtime binding.
Add-Type -Path $interop

$source = @'
using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;

public static class AssemblyCylinderFaceExtentsEarlyBoundV4
{
    private static object[] AsArray(object value)
    {
        var array = value as Array;
        if (array == null) return new object[0];
        var result = new object[array.Length]; array.CopyTo(result, 0); return result;
    }
    private static double[] Numbers(object value)
    {
        var array = value as Array; if (array == null) return new double[0];
        var result = new double[array.Length];
        for (int i = 0; i < array.Length; i++) result[i] = Convert.ToDouble(array.GetValue(i));
        return result;
    }
    private static double[] PointData(IMathPoint point)
    {
        if (point == null) return null;
        var data = point.ArrayData as double[];
        return data != null && data.Length >= 3 ? new double[] { data[0], data[1], data[2] } : null;
    }
    private static double Dot(double[] a, double[] b) { return a[0]*b[0] + a[1]*b[1] + a[2]*b[2]; }
    private static double Length(double[] a) { return Math.Sqrt(Dot(a, a)); }
    private static double[] World(IMathUtility math, IMathTransform transform, double x, double y, double z)
    { return PointData((math.CreatePoint(new double[] {x,y,z}) as IMathPoint).MultiplyTransform(transform) as IMathPoint); }

    public static object Probe(string assemblyPath, int maxCylinders)
    {
        var swType = Type.GetTypeFromProgID("SldWorks.Application");
        if (swType == null) throw new InvalidOperationException("SOLIDWORKS_PROGID_NOT_FOUND");
        var sw = (SldWorks)Activator.CreateInstance(swType); sw.Visible = true;
        int errors = 0, warnings = 0;
        var doc = sw.OpenDoc6(assemblyPath, 2, 1, "", ref errors, ref warnings) as IAssemblyDoc;
        if (doc == null) throw new InvalidOperationException("ASSEMBLY_OPEN_FAILED=" + errors);
        var math = sw.GetMathUtility() as IMathUtility;
        if (math == null) throw new InvalidOperationException("MATH_UTILITY_UNAVAILABLE");
        var rows = new List<Dictionary<string, object>>();
        int occurrences = 0, opened = 0, faces = 0, cylinders = 0, boxesMissing = 0;
        foreach (var rawComponent in AsArray(doc.GetComponents(false)))
        {
            var component = rawComponent as IComponent2; if (component == null) continue;
            string path = ""; try { path = component.GetPathName() ?? ""; } catch { }
            if (!path.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase)) continue;
            occurrences++;
            IMathTransform transform = null; try { transform = component.Transform2 as IMathTransform; } catch { }
            if (transform == null) continue;
            IModelDoc2 model = null; bool closeAfter = false;
            try { model = component.GetModelDoc2() as IModelDoc2; } catch { }
            if (model == null) { int pe=0,pw=0; model = sw.OpenDoc6(path,1,1,"",ref pe,ref pw) as IModelDoc2; closeAfter = model != null; }
            var part = model as IPartDoc; if (part == null) continue; opened++;
            try
            {
                int kept = 0;
                foreach (var rawBody in AsArray(part.GetBodies2(0, false)))
                {
                    var body = rawBody as IBody2; if (body == null) continue;
                    foreach (var rawFace in AsArray(body.GetFaces()))
                    {
                        var face = rawFace as IFace2; if (face == null) continue; faces++;
                        var surface = face.GetSurface() as ISurface;
                        if (surface == null || !surface.IsCylinder()) continue;
                        var cp = Numbers(surface.CylinderParams); if (cp.Length < 7) continue;
                        var localOrigin = new double[] {cp[0], cp[1], cp[2]};
                        var localEnd = new double[] {cp[0]+cp[3], cp[1]+cp[4], cp[2]+cp[5]};
                        var origin = World(math, transform, localOrigin[0], localOrigin[1], localOrigin[2]);
                        var end = World(math, transform, localEnd[0], localEnd[1], localEnd[2]);
                        if (origin == null || end == null) continue;
                        var axis = new double[] {end[0]-origin[0], end[1]-origin[1], end[2]-origin[2]};
                        var len = Length(axis); if (len <= 1e-12) continue;
                        for (int i=0; i<3; i++) axis[i] /= len;
                        double axialMin = Double.NaN, axialMax = Double.NaN;
                        var box = Numbers(face.GetBox());
                        if (box.Length >= 6)
                        {
                            axialMin = Double.PositiveInfinity; axialMax = Double.NegativeInfinity;
                            for (int ix=0; ix<2; ix++) for (int iy=0; iy<2; iy++) for (int iz=0; iz<2; iz++)
                            {
                                var p = World(math, transform, box[ix==0?0:3], box[iy==0?1:4], box[iz==0?2:5]);
                                if (p == null) continue;
                                var delta = new double[] {p[0]-origin[0],p[1]-origin[1],p[2]-origin[2]};
                                var projection = Dot(delta, axis); axialMin = Math.Min(axialMin, projection); axialMax = Math.Max(axialMax, projection);
                            }
                        }
                        else boxesMissing++;
                        var row = new Dictionary<string, object>();
                        row["occurrence"] = component.Name2 ?? "";
                        row["part_path"] = path;
                        row["radius_m"] = cp[6];
                        row["axis_origin_m"] = origin;
                        row["axis_unit_vector"] = axis;
                        row["axial_min_m"] = Double.IsInfinity(axialMin) ? (object)null : axialMin;
                        row["axial_max_m"] = Double.IsInfinity(axialMax) ? (object)null : axialMax;
                        row["axial_extent_mm"] = Double.IsInfinity(axialMin) ? (object)null : (axialMax-axialMin)*1000.0;
                        row["face_in_surface_sense"] = face.FaceInSurfaceSense();
                        row["surface_identity"] = surface.Identity();
                        rows.Add(row); cylinders++; kept++; if (kept >= maxCylinders) break;
                    }
                    if (kept >= maxCylinders) break;
                }
            }
            finally { if (closeAfter && model != null) { try { sw.CloseDoc(model.GetTitle()); } catch {} } }
        }
        return new Dictionary<string, object> {
            {"api_route", "early_bound_dotnet"}, {"part_occurrence_count", occurrences}, {"part_opened_count", opened},
            {"face_count", faces}, {"cylinder_face_count", cylinders}, {"face_box_missing_count", boxesMissing}, {"cylinder_faces", rows}
        };
    }
}
'@

Add-Type -TypeDefinition $source -ReferencedAssemblies $interop
$result = [AssemblyCylinderFaceExtentsEarlyBoundV4]::Probe($assemblyFile, $MaxCylindersPerOccurrence)
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Output "PART_OCCURRENCE_COUNT=$($result.part_occurrence_count)"
Write-Output "PART_OPENED_COUNT=$($result.part_opened_count)"
Write-Output "FACE_COUNT=$($result.face_count)"
Write-Output "CYLINDER_FACE_COUNT=$($result.cylinder_face_count)"
Write-Output "FACE_BOX_MISSING_COUNT=$($result.face_box_missing_count)"
Write-Output "OUTPUT_PATH=$OutputPath"
Write-Output 'ASSEMBLY_CYLINDER_FACE_EXTENTS_EARLYBOUND_V4_STATUS=SUCCESS'
