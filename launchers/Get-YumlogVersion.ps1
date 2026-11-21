# Prints the Yumlog release version: the <Version> in apps\Yumlog.Native\Yumlog.Native.fsproj.
# toolbox.ps1 -Step Uprev bumps that file, so package, archive and shortcut names follow it.
# Prints nothing when the script runs outside a Yumlog checkout.
[CmdletBinding()]
param([string]$RepoRoot = (Join-Path $PSScriptRoot '..'))
$project = Join-Path $RepoRoot 'apps\Yumlog.Native\Yumlog.Native.fsproj'
if (-not (Test-Path -LiteralPath $project)) { return }
$value = @(([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if ($value -notmatch '^\d+\.\d+\.\d+$') { throw "Unexpected Yumlog.Native version '$value' in $project." }
$value
