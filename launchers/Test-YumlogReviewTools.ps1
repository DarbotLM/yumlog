# Review tools contract: runs tools\review end to end on a synthetic review folder.
# Frames are drawn locally (no media, no user data). Pipeline under test:
#   read-frames-winrt.ps1 (classic Windows.Media.Ocr, Windows PowerShell) -> MdkgImport (native MDKG writer)
#   -> make_runbooks.py (Yumlog.Native.Cli runbook) -> MdkgImport verify (every runbook proof resolves).
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$CliRoot,
    [string]$OutputRoot,
    [string]$Python
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$tools = Join-Path $repo 'tools\review'
if (-not $CliRoot) { $CliRoot = Join-Path $repo "apps\Yumlog.Native.Cli\bin\$Configuration\net10.0-windows10.0.26100.0\win-x64" }
if (-not (Test-Path -LiteralPath (Join-Path $CliRoot 'Yumlog.Native.Cli.dll'))) { throw "Build Yumlog.Native.Cli ($Configuration) first; not found under $CliRoot." }
if (-not $Python) { $Python = @(Get-Command python, py -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source)[0] }
if (-not $Python) { throw 'Python 3 is required for tools\review\make_runbooks.py.' }
$ownsOutput = -not $OutputRoot
if (-not $OutputRoot) { $OutputRoot = Join-Path ([IO.Path]::GetTempPath()) ('yumlog-review-tools-' + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$review = Join-Path $OutputRoot 'review'
New-Item -ItemType Directory -Force -Path (Join-Path $review 'frames') | Out-Null
Set-Content -LiteralPath (Join-Path $OutputRoot '.owner') -Value 'Test-YumlogReviewTools.ps1 (Yumlog gate); safe to delete' -Encoding UTF8

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw "Review tools contract failed: $Message" } }

