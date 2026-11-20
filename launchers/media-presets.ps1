[CmdletBinding()]
param(
    [string]$Duration,
    [string]$FrameRate = '30',
    [string]$Resolution = '1080p',
    [string]$Audio = 'video-48k-stereo'
)
$ErrorActionPreference = 'Stop'
$catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../config/media-presets.json') -Raw | ConvertFrom-Json
if (-not $Duration) { $catalog | ConvertTo-Json -Depth 8; return }
$plan = [ordered]@{ schemaVersion = $catalog.schemaVersion }
foreach ($pair in @(@('duration', $Duration), @('frameRate', $FrameRate), @('resolution', $Resolution), @('audio', $Audio))) {
    $key, $requested = $pair
    $found = @($catalog.($key + 'Presets') | Where-Object id -CEQ $requested)
    if ($found.Count -ne 1) { throw "Unknown $key preset: $requested" }
    $plan[$key] = $found[0]
}
$plan.nativeRecordArgs = @('--duration', [string]$plan.duration.seconds)
$plan.nativeFrameRateSupported = $plan.frameRate.denominator -eq 1
if ($plan.nativeFrameRateSupported) { $plan.nativeRecordArgs += @('--fps', [string]$plan.frameRate.numerator) }
$plan.frameRateRational = "$($plan.frameRate.numerator)/$($plan.frameRate.denominator)"
$plan.exportVideoFilter = "scale=$($plan.resolution.width):$($plan.resolution.height):force_original_aspect_ratio=decrease,pad=$($plan.resolution.width):$($plan.resolution.height):(ow-iw)/2:(oh-ih)/2,setsar=1"
$plan.notes = @('Native WGC captures monitor geometry; apply the explicit export filter for preset dimensions.', 'Fractional output rates require explicit FFmpeg conversion; do not round them for native capture.', 'This command plans only and does not start screen recording.')
$plan | ConvertTo-Json -Depth 8
