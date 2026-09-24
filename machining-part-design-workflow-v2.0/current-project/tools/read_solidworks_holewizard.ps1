param(
    [Parameter(Mandatory=$true)][string]$PartPath,
    [Parameter(Mandatory=$true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$swRoot = 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS'
Add-Type -Path (Join-Path $swRoot 'SolidWorks.Interop.sldworks.dll')
Add-Type -Path (Join-Path $swRoot 'SolidWorks.Interop.swconst.dll')

$app = New-Object -ComObject SldWorks.Application
# 某些 SolidWorks 版本通过 PowerShell 设置 Visible 会触发 TYPE_E_ELEMENTNOTFOUND；
# 读取流程不依赖可见界面，因此保持默认状态。
$errors = 0
$warnings = 0
$model = $null
try {
    $model = $app.OpenDoc6($PartPath, 1, 1, '', [ref]$errors, [ref]$warnings)
} catch {
    # 当前版本的 PowerShell COM 绑定对 OpenDoc6 的 by-ref 参数不兼容；
    # OpenDoc 是同样的只读打开路径，用作兼容回退。
    $model = $app.OpenDoc($PartPath, 1)
}
if ($null -eq $model) { throw "OpenDoc6 failed: errors=$errors warnings=$warnings" }

$rows = @()
$features = $model.FeatureManager.GetFeatures($true)
foreach ($feature in $features) {
    $typeName = [string]$feature.GetTypeName2
    if ($typeName -ne 'HoleWzd') { continue }
    $row = [ordered]@{
        name = [string]$feature.Name
        type = $typeName
        definition_accessed = $false
        parameters = [ordered]@{}
    }
    try {
        $definition = $feature.GetDefinition()
        $row.definition_accessed = $true
        foreach ($propertyName in @('Type','HoleType','Diameter','Depth','EndCondition','ThreadType','ThreadClass','ThreadDepth','ThreadPitch')) {
            try {
                $value = $definition.$propertyName
                if ($null -ne $value) { $row.parameters[$propertyName] = $value }
            } catch { }
        }
    } catch { $row.definition_error = $_.Exception.Message }
    $rows += [pscustomobject]$row
}

$result = [ordered]@{
    source_file = $PartPath
    open_errors = $errors
    open_warnings = $warnings
    holewizard_features = $rows
    read_only = $true
}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Output ("Wrote " + $OutputPath)