try {
    # Hygiene: the review tools and the smoke harness ship no binaries, media or personal paths.
    $tracked = @(git -C $repo ls-files -- tools/review qa/studio-smoke)
    Assert-True ($tracked.Count -gt 0) 'tools/review and qa/studio-smoke are not tracked.'
    $binary = @($tracked | Where-Object { $_ -match '\.(exe|dll|pdb|mp4|mov|mkv|wav|mp3|png|jpe?g|zip|mdkg)$' -or $_ -match '(^|/)(bin|obj|node_modules|release-[^/]+)/' })
    Assert-True ($binary.Count -eq 0) "binaries or media are tracked: $($binary -join ', ')"
    $personal = @(git -C $repo grep -l -i -E 'C:\\+Users\\+[a-z]|OneDrive - |insurance_progressive' -- tools/review qa/studio-smoke)
    Assert-True ($personal.Count -eq 0) "personal paths in: $($personal -join ', ')"

    # Two synthetic 1280x720 frames with large text, drawn by Windows PowerShell (System.Drawing).
    $samples = @(
        @{ Name = 'frame-000.png'; Text = 'YUMLOG REVIEW ONE'; Seconds = 0.5; Index = 15 },
        @{ Name = 'frame-001.png'; Text = 'YUMLOG REVIEW TWO'; Seconds = 2.5; Index = 75 }
    )
    foreach ($sample in $samples) {
        $png = Join-Path $review "frames\$($sample.Name)"
        $draw = "Add-Type -AssemblyName System.Drawing; `$b=New-Object Drawing.Bitmap 1280,720; `$g=[Drawing.Graphics]::FromImage(`$b); `$g.Clear([Drawing.Color]::White); `$g.TextRenderingHint='AntiAlias'; `$f=New-Object Drawing.Font 'Segoe UI',64,([Drawing.FontStyle]::Bold); `$g.DrawString('$($sample.Text)',`$f,[Drawing.Brushes]::Black,80,300); `$g.Dispose(); `$b.Save('$png',[Drawing.Imaging.ImageFormat]::Png); `$b.Dispose()"
        powershell.exe -NoProfile -NonInteractive -Command $draw
        Assert-True (Test-Path -LiteralPath $png) "could not draw $($sample.Name)"
        $sample.Sha = Get-Sha256 $png
    }
    $manifest = Join-Path $review 'frames-manifest.json'
    @{ films = @(@{ id = 'demo'; samples = @($samples | ForEach-Object { @{ frame = "frames/$($_.Name)"; sha256 = $_.Sha; timestampSeconds = $_.Seconds; sourceFrameIndex = $_.Index } }) }) } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifest -Encoding UTF8

    # Authored captions and the film list (the "video" is a placeholder hash; no media is read).
    $vtt = Join-Path $review 'demo.vtt'
    "WEBVTT`n`n00:00:00.000 --> 00:00:02.000`nOpen the review workspace.`n`n00:00:02.000 --> 00:00:04.000`nConfirm the second step.`n" | Set-Content -LiteralPath $vtt -Encoding UTF8 -NoNewline
    @{ films = @(@{ id = 'demo'; sha256 = ('0' * 64); captions = @{ path = 'demo.vtt'; sha256 = (Get-Sha256 $vtt) } }) } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $review 'yumlog-native-results.json') -Encoding UTF8

    # 1. Classic Windows OCR over hash-verified frames (WinRT projection needs Windows PowerShell).
    $ocr = Join-Path $review 'winrt-ocr-results.json'
    powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $tools 'read-frames-winrt.ps1') -Manifest $manifest -Output $ocr
    Assert-True ($LASTEXITCODE -eq 0) "read-frames-winrt.ps1 exited $LASTEXITCODE"
    $ocrResult = Get-Content -LiteralPath $ocr -Raw | ConvertFrom-Json
    Assert-True ($ocrResult.successfulFrames -eq 2) "expected 2 OCR frames, got $($ocrResult.successfulFrames)"
    Assert-True ((@($ocrResult.frames.text) -join ' ') -match 'YUMLOG') "OCR did not read the synthetic text: '$(@($ocrResult.frames.text) -join ' | ')'"

    # 2-4. Native MDKG import, runbooks through the CLI, then proof verification.
    $import = Join-Path $OutputRoot 'MdkgImport'
    dotnet build (Join-Path $tools 'MdkgImport\MdkgImport.csproj') -c $Configuration --nologo -v q -o $import
    Assert-True ($LASTEXITCODE -eq 0) 'MdkgImport build failed'
    $previous = $env:YUMLOG_CLI_ROOT
    $env:YUMLOG_CLI_ROOT = [IO.Path]::GetFullPath($CliRoot)
    try {
        & (Join-Path $import 'MdkgImport.exe') $review
        Assert-True ($LASTEXITCODE -eq 0) "MdkgImport exited $LASTEXITCODE"
        Assert-True (Test-Path -LiteralPath (Join-Path $review 'demo-evidence.mdkg')) 'demo-evidence.mdkg was not written'
        & $Python (Join-Path $tools 'make_runbooks.py') $review
        Assert-True ($LASTEXITCODE -eq 0) "make_runbooks.py exited $LASTEXITCODE"
        $index = Get-Content -LiteralPath (Join-Path $review 'yumlog-mdkg-runbook-index.json') -Raw | ConvertFrom-Json
        Assert-True (@($index).Count -eq 1 -and $index[0].mdkgSha256 -eq (Get-Sha256 (Join-Path $review 'demo-evidence.mdkg'))) 'runbook index does not match the MDKG'
        & (Join-Path $import 'MdkgImport.exe') $review verify
        Assert-True ($LASTEXITCODE -eq 0) "MdkgImport verify exited $LASTEXITCODE"
        $verified = @(Get-Content -LiteralPath (Join-Path $review 'yumlog-native-proof-verification.json') -Raw | ConvertFrom-Json)
        Assert-True ($verified.Count -eq 1 -and $verified[0].verified -and $verified[0].totalProofs -gt 0 -and $verified[0].mismatchedProofs -eq 0) "proof verification failed: $($verified | ConvertTo-Json -Compress)"
    } finally { $env:YUMLOG_CLI_ROOT = $previous }
    Write-Host "Review tools contract passed: 2 OCR frames, MDKG import, runbook, $($verified[0].resolvedProofs)/$($verified[0].totalProofs) proofs verified."
} finally {
    if ($ownsOutput) { Remove-Item -LiteralPath $OutputRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
