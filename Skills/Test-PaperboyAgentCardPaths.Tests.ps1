# Relative (Harvest) and absolute (Video library) media.path forms in Paperboy agent cards
# must normalize to the same media identity. Fixtures are JSON only; no media is checked in.
BeforeAll {
    $script:repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    . (Join-Path $PSScriptRoot 'Paperboy.AgentCards.ps1')
    $script:fixtures = Join-Path $PSScriptRoot 'Fixtures\AgentCards'
    $script:videoCard = Join-Path $script:fixtures 'Video\yumlog-v050-review.agent-card.json'
    $script:session = Join-Path $script:fixtures 'Harvest\session-20261119-214619'
    $script:harvestCard = Join-Path $script:session 'yumlog-v050-review.agent-card.json'
    $script:harvest = Get-Content -LiteralPath (Join-Path $script:session 'harvest.json') -Raw | ConvertFrom-Json
    $script:schema = Join-Path $script:repoRoot 'apps\Paperboy.Desktop\Assets\AgentCards\paperboy.video-agent-card.schema.json'
}

Describe "Paperboy agent-card media paths" {
    It "Keeps the library card absolute and the Harvest card relative" {
        (Resolve-PaperboyMediaPath $script:videoCard).IsRelative | Should -BeFalse
        (Resolve-PaperboyMediaPath $script:harvestCard).IsRelative | Should -BeTrue
    }

    It "Resolves a relative media.path against the card's own folder" {
        $media = Resolve-PaperboyMediaPath $script:harvestCard
        $media.Path | Should -Be ([IO.Path]::GetFullPath((Join-Path $script:session 'yumlog-v050-review.mp4')))
        [IO.Path]::IsPathFullyQualified($media.Path) | Should -BeTrue
    }

    It "Normalizes both forms to the same media identity" {
        $library = Resolve-PaperboyMediaPath $script:videoCard
        $harvested = Resolve-PaperboyMediaPath $script:harvestCard
        foreach ($media in $library, $harvested) {
            [IO.Path]::GetFileName($media.Path) | Should -Be $media.FileName
        }
        $harvested.FileName | Should -Be $library.FileName
        $harvested.MediaKey | Should -Be $library.MediaKey
        $library.Sha256 | Should -Match '^[0-9a-f]{64}$'
    }

    It "Matches the Harvest manifest entry for the relative card" {
        $harvested = Resolve-PaperboyMediaPath $script:harvestCard
        $entry = @($script:harvest.files | Where-Object path -EQ $harvested.RawPath)
        $entry.Count | Should -Be 1
        $entry[0].sha256 | Should -Be $harvested.Sha256
        [int64]$entry[0].bytes | Should -Be $harvested.Bytes
        $script:harvest.schema | Should -Be 'urn:yumlog:harvest:v1'
    }

    It "Accepts forward slashes in relative paths" {
        $temp = Join-Path ([IO.Path]::GetTempPath()) ("paperboy-card-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $temp | Out-Null
        try {
            $card = Get-Content -LiteralPath $script:harvestCard -Raw | ConvertFrom-Json
            $card.media.path = 'media/yumlog-v050-review.mp4'
            $path = Join-Path $temp 'card.json'
            $card | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding utf8
            (Resolve-PaperboyMediaPath $path).Path | Should -Be ([IO.Path]::GetFullPath((Join-Path $temp 'media\yumlog-v050-review.mp4')))
        } finally {
            Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It "Keeps both cards valid against the Paperboy video card schema" {
        foreach ($card in $script:videoCard, $script:harvestCard) {
            Get-Content -LiteralPath $card -Raw | Test-Json -SchemaFile $script:schema -ErrorAction Stop | Should -BeTrue
        }
    }

    It "Contains no user profile paths" {
        Get-ChildItem -LiteralPath $script:fixtures -Recurse -File | ForEach-Object {
            Get-Content -LiteralPath $_.FullName -Raw | Should -Not -Match '\\\\Users\\\\'
        }
    }
}
