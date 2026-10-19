#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputRoot,
    [string]$InnoCompiler,
    [string]$ReuseDotnetEvidenceFrom,
    [switch]$SkipTests,
    [switch]$AllowUncommitted
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$island = Join-Path $repo 'src\Yuanshu.Island'
$cargo = Join-Path $island 'src-tauri'
$version = ([xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo ('artifacts\unified\' + $version + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputRoot)
# Never reuse a potentially installed or user-owned directory. All intermediate output is new.
if (Test-Path -LiteralPath $output) { throw 'build_output_already_exists_choose_new_directory' }
if (-not $InnoCompiler) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $InnoCompiler = $command.Source }
    else {
        $InnoCompiler = @(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
        ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler)) { throw 'Inno Setup compiler required; pass -InnoCompiler.' }
$innoAnchor = Join-Path ([IO.Path]::GetDirectoryName($InnoCompiler)) 'unins000.exe'
if (-not (Test-Path -LiteralPath $innoAnchor) -or
    ([Diagnostics.FileVersionInfo]::GetVersionInfo($innoAnchor).ProductVersion).Trim() -cne '6.7.3') { throw 'Pinned Inno Setup 6.7.3 required.' }

function Invoke-Step([string]$Name, [scriptblock]$Action) {
    Write-Host "[$Name]"
    & $Action 2>&1 | Tee-Object -FilePath (Join-Path $evidence ($Name + '.log'))
    if ($LASTEXITCODE -ne 0) { throw "build_step_failed:$Name" }
}
function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 20) + "`n", [Text.UTF8Encoding]::new($false))
}
function Source-Manifest {
    $paths = @(git -C $repo -c core.quotepath=false ls-files --cached --others --exclude-standard | Sort-Object -Unique)
    if ($LASTEXITCODE -ne 0) { throw 'source_inventory_failed' }
    foreach ($relative in $paths) {
        if ($relative -notmatch '^(src/|tests/|scripts/|installer/|prompts/|assets/|distribution/|Directory.Build.props$|global.json$|ScreenGuide.slnx$|NuGet.Config$)') { continue }
        $file = Join-Path $repo $relative
        if (Test-Path -LiteralPath $file -PathType Leaf) {
            [ordered]@{path=$relative;sha256=(Get-FileHash -LiteralPath $file).Hash;bytes=(Get-Item -LiteralPath $file).Length}
        }
    }
}

