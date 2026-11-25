[CmdletBinding()]
param(
    [string]$PackagePath,
    [string]$ExpectedVersion,
    [string]$InstallRoot = (Join-Path $PSScriptRoot ("..\dist\package-smoke-" + [Guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'

function Assert-ReleasePayload {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Version)

    $release = Get-Content -LiteralPath (Join-Path $Root 'release-manifest.json') -Raw | ConvertFrom-Json
    if ($release.schema -ne 'urn:yumlog:release:v1' -or $release.version -ne $Version) {
        throw "Unexpected release manifest in ${Root}: expected $Version."
    }
    $requiredComponents = @(
        'Yumlog.Native\Yumlog.Native.exe', 'Yumlog.Native.Cli\Yumlog.Native.Cli.exe',
        'Paperboy\Paperboy.exe', 'Paperboy.Mcp\Paperboy.Mcp.exe',
        'Yumlog.APIM.Service\Yumlog.APIM.Service.exe', 'Yumlog.WidgetStudio\Yumlog.WidgetStudio.exe'
    )
    $componentPaths = @($release.components | ForEach-Object { $_.path })
    if ($componentPaths.Count -ne $requiredComponents.Count -or @(Compare-Object $requiredComponents $componentPaths).Count -ne 0) {
        throw "The release manifest must list each production component exactly once: $Root"
    }
    foreach ($component in @($release.components)) {
        $exe = Join-Path $Root $component.path
        $dll = [IO.Path]::ChangeExtension($exe, '.dll')
        foreach ($path in @($exe, $dll)) {
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing release artifact: $path" }
            $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
            if ($info.FileVersion -ne "$Version.0" -or ($info.ProductVersion -split '\+', 2)[0] -ne $Version) {
                throw "Unexpected binary version in $path; expected $Version."
            }
        }
        if ([Reflection.AssemblyName]::GetAssemblyName($dll).Version -ne [version]"$Version.0") {
            throw "Unexpected assembly version in $dll; expected $Version."
        }
        $depsPath = [IO.Path]::ChangeExtension($exe, '.deps.json')
        $deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
        $projectKey = "$([IO.Path]::GetFileNameWithoutExtension($exe))/$Version"
        if ($deps.libraries.PSObject.Properties.Name -notcontains $projectKey -or $deps.libraries.$projectKey.type -ne 'project') {
            throw "Unexpected component version in dependency manifest: $depsPath"
        }
        if ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ne $component.sha256) {
            throw "Apphost hash mismatch: $($component.path)"
        }

        $componentRoot = Split-Path -Parent $exe
        $files = @(Get-ChildItem -LiteralPath $componentRoot -File -Recurse | Sort-Object FullName)
        if ($files.Count -ne $component.payloadFileCount -or $component.payloadSha256 -notmatch '^[0-9a-fA-F]{64}$') {
            throw "Invalid payload count or digest: $($component.path)"
        }
        $buffer = [Collections.Generic.List[byte]]::new()
        foreach ($file in $files) {
            $relative = [IO.Path]::GetRelativePath($componentRoot, $file.FullName).Replace('\', '/')
            $bytes = [Text.Encoding]::UTF8.GetBytes("$relative`0$((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant())`n")
            $buffer.AddRange($bytes)
        }
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try {
            $digest = [Convert]::ToHexString($sha256.ComputeHash($buffer.ToArray())).ToLowerInvariant()
        } finally { $sha256.Dispose() }
        if ($digest -ne $component.payloadSha256) { throw "Payload hash mismatch: $($component.path)" }
    }

    $requiredAssemblies = @('Yumlog.Native\Yumlog.Native.dll', 'Yumlog.Native.Cli\Yumlog.Native.Cli.dll')
    $assemblyPaths = @($release.nativeAssemblies | ForEach-Object { $_.path })
    if ($assemblyPaths.Count -ne $requiredAssemblies.Count -or @(Compare-Object $requiredAssemblies $assemblyPaths).Count -ne 0) {
        throw "The release manifest must list each native assembly exactly once: $Root"
    }
    foreach ($assembly in @($release.nativeAssemblies)) {
        if ((Get-FileHash -LiteralPath (Join-Path $Root $assembly.path) -Algorithm SHA256).Hash -ne $assembly.sha256) {
            throw "Native assembly hash mismatch: $($assembly.path)"
        }
    }
}

function Assert-CliVersion {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Version)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing yumlog command shim: $Path" }
    $reported = (& $Path --version 2>&1 | Out-String).Trim()
    $escapedVersion = [regex]::Escape($Version)
    if ($LASTEXITCODE -ne 0 -or $reported -notmatch ("^Yumlog $escapedVersion(?: \($escapedVersion(?:\+[0-9A-Za-z.-]+)?\))?$")) {
        throw "yumlog --version did not report ${Version}: $reported"
    }
}

$repoVersion = & (Join-Path $PSScriptRoot 'Get-YumlogVersion.ps1')
if (-not $ExpectedVersion) { $ExpectedVersion = $repoVersion }
if (-not $PackagePath) {
    if (-not $repoVersion) { throw 'Pass -PackagePath when running outside a Yumlog checkout.' }
    $PackagePath = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path "dist\Yumlog-$repoVersion\package"
}
$manifestPath = Join-Path $PackagePath 'release-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schema -ne 'urn:yumlog:release:v1' -or $manifest.version -notmatch '^\d+\.\d+\.\d+$' -or ($ExpectedVersion -and $manifest.version -ne $ExpectedVersion)) {
    throw "Unexpected package version: $($manifest.version)"
}
if (-not $ExpectedVersion) { $ExpectedVersion = $manifest.version }
$PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
if (Test-Path -LiteralPath $InstallRoot) {
    throw "Smoke InstallRoot must be a new directory, not an existing installation: $InstallRoot"
}
Assert-ReleasePayload -Root $PackagePath -Version $ExpectedVersion

$nativeFfmpeg = Join-Path $PackagePath 'Yumlog.Native\.tools\ffmpeg\bin\ffmpeg.exe'
if (-not (Test-Path -LiteralPath $nativeFfmpeg)) {
    throw "Bundled FFmpeg is missing from Yumlog.Native."
}
& $nativeFfmpeg -version | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Bundled FFmpeg smoke test failed."
}
foreach ($component in @('Yumlog.Native', 'Yumlog.Native.Cli')) {
    $probe = Join-Path $PackagePath "$component\.tools\ffmpeg\bin\ffprobe.exe"
    if (-not (Test-Path -LiteralPath $probe -PathType Leaf)) {
        throw "Bundled FFprobe is missing from $component."
    }
    & $probe -version | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Bundled FFprobe smoke test failed for $component." }
}
foreach ($artifact in @('Yumlog.Native.dll', 'Yumlog.Native.exe', 'Yumlog.Native.Cli.dll')) {
    $component = if ($artifact -eq 'Yumlog.Native.Cli.dll') { 'Yumlog.Native.Cli' } else { 'Yumlog.Native' }
    if (-not (Test-Path -LiteralPath (Join-Path $PackagePath "$component\$artifact"))) {
        throw "Package is missing required artifact $component\$artifact."
    }
}

