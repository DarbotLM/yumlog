[CmdletBinding()]
param(
    [string]$PackagePath,
    [string]$ExpectedVersion,
    [string]$InstallRoot = (Join-Path ([IO.Path]::GetTempPath()) ("yumlog-package-smoke-" + [Guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
$repoVersion = & (Join-Path $PSScriptRoot 'Get-YumlogVersion.ps1')
if (-not $ExpectedVersion) { $ExpectedVersion = $repoVersion }
if (-not $PackagePath) {
    if (-not $repoVersion) { throw 'Pass -PackagePath when running outside a Yumlog checkout.' }
    $PackagePath = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path "dist\Yumlog-$repoVersion\package"
}
$manifestPath = Join-Path $PackagePath 'release-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.version -notmatch '^\d+\.\d+\.\d+$' -or ($ExpectedVersion -and $manifest.version -ne $ExpectedVersion)) {
    throw "Unexpected package version: $($manifest.version)"
}

foreach ($component in @($manifest.components)) {
    $path = Join-Path $PackagePath $component.path
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Missing release component: $($component.path)"
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $component.sha256) {
        throw "Apphost hash mismatch: $($component.path)"
    }
    if ($component.payloadFileCount -lt 1 -or [string]::IsNullOrWhiteSpace($component.payloadSha256)) {
        throw "Missing payload digest: $($component.path)"
    }
}
foreach ($assembly in @($manifest.nativeAssemblies)) {
    $path = Join-Path $PackagePath $assembly.path
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Missing release native assembly: $($assembly.path)"
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $assembly.sha256) {
        throw "Native assembly hash mismatch: $($assembly.path)"
    }
}

$nativeFfmpeg = Join-Path $PackagePath 'Yumlog.Native\.tools\ffmpeg\bin\ffmpeg.exe'
if (-not (Test-Path -LiteralPath $nativeFfmpeg)) {
    throw "Bundled FFmpeg is missing from Yumlog.Native."
}
& $nativeFfmpeg -version | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Bundled FFmpeg smoke test failed."
}
foreach ($artifact in @('Yumlog.Native.dll', 'Yumlog.Native.exe', 'Yumlog.Native.Cli.dll')) {
    $component = if ($artifact -eq 'Yumlog.Native.Cli.dll') { 'Yumlog.Native.Cli' } else { 'Yumlog.Native' }
    if (-not (Test-Path -LiteralPath (Join-Path $PackagePath "$component\$artifact"))) {
        throw "Package is missing required artifact $component\$artifact."
    }
}

$cli = Join-Path $PackagePath 'Yumlog.Native.Cli\Yumlog.Native.Cli.exe'
$selftest = & $cli selftest 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Installed CLI self-test failed: $($selftest -join [Environment]::NewLine)"
}

& (Join-Path $PSScriptRoot 'Install-YumlogProduction.ps1') -PackagePath $PackagePath -Destination $InstallRoot -Force -NoUserIntegration | Out-Null
$current = (Get-Content -LiteralPath (Join-Path $InstallRoot 'current.txt') -Raw).Trim()
if (-not (Test-Path -LiteralPath (Join-Path $current 'Yumlog.Native\Yumlog.Native.exe'))) {
    throw 'Installed native executable is missing.'
}
if (-not (Test-Path -LiteralPath (Join-Path $current 'Yumlog.Native\Yumlog.Native.dll'))) {
    throw 'Installed native assembly is missing.'
}
if (-not (Test-Path -LiteralPath (Join-Path $current 'Yumlog.Native.Cli\Yumlog.Native.Cli.dll'))) {
    throw 'Installed native CLI assembly is missing.'
}
if (-not (Test-Path -LiteralPath (Join-Path $current 'Paperboy\Paperboy.exe'))) {
    throw 'Installed Paperboy executable is missing.'
}
if (-not (Test-Path -LiteralPath (Join-Path $current 'config\yumlog.native.json'))) {
    throw 'Installed native config is missing.'
}

[pscustomobject]@{
    PackageVersion = $manifest.version
    Components = @($manifest.components).Count
    Ffmpeg = (Get-Item -LiteralPath $nativeFfmpeg).Length
    SmokeInstall = $current
    SelfTestPassed = $true
}

Remove-Item -LiteralPath $InstallRoot -Recurse -Force
