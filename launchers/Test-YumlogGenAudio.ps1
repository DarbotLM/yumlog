# gen-audio boundary contract: Yumlog drives DarbotLM/gen-audio only through its CLIs
# (gen_audio.cli.render_speech, gen_audio.cli.transcribe_vibevoice) and verifies their receipts.
# The default run uses a stdlib-only stub of those two entry points, so it needs Python but no
# models. Pass -LiveRoot/-LivePython/-LiveVoice to also render real speech with a gen-audio checkout.
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Python,
    [string]$OutputRoot,
    [string]$LivePython,
    [string]$LiveRoot,
    [string]$LiveEngine = 'kokoro_onnx',
    [string]$LiveVoice
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cliRoot = & (Join-Path $PSScriptRoot 'Get-YumlogBuildOutput.ps1') -Project Yumlog.Native.Cli -Configuration $Configuration
$cli = Join-Path $cliRoot 'Yumlog.Native.Cli.exe'
$bin = & (Join-Path $PSScriptRoot 'Get-YumlogBuildOutput.ps1') -Project Yumlog.Native -Configuration $Configuration
foreach ($required in $cli, (Join-Path $bin 'Yumlog.Native.dll')) { if (-not (Test-Path -LiteralPath $required)) { throw "Build Yumlog.Native and Yumlog.Native.Cli ($Configuration) first; missing $required." } }
if (-not $Python) { $Python = @(Get-Command python, py -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source)[0] }
if (-not $Python) { throw 'Python 3 is required for the gen-audio boundary stub.' }
$Python = (Get-Command $Python -CommandType Application).Source
$ownsOutput = -not $OutputRoot
if (-not $OutputRoot) { $OutputRoot = Join-Path ([IO.Path]::GetTempPath()) ('yumlog-gen-audio-' + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
Set-Content -LiteralPath (Join-Path $OutputRoot '.owner') -Value 'Test-YumlogGenAudio.ps1 (Yumlog gate); safe to delete' -Encoding UTF8
$stub = Join-Path $repo 'qa\gen-audio-stub'
$model = New-Item -ItemType Directory -Force -Path (Join-Path $OutputRoot 'vibevoice-asr-stub')
Set-Content -LiteralPath (Join-Path $model 'config.json') -Value '{"stub": true}' -Encoding UTF8
$checks = 0
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw "gen-audio contract failed: $Message" }; $script:checks++ }
function Invoke-Cli([string[]]$Arguments, [hashtable]$Environment = @{}) {
    $saved = @{}
    foreach ($key in $Environment.Keys) { $saved[$key] = [Environment]::GetEnvironmentVariable($key); [Environment]::SetEnvironmentVariable($key, $Environment[$key]) }
    try {
        $errFile = Join-Path $OutputRoot 'stderr.txt'
        $out = & $cli @Arguments 2> $errFile
        [pscustomobject]@{ Exit = $LASTEXITCODE; Out = ($out -join "`n"); Err = (Get-Content -LiteralPath $errFile -Raw -ErrorAction SilentlyContinue) + '' }
    } finally { foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) } }
}
function Get-Receipt([string]$Text) { $Text.Substring($Text.IndexOf('{')) | ConvertFrom-Json }
$clean = @{ YUMLOG_GEN_AUDIO_VOICE = $null; GEN_AUDIO_VIBEVOICE_ASR_MODEL = $null; GEN_AUDIO_ROOT = $null; YUMLOG_GEN_AUDIO_STUB = $null }
function Get-StubEnvironment([string]$Mode) { $copy = $clean.Clone(); $copy['YUMLOG_GEN_AUDIO_STUB'] = $Mode; $copy }

