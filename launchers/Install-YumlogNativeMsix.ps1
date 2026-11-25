[CmdletBinding()]
param(
    [string]$PackagePath,
    [string]$OutputRoot = (Join-Path $PSScriptRoot "..\dist\Yumlog.Native"),
    [string]$ExpectedVersion,
    [string]$DotnetPath = "dotnet",
    [switch]$BuildIfMissing,
    [switch]$Reinstall
)

$ErrorActionPreference = "Stop"
$launcherRoot = Split-Path -Parent $PSCommandPath
$packageName = "DarbotLabs.Yumlog.Native"
$packageFamily = "DarbotLabs.Yumlog.Native_gp1f7vakypzgp"
$publisher = "CN=Darbot Labs"

function Read-MsixManifest {
    param([Parameter(Mandatory)][string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName -ieq "AppxManifest.xml" })
        if ($entries.Count -ne 1 -or $entries[0].Length -gt 1MB) {
            throw "MSIX must contain exactly one bounded root AppxManifest.xml."
        }
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $settings.MaxCharactersInDocument = 1MB
        $stream = $entries[0].Open()
        try {
            $reader = [Xml.XmlReader]::Create($stream, $settings)
            try {
                $document = [Xml.XmlDocument]::new()
                $document.XmlResolver = $null
                $document.Load($reader)
                return ,$document
            } finally { $reader.Dispose() }
        } finally { $stream.Dispose() }
    } finally { $archive.Dispose() }
}

function Assert-PackageManifest {
    param([Parameter(Mandatory)][xml]$Manifest)

    $identity = @($Manifest.Package.Identity)
    $application = @($Manifest.Package.Applications.Application | Where-Object { $_.Id -ceq "YumlogNative" })
    if ($Manifest.DocumentElement.NamespaceURI -ne "http://schemas.microsoft.com/appx/manifest/foundation/windows10" -or
        $identity.Count -ne 1 -or $identity[0].Name -cne $packageName -or $identity[0].Publisher -cne $publisher -or
        $identity[0].ProcessorArchitecture -cne "x64" -or $identity[0].Version -cne $ExpectedVersion -or
        $application.Count -ne 1 -or $application[0].Executable -cne "Yumlog.Native.exe") {
        throw "Package identity, application, architecture or version does not match $packageName $ExpectedVersion."
    }
}

if (-not $ExpectedVersion) {
    $ExpectedVersion = & (Join-Path $launcherRoot "Get-YumlogVersion.ps1")
}
if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "Pass -ExpectedVersion when running outside a Yumlog checkout."
}
$expectedProductVersion = ([version]$ExpectedVersion).ToString(3)
$exactPackageVersion = if ($ExpectedVersion -match '^\d+\.\d+\.\d+\.\d+$') { $ExpectedVersion } else { $null }

if (-not $PackagePath) {
    if ($exactPackageVersion) {
        $PackagePath = Join-Path $OutputRoot "${packageName}_${exactPackageVersion}_x64.msix"
    } elseif (Test-Path -LiteralPath $OutputRoot -PathType Container) {
        $pattern = '^' + [regex]::Escape("${packageName}_${expectedProductVersion}.") + '(\d{1,5})_x64\.msix$'
        $candidate = Get-ChildItem -LiteralPath $OutputRoot -File |
            Where-Object { $_.Name -match $pattern -and [int]$Matches[1] -le 65535 } |
            Sort-Object { [version](($_.BaseName -split '_')[1]) } -Descending |
            Select-Object -First 1
        if ($candidate) { $PackagePath = $candidate.FullName }
    }
    if (-not $PackagePath) { $PackagePath = Join-Path $OutputRoot "${packageName}_${expectedProductVersion}.0_x64.msix" }
}

if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf) -and $BuildIfMissing -and -not $PSBoundParameters.ContainsKey("PackagePath")) {
    $revision = if ($exactPackageVersion) { ([version]$exactPackageVersion).Revision } else { 0 }
    & (Join-Path $launcherRoot "Build-YumlogNativeMsix.ps1") -OutputRoot $OutputRoot -DotnetPath $DotnetPath -PackageRevision $revision | Out-Host
}

if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw "MSIX package not found. Run Build-YumlogNativeMsix.ps1 first or pass -BuildIfMissing."
}

$PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
$packageManifest = Read-MsixManifest -Path $PackagePath
$manifestVersion = [string]$packageManifest.Package.Identity.Version
if ($manifestVersion -notmatch '^\d+\.\d+\.\d+\.\d+$' -or
    ([version]$manifestVersion).ToString(3) -ne $expectedProductVersion -or
    ($exactPackageVersion -and $manifestVersion -ne $exactPackageVersion)) {
    throw "Package version '$manifestVersion' does not match expected version '$ExpectedVersion'."
}
$ExpectedVersion = $manifestVersion
$expectedPackageVersion = [version]$ExpectedVersion
Assert-PackageManifest -Manifest $packageManifest
$installed = @(Get-AppxPackage -Name $packageName -ErrorAction Stop)
foreach ($existing in $installed) {
    if ($existing.PackageFamilyName -cne $packageFamily -or $existing.Publisher -cne $publisher) {
        throw "The installed package does not match the known Yumlog Native package family."
    }
    if ([version]$existing.Version -gt $expectedPackageVersion) {
        throw "Refusing to downgrade Yumlog Native from $($existing.Version) to $ExpectedVersion."
    }
}

# Update the same family in place. Reinstall never removes the package or its user data.
Add-AppxPackage -Path $PackagePath -ForceApplicationShutdown:$Reinstall -ErrorAction Stop

$packages = @(Get-AppxPackage -Name $packageName -ErrorAction Stop | Where-Object { $_.PackageFamilyName -ceq $packageFamily })
if ($packages.Count -ne 1 -or [version]$packages[0].Version -ne $expectedPackageVersion -or $packages[0].Publisher -cne $publisher) {
    throw "Post-install package identity/version validation failed for $packageFamily."
}
$package = $packages[0]
Assert-PackageManifest -Manifest (Get-AppxPackageManifest -Package $package.PackageFullName -ErrorAction Stop)
foreach ($artifact in @("Yumlog.Native.exe", "Yumlog.Native.dll", "Yumlog.Native.Cli.exe", "Yumlog.Native.Cli.dll")) {
    $path = Join-Path $package.InstallLocation $artifact
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or [Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion -ne "$expectedProductVersion.0") {
        throw "Installed binary is missing or has the wrong version: $path"
    }
    if ($artifact.EndsWith(".dll") -and [Reflection.AssemblyName]::GetAssemblyName($path).Version -ne [version]"$expectedProductVersion.0") {
        throw "Installed assembly has the wrong version: $path"
    }
}

[PSCustomObject]@{
    Name              = $package.Name
    Version           = $package.Version
    PackageFamilyName = $package.PackageFamilyName
    InstallLocation   = $package.InstallLocation
    PackagePath       = $PackagePath
    AppUserModelId    = "${packageFamily}!YumlogNative"
}
