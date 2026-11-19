[CmdletBinding()]
param(
    [Nullable[int]]$Fps,
    [Nullable[int]]$DurationSec,
    [string]$OutDir = ""
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$config = Get-Content -LiteralPath (Join-Path $repoRoot 'config\tools.json') -Raw | ConvertFrom-Json
$resolvedFps = if ($PSBoundParameters.ContainsKey('Fps')) { [int]$Fps } else { [int]$config.capture.defaultFps }
$resolvedDuration = if ($PSBoundParameters.ContainsKey('DurationSec')) { [int]$DurationSec } else { [int]$config.capture.defaultDurationSec }
$resolvedOutDir = if ($OutDir) { $OutDir } else { [string]$config.capture.defaultOutDir }

if ($resolvedFps -le 0) { throw "Fps must be greater than zero." }
if ($resolvedDuration -le 0) { throw "DurationSec must be greater than zero." }

. (Join-Path $repoRoot 'Skills\Capture-Screens.ps1')
Capture-Screens -Fps $resolvedFps -DurationSec $resolvedDuration -OutDir $resolvedOutDir
