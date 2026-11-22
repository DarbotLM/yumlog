# Real-media Studio integration: local Whisper, Windows voices, semantic schemas and hash-verified Harvest.
# Pass -InputVideo <short video with speech> on another checkout. Outputs are retained for review.
# The default uses the previously rendered 43-second Ignite excerpt; it never rewrites the source.
param([string]$Configuration='Release',[string]$InputVideo='',[string]$OutputRoot='',[switch]$SkipBuild,[switch]$SkipDecimation)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $InputVideo) { $InputVideo=Join-Path $repo 'dist\ignite-2026-semantic\short-video\clip-01.mp4' }
$InputVideo=(Resolve-Path -LiteralPath $InputVideo).Path
if (-not $OutputRoot) { $OutputRoot=Join-Path $repo ('dist\StudioTests-'+[guid]::NewGuid().ToString('N')) }
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
if (-not $SkipBuild) {
 dotnet build (Join-Path $repo 'apps\Yumlog.Native\Yumlog.Native.fsproj') -c $Configuration --nologo
 if ($LASTEXITCODE -ne 0) { throw 'Studio build failed.' }
}
$bin=& (Join-Path $PSScriptRoot 'Get-YumlogBuildOutput.ps1') -Project Yumlog.Native -Configuration $Configuration
if (-not (Test-Path -LiteralPath (Join-Path $bin 'Yumlog.Native.dll'))) {
    throw "Studio build did not produce Yumlog.Native.dll at $bin. Run without -SkipBuild."
}
$desktop=Get-ChildItem 'C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App' -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$script=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'YumlogStudioIntegration.fsx'))
$script=$script.Replace('__DLL__',(Join-Path $bin 'Yumlog.Native.dll')).Replace('__SPEECH__',(Join-Path $bin 'System.Speech.dll')).Replace('__DRAWING__',(Join-Path $desktop.FullName 'System.Drawing.Common.dll')).Replace('__WINDOWS__',(Join-Path $bin 'Microsoft.Windows.SDK.NET.dll')).Replace('__WINRT__',(Join-Path $bin 'WinRT.Runtime.dll')).Replace('__OUTPUT__',$OutputRoot).Replace('__REPO__',$repo).Replace('__VIDEO__',$InputVideo).Replace('__SKIP__',([bool]$SkipDecimation).ToString().ToLowerInvariant())
$fsx=Join-Path $OutputRoot 'StudioIntegration.fsx'
Set-Content -LiteralPath $fsx -Value $script -Encoding utf8
dotnet fsi --exec $fsx
if ($LASTEXITCODE -ne 0) { throw "Studio integration failed. See $OutputRoot\results.json" }
Write-Host "Studio integration passed. Report: $OutputRoot\results.json"
