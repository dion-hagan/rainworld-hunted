<#
.SYNOPSIS
  Builds Hunted and copies the mod/ folder into your local Rain World install
  so it can be enabled from the in-game Remix menu.

.PARAMETER RainWorldPath
  Override if Rain World is not installed at the default Steam location.

.PARAMETER SkipBuild
  Skip the dotnet build step and just copy mod/ as-is (uses whatever
  Hunted.dll is already sitting in mod/plugins/).
#>
param(
    [string]$RainWorldPath = $(if ($env:RAINWORLD_PATH) { $env:RAINWORLD_PATH } else { "C:\Program Files (x86)\Steam\steamapps\common\Rain World" }),
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $repoRoot "src\Hunted.csproj"
$modSource = Join-Path $repoRoot "mod"
$modId = "dion_hunted"
$modDest = Join-Path $RainWorldPath "RainWorld_Data\StreamingAssets\mods\$modId"

if ($SkipBuild) {
    $existingDll = Join-Path $modSource "plugins\Hunted.dll"
    if (-not (Test-Path $existingDll)) {
        Write-Error "-SkipBuild was passed but $existingDll does not exist. Run without -SkipBuild at least once first."
    }
    Write-Host "Skipping build, using existing $existingDll" -ForegroundColor Yellow
}
else {
    Write-Host "Building $csproj ..." -ForegroundColor Cyan
    dotnet build $csproj --configuration Debug
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed, aborting deploy."
    }
}

if (-not (Test-Path $RainWorldPath)) {
    Write-Error "Rain World not found at '$RainWorldPath'. Pass -RainWorldPath or set RAINWORLD_PATH."
}

Write-Host "Copying mod/ -> $modDest ..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $modDest | Out-Null
Copy-Item -Path (Join-Path $modSource '*') -Destination $modDest -Recurse -Force

Write-Host ""
Write-Host "Done. In-game: Remix -> enable 'Hunted' -> apply/restart." -ForegroundColor Green
Write-Host "See docs/testing.md for the testing hotkeys (F5 summon, F6 bring to your room, F7 advance a cycle, ...)."
