[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InputFile,
    [Parameter(Mandatory)][string]$OutputFile,
    [Parameter(Mandatory)][string]$Duration,
    [string]$FrameRate = '30',
    [string]$Resolution = '1080p',
    [string]$Audio = 'video-48k-stereo',
    [double]$StartSeconds = 0,
    [switch]$PlanOnly
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$catalogPath = @('library/media-presets.json','config/media-presets.json','src/gen_audio/data/media-presets.json') |
    ForEach-Object { Join-Path $repoRoot $_ } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $catalogPath) { throw 'Shared media preset catalog not found.' }
$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
$plan = [ordered]@{ schemaVersion = '1.0.0'; catalogSha256 = (Get-FileHash -LiteralPath $catalogPath -Algorithm SHA256).Hash.ToLowerInvariant() }
foreach ($pair in @(@('duration',$Duration),@('frameRate',$FrameRate),@('resolution',$Resolution),@('audio',$Audio))) {
    $key,$requested = $pair
    $found = @($catalog.($key+'Presets') | Where-Object id -CEQ $requested)
    if ($found.Count -ne 1) { throw "Unknown $key preset: $requested" }
    $plan[$key] = $found[0]
}
if (-not [double]::IsFinite($StartSeconds) -or $StartSeconds -lt 0) { throw 'Invalid clip start.' }
$source = (Resolve-Path -LiteralPath $InputFile).Path
$destination = [IO.Path]::GetFullPath($OutputFile)
if ($source -eq $destination -or (Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath ($destination+'.receipt.json'))) { throw 'Export requires a new output path.' }
if ([IO.Path]::GetExtension($destination) -ne '.mp4') { throw 'Video export currently supports .mp4 only.' }
$probeText = & ffprobe -v error -show_format -show_streams -of json $source
if ($LASTEXITCODE -ne 0) { throw 'Input media probe failed.' }
$probe = $probeText | ConvertFrom-Json
if (-not @($probe.streams | Where-Object codec_type -EQ video).Count) { throw 'Input has no video stream.' }
if ($StartSeconds + $plan.duration.seconds -gt [double]$probe.format.duration + 0.1) { throw 'Source is shorter than the requested clip; no implicit looping or padding.' }
$rate = "$($plan.frameRate.numerator)/$($plan.frameRate.denominator)"
$width,$height = $plan.resolution.width,$plan.resolution.height
$filter = "scale=${width}:${height}:force_original_aspect_ratio=decrease,pad=${width}:${height}:(ow-iw)/2:(oh-ih)/2,setsar=1"
$arguments = @('-v','error','-nostdin','-n','-threads','2','-filter_threads','1','-ss',$StartSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),'-i',$source,
    '-t',[string]$plan.duration.seconds,'-map','0:v:0','-map','0:a:0?','-vf',$filter,'-r',$rate,'-c:v','libx264','-preset','fast','-pix_fmt','yuv420p',
    '-c:a','aac','-ar',[string]$plan.audio.sampleRate,'-ac',[string]$plan.audio.channels,'-movflags','+faststart',$destination)
$plan.startSeconds = $StartSeconds
$plan.arguments = $arguments
$plan.operation = 'Explicit time-window export; never modifies the source.'
if ($PlanOnly) { $plan | ConvertTo-Json -Depth 8; return }
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
& ffmpeg @arguments
if ($LASTEXITCODE -ne 0) { throw 'Export encode failed; retain output for inspection.' }
$actualText = & ffprobe -v error -show_streams -show_format -of json $destination
if ($LASTEXITCODE -ne 0) { throw 'Output probe failed.' }
$actual = $actualText | ConvertFrom-Json
$video = @($actual.streams | Where-Object codec_type -EQ video)[0]
$audioStream = @($actual.streams | Where-Object codec_type -EQ audio) | Select-Object -First 1
$fraction = $video.avg_frame_rate -split '/'
$actualRate = [double]$fraction[0]/[double]$fraction[1]
$targetRate = [double]$plan.frameRate.numerator/$plan.frameRate.denominator
$hadAudio = @($probe.streams | Where-Object codec_type -EQ audio).Count -gt 0
$checks = [ordered]@{
    duration = [Math]::Abs([double]$actual.format.duration-$plan.duration.seconds) -le [Math]::Max(0.1,1/$targetRate)
    resolution = $video.width -eq $width -and $video.height -eq $height
    frameRate = [Math]::Abs($actualRate-$targetRate) -lt 0.002
    audio = (-not $hadAudio) -or ($audioStream -and $audioStream.sample_rate -eq $plan.audio.sampleRate -and $audioStream.channels -eq $plan.audio.channels)
}
& ffmpeg -v error -nostdin -i $destination -f null -
$checks.fullDecode = $LASTEXITCODE -eq 0
$plan.actual = $actual
$plan.checks = $checks
$plan.passed = @($checks.Values | Where-Object { -not $_ }).Count -eq 0
$plan.sourceSha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
$plan.outputSha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
$plan | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath ($destination+'.receipt.json') -Encoding utf8
if (-not $plan.passed) { throw 'Export did not meet the selected presets; inspect the receipt.' }
Write-Output "Verified export: $destination"
