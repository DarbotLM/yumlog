[CmdletBinding()]
param(
    [string]$ProjectPath = (Join-Path $PSScriptRoot "..\apps\Yumlog.Native\Yumlog.Native.fsproj"),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("win-x64")]
    [string]$RuntimeIdentifier = "win-x64",
    [string]$OutputRoot = (Join-Path $PSScriptRoot "..\dist\Yumlog.Native"),
    [string]$DotnetPath = "dotnet",
    [string]$ProductionPackagePath,
    [switch]$NoSign,
    [switch]$ForceNewCertificate
)

$ErrorActionPreference = "Stop"

function Get-ProjectProperty {
    param([xml]$Project, [string]$Name)

    foreach ($group in $Project.Project.PropertyGroup) {
        $node = $group.ChildNodes | Where-Object {
            $_.NodeType -eq "Element" -and $_.Name -eq $Name
        } | Select-Object -First 1
        if ($node) {
            return $node.InnerText
        }
    }

    return $null
}

function Find-WindowsSdkTool {
    param([string]$ToolName)

    $roots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "${env:ProgramFiles}\Windows Kits\10\bin"
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

    foreach ($root in $roots) {
        $tool = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName "x64\$ToolName" } |
            Where-Object { Test-Path -LiteralPath $_ } |
            Select-Object -First 1
        if ($tool) {
            return $tool
        }
    }

    $command = Get-Command $ToolName -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    throw "Could not find $ToolName. Install the Windows SDK packaging tools."
}

