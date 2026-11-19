[CmdletBinding()]
param(
    [Nullable[int]]$Fps,
    [Nullable[int]]$DurationSec,
    [string]$OutFile = ""
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$config = Get-Content -LiteralPath (Join-Path $repoRoot 'config\tools.json') -Raw | ConvertFrom-Json
$resolvedFps = if ($PSBoundParameters.ContainsKey('Fps')) { [int]$Fps } else { [int]$config.record.defaultFps }
$resolvedDuration = if ($PSBoundParameters.ContainsKey('DurationSec')) { [int]$DurationSec } else { [int]$config.record.defaultDurationSec }
$resolvedOutFile = if ($OutFile) { $OutFile } else { [string]$config.record.defaultOutFile }

if ($resolvedFps -le 0) { throw "Fps must be greater than zero." }
if ($resolvedDuration -le 0) { throw "DurationSec must be greater than zero." }

$parent = Split-Path -Parent $resolvedOutFile
if ($parent -and -not (Test-Path -LiteralPath $parent)) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}

. (Join-Path $repoRoot 'Skills\Record-Screen.ps1')
Record-Screen -Fps $resolvedFps -DurationSec $resolvedDuration -OutFile $resolvedOutFile
