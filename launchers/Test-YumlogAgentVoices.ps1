# Tests the native Agent Voices workflow with a source recording, then verifies its source audio and Harvest output.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InputVideo,
    [string]$OutputRoot,
    [string]$ExistingWorkspace,
    [string]$ExistingBundle,
    [string]$FfmpegPath,
    [ValidateRange(1,1000)][int]$MinimumSpeakers = 2,
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$inputPath = (Resolve-Path -LiteralPath $InputVideo).Path
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo ('dist\AgentVoiceTests-' + [guid]::NewGuid().ToString('N')) }
$outputPath = (New-Item -ItemType Directory -Force -Path $OutputRoot).FullName
if ($ExistingWorkspace) { $ExistingWorkspace = (Resolve-Path -LiteralPath $ExistingWorkspace).Path }
if ($ExistingBundle) {
    if (-not $ExistingWorkspace) { throw '-ExistingBundle requires the workspace whose artifacts it contains (-ExistingWorkspace).' }
    $ExistingBundle = (Resolve-Path -LiteralPath $ExistingBundle).Path
}
if (-not $FfmpegPath) { $FfmpegPath = Join-Path $repo '.tools\ffmpeg\bin\ffmpeg.exe' }
$ffmpeg = (Resolve-Path -LiteralPath $FfmpegPath).Path
$bin = & (Join-Path $PSScriptRoot 'Get-YumlogBuildOutput.ps1') -Project Yumlog.Native -Configuration $Configuration
$dll = Join-Path $bin 'Yumlog.Native.dll'
if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "Build Yumlog.Native ($Configuration) before running this test." }
$desktop = Get-ChildItem 'C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App' -Directory |
    Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $desktop) { throw '.NET 10 Windows Desktop Runtime is required.' }
$script = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'YumlogAgentVoiceIntegration.fsx'))
$script = $script.Replace('__DLL__',$dll).Replace('__DRAWING__',(Join-Path $desktop.FullName 'System.Drawing.Common.dll'))
$runnerPath = Join-Path $outputPath 'AgentVoiceIntegration.fsx'
Set-Content -LiteralPath $runnerPath -Value $script -Encoding utf8
# Pass paths as environment values because PowerShell may discard empty native-command arguments.
$env:YUMLOG_TEST_VOICE_SOURCE = $inputPath
$env:YUMLOG_TEST_VOICE_OUTPUT = $outputPath
$env:YUMLOG_TEST_VOICE_WORKSPACE = $ExistingWorkspace
$env:YUMLOG_TEST_VOICE_BUNDLE = $ExistingBundle
$env:YUMLOG_TEST_VOICE_FFMPEG = $ffmpeg
$env:YUMLOG_TEST_VOICE_MINIMUM = [string]$MinimumSpeakers
try {
    dotnet fsi --exec $runnerPath
    if ($LASTEXITCODE -ne 0) { throw "Agent Voices validation failed. See $outputPath\results.json" }
} finally {
    Remove-Item Env:YUMLOG_TEST_VOICE_SOURCE,Env:YUMLOG_TEST_VOICE_OUTPUT,Env:YUMLOG_TEST_VOICE_WORKSPACE,Env:YUMLOG_TEST_VOICE_BUNDLE,Env:YUMLOG_TEST_VOICE_FFMPEG,Env:YUMLOG_TEST_VOICE_MINIMUM -ErrorAction SilentlyContinue
}
Write-Host "Agent Voices validation passed. Report: $outputPath\results.json"
