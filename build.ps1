# Publishes xClipboard (ReadyToRun) into .\dist, stamps a date version (yyyy.MMdd.HHmm) and relaunches it.
# Conductor restarts xClipboard ~2 s after it disappears, so the build goes to a staging folder first and the
# running copy is only quit right before the (fast) copy into dist.
param([string]$Version = "", [switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $Version) { $now = Get-Date; $Version = "$($now.Year).$([int]$now.ToString('MMdd')).$([int]$now.ToString('HHmm'))" }
$exe = "$root\dist\xClipboard.exe"
$stage = "$root\app\obj\stage"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
dotnet publish "$root\app\xClipboard.csproj" -c Release -o $stage --nologo -v q "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Set-Content -Path "$stage\version.txt" -Value $Version -Encoding ascii

function Stop-xClipboard {
    # Ask the running copy to exit cleanly (it saves first); force only as a last resort.
    if (-not (Get-Process xClipboard -ErrorAction SilentlyContinue)) { return }
    if (Test-Path $exe) { & $exe --quit }
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Process xClipboard -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100 }
    Get-Process xClipboard -ErrorAction SilentlyContinue | Stop-Process -Force
}

$copied = $false
for ($try = 1; $try -le 5 -and -not $copied; $try++) {
    Stop-xClipboard
    robocopy $stage "$root\dist" /E /R:0 /W:0 /NJH /NJS /NFL /NDL /NP | Out-Null
    $copied = $LASTEXITCODE -lt 8
}
if (-not $copied) { throw "could not copy the build into dist (xClipboard kept restarting?)" }
Write-Host "Built: $exe v$Version"

if (-not $NoLaunch) {
    # Conductor usually brings it back by itself; launching a second copy would just pop the window open.
    $deadline = (Get-Date).AddSeconds(5)
    while (-not (Get-Process xClipboard -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    if (Get-Process xClipboard -ErrorAction SilentlyContinue) { Write-Host "Running (restarted by Conductor)." }
    else { Start-Process $exe -ArgumentList "--background"; Write-Host "Launched." }
}
$global:LASTEXITCODE = 0   # robocopy's success codes (1-7) would otherwise look like a failure