try {
    # 1. speak: text reaches gen-audio on stdin; the WAV must match the receipt hash.
    $wav = Join-Path $OutputRoot 'narration.wav'
    $script = 'Open the review workspace — then confirm “step two”.'
    $r = Invoke-Cli @('speak', '--text', $script, '--out-file', $wav, '--engine', 'kokoro_onnx', '--voice', 'af_heart', '--python', $Python, '--gen-audio-root', $stub) $clean
    Assert-True ($r.Exit -eq 0) "speak exited $($r.Exit): $($r.Err)"
    $receipt = Get-Receipt $r.Out
    Assert-True ($receipt.engine -eq 'kokoro_onnx' -and $receipt.voice -eq 'af_heart' -and $receipt.sampleRate -eq 24000 -and $receipt.durationSeconds -gt 0) "unexpected speech receipt $($r.Out)"
    Assert-True ($receipt.audioSha256 -eq (Get-FileHash -LiteralPath $wav -Algorithm SHA256).Hash.ToLowerInvariant()) 'WAV hash does not match the receipt'
    Assert-True ((Get-Content -LiteralPath "$wav.script.txt" -Raw -Encoding UTF8) -ceq $script) 'gen-audio did not receive the exact UTF-8 script'

    # 2. Failures stay loud: gen-audio errors, hash drift and missing configuration all exit non-zero.
    $r = Invoke-Cli @('speak', '--text', 'x', '--out-file', (Join-Path $OutputRoot 'fail.wav'), '--voice', 'af_heart', '--python', $Python, '--gen-audio-root', $stub) (Get-StubEnvironment 'fail')
    Assert-True ($r.Exit -ne 0 -and $r.Err -match 'stub engine refused') "gen-audio failure was not surfaced: exit $($r.Exit) $($r.Err)"
    $r = Invoke-Cli @('speak', '--text', 'x', '--out-file', (Join-Path $OutputRoot 'drift.wav'), '--voice', 'af_heart', '--python', $Python, '--gen-audio-root', $stub) (Get-StubEnvironment 'badhash')
    Assert-True ($r.Exit -ne 0 -and $r.Err -match 'does not match') "receipt hash drift was accepted: exit $($r.Exit) $($r.Err)"
    $r = Invoke-Cli @('speak', '--text', 'x', '--out-file', (Join-Path $OutputRoot 'novoice.wav'), '--python', $Python, '--gen-audio-root', $stub) $clean
    Assert-True ($r.Exit -ne 0 -and $r.Err -match 'Set a gen-audio voice') "missing voice was accepted: exit $($r.Exit) $($r.Err)"
    $r = Invoke-Cli @('speak', '--text', 'x', '--out-file', (Join-Path $OutputRoot 'bad.wav'), '--voice', 'v', '--bogus', '1') $clean
    Assert-True ($r.Exit -ne 0 -and $r.Err -match 'Unknown option') 'unknown speak option was accepted'

    # 3. transcribe: timed VibeVoice segments become SubRip cues with speaker labels.
    $srt = Join-Path $OutputRoot 'transcript.srt'
    $r = Invoke-Cli @('transcribe', '--input', $wav, '--model', $model.FullName, '--srt-out', $srt, '--python', $Python, '--gen-audio-root', $stub) $clean
    Assert-True ($r.Exit -eq 0) "transcribe exited $($r.Exit): $($r.Err)"
    $transcript = Get-Receipt $r.Out
    Assert-True (@($transcript.segments).Count -eq 2 -and $transcript.audioSeconds -gt 0 -and $transcript.model -eq 'vibevoice-asr-stub') "unexpected transcript receipt $($r.Out)"
    $cues = Get-Content -LiteralPath $srt -Raw
    Assert-True ($cues -match '(?m)^00:00:00,000 --> 00:00:00,\d{3}$' -and $cues -match 'Speaker 0: Open the review workspace\.' -and $cues -match 'Speaker 1: Confirm step two\.') "SRT cues are wrong:`n$cues"
    $r = Invoke-Cli @('transcribe', '--input', $wav, '--python', $Python, '--gen-audio-root', $stub) $clean
    Assert-True ($r.Exit -ne 0 -and $r.Err -match 'VibeVoice ASR checkpoint') 'transcribe without a checkpoint was accepted'

    # 4. Studio workspace: settings persist and both actions attach verified artifacts to the card.
    $ffmpeg = Join-Path $repo '.tools\ffmpeg\bin\ffmpeg.exe'
    if (-not (Test-Path -LiteralPath $ffmpeg)) { $ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source }
    $video = Join-Path $OutputRoot 'fixture.mp4'
    & $ffmpeg -hide_banner -loglevel error -y -f lavfi -i 'color=c=0x182721:s=640x360:d=3' -f lavfi -i 'sine=frequency=440:duration=3' -shortest -c:v libx264 -pix_fmt yuv420p -c:a aac $video
    Assert-True ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $video)) 'could not render the fixture video'
    $desktop = Get-ChildItem 'C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App' -Directory | Where-Object Name -Like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    $fsxText = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'YumlogGenAudioIntegration.fsx'))
    $fsxText = $fsxText.Replace('__DLL__', (Join-Path $bin 'Yumlog.Native.dll')).Replace('__SPEECH__', (Join-Path $bin 'System.Speech.dll')).Replace('__DRAWING__', (Join-Path $desktop.FullName 'System.Drawing.Common.dll')).Replace('__WINDOWS__', (Join-Path $bin 'Microsoft.Windows.SDK.NET.dll')).Replace('__WINRT__', (Join-Path $bin 'WinRT.Runtime.dll')).Replace('__OUTPUT__', $OutputRoot).Replace('__VIDEO__', $video).Replace('__FFMPEG__', $ffmpeg).Replace('__PYTHON__', $Python).Replace('__STUB__', $stub).Replace('__MODEL__', $model.FullName)
    $fsx = Join-Path $OutputRoot 'GenAudioIntegration.fsx'
    Set-Content -LiteralPath $fsx -Value $fsxText -Encoding utf8
    $saved = @{}; foreach ($key in $clean.Keys) { $saved[$key] = [Environment]::GetEnvironmentVariable($key); [Environment]::SetEnvironmentVariable($key, $null) }
    try { dotnet fsi --exec $fsx; Assert-True ($LASTEXITCODE -eq 0) 'Studio gen-audio workspace checks failed' }
    finally { foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) } }

    # 5. Optional: real speech through a gen-audio checkout (not part of CI).
    if ($LiveRoot) {
        if (-not $LivePython) { $LivePython = Join-Path $LiveRoot '.venv\Scripts\python.exe' }
        if (-not $LiveVoice) { throw '-LiveVoice is required with -LiveRoot.' }
        $live = Join-Path $OutputRoot 'live-narration.wav'
        $r = Invoke-Cli @('speak', '--text', 'Yumlog renders this narration with gen-audio.', '--out-file', $live, '--engine', $LiveEngine, '--voice', $LiveVoice, '--python', $LivePython, '--gen-audio-root', $LiveRoot) @{}
        Assert-True ($r.Exit -eq 0) "live gen-audio speak exited $($r.Exit): $($r.Err)"
        $liveReceipt = Get-Receipt $r.Out
        Write-Host "Live gen-audio speech: $($liveReceipt.engine)/$($liveReceipt.voice) $([math]::Round($liveReceipt.durationSeconds, 2))s at $($liveReceipt.sampleRate) Hz sha256 $($liveReceipt.audioSha256)"
    }
    Write-Host "gen-audio boundary contract passed ($checks checks)."
} finally {
    if ($ownsOutput) { Remove-Item -LiteralPath $OutputRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
