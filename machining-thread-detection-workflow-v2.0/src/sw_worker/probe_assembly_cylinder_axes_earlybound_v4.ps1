[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$AssemblyPath,
    [Parameter(Mandatory = $true)] [string]$OutputPath,
    [int]$MaxCylindersPerOccurrence = 80
)

$ErrorActionPreference = "Stop"

function Find-SolidWorksInterop {
    foreach ($root in @("C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS", "C:\Program Files\SOLIDWORKS Corp")) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        $found = Get-ChildItem -LiteralPath $root -Filter "SolidWorks.Interop.sldworks.dll" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    throw "SOLIDWORKS_INTEROP_ASSEMBLY_NOT_FOUND"
}

$assemblyFile = [IO.Path]::GetFullPath($AssemblyPath)
if (-not (Test-Path -LiteralPath $assemblyFile -PathType Leaf)) { throw "ASSEMBLY_NOT_FOUND=$assemblyFile" }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
$interop = Find-SolidWorksInterop
Add-Type -Path $interop

$source = @'
using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;

public static class AssemblyCylinderAxesEarlyBoundV4
{
    private static object[] AsArray(object value)
    {
        var array = value as Array;
        if (array == null) return new object[0];
        var result = new object[array.Length];
        array.CopyTo(result, 0);
        return result;
    }

    private static double[] Numbers(object value)
    {
        var input = value as Array;
        if (input == null) return new double[0];
        var result = new double[input.Length];
        for (int i = 0; i < input.Length; i++) result[i] = Convert.ToDouble(input.GetValue(i));
        return result;
    }

    private static double[] PointData(IMathPoint point)
    {
        if (point == null) return null;
        var data = point.ArrayData as double[];
        return data != null && data.Length >= 3 ? new double[] { data[0], data[1], data[2] } : null;
    }

    private static double Length(double[] v)
    {
        return Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
    }

    public static object Probe(string assemblyPath, int maxCylinders)
    {
        var swType = Type.GetTypeFromProgID("SldWorks.Application");
        if (swType == null) throw new InvalidOperationException("SOLIDWORKS_PROGID_NOT_FOUND");
        var sw = (SldWorks)Activator.CreateInstance(swType);
        sw.Visible = true;
        int errors = 0, warnings = 0;
        var doc = sw.OpenDoc6(assemblyPath, 2, 1, "", ref errors, ref warnings) as IAssemblyDoc;
        if (doc == null) throw new InvalidOperationException("ASSEMBLY_OPEN_FAILED=" + errors);
        var math = sw.GetMathUtility() as IMathUtility;
        if (math == null) throw new InvalidOperationException("MATH_UTILITY_UNAVAILABLE");

        var rows = new List<Dictionary<string, object>>();
        int occurrenceCount = 0, openedParts = 0, faceCount = 0, cylinderCount = 0, transformUnavailable = 0;
        foreach (var rawComponent in AsArray(doc.GetComponents(false)))
        {
            var component = rawComponent as IComponent2;
            if (component == null) continue;
            string path = "";
            try { path = component.GetPathName() ?? ""; } catch { }
            if (!path.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase)) continue;
            occurrenceCount++;
            IMathTransform transform = null;
            try { transform = component.Transform2 as IMathTransform; } catch { }
            if (transform == null) { transformUnavailable++; continue; }

            IModelDoc2 partModel = null;
            bool closeAfter = false;
            try { partModel = component.GetModelDoc2() as IModelDoc2; } catch { }
            if (partModel == null)
            {
                int partErrors = 0, partWarnings = 0;
                partModel = sw.OpenDoc6(path, 1, 1, "", ref partErrors, ref partWarnings) as IModelDoc2;
                closeAfter = partModel != null;
            }
            var part = partModel as IPartDoc;
            if (part == null) continue;
            openedParts++;
            try
            {
                int kept = 0;
                foreach (var rawBody in AsArray(part.GetBodies2(0, false)))
                {
                    var body = rawBody as IBody2;
                    if (body == null) continue;
                    foreach (var rawFace in AsArray(body.GetFaces()))
                    {
                        var face = rawFace as IFace2;
                        if (face == null) continue;
                        faceCount++;
                        var surface = face.GetSurface() as ISurface;
                        if (surface == null || !surface.IsCylinder()) continue;
                        var cp = Numbers(surface.CylinderParams);
                        if (cp.Length < 7) continue;
                        var localOrigin = new double[] { cp[0], cp[1], cp[2] };
                        var localAxis = new double[] { cp[3], cp[4], cp[5] };
                        var localEnd = new double[] { cp[0] + cp[3], cp[1] + cp[4], cp[2] + cp[5] };
                        var worldOrigin = PointData((math.CreatePoint(localOrigin) as IMathPoint).MultiplyTransform(transform) as IMathPoint);
                        var worldEnd = PointData((math.CreatePoint(localEnd) as IMathPoint).MultiplyTransform(transform) as IMathPoint);
                        if (worldOrigin == null || worldEnd == null) continue;
                        var worldAxis = new double[] { worldEnd[0] - worldOrigin[0], worldEnd[1] - worldOrigin[1], worldEnd[2] - worldOrigin[2] };
                        var length = Length(worldAxis);
                        if (length <= 1e-12) continue;
                        for (int i = 0; i < 3; i++) worldAxis[i] /= length;
                        var row = new Dictionary<string, object>();
                        row["occurrence"] = component.Name2 ?? "";
                        row["part_path"] = path;
                        row["radius_m"] = cp[6];
                        row["axis_origin_m"] = worldOrigin;
                        row["axis_unit_vector"] = worldAxis;
                        row["surface_identity"] = surface.Identity();
                        rows.Add(row);
                        cylinderCount++;
                        kept++;
                        if (kept >= maxCylinders) break;
                    }
                    if (kept >= maxCylinders) break;
                }
            }
            finally
            {
                if (closeAfter && partModel != null) { try { sw.CloseDoc(partModel.GetTitle()); } catch { } }
            }
        }
        return new Dictionary<string, object> {
            { "api_route", "early_bound_dotnet" },
            { "part_occurrence_count", occurrenceCount },
            { "part_opened_count", openedParts },
            { "face_count", faceCount },
            { "cylinder_axis_count", cylinderCount },
            { "transform_unavailable_count", transformUnavailable },
            { "cylinder_axes", rows }
        };
    }
}
'@

Add-Type -TypeDefinition $source -ReferencedAssemblies $interop
$result = [AssemblyCylinderAxesEarlyBoundV4]::Probe($assemblyFile, $MaxCylindersPerOccurrence)
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Output "PART_OCCURRENCE_COUNT=$($result.part_occurrence_count)"
Write-Output "PART_OPENED_COUNT=$($result.part_opened_count)"
Write-Output "FACE_COUNT=$($result.face_count)"
Write-Output "CYLINDER_AXIS_COUNT=$($result.cylinder_axis_count)"
Write-Output "TRANSFORM_UNAVAILABLE_COUNT=$($result.transform_unavailable_count)"
Write-Output "OUTPUT_PATH=$OutputPath"
Write-Output "ASSEMBLY_CYLINDER_AXES_EARLYBOUND_V4_STATUS=SUCCESS"
