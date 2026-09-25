<#
.SYNOPSIS
  Runs the headless arena for hours, unattended: several instances at once, round
  after round, rewriting the baseline tactics file and the summary after every round.

.DESCRIPTION
  Builds tools/Hunted.Arena once, then runs it with --rounds 0 until -Hours pass or
  a STOP file appears in the output folder. Everything the tool prints goes to
  <OutDir>\arena.log. See docs/arena.md for the outputs.

.PARAMETER Instances
  Arenas (and policies) trained at the same time; one thread each.

.PARAMETER Episodes
  Encounters per instance per round. Rounds are the checkpoint cadence.

.PARAMETER Hours
  Stop after the round that passes this many hours.

.PARAMETER OutDir
  Output folder (default arena-out under the repo).

.PARAMETER Resume
  Continue from the baseline already in OutDir instead of starting fresh.

.PARAMETER NoCurriculum
  Train every round on the judged setup instead of rotating gear and a dodging opponent.

.PARAMETER Install
  Also copy the baseline into the game's ModConfigs folder after each round.

.EXAMPLE
  ./scripts/arena_overnight.ps1 -Instances 6 -Hours 8
#>
param(
    [int]$Instances = 6,
    [int]$Episodes = 400000,
    [int]$Eval = 4000,
    [int]$Block = 10000,
    [double]$Hours = 8,
    [int]$Seed = 1,
    [string]$OutDir = "",
    [switch]$Resume,
    [switch]$NoCurriculum,
    [switch]$Install,
    [string[]]$ExtraArgs = @()
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "tools\Hunted.Arena\Hunted.Arena.csproj"
if ($OutDir -eq "") { $OutDir = Join-Path $repoRoot "arena-out" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$log = Join-Path $OutDir "arena.log"

Write-Host "Building $project ..." -ForegroundColor Cyan
dotnet build $project --configuration Release --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed." }
$dll = Join-Path $repoRoot "tools\Hunted.Arena\bin\Release\net8.0\Hunted.Arena.dll"

$arenaArgs = @($dll, "--instances", $Instances, "--episodes", $Episodes, "--eval", $Eval, "--block", $Block, "--rounds", "0", "--hours", $Hours, "--seed", $Seed, "--out", $OutDir, "--quiet")
if (-not $NoCurriculum) { $arenaArgs += "--curriculum" }
if ($Resume) { $arenaArgs += "--resume" }
if ($Install) { $arenaArgs += "--install" }
$arenaArgs += $ExtraArgs

Write-Host "Running: dotnet $($arenaArgs -join ' ')" -ForegroundColor Cyan
Write-Host "Log: $log  (create $OutDir\STOP to end after the current round)" -ForegroundColor Cyan
& dotnet @arenaArgs 2>&1 | Tee-Object -FilePath $log -Append
