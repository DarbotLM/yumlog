[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://127.0.0.1:5117',
    [string]$Endpoint = 'http://127.0.0.1:52495/v1',
    [int]$TimeoutSec = 30,
    [string]$TagPath = '',
    [string]$Model = '',
    [string]$ReportPath = '',
    [string]$DotnetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repoRoot 'apps\Paperboy.Mcp\Paperboy.Mcp.csproj'
& $DotnetPath build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Paperboy.Mcp build failed with exit code $LASTEXITCODE."
}

$dll = Join-Path $repoRoot 'apps\Paperboy.Mcp\bin\Release\net11.0-windows\Paperboy.Mcp.dll'
$allowedRoot = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Yumlog Native'
$allowedRoots = @($allowedRoot)
if ($TagPath) {
    $resolvedTagPath = (Resolve-Path -LiteralPath $TagPath -ErrorAction Stop).Path
    $allowedRoots += $resolvedTagPath
}
$previousAllowed = $env:YUMLOG_AGENT_ALLOWED_ROOTS
$process = $null

function Invoke-Mcp([object]$Message) {
    return Invoke-RestMethod `
        -Method Post `
        -Uri "$BaseUrl/mcp" `
        -ContentType 'application/json' `
        -Headers @{ Accept = 'application/json' } `
        -Body ($Message | ConvertTo-Json -Depth 20 -Compress)
}

try {
    $env:YUMLOG_AGENT_ALLOWED_ROOTS = $allowedRoots -join ';'
    $process = Start-Process -FilePath $DotnetPath -ArgumentList @(
        "`"$dll`"",
        '--http',
        '--url',
        $BaseUrl
    ) -WorkingDirectory (Split-Path -Parent $dll) -PassThru -WindowStyle Hidden

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSec)
    do {
        try {
            $health = Invoke-RestMethod "$BaseUrl/health" -TimeoutSec 2
            break
        } catch {
            if ([DateTimeOffset]::UtcNow -ge $deadline) {
                throw "Paperboy agent protocol did not become healthy at $BaseUrl."
            }
            Start-Sleep -Milliseconds 300
        }
    } while ($true)

    $initialize = Invoke-Mcp @{
        jsonrpc = '2.0'
        id = 1
        method = 'initialize'
        params = @{
            protocolVersion = '2025-06-18'
            capabilities = @{}
            clientInfo = @{ name = 'yumlog-smoke'; version = '1.0' }
        }
    }
    if ($initialize.result.protocolVersion -ne '2025-06-18') {
        throw 'MCP protocol negotiation failed.'
    }

    $notification = Invoke-WebRequest `
        -Method Post `
        -Uri "$BaseUrl/mcp" `
        -ContentType 'application/json' `
        -Headers @{ Accept = 'application/json' } `
        -Body '{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}'
    if ($notification.StatusCode -ne 202 -or $notification.RawContentLength -ne 0) {
        throw 'MCP notification must return 202 with no body.'
    }

    $tools = Invoke-Mcp @{
        jsonrpc = '2.0'
        id = 2
        method = 'tools/list'
        params = @{}
    }
    $toolNames = @($tools.result.tools | ForEach-Object { $_.name })
    if ($toolNames -notcontains 'paperboy.foundryLocal.analyze') {
        throw 'Read-only tagging tool is missing from HTTP MCP.'
    }
    if ($toolNames | Where-Object { $_ -match 'bundle\.pack|bundle\.unpack|pmm\.download|pmm\.load|extensions\.invoke' }) {
        throw 'HTTP MCP exposed a mutation or arbitrary execution tool.'
    }

    $plugin = Invoke-RestMethod "$BaseUrl/.well-known/ai-plugin.json"
    $openapi = Invoke-RestMethod "$BaseUrl/openapi.json"
    if ($plugin.schema_version -ne 'v1' -or $plugin.api.type -ne 'openapi') {
        throw 'Agent Plugin 1.0 manifest is invalid.'
    }
    $tagProperties = $openapi.paths.'/api/tag'.post.requestBody.content.'application/json'.schema.properties
    if ($tagProperties.PSObject.Properties.Name -contains 'outputDirectory') {
        throw 'Read-only OpenAPI tagging exposed outputDirectory.'
    }

    $sse = Invoke-WebRequest `
        -Method Post `
        -Uri "$BaseUrl/mcp" `
        -ContentType 'application/json' `
        -Headers @{ Accept = 'text/event-stream' } `
        -Body '{"jsonrpc":"2.0","id":3,"method":"ping","params":{}}'
    if ($sse.Headers.'Content-Type' -notlike 'text/event-stream*' -or $sse.Content -notmatch 'event: message') {
        throw 'Streamable HTTP SSE response negotiation failed.'
    }

    $outside = Invoke-RestMethod `
        -Method Post `
        -Uri "$BaseUrl/api/tag" `
        -ContentType 'application/json' `
        -Body (@{
            sourcePaths = @((Join-Path $env:SystemDrive 'Windows\System32\notepad.exe'))
            model = 'smoke'
        } | ConvertTo-Json -Compress)
    if ($outside.error.code -ne -32602) {
        throw 'HTTP tagging did not reject a source outside allowed Yumlog roots.'
    }

    $tagResult = $null
    if ($TagPath -and $Model) {
        $tagResult = Invoke-RestMethod `
            -Method Post `
            -Uri "$BaseUrl/api/tag" `
            -ContentType 'application/json' `
            -Body (@{
                sourcePaths = @($TagPath)
                model = $Model
                endpoint = $Endpoint
                includeImageContent = $true
                sampleVideoFrames = $true
            } | ConvertTo-Json -Compress)
        if ($tagResult.error -or $tagResult.result.isError) {
            $detail = if ($tagResult.error) { $tagResult.error.message } else { $tagResult.result.content[0].text }
            throw "Tagging pipeline failed: $detail"
        }
        if (-not $ReportPath) {
            $reportDirectory = Join-Path $repoRoot 'dist\tagging-reports'
            New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
            $ReportPath = Join-Path $reportDirectory ('paperboy-tagging-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
        }
        $ReportPath = [IO.Path]::GetFullPath($ReportPath)
        New-Item -ItemType Directory -Path (Split-Path -Parent $ReportPath) -Force | Out-Null
        $tagResult | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
    }

    [pscustomobject]@{
        BaseUrl = $BaseUrl
        ProtocolVersion = $initialize.result.protocolVersion
        ReadOnlyToolCount = $toolNames.Count
        StreamableSse = $true
        AgentPlugin = $plugin.schema_version
        TaggingExecuted = $null -ne $tagResult
        TaggedFiles = if ($tagResult) { @($tagResult.result.content[0].text | ConvertFrom-Json | ForEach-Object { $_.result.files }).Count } else { 0 }
        ReportPath = if ($ReportPath) { $ReportPath } else { $null }
    }
} finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit(5000) | Out-Null
        }
        $process.Dispose()
    }
    $env:YUMLOG_AGENT_ALLOWED_ROOTS = $previousAllowed
}
