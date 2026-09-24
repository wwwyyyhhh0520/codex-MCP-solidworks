param(
  [switch]$Kill,
  [switch]$OnlyNoWindow,
  [switch]$ForceKill
)

$items = Get-Process SLDWORKS -ErrorAction SilentlyContinue |
  Select-Object Id, CPU, StartTime, MainWindowHandle, MainWindowTitle, Path

if (-not $items) {
  Write-Output "SLDWORKS_PROCESS_COUNT=0"
  return
}

$targets = $items
if ($OnlyNoWindow) {
  $targets = $items | Where-Object {
    ([int64]$_.MainWindowHandle -eq 0) -and [string]::IsNullOrWhiteSpace($_.MainWindowTitle)
  }
}

Write-Output ("SLDWORKS_PROCESS_COUNT=" + ($items | Measure-Object).Count)
Write-Output ("SLDWORKS_TARGET_COUNT=" + ($targets | Measure-Object).Count)
$items | Format-Table Id, CPU, StartTime, MainWindowHandle, MainWindowTitle -AutoSize

if ($Kill) {
  if ($OnlyNoWindow -and -not $ForceKill -and (($targets | Measure-Object).Count -eq ($items | Measure-Object).Count)) {
    Write-Output "KILL_SKIPPED_UNCERTAIN_ALL_TARGETS=True"
    Write-Output "REASON=SolidWorks sometimes reports empty title/handle while a real UI exists. Use -ForceKill only after visually confirming these are orphan processes."
    return
  }
  foreach ($p in $targets) {
    try {
      Stop-Process -Id $p.Id -Force -ErrorAction Stop
      Write-Output "KILLED_SLDWORKS_PID=$($p.Id)"
    } catch {
      Write-Output "KILL_FAILED_PID=$($p.Id)|ERROR=$($_.Exception.Message)"
    }
  }
}
