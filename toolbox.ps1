#Requires -Version 7.2
<#
.SYNOPSIS
Yumlog Windows toolbox: uprev, gate, clean production build, package verify, install, launch and verify.

.DESCRIPTION
One entry point for the Yumlog release workflow, modeled on the a2swe toolbox.

Steps (run in this order when -Step All, the default):
  Uprev    Bumps the version (default: Patch; -Bump Minor|Major, or -Version x.y.z) in
           every version source and keeps them in sync: all app .csproj/.fsproj
           Version/AssemblyVersion/FileVersion, Package.appxmanifest, Paperboy's
           generated-card version, the NPU emulator package.json/package-lock.json,
           Cargo.toml, Cargo.lock entry and tauri.conf.json, and the production
           Build/Install/Test launchers.
  Gate     Mirrors .github/workflows/ci.yml (all builds, self-tests, Pester, APIM,
           WidgetStudio, native/MDKG/handoff contracts, agent protocol, media
           presets, Studio smoke, cargo check/test). Fails on any red step.
  Build    Clean production build: removes every app bin/obj and dist\Yumlog-<version>,
           then runs launchers\Build-YumlogProduction.ps1 (self-contained win-x64).
  Verify   launchers\Test-YumlogProductionPackage.ps1 plus SHA256 of the archive.
  Install  Stops running Yumlog processes, then installs the package in place
           (launchers\Install-YumlogProduction.ps1 -Force).
  Launch   Starts the installed Yumlog.Native.exe and checks for a responsive
           "Yumlog Studio" window whose ProductVersion matches the repo version.

Every run writes a timestamped log under logs\toolbox-*.log.

