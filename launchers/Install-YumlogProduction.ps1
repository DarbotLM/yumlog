[CmdletBinding()]
param(
    [string]$PackagePath,
    [string]$ExpectedVersion,
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'DarbotLabs\Yumlog'),
    [switch]$Force,
    [switch]$NoUserIntegration
)

$ErrorActionPreference = 'Stop'
# Inside a checkout the expected version is the repo version; elsewhere the package manifest decides.
$repoVersion = & (Join-Path $PSScriptRoot 'Get-YumlogVersion.ps1')
if (-not $ExpectedVersion) { $ExpectedVersion = $repoVersion }
if (-not $PackagePath) {
    if (-not $repoVersion) { throw 'Pass -PackagePath when running outside a Yumlog checkout.' }
    $PackagePath = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path "dist\Yumlog-$repoVersion\package"
}
$manifestPath = Join-Path $PackagePath 'release-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Production package manifest was not found: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schema -ne 'urn:yumlog:release:v1' -or $manifest.version -notmatch '^\d+\.\d+\.\d+$' -or ($ExpectedVersion -and $manifest.version -ne $ExpectedVersion)) {
    throw "Unsupported package version '$($manifest.version)'."
}

$Destination = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
$versionRoot = Join-Path $Destination $manifest.version
# The package ships default settings (backend ffmpeg). Carry the user's native config over an
# upgrade or a -Force reinstall instead of resetting it; only a parseable config under this root is kept.
$userConfig = $null
$currentFile = Join-Path $Destination 'current.txt'
$previousRoot = if (Test-Path -LiteralPath $currentFile) { (Get-Content -LiteralPath $currentFile -Raw).Trim() } else { $null }
foreach ($candidateRoot in @($previousRoot, $versionRoot)) {
    if (-not $candidateRoot -or -not ([IO.Path]::GetFullPath($candidateRoot)).StartsWith("$Destination\", [StringComparison]::OrdinalIgnoreCase)) { continue }
    $candidateConfig = Join-Path $candidateRoot 'config\yumlog.native.json'
    if (-not (Test-Path -LiteralPath $candidateConfig -PathType Leaf)) { continue }
    try {
        Get-Content -LiteralPath $candidateConfig -Raw | ConvertFrom-Json | Out-Null
        $userConfig = [IO.File]::ReadAllBytes($candidateConfig)
        break
    } catch {
        Write-Warning "Ignoring unreadable native config $candidateConfig; the packaged default is installed."
    }
}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
if (Test-Path -LiteralPath $versionRoot) {
    if (-not $Force) {
        throw "Version $($manifest.version) is already installed at $versionRoot. Use -Force to replace it."
    }
    Remove-Item -LiteralPath $versionRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $versionRoot -Force | Out-Null
Copy-Item -Path (Join-Path $PackagePath '*') -Destination $versionRoot -Recurse -Force
if ($null -ne $userConfig) {
    $installedConfig = Join-Path $versionRoot 'config\yumlog.native.json'
    New-Item -ItemType Directory -Path (Split-Path -Parent $installedConfig) -Force | Out-Null
    [IO.File]::WriteAllBytes($installedConfig, $userConfig)
}
# Packages up to 0.6.0 carry a CLI apphost without its dll in the Studio folder; it fails with
# "The application to execute does not exist". Drop that stub; the real CLI lives in Yumlog.Native.Cli.
$studioRoot = Join-Path $versionRoot 'Yumlog.Native'
if (-not (Test-Path -LiteralPath (Join-Path $studioRoot 'Yumlog.Native.Cli.dll'))) {
    foreach ($stubName in @('Yumlog.Native.Cli.exe', 'Yumlog.Native.Cli.deps.json', 'Yumlog.Native.Cli.runtimeconfig.json', 'Yumlog.Native.Cli.pdb')) {
        $stubPath = Join-Path $studioRoot $stubName
        if (Test-Path -LiteralPath $stubPath) { Remove-Item -LiteralPath $stubPath -Force }
    }
}
# `yumlog` on the user PATH forwards to the real CLI (older packages do not ship the shim).
$shimPath = Join-Path $versionRoot 'Yumlog.Native.Cli\yumlog.cmd'
if ((Test-Path -LiteralPath (Join-Path $versionRoot 'Yumlog.Native.Cli\Yumlog.Native.Cli.exe')) -and -not (Test-Path -LiteralPath $shimPath)) {
    Set-Content -LiteralPath $shimPath -Value "@echo off`r`n`"%~dp0Yumlog.Native.Cli.exe`" %*`r`n" -Encoding ascii -NoNewline
}
Set-Content -LiteralPath (Join-Path $Destination 'current.txt') -Value $versionRoot -Encoding UTF8
# Upgrade in place: one installed version only. Remove other version folders
# and their user PATH entries once the new version is in place.
# Only a DarbotLabs\Yumlog install root is cleaned, and only folders that hold a Yumlog release manifest.
$managedRoot = $Destination.EndsWith('\DarbotLabs\Yumlog', [StringComparison]::OrdinalIgnoreCase)
$retired = @(if ($managedRoot) {
    Get-ChildItem -LiteralPath $Destination -Directory | Where-Object {
        $oldManifest = Join-Path $_.FullName 'release-manifest.json'
        $_.Name -match '^\d+\.\d+\.\d+$' -and $_.Name -ne $manifest.version -and (Test-Path -LiteralPath $oldManifest) -and
            ((Get-Content -LiteralPath $oldManifest -Raw | ConvertFrom-Json).schema -eq 'urn:yumlog:release:v1')
    }
})
foreach ($old in $retired) {
    Remove-Item -LiteralPath $old.FullName -Recurse -Force
}

$cliRoot = Join-Path $versionRoot 'Yumlog.Native.Cli'
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$retiredCliRoots = @($retired | ForEach-Object { Join-Path $_.FullName 'Yumlog.Native.Cli' })
$pathEntries = @($userPath -split ';' | Where-Object { $_ -and $retiredCliRoots -notcontains $_ })
if (-not $NoUserIntegration -and $retiredCliRoots.Count -gt 0 -and $pathEntries -contains $cliRoot) {
    [Environment]::SetEnvironmentVariable('Path', ($pathEntries -join ';'), 'User')
}
if (-not $NoUserIntegration -and $pathEntries -notcontains $cliRoot) {
    [Environment]::SetEnvironmentVariable('Path', (($pathEntries + $cliRoot) -join ';'), 'User')
}

$shortcutDirectory = $null
if (-not $NoUserIntegration) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcutDirectory = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\DarbotLabs Yumlog'
    New-Item -ItemType Directory -Path $shortcutDirectory -Force | Out-Null
    # Retire only this suite's versioned shortcuts; anything else in the folder is left alone.
    Get-ChildItem -LiteralPath $shortcutDirectory -Filter '*.lnk' |
        Where-Object { $_.Name -match '^(Yumlog Native|Yumlog Widget Studio|Paperboy) \d+\.\d+\.\d+\.lnk$' -and $_.Name -notlike "* $($manifest.version).lnk" } |
        Remove-Item -Force

    foreach ($shortcut in @(
        @{ Name = "Yumlog Native $($manifest.version).lnk"; Target = Join-Path $versionRoot 'Yumlog.Native\Yumlog.Native.exe'; Arguments = '' }
        @{ Name = "Paperboy $($manifest.version).lnk"; Target = Join-Path $versionRoot 'Paperboy\Paperboy.exe'; Arguments = '' }
        @{ Name = "Yumlog Widget Studio $($manifest.version).lnk"; Target = Join-Path $versionRoot 'Yumlog.WidgetStudio\Yumlog.WidgetStudio.exe'; Arguments = '--help' }
    )) {
        $link = $shell.CreateShortcut((Join-Path $shortcutDirectory $shortcut.Name))
        $link.TargetPath = $shortcut.Target
        $link.Arguments = $shortcut.Arguments
        $link.WorkingDirectory = Split-Path -Parent $shortcut.Target
        $link.Description = "DarbotLabs $($shortcut.Name -replace '\.lnk$', '')"
        $link.Save()
    }
}

[pscustomobject]@{
    Version = $manifest.version
    InstallRoot = $versionRoot
    StartMenu = $shortcutDirectory
    CliAddedToUserPath = -not $NoUserIntegration
}
