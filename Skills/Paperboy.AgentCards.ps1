# Paperboy agent-card helpers shared by Skills tests and tooling.
# A card's media.path is absolute when written next to a library video and
# relative (to the card's own folder) when written into a Harvest session.

function Resolve-PaperboyMediaPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$CardPath)

    $cardFull = [IO.Path]::GetFullPath($CardPath)
    $card = Get-Content -LiteralPath $cardFull -Raw | ConvertFrom-Json
    $raw = [string]$card.media.path
    if ([string]::IsNullOrWhiteSpace($raw)) { throw "Agent card has no media.path: $cardFull" }

    $portable = $raw.Replace('/', '\')
    $isRelative = -not [IO.Path]::IsPathFullyQualified($portable)
    $resolved = if ($isRelative) {
        [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $cardFull) $portable))
    } else {
        [IO.Path]::GetFullPath($portable)
    }

    [pscustomobject]@{
        CardPath   = $cardFull
        RawPath    = $raw
        IsRelative = $isRelative
        Path       = $resolved
        FileName   = [string]$card.media.fileName
        Sha256     = ([string]$card.media.sha256).ToLowerInvariant()
        Bytes      = [int64]$card.media.bytes
        MediaKey   = "$(([string]$card.media.sha256).ToLowerInvariant()):$([int64]$card.media.bytes)"
    }
}