Push-Location $repo
try {
    $head = (git rev-parse HEAD).Trim()
    $dirty = -not [string]::IsNullOrWhiteSpace((git status --porcelain | Out-String))
    if ($dirty -and -not $AllowUncommitted) { throw 'Uncommitted source: commit the reviewed source or explicitly use -AllowUncommitted for a local candidate.' }
    $package = Get-Content -LiteralPath (Join-Path $island 'package.json') -Raw | ConvertFrom-Json
    $lock = Get-Content -LiteralPath (Join-Path $island 'package-lock.json') -Raw | ConvertFrom-Json -AsHashtable
    $tauri = Get-Content -LiteralPath (Join-Path $cargo 'tauri.conf.json') -Raw | ConvertFrom-Json
    $cargoText = Get-Content -LiteralPath (Join-Path $cargo 'Cargo.toml') -Raw
    if ($package.version -cne $version -or $lock.version -cne $version -or $tauri.version -cne $version -or
        $cargoText -notmatch ('(?m)^version = "' + [regex]::Escape($version) + '"\r?$') -or $tauri.bundle.active) { throw 'unified_version_or_bundle_mismatch' }

    $publish = Join-Path $output 'app'
    $clientStage = Join-Path $output 'staging\client'
    $hostStage = Join-Path $output 'staging\host'
    $evidence = Join-Path $output 'evidence'
    $installerOutput = Join-Path $output 'installer'
    New-Item -ItemType Directory -Path $publish,$clientStage,$hostStage,$evidence,$installerOutput -Force | Out-Null
    $source = @(Source-Manifest)
    Write-Json (Join-Path $evidence 'source-manifest.json') @{version=$version;head=$head;uncommitted=$dirty;files=$source}
    $sourceHash = (Get-FileHash -LiteralPath (Join-Path $evidence 'source-manifest.json')).Hash
    $identity = "$version+source.$($sourceHash.Substring(0,16).ToLowerInvariant())"
    $offline = Join-Path $evidence 'offline-nuget.config'
    [IO.File]::WriteAllText($offline, '<configuration><packageSources><clear /></packageSources></configuration>')
    $solution = Join-Path $repo 'ScreenGuide.slnx'
    # Locked restores may use the configured sources on a new build machine; no version updates.
    Invoke-Step 'dotnet-restore' { dotnet restore $solution --locked-mode --disable-parallel --nologo -p:NuGetAudit=false }
    if (-not $SkipTests) {
        if ($ReuseDotnetEvidenceFrom) {
            Invoke-Step 'dotnet-test-evidence' { & (Join-Path $PSScriptRoot 'Test-UnifiedDotnetEvidence.ps1') -PreviousEvidenceRoot $ReuseDotnetEvidenceFrom -CurrentSourceManifest (Join-Path $evidence 'source-manifest.json') -OutputRoot $evidence }
        } else {
            Invoke-Step 'dotnet-test' { dotnet test $solution -c Release --no-restore --maxcpucount:1 --logger trx --results-directory (Join-Path $evidence 'tests') }
        }
        Invoke-Step 'installer-contract-tests' { & (Join-Path $repo 'tests\ScreenGuide.Release.Tests\UnifiedInstallerLifecycle.Tests.ps1') }
    }
    foreach ($component in @('Host','Client')) {
        $project = Join-Path $repo "src\ScreenGuide.Desktop$component\ScreenGuide.Desktop$component.csproj"
        $stage = if ($component -eq 'Host') { $hostStage } else { $clientStage }
        Invoke-Step "dotnet-restore-$component" { dotnet restore $project --locked-mode --disable-parallel --nologo -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:NuGetAudit=false }
        Invoke-Step "dotnet-publish-$component" {
            dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore --output $stage `
                -p:RestoreLockedMode=true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
                "-p:InformationalVersion=$identity" -p:IncludeSourceRevisionInInformationalVersion=false
        }
        Copy-Item -Path (Join-Path $stage '*') -Destination $publish -Recurse -Force
    }
    Push-Location $island
    try {
        Invoke-Step 'npm-ci' { npm.cmd ci --no-audit --no-fund }
        if (-not $SkipTests) { Invoke-Step 'frontend-tests' { npm.cmd test } }
        Push-Location $cargo
        try {
            if (-not $SkipTests) {
                Invoke-Step 'rust-tests' { cargo test --locked --lib }
                Invoke-Step 'rust-clippy' { cargo clippy --locked --all-targets -- -D warnings }
            }
        } finally { Pop-Location }
        # This is only the child binary: Inno above remains the single installer owner.
        Invoke-Step 'island-release' { npm.cmd run tauri -- build --no-bundle -- --locked }
    } finally { Pop-Location }
    $native = Join-Path $cargo 'target\release\yuanshu-island.exe'
    New-Item -ItemType Directory -Path (Join-Path $publish 'island') -Force | Out-Null
    Copy-Item -LiteralPath $native -Destination (Join-Path $publish 'island\yuanshu-island.exe')
    foreach ($relative in @('ScreenGuide.DesktopClient.exe','ScreenGuide.DesktopHost.exe','island\yuanshu-island.exe')) {
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish $relative))
        $expectedFileVersions = if ($relative -eq 'island\yuanshu-island.exe') { @($version, $version + '.0') } else { @($version + '.0') }
        if ($info.FileVersion -cnotin $expectedFileVersions) { throw "binary_version_mismatch:$relative" }
    }
    Invoke-Step 'attribution' { & (Join-Path $PSScriptRoot 'New-UnifiedAttribution.ps1') -RepositoryRoot $repo -PublishRoot $publish -OutputRoot $evidence -ReleaseVersion $version }
    $include = Join-Path $output 'staging\distribution-notice-files.iss'
    [IO.File]::WriteAllText($include, '; All notices are already inside PublishRoot and installed with the unified payload.')
    $after = @(Source-Manifest)
    if (($source | ConvertTo-Json -Depth 5 -Compress) -cne ($after | ConvertTo-Json -Depth 5 -Compress)) { throw 'Source changed during the build. Create a fresh candidate.' }
    $artifactFiles = @(Get-ChildItem -LiteralPath $publish -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($publish,$_.FullName).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName).Hash;bytes=$_.Length}
    })
    Write-Json (Join-Path $publish 'product-manifest.json') @{version=$version;sourceSha256=$sourceHash;sourceHead=$head;uncommitted=$dirty;testsExecuted=(-not $SkipTests);files=$artifactFiles}
    Invoke-Step 'inno-compile' { & $InnoCompiler "/DMyAppVersion=$version" "/DPublishRoot=$publish" "/DDistributionInclude=$include" "/O$installerOutput" (Join-Path $repo 'installer\ScreenGuideDesktop.iss') }
    $installer = @(Get-ChildItem -LiteralPath $installerOutput -Filter '*.exe')
    if ($installer.Count -ne 1) { throw 'installer_count_mismatch' }
    $result = [ordered]@{passed=$true;version=$version;sourceSha256=$sourceHash;sourceHead=$head;uncommitted=$dirty;testsExecuted=(-not $SkipTests);publishRoot=$publish;installerPath=$installer[0].FullName;installerSha256=(Get-FileHash -LiteralPath $installer[0].FullName).Hash}
    Write-Json (Join-Path $output 'build-result.json') $result
    $result | ConvertTo-Json
} finally { Pop-Location }
