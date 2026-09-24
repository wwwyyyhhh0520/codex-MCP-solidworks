param(
    [Parameter(Mandatory = $true)] [string]$PartPath,
    [Parameter(Mandatory = $true)] [string]$OutputPath
)

$ErrorActionPreference = "Stop"
$interop = 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SolidWorks.Interop.sldworks.dll'
if (-not (Test-Path -LiteralPath $interop)) { throw "SOLIDWORKS_INTEROP_ASSEMBLY_NOT_FOUND" }
Add-Type -Path $interop
$genericCollections = [System.Collections.Generic.List[object]].Assembly.Location
$runtimeAssembly = [System.Runtime.GCSettings].Assembly.Location
$collectionsAssembly = [System.Collections.ArrayList].Assembly.Location

$source = @'
using System;
using System.Collections;
using SolidWorks.Interop.sldworks;

public static class PartCylinderAxesEarlyBound
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

    public static object Probe(string partPath)
    {
        var swType = Type.GetTypeFromProgID("SldWorks.Application");
        if (swType == null) throw new InvalidOperationException("SOLIDWORKS_PROGID_NOT_FOUND");
        var sw = (SldWorks)Activator.CreateInstance(swType);
        sw.Visible = true;
        int errors = 0, warnings = 0;
        var doc = sw.OpenDoc6(partPath, 1, 1, "", ref errors, ref warnings) as IPartDoc;
        if (doc == null) throw new InvalidOperationException("PART_OPEN_FAILED=" + errors);

        var rows = new ArrayList();
        int faceCount = 0;
        foreach (var rawBody in AsArray(doc.GetBodies2(0, false)))
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
                rows.Add(new {
                    radius_m = cp[6],
                    axis_origin_m = new double[] { cp[0], cp[1], cp[2] },
                    axis_unit_vector = new double[] { cp[3], cp[4], cp[5] },
                    surface_identity = surface.Identity()
                });
            }
        }
        return new {
            api_route = "early_bound_dotnet",
            part_path = partPath,
            face_count = faceCount,
            cylinder_axis_count = rows.Count,
            cylinder_axes = rows
        };
    }
}
'@

Add-Type -TypeDefinition $source -ReferencedAssemblies @($interop, $genericCollections, $runtimeAssembly, $collectionsAssembly)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
$result = [PartCylinderAxesEarlyBound]::Probe([IO.Path]::GetFullPath($PartPath))
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Output "FACE_COUNT=$($result.face_count)"
Write-Output "CYLINDER_AXIS_COUNT=$($result.cylinder_axis_count)"
Write-Output "OUTPUT_PATH=$OutputPath"
