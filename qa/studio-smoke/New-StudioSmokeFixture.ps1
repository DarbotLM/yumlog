# Builds the synthetic 6-second Studio smoke recording: three 960x540 scenes,
# a 48 kHz tone, and an embedded mov_text caption track. No user media is used.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Output,
    [Parameter(Mandatory)][string]$Ffmpeg
)
$ErrorActionPreference = 'Stop'
$Output = [IO.Path]::GetFullPath($Output)
$folder = Split-Path -Parent $Output
New-Item -ItemType Directory -Force -Path $folder | Out-Null
$srt = Join-Path $folder 'fixture.srt'
[IO.File]::WriteAllText($srt,
    "1`n00:00:00,000 --> 00:00:01,750`nWelcome to the capture review.`n`n" +
    "2`n00:00:02,000 --> 00:00:03,750`nCompare scenes across the recording.`n`n" +
    "3`n00:00:04,000 --> 00:00:05,750`nPackage the outputs with Paperboy.`n",
    [Text.UTF8Encoding]::new($false))
$scenes = @(
    @{ Background = '0x182721'; Accent = '0xadf088' },
    @{ Background = '0xe6f1fa'; Accent = '0x1f6fb2' },
    @{ Background = '0x342340'; Accent = '0xedbeff' }
)
$arguments = @('-hide_banner', '-loglevel', 'error', '-nostdin', '-y')
foreach ($scene in $scenes) {
    $arguments += @('-f', 'lavfi', '-i', "color=c=$($scene.Background):s=960x540:r=30:d=2")
}
$arguments += @('-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000:duration=6', '-i', $srt)
$filters = for ($index = 0; $index -lt $scenes.Count; $index++) {
    $accent = $scenes[$index].Accent
    "[$($index):v]drawbox=x=600:y=0:w=360:h=540:color=$($accent)@0.45:t=fill,drawbox=x=65:y=88:w=90:h=12:color=$($accent):t=fill,drawbox=x=65:y=170:w=470:h=70:color=0xf4fff6:t=fill,drawbox=x=728:y=253:w=172:h=171:color=$($accent):t=4,drawbox=x=774:y=298:w=78:h=80:color=$($accent):t=fill[s$index]"
}
$filterGraph = ($filters -join ';') + ';[s0][s1][s2]concat=n=3:v=1:a=0[v]'
$arguments += @('-filter_complex', $filterGraph, '-map', '[v]', '-map', '3:a', '-map', '4:s',
    '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-b:a', '96k', '-c:s', 'mov_text',
    '-metadata:s:s:0', 'language=eng', '-t', '6', '-movflags', '+faststart', $Output)
& $Ffmpeg @arguments
if ($LASTEXITCODE -ne 0) { throw "FFmpeg could not build the Studio smoke fixture (exit $LASTEXITCODE)." }
Remove-Item -LiteralPath $srt -Force
$Output
