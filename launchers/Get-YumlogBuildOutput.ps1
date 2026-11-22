# Prints the build output folder of a Yumlog project (for example apps\Yumlog.Native.Cli\bin\Release\<tfm>\<rid>).
# A literal TargetFramework/RuntimeIdentifier is read straight from the project file; a computed one
# (such as $(YumlogDotnetTfm) from apps\Yumlog.Dotnet.props, which follows the active SDK) is resolved
# with `dotnet msbuild -getProperty:TargetDir`. Launchers and toolbox.ps1 call this instead of hardcoding
# framework folders, so they work on .NET SDK 10 and 11 alike.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Project,
    [string]$Configuration = 'Release',
    [string]$RepoRoot = (Join-Path $PSScriptRoot '..'),
    [string]$DotnetPath = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$path = if ([IO.Path]::IsPathRooted($Project) -or $Project -match '\.(cs|fs)proj$') { $Project } else {
    $dir = Join-Path $RepoRoot "apps\$Project"
    Get-ChildItem -LiteralPath $dir -File | Where-Object { $_.Name -in "$Project.fsproj", "$Project.csproj" } | Select-Object -First 1 -ExpandProperty FullName
}
if (-not [IO.Path]::IsPathRooted($path)) { $path = Join-Path $RepoRoot $path }
if (-not $path -or -not (Test-Path -LiteralPath $path)) { throw "Project file not found for '$Project' under $RepoRoot." }
Set-StrictMode -Version 3.0
$xml = [xml](Get-Content -LiteralPath $path -Raw)
# SelectNodes keeps this safe under the callers' strict mode (toolbox.ps1) when a property is absent.
function Get-Prop([string]$Name) { $xml.SelectNodes("/Project/PropertyGroup/$Name") | ForEach-Object InnerText | Where-Object { $_ } | Select-Object -First 1 }
$tfm = Get-Prop 'TargetFramework'
$rid = Get-Prop 'RuntimeIdentifier'
if ($tfm -and -not $tfm.Contains('$(') -and -not ($rid -and $rid.Contains('$('))) {
    $out = Join-Path (Split-Path -Parent $path) (Join-Path 'bin' (Join-Path $Configuration $tfm))
    if ($rid) { $out = Join-Path $out $rid }
    return [IO.Path]::GetFullPath($out)
}
$targetDir = & $DotnetPath msbuild $path -nologo "-getProperty:TargetDir" "-p:Configuration=$Configuration"
if ($LASTEXITCODE -ne 0 -or -not $targetDir) { throw "Could not resolve TargetDir for $path ($Configuration)." }
[IO.Path]::GetFullPath(($targetDir | Select-Object -Last 1).Trim()).TrimEnd('\')
