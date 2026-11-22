# System specs and update-check contract for Yumlog.Native.Cli.
#   Default: spec collection (WMI/CIM + registry) and an offline update check that must fail quietly.
#   -Capture: also opens Settings > System > About, captures it and checks the PNG and its .specs.json sidecar
#             (needs an unlocked interactive desktop, so the gate runs it only when -Capture is passed).
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputRoot,
    [switch]$Capture,
    [int]$MinimumPngBytes = 20000
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cli = Join-Path (& (Join-Path $PSScriptRoot 'Get-YumlogBuildOutput.ps1') -Project Yumlog.Native.Cli -Configuration $Configuration) 'Yumlog.Native.Cli.exe'
if (-not (Test-Path -LiteralPath $cli)) { throw "Build Yumlog.Native.Cli ($Configuration) first: $cli" }
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo ('dist\SystemSpecs-' + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$checks = 0
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw "System specs contract failed: $Message" }; $script:checks++ }

$collected = Join-Path $OutputRoot 'collect.json'
& $cli specs --collect-only true --json-out $collected | Out-Null
Assert ($LASTEXITCODE -eq 0) "specs --collect-only exited $LASTEXITCODE"
$doc = Get-Content -LiteralPath $collected -Raw | ConvertFrom-Json
Assert ($doc.schema -eq 'yumlog.system-specs/1.0') "schema is '$($doc.schema)'"
foreach ($field in 'deviceName', 'processor', 'osEdition', 'osBuild', 'systemType') { Assert (-not [string]::IsNullOrWhiteSpace($doc.specs.$field)) "specs.$field is empty" }
Assert ($doc.specs.installedRamGb -gt 0) 'specs.installedRamGb is not positive'
Assert (@($doc.specs.volumes).Count -gt 0) 'no fixed volumes reported'
Assert (@($doc.tags) -contains 'system-specs') 'tags miss system-specs'

# Offline update check: loopback discard port refuses, the command must still exit 0 with "unavailable".
$update = Join-Path $OutputRoot 'update-check.json'
$watch = [Diagnostics.Stopwatch]::StartNew()
& $cli update-check --api 'http://127.0.0.1:9' --timeout-ms 1500 --json-out $update 2>$null | Out-Null
$watch.Stop()
Assert ($LASTEXITCODE -eq 0) "offline update-check exited $LASTEXITCODE"
$result = Get-Content -LiteralPath $update -Raw | ConvertFrom-Json
Assert ($result.status -eq 'unavailable') "offline update-check status '$($result.status)'"
Assert ($watch.ElapsedMilliseconds -lt 10000) "offline update-check took $($watch.ElapsedMilliseconds) ms"
& $cli update-check --repo 'not a repo' --json-out $update 2>$null | Out-Null
Assert ((Get-Content -LiteralPath $update -Raw | ConvertFrom-Json).detail -match 'owner/name') 'invalid repo was not rejected'

if ($Capture) {
    $shots = Join-Path $OutputRoot 'capture'
    $sidecarOut = Join-Path $OutputRoot 'capture.json'
    & $cli specs --out-dir $shots --json-out $sidecarOut | Out-Null
    Assert ($LASTEXITCODE -eq 0) "specs capture exited $LASTEXITCODE"
    $sidecar = Get-Content -LiteralPath $sidecarOut -Raw | ConvertFrom-Json
    $png = Get-Item -LiteralPath $sidecar.screenshot.path
    Assert ($png.Length -ge $MinimumPngBytes) "screenshot is only $($png.Length) bytes"
    Assert ((Get-FileHash -LiteralPath $png.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -eq $sidecar.screenshot.sha256) 'screenshot hash does not match the sidecar'
    Assert (Test-Path -LiteralPath ([IO.Path]::ChangeExtension($png.FullName, '.specs.json'))) 'sidecar is not beside the screenshot'
}
Write-Host "System specs contract passed ($checks checks$(if ($Capture) { ', with capture' }))."
