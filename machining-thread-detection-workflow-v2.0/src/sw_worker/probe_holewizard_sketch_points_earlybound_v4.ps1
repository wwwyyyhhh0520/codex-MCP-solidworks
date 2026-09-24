[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$AssemblyPath,
    [Parameter(Mandatory = $true)] [string]$OutputPath
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
$assemblyFile = [IO.Path]::GetFullPath($AssemblyPath)
if (-not (Test-Path -LiteralPath $assemblyFile -PathType Leaf)) { throw "ASSEMBLY_NOT_FOUND=$assemblyFile" }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
$interop = Find-SolidWorksInterop
Add-Type -Path $interop

$source = @'
using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;

public static class HoleWizardSketchPointsEarlyBoundV4
{
    private static object[] AsArray(object value)
    {
        var array = value as Array; if (array == null) return new object[0];
        var output = new object[array.Length]; array.CopyTo(output, 0); return output;
    }
    private static string TryString(Func<string> action) { try { return action() ?? ""; } catch { return ""; } }
    private static object TryDouble(Func<double> action) { try { return action(); } catch { return null; } }
    private static double[] PointData(IMathPoint point)
    {
        if (point == null) return null; var values = point.ArrayData as double[];
        return values != null && values.Length >= 3 ? new double[] {values[0], values[1], values[2]} : null;
    }
    // ISketchPoint.X/Y/Z are in the local sketch coordinate system.  Convert
    // sketch -> part model using the inverse of ModelToSketchTransform, then
    // part model -> assembly using the component transform.
    private static double[] TransformPoint(IMathUtility math, IMathTransform componentTransform, ISketchPoint point)
    {
        if (componentTransform == null) return PartModelPoint(math, point);
        if (math == null || point == null) return null;
        try
        {
            var sketch = point.GetSketch() as ISketch;
            var modelToSketch = sketch == null ? null : sketch.ModelToSketchTransform as IMathTransform;
            var sketchToModel = modelToSketch == null ? null : modelToSketch.IInverse() as IMathTransform;
            if (sketchToModel == null) return null;
            var localPoint = math.CreatePoint(new double[] {point.X, point.Y, point.Z}) as IMathPoint;
            var modelPoint = localPoint.MultiplyTransform(sketchToModel) as IMathPoint;
            return PointData(modelPoint == null ? null : modelPoint.MultiplyTransform(componentTransform) as IMathPoint);
        }
        catch { return null; }
    }
    // Keep the native part-model coordinate in addition to the assembly-global
    // coordinate.  Drawing views created from a SLDPRT use this local frame;
    // using only assembly-global points can rotate the semantic X/Y/Z axes.
    private static double[] PartModelPoint(IMathUtility math, ISketchPoint point)
    {
        if (math == null || point == null) return null;
        try
        {
            var sketch = point.GetSketch() as ISketch;
            var modelToSketch = sketch == null ? null : sketch.ModelToSketchTransform as IMathTransform;
            var sketchToModel = modelToSketch == null ? null : modelToSketch.IInverse() as IMathTransform;
            if (sketchToModel == null) return null;
            var localPoint = math.CreatePoint(new double[] {point.X, point.Y, point.Z}) as IMathPoint;
            return PointData(localPoint == null ? null : localPoint.MultiplyTransform(sketchToModel) as IMathPoint);
        }
        catch { return null; }
    }
    private static void CollectFeature(IFeature feature, IModelDoc2 model, IComponent2 component, IMathUtility math, List<Dictionary<string, object>> rows)
    {
        if (feature == null) return;
        string type = ""; try { type = feature.GetTypeName2() ?? ""; } catch { }
        if (String.Equals(type, "HoleWzd", StringComparison.OrdinalIgnoreCase))
        {
            var data = feature.GetDefinition() as IWizardHoleFeatureData2;
            var row = new Dictionary<string, object>();
            row["feature_name"] = TryString(() => feature.Name); row["feature_type"] = type;
            row["fastener_size"] = data == null ? "" : TryString(() => data.FastenerSize);
            row["standard"] = data == null ? "" : TryString(() => data.Standard);
            row["thread_depth_m"] = data == null ? null : TryDouble(() => data.ThreadDepth);
            var points = new List<double[]>(); var partPoints = new List<double[]>(); bool access = false;
            try
            {
                if (data != null) access = data.AccessSelections(model, null);
                if (access)
                {
                    foreach (var rawPoint in AsArray(data.GetSketchPoints()))
                    {
                        var sketchPoint = rawPoint as ISketchPoint;
                        var global = TransformPoint(math, component.Transform2 as IMathTransform, sketchPoint);
                        var partPoint = PartModelPoint(math, sketchPoint);
                        if (global != null) points.Add(global);
                        if (partPoint != null) partPoints.Add(partPoint);
                    }
                }
            }
            catch { }
            finally { if (access && data != null) { try { data.ReleaseSelectionAccess(); } catch {} } }
            row["selection_accessed"] = access;
            row["sketch_point_global_m"] = points;
            row["sketch_point_part_local_m"] = partPoints;
            row["sketch_point_count"] = points.Count;
            rows.Add(row);
        }
        IFeature child = null; try { child = feature.GetFirstSubFeature() as IFeature; } catch { }
        while (child != null) { CollectFeature(child, model, component, math, rows); try { child = child.GetNextSubFeature() as IFeature; } catch { child = null; } }
    }
    private static List<Dictionary<string, object>> Features(IModelDoc2 model, IComponent2 component, IMathUtility math)
    {
        var rows = new List<Dictionary<string, object>>(); IFeature feature = null;
        try { feature = model.FirstFeature() as IFeature; } catch { }
        while (feature != null) { CollectFeature(feature, model, component, math, rows); try { feature = feature.GetNextFeature() as IFeature; } catch { feature = null; } }
        return rows;
    }
    public static object Probe(string assemblyPath)
    {
        var swType = Type.GetTypeFromProgID("SldWorks.Application"); if (swType == null) throw new InvalidOperationException("SOLIDWORKS_PROGID_NOT_FOUND");
        var sw = (SldWorks)Activator.CreateInstance(swType); sw.Visible = true;
        int errors=0,warnings=0;
        if (assemblyPath.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase))
        {
            IModelDoc2 part = null;
            try { var active = sw.ActiveDoc as IModelDoc2; if (active != null && String.Equals(active.GetPathName(), assemblyPath, StringComparison.OrdinalIgnoreCase)) part = active; } catch { }
            if (part == null) part = sw.OpenDoc6(assemblyPath, 1, 1, "", ref errors, ref warnings) as IModelDoc2;
            if (part == null) throw new InvalidOperationException("PART_OPEN_FAILED=" + errors);
            var mathPart = sw.GetMathUtility() as IMathUtility;
            if (mathPart == null) throw new InvalidOperationException("MATH_UTILITY_UNAVAILABLE");
            var partRows = Features(part, null, mathPart);
            var occurrence = System.IO.Path.GetFileNameWithoutExtension(assemblyPath) + "-1";
            foreach (var row in partRows) { row["occurrence"] = occurrence; row["part_path"] = assemblyPath; }
            int partPointTotal = 0;
            foreach (var row in partRows) partPointTotal += Convert.ToInt32(row["sketch_point_count"]);
            return new Dictionary<string, object> {
                {"version", "V4"}, {"api_route", "early_bound_IWizardHoleFeatureData2_AccessSelections_PART"},
                {"part_occurrence_count", 1}, {"hole_wizard_feature_occurrence_count", partRows.Count},
                {"selection_accessed_feature_count", partRows.FindAll(r => Convert.ToBoolean(r["selection_accessed"])).Count},
                {"sketch_point_count", partPointTotal}, {"features", partRows}
            };
        }
        var assembly = sw.OpenDoc6(assemblyPath,2,1,"",ref errors,ref warnings) as IAssemblyDoc;
        if (assembly == null) throw new InvalidOperationException("ASSEMBLY_OPEN_FAILED=" + errors);
        var math = sw.GetMathUtility() as IMathUtility; if (math == null) throw new InvalidOperationException("MATH_UTILITY_UNAVAILABLE");
        var rows = new List<Dictionary<string, object>>(); int occurrences=0, features=0, points=0, accessCount=0;
        foreach (var raw in AsArray(assembly.GetComponents(false)))
        {
            var component = raw as IComponent2; if (component == null) continue;
            string path=""; try { path=component.GetPathName() ?? ""; } catch {}
            if (!path.EndsWith(".SLDPRT",StringComparison.OrdinalIgnoreCase)) continue; occurrences++;
            IModelDoc2 model=null; bool closeAfter=false; try { model=component.GetModelDoc2() as IModelDoc2; } catch {}
            if (model==null) { int pe=0,pw=0; model=sw.OpenDoc6(path,1,1,"",ref pe,ref pw) as IModelDoc2; closeAfter=model!=null; }
            if (model == null) continue;
            try {
                foreach (var record in Features(model,component,math)) {
                    record["occurrence"] = component.Name2 ?? ""; record["part_path"] = path; rows.Add(record); features++;
                    points += Convert.ToInt32(record["sketch_point_count"]); if (Convert.ToBoolean(record["selection_accessed"])) accessCount++;
                }
            } finally { if (closeAfter) { try { sw.CloseDoc(model.GetTitle()); } catch {} } }
        }
        return new Dictionary<string, object> {
            {"version","V4"}, {"api_route","early_bound_IWizardHoleFeatureData2_AccessSelections_sketch_to_model_to_assembly"},
            {"part_occurrence_count",occurrences}, {"hole_wizard_feature_occurrence_count",features},
            {"selection_accessed_feature_count",accessCount}, {"sketch_point_count",points}, {"features",rows}
        };
    }
}
'@

Add-Type -TypeDefinition $source -ReferencedAssemblies $interop
$result = [HoleWizardSketchPointsEarlyBoundV4]::Probe($assemblyFile)
$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Output "PART_OCCURRENCE_COUNT=$($result.part_occurrence_count)"
Write-Output "HOLE_WIZARD_FEATURE_OCCURRENCE_COUNT=$($result.hole_wizard_feature_occurrence_count)"
Write-Output "SELECTION_ACCESSED_FEATURE_COUNT=$($result.selection_accessed_feature_count)"
Write-Output "SKETCH_POINT_COUNT=$($result.sketch_point_count)"
Write-Output "OUTPUT_PATH=$OutputPath"
Write-Output 'HOLEWIZARD_SKETCH_POINTS_EARLYBOUND_V4_STATUS=SUCCESS'
