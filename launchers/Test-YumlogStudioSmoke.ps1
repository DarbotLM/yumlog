# Studio smoke: native workspace flow plus an optional Playwright UI pass over the real WebView2 Studio.
# Uses a synthetic FFmpeg fixture only. Outputs stay under dist\StudioSmoke-* (ignored by git).
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputRoot,
    [string]$FfmpegPath,
    [int]$DevToolsPort = 47619,
    # Existing node_modules folder that already contains playwright-core (for offline or locked-down npm).
    [string]$NodeModules,
    [switch]$SkipUi
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$harness = Join-Path $repo 'qa\studio-smoke'
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo ('dist\StudioSmoke-' + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

if (-not $FfmpegPath) {
    $local = Join-Path $repo '.tools\ffmpeg\bin\ffmpeg.exe'
    $FfmpegPath = if (Test-Path -LiteralPath $local) { $local } else { (Get-Command ffmpeg -ErrorAction Stop).Source }
}

$bin = Join-Path $OutputRoot 'bin'
dotnet build (Join-Path $harness 'StudioSmoke.csproj') -c $Configuration --nologo -v q -o $bin
if ($LASTEXITCODE -ne 0) { throw 'Studio smoke host build failed.' }
if (-not (Test-Path -LiteralPath (Join-Path $bin 'Yumlog.Native.dll'))) {
    throw 'Studio smoke host did not receive the required Yumlog.Native.dll.'
}

$video = & (Join-Path $harness 'New-StudioSmokeFixture.ps1') -Output (Join-Path $OutputRoot 'fixture\Capture Review.mp4') -Ffmpeg $FfmpegPath
$workspace = Join-Path $OutputRoot 'workspace'
$smoke = Join-Path $bin 'StudioSmoke.exe'
& $smoke native $workspace $video
if ($LASTEXITCODE -ne 0) { throw "Studio native smoke failed (exit $LASTEXITCODE)." }

if ($SkipUi) { Write-Host 'Studio native smoke passed (UI pass skipped).'; return }

$ui = Join-Path $OutputRoot 'ui'
New-Item -ItemType Directory -Force -Path $ui | Out-Null
Copy-Item -LiteralPath (Join-Path $harness 'package.json'), (Join-Path $harness 'check-ui.mjs') -Destination $ui
$linkedModules = $null
if ($NodeModules) {
    if (-not (Test-Path -LiteralPath (Join-Path $NodeModules 'playwright-core\package.json'))) { throw "playwright-core was not found under $NodeModules." }
    $linkedModules = Join-Path $ui 'node_modules'
    New-Item -ItemType Junction -Path $linkedModules -Target ([IO.Path]::GetFullPath($NodeModules)) | Out-Null
} else {
    Push-Location $ui
    try {
        npm install --no-audit --no-fund --loglevel=error
        if ($LASTEXITCODE -ne 0) { throw 'npm install for the Studio UI check failed. Pass -NodeModules with an existing playwright-core install, or -SkipUi.' }
    } finally { Pop-Location }
}

$previous = $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS
$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$DevToolsPort"
$host_ = $null
try {
    $host_ = Start-Process -FilePath $smoke -ArgumentList @('ui', "`"$workspace`"") -PassThru -WindowStyle Normal
    $deadline = (Get-Date).AddSeconds(60)
    $ready = $false
    while (-not $ready -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        if ($host_.HasExited) { throw "Studio UI host exited early (exit $($host_.ExitCode))." }
        try { $ready = @((Invoke-RestMethod "http://127.0.0.1:$DevToolsPort/json/list" -TimeoutSec 2) | Where-Object url -like '*yumlog-studio.local*').Count -gt 0 } catch { }
    }
    if (-not $ready) { throw 'Studio WebView2 did not expose the DevTools endpoint in time.' }
    node (Join-Path $ui 'check-ui.mjs') (Join-Path $OutputRoot 'studio-smoke.png')
    if ($LASTEXITCODE -ne 0) { throw "Studio UI check failed (exit $LASTEXITCODE)." }
} finally {
    $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = $previous
    if ($host_ -and -not $host_.HasExited) { $host_.Kill($true); $host_.WaitForExit(10000) | Out-Null }
    if ($linkedModules -and (Test-Path -LiteralPath $linkedModules)) { [IO.Directory]::Delete($linkedModules) }
}
Write-Host "Studio smoke passed (native and UI). Screenshot: $(Join-Path $OutputRoot 'studio-smoke.png')"
