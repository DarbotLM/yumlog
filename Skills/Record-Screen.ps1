. "$PSScriptRoot/Run-FFmpeg.ps1"
function Record-Screen {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$false)]
        [ValidateRange(1, 240)]
        [int]$Fps = 30,
        [Parameter(Mandatory=$false)]
        [ValidateRange(1, 86400)]
        [int]$DurationSec = 10,
        [Parameter(Mandatory=$false)]
        [string]$OutFile = "./screenrecord.mp4",
        # HEVC through libx265 is the default; H.264 through libx264 stays available.
        [Parameter(Mandatory=$false)]
        [ValidateSet('hevc', 'h264')]
        [string]$Codec = 'hevc',
        [Parameter(Mandatory=$false)]
        [ValidateSet('ultrafast', 'superfast', 'veryfast', 'faster', 'fast', 'medium', 'slow', 'slower', 'veryslow')]
        [string]$Preset = 'medium'
    )
    $ErrorActionPreference = 'Stop'
    $encoder = if ($Codec -eq 'h264') { @('-c:v', 'libx264') } else { @('-c:v', 'libx265', '-tag:v', 'hvc1') }
    $ffmpegArgs = @("-y", "-f", "gdigrab", "-framerate", $Fps, "-t", $DurationSec, "-i", "desktop",
        "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2") + $encoder + @("-preset", $Preset, "-pix_fmt", "yuv420p", $OutFile)
    Run-FFmpeg -Arguments $ffmpegArgs
    Write-Host "Screen recording saved to $OutFile"
}

# Only export if we're being imported as a module
if ($MyInvocation.Line -match 'Import-Module') {
    Export-ModuleMember -Function Record-Screen
}
