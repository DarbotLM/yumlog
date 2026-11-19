[CmdletBinding()]
param(
    [string]$PackagePath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'dist\Yumlog-0.5.0\package'),
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'DarbotLabs\Yumlog'),
    [switch]$Force,
    [switch]$NoUserIntegration
)

$ErrorActionPreference = 'Stop'
$manifestPath = Join-Path $PackagePath 'release-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Production package manifest was not found: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.version -ne '0.5.0') {
    throw "Unsupported package version '$($manifest.version)'."
}

$versionRoot = Join-Path $Destination $manifest.version
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
if (Test-Path -LiteralPath $versionRoot) {
    if (-not $Force) {
        throw "Version $($manifest.version) is already installed at $versionRoot. Use -Force to replace it."
    }
    Remove-Item -LiteralPath $versionRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $versionRoot -Force | Out-Null
Copy-Item -Path (Join-Path $PackagePath '*') -Destination $versionRoot -Recurse -Force
Set-Content -LiteralPath (Join-Path $Destination 'current.txt') -Value $versionRoot -Encoding UTF8

$cliRoot = Join-Path $versionRoot 'Yumlog.Native.Cli'
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$pathEntries = @($userPath -split ';' | Where-Object { $_ })
if (-not $NoUserIntegration -and $pathEntries -notcontains $cliRoot) {
    [Environment]::SetEnvironmentVariable('Path', (($pathEntries + $cliRoot) -join ';'), 'User')
}

$shortcutDirectory = $null
if (-not $NoUserIntegration) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcutDirectory = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\DarbotLabs Yumlog'
    New-Item -ItemType Directory -Path $shortcutDirectory -Force | Out-Null

    foreach ($shortcut in @(
        @{ Name = 'Yumlog Native 0.5.0.lnk'; Target = Join-Path $versionRoot 'Yumlog.Native\Yumlog.Native.exe'; Arguments = '' }
        @{ Name = 'Paperboy 0.5.0.lnk'; Target = Join-Path $versionRoot 'Paperboy\Paperboy.exe'; Arguments = '' }
        @{ Name = 'Yumlog Widget Studio 0.5.0.lnk'; Target = Join-Path $versionRoot 'Yumlog.WidgetStudio\Yumlog.WidgetStudio.exe'; Arguments = '--help' }
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
