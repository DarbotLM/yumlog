[CmdletBinding()]
param(
    [ValidateSet('None', 'Minimal', 'Normal', 'Detailed', 'Diagnostic')]
    [string]$Verbosity = 'Detailed'
)

$ErrorActionPreference = 'Stop'
Import-Module Pester -MinimumVersion 5.0 -ErrorAction Stop

$testPaths = @(
    "$PSScriptRoot\..\Skills\Test-ScreenSkills.Tests.ps1"
    "$PSScriptRoot\..\Skills\Test-PaperboyBundle.Tests.ps1"
    "$PSScriptRoot\..\Skills\Test-PaperboyExtensions.Tests.ps1"
)

$result = Invoke-Pester -Path $testPaths -Output $Verbosity -PassThru
if ($result.FailedCount -gt 0 -or $result.PassedCount -eq 0) {
    exit 1
}

"Pester: $($result.PassedCount) passed, $($result.FailedCount) failed, $($result.SkippedCount) skipped."
