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

public static class HoleWizardThreadParametersEarlyBoundV4
{
    private static object[] AsArray(object value)
    {
        var array = value as Array;
        if (array == null) return new object[0];
        var output = new object[array.Length]; array.CopyTo(output, 0); return output;
    }
    private static string TryString(Func<string> action)
    {
        try { return action() ?? ""; } catch { return ""; }
    }
    private static object TryDouble(Func<double> action)
    {
        try { return action(); } catch { return null; }
    }
    private static object TryInt(Func<int> action)
    {
        try { return action(); } catch { return null; }
    }
    private static void CollectFeature(IFeature feature, List<Dictionary<string, object>> rows)
    {
        if (feature == null) return;
        var type = ""; try { type = feature.GetTypeName2() ?? ""; } catch { }
        if (String.Equals(type, "HoleWzd", StringComparison.OrdinalIgnoreCase))
        {
            var data = feature.GetDefinition() as IWizardHoleFeatureData2;
            var row = new Dictionary<string, object>();
            row["feature_name"] = TryString(() => feature.Name);
            row["feature_type"] = type;
            row["definition_available"] = data != null;
            if (data != null)
            {
                row["standard"] = TryString(() => data.Standard);
                row["fastener_size"] = TryString(() => data.FastenerSize);
                row["thread_class"] = TryString(() => data.ThreadClass);
                row["thread_depth_m"] = TryDouble(() => data.ThreadDepth);
                row["tap_drill_depth_m"] = TryDouble(() => data.TapDrillDepth);
                row["thru_tap_drill_depth_m"] = TryDouble(() => data.ThruTapDrillDepth);
                row["tap_drill_diameter_m"] = TryDouble(() => data.TapDrillDiameter);
                row["thru_tap_drill_diameter_m"] = TryDouble(() => data.ThruTapDrillDiameter);
                row["thread_diameter_m"] = TryDouble(() => data.ThreadDiameter);
                row["thread_end_condition"] = TryInt(() => data.ThreadEndCondition);
                row["hole_type"] = TryInt(() => data.Type);
            }
            rows.Add(row);
        }
        IFeature child = null;
        try { child = feature.GetFirstSubFeature() as IFeature; } catch { }
        while (child != null)
        {
            CollectFeature(child, rows);
            try { child = child.GetNextSubFeature() as IFeature; } catch { child = null; }
        }
    }
    private static List<Dictionary<string, object>> ReadHoleWizardFeatures(IModelDoc2 model)
    {
        var rows = new List<Dictionary<string, object>>();
        IFeature feature = null;
        try { feature = model.FirstFeature() as IFeature; } catch { }
        while (feature != null)
        {
            CollectFeature(feature, rows);
            try { feature = feature.GetNextFeature() as IFeature; } catch { feature = null; }
        }
        return rows;
    }
    public static object Probe(string assemblyPath)
    {
        var swType = Type.GetTypeFromProgID("SldWorks.Application");
        if (swType == null) throw new InvalidOperationException("SOLIDWORKS_PROGID_NOT_FOUND");
        var sw = (SldWorks)Activator.CreateInstance(swType); sw.Visible = true;
        int errors = 0, warnings = 0;
        if (assemblyPath.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase))
        {
            IModelDoc2 part = null;
            try { var active = sw.ActiveDoc as IModelDoc2; if (active != null && String.Equals(active.GetPathName(), assemblyPath, StringComparison.OrdinalIgnoreCase)) part = active; } catch { }
            if (part == null) part = sw.OpenDoc6(assemblyPath, 1, 1, "", ref errors, ref warnings) as IModelDoc2;
            if (part == null) throw new InvalidOperationException("PART_OPEN_FAILED=" + errors);
            var partRows = ReadHoleWizardFeatures(part);
            var occurrence = System.IO.Path.GetFileNameWithoutExtension(assemblyPath) + "-1";
            foreach (var row in partRows) { row["occurrence"] = occurrence; row["part_path"] = assemblyPath; }
            return new Dictionary<string, object> {
                {"version", "V4"}, {"api_route", "early_bound_IWizardHoleFeatureData2_PART"},
                {"part_occurrence_count", 1}, {"unique_part_open_count", 1},
                {"hole_wizard_feature_occurrence_count", partRows.Count}, {"hole_wizard_features", partRows},
                {"pitch_notice", "IWizardHoleFeatureData2 does not provide a stable generic pitch field."}
            };
        }
        var assembly = sw.OpenDoc6(assemblyPath, 2, 1, "", ref errors, ref warnings) as IAssemblyDoc;
        if (assembly == null) throw new InvalidOperationException("ASSEMBLY_OPEN_FAILED=" + errors);
        var cache = new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<Dictionary<string, object>>(); int occurrenceCount = 0, openedCount = 0;
        foreach (var raw in AsArray(assembly.GetComponents(false)))
        {
            var component = raw as IComponent2; if (component == null) continue;
            string path = ""; try { path = component.GetPathName() ?? ""; } catch { }
            if (!path.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase)) continue;
            occurrenceCount++;
            List<Dictionary<string, object>> features;
            if (!cache.TryGetValue(path, out features))
            {
                IModelDoc2 model = null; bool closeAfter = false;
                try { model = component.GetModelDoc2() as IModelDoc2; } catch { }
                if (model == null) { int pe=0,pw=0; model = sw.OpenDoc6(path,1,1,"",ref pe,ref pw) as IModelDoc2; closeAfter = model != null; }
                features = model == null ? new List<Dictionary<string, object>>() : ReadHoleWizardFeatures(model);
                cache[path] = features; openedCount++;
                if (closeAfter && model != null) { try { sw.CloseDoc(model.GetTitle()); } catch {} }
            }
            foreach (var source in features)
            {
                var row = new Dictionary<string, object>(source);
                row["occurrence"] = component.Name2 ?? "";
                row["part_path"] = path;
                rows.Add(row);
            }
        }
        return new Dictionary<string, object> {
            {"version", "V4"}, {"api_route", "early_bound_IWizardHoleFeatureData2"},
            {"part_occurrence_count", occurrenceCount}, {"unique_part_open_count", openedCount},
            {"hole_wizard_feature_occurrence_count", rows.Count}, {"hole_wizard_features", rows},
            {"pitch_notice", "IWizardHoleFeatureData2 does not provide a stable generic pitch field. Pitch remains NOT_VERIFIABLE unless supplied by approved metadata or a design standard lookup."}
        };
    }
}
'@

Add-Type -TypeDefinition $source -ReferencedAssemblies $interop
$result = [HoleWizardThreadParametersEarlyBoundV4]::Probe($assemblyFile)
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Output "PART_OCCURRENCE_COUNT=$($result.part_occurrence_count)"
Write-Output "UNIQUE_PART_OPEN_COUNT=$($result.unique_part_open_count)"
Write-Output "HOLE_WIZARD_FEATURE_OCCURRENCE_COUNT=$($result.hole_wizard_feature_occurrence_count)"
Write-Output "OUTPUT_PATH=$OutputPath"
Write-Output 'HOLE_WIZARD_THREAD_PARAMETERS_EARLYBOUND_V4_STATUS=SUCCESS'