.EXAMPLE
pwsh -File .\toolbox.ps1 -Bump Minor             # 0.5.0 -> 0.6.0, gate, build, verify, install, launch
pwsh -File .\toolbox.ps1 -Step Uprev -Bump Minor # only bump versions
pwsh -File .\toolbox.ps1 -NoUprev                # rebuild the current version
#>
[CmdletBinding()]
param(
    [ValidateSet('All', 'Uprev', 'Gate', 'Build', 'Verify', 'Install', 'Launch')]
    [string[]]$Step = 'All',
    [ValidateSet('Patch', 'Minor', 'Major')]
    [string]$Bump = 'Patch',
    [string]$Version,
    [switch]$NoUprev,
    [switch]$SkipStudioUi,
    [switch]$SkipCargo
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$logs = Join-Path $repo 'logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
$log = Join-Path $logs ('toolbox-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')

function Write-Log([string]$Message) {
    $line = '{0} {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message
    Write-Host $line
    Add-Content -LiteralPath $log -Value $line
}

function Invoke-Checked([string]$Name, [scriptblock]$Block) {
    Write-Log "== $Name"
    $global:LASTEXITCODE = 0
    & $Block 2>&1 | ForEach-Object { Add-Content -LiteralPath $log -Value $_; $_ }
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
}

function Get-RepoVersion {
    $project = [xml](Get-Content -LiteralPath (Join-Path $repo 'apps\Yumlog.Native\Yumlog.Native.fsproj') -Raw)
    $value = @($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
    if ($value -notmatch '^\d+\.\d+\.\d+$') { throw "Unexpected Yumlog.Native version '$value'." }
    $value
}

function Get-NextVersion([string]$Current) {
    if ($Version) {
        if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "-Version must be major.minor.patch, got '$Version'." }
        return $Version
    }
    $major, $minor, $patch = $Current.Split('.') | ForEach-Object { [int]$_ }
    switch ($Bump) {
        'Major' { "$($major + 1).0.0" }
        'Minor' { "$major.$($minor + 1).0" }
        default { "$major.$minor.$($patch + 1)" }
    }
}

function Update-File([string]$RelativePath, [scriptblock]$Transform) {
    $path = Join-Path $repo $RelativePath
    $text = [IO.File]::ReadAllText($path)
    $updated = & $Transform $text
    if ($updated -eq $text) { throw "Uprev made no change in $RelativePath." }
    [IO.File]::WriteAllText($path, $updated, [Text.UTF8Encoding]::new($false))
    Write-Log "   updated $RelativePath"
}

function Invoke-Uprev {
    $old = Get-RepoVersion
    $new = Get-NextVersion $old
    if ($new -eq $old) { throw "Version is already $new." }
    Write-Log "== Uprev $old -> $new ($Bump)"
    $o = [regex]::Escape($old)
    $projects = @(
        'apps\Paperboy.Desktop\Paperboy.Desktop.csproj'
        'apps\Paperboy.Mcp\Paperboy.Mcp.csproj'
        'apps\Yumlog.APIM.Service\Yumlog.APIM.Service.fsproj'
        'apps\Yumlog.Native\Yumlog.Native.fsproj'
        'apps\Yumlog.Native.Cli\Yumlog.Native.Cli.fsproj'
        'apps\Yumlog.WidgetStudio\Yumlog.WidgetStudio.csproj'
    )
    foreach ($project in $projects) {
        Update-File $project {
            param($t)
            $t = [regex]::Replace($t, "<Version>$o</Version>", "<Version>$new</Version>")
            [regex]::Replace($t, "<(AssemblyVersion|FileVersion)>$o\.0</\1>", { param($m) "<$($m.Groups[1].Value)>$new.0</$($m.Groups[1].Value)>" })
        }
    }
    Update-File 'apps\Yumlog.Native\Package.appxmanifest' { param($t) [regex]::Replace($t, "Version=`"$o\.0`"", "Version=`"$new.0`"") }
    Update-File 'apps\Paperboy.Desktop\MainWindow.xaml.cs' {
        param($t)
        $t = $t.Replace("root[`"version`"] = `"$old`";", "root[`"version`"] = `"$new`";")
        $t.Replace("Paperboy v$old", "Paperboy v$new")
    }
    Update-File 'apps\Yumlog.NpuEmulator\package.json' { param($t) [regex]::Replace($t, "(?m)^(  `"version`": `")$o`"", "`${1}$new`"") }
    Update-File 'apps\Yumlog.NpuEmulator\package-lock.json' {
        param($t)
        $t = [regex]::Replace($t, "(?m)^(  `"version`": `")$o`"", "`${1}$new`"", 1)
        [regex]::Replace($t, "(?s)(`"packages`": \{\s*`"`": \{.*?`"version`": `")$o`"", "`${1}$new`"", 1)
    }
    Update-File 'apps\Yumlog.NpuEmulator\src-tauri\Cargo.toml' { param($t) [regex]::Replace($t, "(?m)^version = `"$o`"", "version = `"$new`"", 1) }
    Update-File 'apps\Yumlog.NpuEmulator\src-tauri\Cargo.lock' { param($t) [regex]::Replace($t, "(name = `"yumlog-npu-emulator`"\r?\nversion = `")$o`"", "`${1}$new`"") }
    Update-File 'apps\Yumlog.NpuEmulator\src-tauri\tauri.conf.json' { param($t) [regex]::Replace($t, "(`"version`": `")$o`"", "`${1}$new`"", 1) }
    foreach ($script in 'launchers\Build-YumlogProduction.ps1', 'launchers\Install-YumlogProduction.ps1', 'launchers\Test-YumlogProductionPackage.ps1') {
        Update-File $script { param($t) $t.Replace("'$old'", "'$new'").Replace("Yumlog-$old", "Yumlog-$new").Replace(" $old.lnk", " $new.lnk") }
    }
    $left = git -C $repo grep -n -F $old -- 'apps/*.csproj' 'apps/*.fsproj' 'launchers/*Production*.ps1' 'apps/Yumlog.Native/Package.appxmanifest'
    if ($left) { throw "Uprev left old version references:`n$($left -join "`n")" }
    Write-Log "   version is now $new"
}

function Invoke-Gate {
    Push-Location $repo
    try {
        Invoke-Checked 'Paperboy desktop build' { dotnet build .\apps\Paperboy.Desktop\Paperboy.Desktop.csproj -c Release --nologo -v q }
        Invoke-Checked 'Paperboy MCP build' { dotnet build .\apps\Paperboy.Mcp\Paperboy.Mcp.csproj -c Release --nologo -v q }
        Invoke-Checked 'Native desktop build' { dotnet build .\apps\Yumlog.Native\Yumlog.Native.fsproj -c Release --nologo -v q }
        Invoke-Checked 'Native CLI build' { dotnet build .\apps\Yumlog.Native.Cli\Yumlog.Native.Cli.fsproj -c Release --nologo -v q }
        Invoke-Checked 'Native self-test' { & .\apps\Yumlog.Native.Cli\bin\Release\net10.0-windows10.0.26100.0\win-x64\Yumlog.Native.Cli.exe selftest }
        Invoke-Checked 'Pester contracts' {
            Import-Module Pester -MinimumVersion 5.0 -ErrorAction Stop
            $result = Invoke-Pester -Path .\Skills\Test-ScreenSkills.Tests.ps1, .\Skills\Test-PaperboyBundle.Tests.ps1, .\Skills\Test-PaperboyExtensions.Tests.ps1, .\Skills\Test-PaperboyAgentCardPaths.Tests.ps1 -Output Normal -PassThru
            "Pester passed=$($result.PassedCount) failed=$($result.FailedCount)"
            if ($result.FailedCount -gt 0 -or $result.PassedCount -eq 0) { $global:LASTEXITCODE = 1 }
        }
        Invoke-Checked 'APIM build' { dotnet build .\apps\Yumlog.APIM.Service\Yumlog.APIM.Service.fsproj -c Release --nologo -v q }
        Invoke-Checked 'APIM contracts' { .\launchers\Test-YumlogAPIMRoundTrip.ps1 }
        Invoke-Checked 'WidgetStudio self-test' { dotnet run --project .\apps\Yumlog.WidgetStudio\Yumlog.WidgetStudio.csproj -c Release -- self-test }
        Invoke-Checked 'Native extension, MDKG and handoff contracts' {
            .\apps\Yumlog.Native\Test-YumlogNativeExtensibility.ps1 -Configuration Release
            .\launchers\Test-YumlogNativeMdkgRoundTrip.ps1 -Configuration Release
            .\launchers\Test-YumlogNativeHandoff.ps1 -Configuration Release
        }
        Invoke-Checked 'Agent client protocol' { .\launchers\Test-YumlogAgentProtocol.ps1 -DotnetPath dotnet }
        Invoke-Checked 'Media presets' { dotnet fsi .\launchers\test-media-presets.fsx }
        Invoke-Checked 'Studio smoke' {
            $smoke = Join-Path $repo 'dist\StudioSmoke-toolbox'
            Remove-Item -LiteralPath $smoke -Recurse -Force -ErrorAction SilentlyContinue
            try { .\launchers\Test-YumlogStudioSmoke.ps1 -OutputRoot $smoke -SkipUi:$SkipStudioUi }
            finally { Remove-Item -LiteralPath $smoke -Recurse -Force -ErrorAction SilentlyContinue }
        }
        if (-not $SkipCargo) {
            Invoke-Checked 'NPU emulator npm dependencies' { npm ci --prefix apps/Yumlog.NpuEmulator --ignore-scripts --no-audit --no-fund }
            Invoke-Checked 'NPU emulator cargo check' {
                cargo check --locked --manifest-path apps/Yumlog.NpuEmulator/src-tauri/Cargo.toml
                $graph = cargo tree --locked --manifest-path apps/Yumlog.NpuEmulator/src-tauri/Cargo.toml -i glib --target all
                if ($LASTEXITCODE -ne 0 -or ($graph -join [Environment]::NewLine) -match 'glib v0\.(?:[0-9]|1[0-9])\.') { throw "Vulnerable glib graph detected." }
                $randGraph = cargo tree --locked --manifest-path apps/Yumlog.NpuEmulator/src-tauri/Cargo.toml -d --target all
                if (($randGraph -join [Environment]::NewLine) -match 'rand v0\.7\.') { throw "Vulnerable rand graph detected." }
            }
            Invoke-Checked 'NPU emulator cargo test' { cargo test --locked --manifest-path apps/Yumlog.NpuEmulator/src-tauri/Cargo.toml }
            Invoke-Checked 'NPU emulator executable' { npm run --prefix apps/Yumlog.NpuEmulator tauri -- build --ci --no-bundle }
        }
    } finally { Pop-Location }
    Write-Log 'Gate passed.'
}

function Invoke-CleanBuild {
    $current = Get-RepoVersion
    Push-Location $repo
    try {
        Write-Log "== Clean production build $current"
        Get-ChildItem -LiteralPath (Join-Path $repo 'apps') -Directory | ForEach-Object {
            foreach ($child in 'bin', 'obj') { Remove-Item -LiteralPath (Join-Path $_.FullName $child) -Recurse -Force -ErrorAction SilentlyContinue }
        }
        Remove-Item -LiteralPath (Join-Path $repo "dist\Yumlog-$current") -Recurse -Force -ErrorAction SilentlyContinue
        Invoke-Checked 'FFmpeg tools' { .\launchers\install.ps1 }
        Invoke-Checked 'Production package' { .\launchers\Build-YumlogProduction.ps1 -DotnetPath dotnet | Format-List | Out-String }
    } finally { Pop-Location }
}

function Invoke-PackageVerify {
    $current = Get-RepoVersion
    Invoke-Checked 'Production package smoke test' { & (Join-Path $repo 'launchers\Test-YumlogProductionPackage.ps1') | Format-List | Out-String }
    $archive = Join-Path $repo "dist\Yumlog-$current\Yumlog-$current-win-x64.zip"
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Log "   artifact $archive"
    Write-Log "   sha256 $hash bytes $((Get-Item -LiteralPath $archive).Length)"
}

function Stop-Yumlog {
    $root = Join-Path $env:LOCALAPPDATA 'DarbotLabs\Yumlog'
    Get-Process Yumlog.Native, Yumlog.Native.Cli, Paperboy, Paperboy.Mcp, Yumlog.APIM.Service, Yumlog.WidgetStudio -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { Write-Log "   stopping $($_.Name) pid $($_.Id)"; $_.Kill($true); $_.WaitForExit(10000) | Out-Null }
}

function Invoke-Install {
    $current = Get-RepoVersion
    Stop-Yumlog
    Invoke-Checked 'Install in place' { & (Join-Path $repo 'launchers\Install-YumlogProduction.ps1') -PackagePath (Join-Path $repo "dist\Yumlog-$current\package") -Force | Format-List | Out-String }
}

function Invoke-LaunchVerify {
    $current = Get-RepoVersion
    $root = (Get-Content -LiteralPath (Join-Path $env:LOCALAPPDATA 'DarbotLabs\Yumlog\current.txt') -Raw).Trim()
    $exe = Join-Path $root 'Yumlog.Native\Yumlog.Native.exe'
    $product = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
    if (-not $product.StartsWith($current, [StringComparison]::Ordinal)) { throw "Installed ProductVersion $product does not match $current." }
    Stop-Yumlog
    $process = Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -PassThru
    $deadline = (Get-Date).AddSeconds(45)
    do { Start-Sleep -Seconds 1; $process.Refresh() } until ($process.HasExited -or $process.MainWindowTitle -eq 'Yumlog Studio' -or (Get-Date) -gt $deadline)
    if ($process.HasExited) { throw "Yumlog Studio exited during launch (exit $($process.ExitCode))." }
    if ($process.MainWindowTitle -ne 'Yumlog Studio' -or -not $process.Responding) { throw "Yumlog Studio window not ready (title '$($process.MainWindowTitle)', responding $($process.Responding))." }
    Write-Log "   launched pid $($process.Id) title '$($process.MainWindowTitle)' responding $($process.Responding) version $product"
}

$all = $Step -contains 'All'
Write-Log "Yumlog toolbox: steps $($Step -join ',') log $log"
try {
    if (($all -and -not $NoUprev) -or $Step -contains 'Uprev') { Invoke-Uprev }
    if ($all -or $Step -contains 'Gate') { Invoke-Gate }
    if ($all -or $Step -contains 'Build') { Invoke-CleanBuild }
    if ($all -or $Step -contains 'Verify') { Invoke-PackageVerify }
    if ($all -or $Step -contains 'Install') { Invoke-Install }
    if ($all -or $Step -contains 'Launch') { Invoke-LaunchVerify }
    Write-Log "Toolbox finished: version $(Get-RepoVersion)"
} catch {
    Write-Log "TOOLBOX FAILED: $($_.Exception.Message)"
    throw
}
