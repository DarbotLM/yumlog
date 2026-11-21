[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputRoot = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'dist\Yumlog-0.5.0'),
    [string]$DotnetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$stage = Join-Path $OutputRoot 'staging'
$packageRoot = Join-Path $OutputRoot 'package'

if (([IO.Path]::GetFullPath($OutputRoot)) -eq [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($OutputRoot))) {
    throw 'OutputRoot cannot be a filesystem root.'
}

Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stage -Force | Out-Null

function Publish-Component {
    param(
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$Name,
        [string]$TargetRuntime = 'win-x64'
    )

    $output = Join-Path $stage $Name
    & $DotnetPath publish $Project -c $Configuration -r $TargetRuntime --self-contained true --nologo -o $output
    if ($LASTEXITCODE -ne 0) {
        throw "$Name publish failed with exit code $LASTEXITCODE."
    }
}

function Get-PayloadDigest {
    param([Parameter(Mandatory)][string]$Path)

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $files = Get-ChildItem -LiteralPath $Path -File -Recurse | Sort-Object FullName
        $buffer = [Collections.Generic.List[byte]]::new()
        foreach ($file in $files) {
            $relative = [IO.Path]::GetRelativePath($Path, $file.FullName).Replace('\', '/')
            $bytes = [Text.Encoding]::UTF8.GetBytes("$relative`0$((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant())`n")
            $buffer.AddRange($bytes)
        }
        [Convert]::ToHexString($sha256.ComputeHash($buffer.ToArray())).ToLowerInvariant()
    } finally {
        $sha256.Dispose()
    }
}

Publish-Component -Project (Join-Path $repoRoot 'apps\Yumlog.Native.Cli\Yumlog.Native.Cli.fsproj') -Name 'Yumlog.Native.Cli'
Publish-Component -Project (Join-Path $repoRoot 'apps\Yumlog.Native\Yumlog.Native.fsproj') -Name 'Yumlog.Native'
Publish-Component -Project (Join-Path $repoRoot 'apps\Paperboy.Desktop\Paperboy.Desktop.csproj') -Name 'Paperboy'
Publish-Component -Project (Join-Path $repoRoot 'apps\Paperboy.Mcp\Paperboy.Mcp.csproj') -Name 'Paperboy.Mcp'
Publish-Component -Project (Join-Path $repoRoot 'apps\Yumlog.APIM.Service\Yumlog.APIM.Service.fsproj') -Name 'Yumlog.APIM.Service'
Publish-Component -Project (Join-Path $repoRoot 'apps\Yumlog.WidgetStudio\Yumlog.WidgetStudio.csproj') -Name 'Yumlog.WidgetStudio'

$ffmpeg = Join-Path $repoRoot '.tools\ffmpeg\bin\ffmpeg.exe'
if (Test-Path -LiteralPath $ffmpeg) {
    foreach ($nativeRoot in @('Yumlog.Native', 'Yumlog.Native.Cli')) {
        $target = Join-Path $stage "$nativeRoot\.tools\ffmpeg\bin"
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Copy-Item -LiteralPath $ffmpeg -Destination (Join-Path $target 'ffmpeg.exe') -Force
        # Studio import reads media properties with the sibling ffprobe.exe; ship it when available.
        $ffprobe = Join-Path (Split-Path -Parent $ffmpeg) 'ffprobe.exe'
        if (Test-Path -LiteralPath $ffprobe) { Copy-Item -LiteralPath $ffprobe -Destination (Join-Path $target 'ffprobe.exe') -Force }
    }
}

Copy-Item -LiteralPath (Join-Path $repoRoot 'config') -Destination (Join-Path $stage 'config') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE.txt') -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'NOTICE.md') -Destination $stage -Force

$exes = @(
    'Yumlog.Native\Yumlog.Native.exe'
    'Yumlog.Native.Cli\Yumlog.Native.Cli.exe'
    'Paperboy\Paperboy.exe'
    'Paperboy.Mcp\Paperboy.Mcp.exe'
    'Yumlog.APIM.Service\Yumlog.APIM.Service.exe'
    'Yumlog.WidgetStudio\Yumlog.WidgetStudio.exe'
)

$manifest = [ordered]@{
    schema = 'urn:yumlog:release:v1'
    version = '0.5.0'
    configuration = $Configuration
    createdUtc = [DateTimeOffset]::UtcNow.ToString('o')
    runtime = 'win-x64-self-contained'
    components = $exes | ForEach-Object {
        $path = Join-Path $stage $_
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Published executable is missing: $path"
        }
        $componentRoot = Split-Path -Parent $path
        [ordered]@{
            path = $_
            bytes = (Get-Item -LiteralPath $path).Length
            sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            payloadFileCount = @(Get-ChildItem -LiteralPath $componentRoot -File -Recurse).Count
            payloadSha256 = Get-PayloadDigest -Path $componentRoot
        }
    }
}

$manifestPath = Join-Path $stage 'release-manifest.json'
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Remove-Item -LiteralPath $packageRoot -Recurse -Force -ErrorAction SilentlyContinue
Move-Item -LiteralPath $stage -Destination $packageRoot

$archivePath = Join-Path $OutputRoot 'Yumlog-0.5.0-win-x64.zip'
Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal

[pscustomobject]@{
    Version = '0.5.0'
    Package = $packageRoot
    Archive = $archivePath
    Bytes = (Get-Item -LiteralPath $archivePath).Length
}