function Get-CodeSigningCertificate {
    param(
        [string]$Subject,
        [switch]$ForceNew
    )

    if (-not $ForceNew) {
        $existing = Get-ChildItem Cert:\CurrentUser\My |
            Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey -and $_.NotBefore -le (Get-Date) -and $_.NotAfter -gt (Get-Date) } |
            Sort-Object NotAfter -Descending |
            Select-Object -First 1
        if ($existing) {
            return $existing
        }
    }

    return New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -FriendlyName "Yumlog Native MSIX Code Signing" `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyExportPolicy Exportable `
        -KeyUsage DigitalSignature `
        -HashAlgorithm SHA256
}

$projectFullPath = (Resolve-Path -LiteralPath $ProjectPath).Path
$projectDirectory = Split-Path -Parent $projectFullPath
$manifestSource = Join-Path $projectDirectory "Package.appxmanifest"

if (-not (Test-Path -LiteralPath $manifestSource)) {
    throw "Package manifest not found: $manifestSource"
}

[xml]$project = Get-Content -LiteralPath $projectFullPath -Raw
$version = Get-ProjectProperty -Project $project -Name "Version"
$assemblyName = Get-ProjectProperty -Project $project -Name "AssemblyName"
if (-not $assemblyName) {
    $assemblyName = [IO.Path]::GetFileNameWithoutExtension($projectFullPath)
}

[xml]$manifest = Get-Content -LiteralPath $manifestSource -Raw
$identity = $manifest.Package.Identity
$packageIdentityName = $identity.Name
$packageVersion = $identity.Version
$publisher = $identity.Publisher

$application = @($manifest.Package.Applications.Application | Where-Object { $_.Id -eq "YumlogNative" })
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $packageVersion -ne "$version.0" -or
    $packageIdentityName -ne "DarbotLabs.Yumlog.Native" -or $publisher -ne "CN=Darbot Labs" -or
    $identity.ProcessorArchitecture -ne "x64" -or $application.Count -ne 1 -or $application[0].Executable -ne "$assemblyName.exe") {
    throw "The MSIX manifest must match the project version and the existing Yumlog Native package identity."
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
if (-not $ProductionPackagePath) {
    $ProductionPackagePath = Join-Path $repoRoot "dist\Yumlog-$version\package"
}
$productionManifest = Join-Path $ProductionPackagePath "release-manifest.json"
$hasProductionPayload = Test-Path -LiteralPath $productionManifest -PathType Leaf
if ($PSBoundParameters.ContainsKey("ProductionPackagePath") -and -not $hasProductionPayload) {
    throw "Production package manifest not found: $productionManifest"
}
if ($hasProductionPayload) {
    $release = Get-Content -LiteralPath $productionManifest -Raw | ConvertFrom-Json
    if ($release.schema -ne "urn:yumlog:release:v1" -or $release.version -ne $version) {
        throw "The production payload must be a Yumlog $version release: $ProductionPackagePath"
    }
}
$ffmpeg = if ($hasProductionPayload) { Join-Path $ProductionPackagePath "Yumlog.Native\.tools\ffmpeg\bin\ffmpeg.exe" } else { $null }
if (-not $ffmpeg -or -not (Test-Path -LiteralPath $ffmpeg -PathType Leaf)) {
    $ffmpeg = Join-Path $repoRoot ".tools\ffmpeg\bin\ffmpeg.exe"
}
if (-not (Test-Path -LiteralPath $ffmpeg -PathType Leaf)) {
    $onPath = Get-Command ffmpeg.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) {
        $item = Get-Item -LiteralPath $onPath.Source
        $ffmpeg = if ($item.LinkType -eq "SymbolicLink" -and $item.Target) { [IO.Path]::GetFullPath([IO.Path]::Combine((Split-Path -Parent $item.FullName), @($item.Target)[0])) } else { $item.FullName }
    }
}
if (-not (Test-Path -LiteralPath $ffmpeg -PathType Leaf)) {
    throw "FFmpeg is missing. Build the production payload or run launchers\install.ps1 first."
}
$configSource = if ($hasProductionPayload) { Join-Path $ProductionPackagePath "config" } else { Join-Path $repoRoot "config" }
$configPath = Join-Path $configSource "yumlog.native.json"
Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json | Out-Null

$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if ($OutputRoot -eq [IO.Path]::GetPathRoot($OutputRoot)) { throw "OutputRoot cannot be a filesystem root." }
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$publishDirectory = Join-Path $OutputRoot "publish\Yumlog.Native"
$cliPublish = Join-Path $OutputRoot "publish\Yumlog.Native.Cli"
$layoutDirectory = Join-Path $OutputRoot "layout"
$packageFileName = "{0}_{1}_{2}.msix" -f $packageIdentityName, $packageVersion, "x64"
$packagePath = Join-Path $OutputRoot $packageFileName

foreach ($directory in @($publishDirectory, $cliPublish, $layoutDirectory)) {
    if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
}
& $DotnetPath publish $projectFullPath `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $publishDirectory `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $publishDirectory)) {
    throw "Publish directory not found: $publishDirectory"
}

if (Test-Path -LiteralPath $layoutDirectory) {
    Remove-Item -LiteralPath $layoutDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $layoutDirectory -Force | Out-Null

Copy-Item -Path (Join-Path $publishDirectory "*") -Destination $layoutDirectory -Recurse -Force
Copy-Item -LiteralPath $manifestSource -Destination (Join-Path $layoutDirectory "AppxManifest.xml") -Force

$assetsSource = Join-Path $projectDirectory "Assets"
if (Test-Path -LiteralPath $assetsSource) {
    $assetsDestination = Join-Path $layoutDirectory "Assets"
    New-Item -ItemType Directory -Path $assetsDestination -Force | Out-Null
    Copy-Item -Path (Join-Path $assetsSource "*") -Destination $assetsDestination -Recurse -Force
}

$exePath = Join-Path $layoutDirectory "$assemblyName.exe"
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Packaged executable not found in layout: $exePath"
}
$assemblyPath = Join-Path $layoutDirectory "$assemblyName.dll"
if (-not (Test-Path -LiteralPath $assemblyPath)) {
    throw "Packaged native assembly not found in layout: $assemblyPath"
}

$cliProject = Join-Path $projectDirectory "..\Yumlog.Native.Cli\Yumlog.Native.Cli.fsproj"
& $DotnetPath publish $cliProject -c $Configuration -r $RuntimeIdentifier --self-contained true -p:PublishSingleFile=false -o $cliPublish --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Yumlog CLI publish failed with exit code $LASTEXITCODE."
}
Copy-Item -Path (Join-Path $cliPublish "*") -Destination $layoutDirectory -Recurse -Force
if (-not (Test-Path -LiteralPath (Join-Path $layoutDirectory "Yumlog.Native.Cli.exe"))) {
    throw "Packaged console executable was not found in layout."
}
if (-not (Test-Path -LiteralPath (Join-Path $layoutDirectory "Yumlog.Native.Cli.dll"))) {
    throw "Packaged console assembly was not found in layout."
}

$ffmpegDestination = Join-Path $layoutDirectory ".tools\ffmpeg\bin"
New-Item -ItemType Directory -Path $ffmpegDestination -Force | Out-Null
# Keep ffprobe and any sibling runtime DLLs alongside FFmpeg.
Get-ChildItem -LiteralPath (Split-Path -Parent $ffmpeg) -File -Force |
    Where-Object { $_.Name -in @("ffmpeg.exe", "ffprobe.exe") -or $_.Extension -eq ".dll" } |
    Copy-Item -Destination $ffmpegDestination -Force
Copy-Item -LiteralPath $configSource -Destination (Join-Path $layoutDirectory "config") -Recurse -Force
foreach ($artifact in @("$assemblyName.exe", "$assemblyName.dll", "Yumlog.Native.Cli.exe", "Yumlog.Native.Cli.dll")) {
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $layoutDirectory $artifact)).FileVersion
    if ($fileVersion -ne $packageVersion) { throw "Unexpected binary version '$fileVersion' for $artifact; expected $packageVersion." }
}

$makeappx = Find-WindowsSdkTool -ToolName "makeappx.exe"
if (Test-Path -LiteralPath $packagePath) {
    Remove-Item -LiteralPath $packagePath -Force
}
& $makeappx pack /d $layoutDirectory /p $packagePath /o
if ($LASTEXITCODE -ne 0) {
    throw "makeappx failed with exit code $LASTEXITCODE."
}

$certificateThumbprint = $null
$certificatePath = $null
if (-not $NoSign) {
    $signtool = Find-WindowsSdkTool -ToolName "signtool.exe"
    $certificate = Get-CodeSigningCertificate -Subject $publisher -ForceNew:$ForceNewCertificate
    $certificateThumbprint = $certificate.Thumbprint

    $certificatePath = Join-Path $OutputRoot "$packageIdentityName.cer"
    Export-Certificate -Cert $certificate -FilePath $certificatePath | Out-Null

    & $signtool sign /fd SHA256 /sha1 $certificateThumbprint $packagePath
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed with exit code $LASTEXITCODE."
    }
}

[PSCustomObject]@{
    PackageIdentityName = $packageIdentityName
    Version             = $packageVersion
    Publisher           = $publisher
    PackagePath         = $packagePath
    LayoutDirectory     = $layoutDirectory
    PublishDirectory    = $publishDirectory
    Certificate         = $certificateThumbprint
    CertificatePath     = $certificatePath
}