$stub = Join-Path $PackagePath 'Yumlog.Native\Yumlog.Native.Cli.exe'
if ((Test-Path -LiteralPath $stub) -and -not (Test-Path -LiteralPath (Join-Path $PackagePath 'Yumlog.Native\Yumlog.Native.Cli.dll'))) {
    throw "Package has a CLI stub without its assembly: $stub"
}
$shim = Join-Path $PackagePath 'Yumlog.Native.Cli\yumlog.cmd'
Assert-CliVersion -Path $shim -Version $ExpectedVersion

$cli = Join-Path $PackagePath 'Yumlog.Native.Cli\Yumlog.Native.Cli.exe'
$selftest = & $cli selftest 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Installed CLI self-test failed: $($selftest -join [Environment]::NewLine)"
}

& (Join-Path $PSScriptRoot 'Install-YumlogProduction.ps1') -PackagePath $PackagePath -ExpectedVersion $ExpectedVersion -Destination $InstallRoot -Force -NoUserIntegration | Out-Null
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
# A reinstall keeps the user's native config instead of resetting it to the packaged default.
$installedConfig = Join-Path $current 'config\yumlog.native.json'
$userConfig = (Get-Content -LiteralPath $installedConfig -Raw | ConvertFrom-Json)
$userConfig | Add-Member -NotePropertyName packageTestMarker -NotePropertyValue 'kept' -Force
$userConfig | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $installedConfig -Encoding utf8
& (Join-Path $PSScriptRoot 'Install-YumlogProduction.ps1') -PackagePath $PackagePath -ExpectedVersion $ExpectedVersion -Destination $InstallRoot -Force -NoUserIntegration | Out-Null
if ((Get-Content -LiteralPath $installedConfig -Raw | ConvertFrom-Json).packageTestMarker -ne 'kept') {
    throw 'A reinstall reset the user native config.'
}
Assert-ReleasePayload -Root $current -Version $ExpectedVersion
Assert-CliVersion -Path (Join-Path $current 'Yumlog.Native.Cli\yumlog.cmd') -Version $ExpectedVersion
$installedSelftest = & (Join-Path $current 'Yumlog.Native.Cli\Yumlog.Native.Cli.exe') selftest 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Installed CLI self-test failed: $($installedSelftest -join [Environment]::NewLine)"
}

$widgetStudio = Join-Path $current 'Yumlog.WidgetStudio\Yumlog.WidgetStudio.exe'
if (-not (Test-Path -LiteralPath $widgetStudio)) {
    throw 'Installed Widget Studio executable is missing.'
}
$widgetSelftest = & $widgetStudio self-test --work-dir (Join-Path $InstallRoot 'widgetstudio-selftest') 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Installed Widget Studio self-test failed: $($widgetSelftest -join [Environment]::NewLine)"
}

[pscustomobject]@{
    PackageVersion = $manifest.version
    Components = @($manifest.components).Count
    Ffmpeg = (Get-Item -LiteralPath $nativeFfmpeg).Length
    SmokeInstall = $current
    SelfTestPassed = $true
    WidgetSelfTestPassed = $true
}

Remove-Item -LiteralPath $InstallRoot -Recurse -Force
