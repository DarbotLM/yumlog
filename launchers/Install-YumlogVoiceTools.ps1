# Install the pinned x64 CPU speaker detector into the current user's Yumlog data.
# PackageCache optionally points at already downloaded archives/models for offline setup.
[CmdletBinding()]
param([string]$PackageCache)

$ErrorActionPreference = 'Stop'
if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
    throw 'This Yumlog Voice Tools installer requires Windows x64.'
}

$voiceToolsRoot = Join-Path $env:LOCALAPPDATA 'DarbotLabs\Yumlog\VoiceTools'
$runtimeVersion = 'v1.13.8'
$destination = Join-Path $voiceToolsRoot "sherpa-onnx-$runtimeVersion"
$runtime = "sherpa-onnx-$runtimeVersion-win-x64-shared-MT-Release-no-tts"
$segmentation = 'sherpa-onnx-pyannote-segmentation-3-0'
$embedding = '3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx'
$downloadRoot = 'https://github.com/k2-fsa/sherpa-onnx/releases/download'
$packages = @(
    @{ Name = "$runtime.tar.bz2"; Tag = $runtimeVersion; Hash = '4B0A94F7B5C606B1B64A19A831C2127559E4B3D34E195465EBC7BE73D9ED4783' }
    @{ Name = "$segmentation.tar.bz2"; Tag = 'speaker-segmentation-models'; Hash = '24615EE884C897D9D2BA09BB4D30DA6BB1B15E685065962DB5B02E76E4996488' }
    @{ Name = $embedding; Tag = 'speaker-recongition-models'; Hash = '53CAB20BA8F2EB2F6E2D89ECC18E88EF3222169B61B4419CBC13666744A4574B' }
)

$work = Join-Path $voiceToolsRoot ('.setup-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $work 'payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
try {
    $downloaded = @{}
    foreach ($package in $packages) {
        $name = $package.Name
        if ($PackageCache) {
            $path = Join-Path $PackageCache $name
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Voice Tools package is missing from the cache: $name" }
        } else {
            $path = Join-Path $work $name
            $url = "$downloadRoot/$($package.Tag)/$name"
            curl.exe --fail --location --silent --show-error --retry 2 --connect-timeout 15 --max-time 180 --output $path $url
            if ($LASTEXITCODE -ne 0) { throw "Voice Tools could not download $name." }
        }
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash -ne $package.Hash) {
            throw "Voice Tools rejected $name because its SHA-256 does not match the pinned package."
        }
        $downloaded[$name] = (Resolve-Path -LiteralPath $path).Path
    }

    $detector = 'sherpa-onnx-offline-speaker-diarization.exe'
    tar.exe -xf $downloaded["$runtime.tar.bz2"] -C $work "$runtime/bin/$detector" "$runtime/bin/onnxruntime.dll"
    if ($LASTEXITCODE -ne 0) { throw 'Voice Tools could not unpack the sherpa-onnx runtime.' }
    tar.exe -xf $downloaded["$segmentation.tar.bz2"] -C $work "$segmentation/model.onnx" "$segmentation/LICENSE"
    if ($LASTEXITCODE -ne 0) { throw 'Voice Tools could not unpack the segmentation model.' }
    Copy-Item -LiteralPath (Join-Path $work "$runtime/bin/$detector"),(Join-Path $work "$runtime/bin/onnxruntime.dll") -Destination $payload
    Copy-Item -LiteralPath (Join-Path $work "$segmentation/model.onnx") -Destination (Join-Path $payload 'pyannote-segmentation-3.0.onnx')
    Copy-Item -LiteralPath (Join-Path $work "$segmentation/LICENSE") -Destination (Join-Path $payload 'pyannote-segmentation-LICENSE.txt')
    Copy-Item -LiteralPath $downloaded[$embedding] -Destination (Join-Path $payload '3dspeaker-campplus.onnx')
    if (Test-Path -LiteralPath $destination -PathType Container) {
        foreach ($file in Get-ChildItem -LiteralPath $payload -File) {
            $installed = Join-Path $destination $file.Name
            if ((Test-Path -LiteralPath $installed -PathType Leaf) -and
                ((Get-FileHash -LiteralPath $installed).Hash -eq (Get-FileHash -LiteralPath $file.FullName).Hash)) { continue }
            Copy-Item -LiteralPath $file.FullName -Destination $installed -Force
        }
    } else {
        [IO.Directory]::Move($payload, $destination)
    }
    Write-Host "Installed Yumlog Voice Tools at $destination"
} finally {
    $resolvedWork = (Resolve-Path -LiteralPath $work -ErrorAction SilentlyContinue).Path
    if ($resolvedWork -and [IO.Path]::GetDirectoryName($resolvedWork) -eq [IO.Path]::GetFullPath($voiceToolsRoot) -and
        [IO.Path]::GetFileName($resolvedWork) -like '.setup-*') {
        Remove-Item -LiteralPath $resolvedWork -Force -Recurse
    }
}
