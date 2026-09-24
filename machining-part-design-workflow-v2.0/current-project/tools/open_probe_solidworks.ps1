param(
    [Parameter(Mandatory = $true)]
    [string]$SourceFile,

    [Parameter(Mandatory = $true)]
    [string]$OutputFile
)

$result = [ordered]@{
    schema_version = "1.0"
    mode = "powershell_solidworks_open_probe"
    created_at = (Get-Date).ToString("s")
    source_file = $SourceFile
    status = "failed"
    source_model_modified = $false
    steps = @()
    risks = @()
}

try {
    if (-not (Test-Path -LiteralPath $SourceFile -PathType Leaf)) {
        throw "Source SLDPRT does not exist: $SourceFile"
    }
    $app = New-Object -ComObject SldWorks.Application
    $app.Visible = $true
    $result.steps += [ordered]@{ step = "connect_solidworks"; status = "ok" }

    $errors = 0
    $warnings = 0
    $model = $app.OpenDoc6($SourceFile, 1, 1, "", [ref]$errors, [ref]$warnings)
    $result.steps += [ordered]@{
        step = "opendoc6_byref"
        status = $(if ($null -ne $model) { "ok" } else { "error" })
        errors = $errors
        warnings = $warnings
    }
    if ($null -eq $model) {
        throw "OpenDoc6 returned a null document object."
    }
    $title = $model.GetTitle()
    $result.document_title = $title
    $result.status = "passed"
    try {
        $app.CloseDoc($title)
        $result.steps += [ordered]@{ step = "close_document"; status = "attempted"; message = "CloseDoc requested: $title" }
    } catch {
        $result.steps += [ordered]@{ step = "close_document"; status = "warning"; message = $_.Exception.Message }
    }
} catch {
    $result.steps += [ordered]@{ step = "probe"; status = "error"; message = $_.Exception.Message }
}

$parent = Split-Path -Parent $OutputFile
if ($parent -and -not (Test-Path -LiteralPath $parent)) {
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputFile -Encoding UTF8
$summary = [ordered]@{ status = $result.status; output = $OutputFile }
$summary | ConvertTo-Json -Compress
if ($result.status -eq "passed") { exit 0 }
exit 2
