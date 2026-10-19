#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RepositoryRoot,
    [Parameter(Mandatory = $true)][string]$PublishRoot,
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [Parameter(Mandatory = $true)][string]$ReleaseVersion
)

$ErrorActionPreference = 'Stop'
$records = [Collections.Generic.List[object]]::new()
$issues = [Collections.Generic.List[object]]::new()
$copies = @{}
$utf8 = [Text.UTF8Encoding]::new($false)

function Write-Json([string]$Path, $Value) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, (($Value | ConvertTo-Json -Depth 40).Replace("`r`n", "`n") + "`n"), $utf8)
}

function Resolve-Child([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':')) {
        throw 'attribution_relative_path_invalid'
    }
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $prefix $Relative.Replace('/', [IO.Path]::DirectorySeparatorChar)))
    if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'attribution_path_escape' }
    return $path
}

function Relative-Path([string]$Root, [string]$Path) {
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath($Path)
    if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'attribution_path_escape' }
    return $path.Substring($prefix.Length).Replace('\', '/')
}

function Add-Issue([string]$Ecosystem, [string]$Name, [string]$Version, [string]$Code) {
    $issues.Add([ordered]@{ ecosystem = $Ecosystem; name = $Name; version = $Version; code = $Code })
}

function Add-LicenseFile([string]$Source, [string]$Relative, [string]$ExpectedHash = '') {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw 'attribution_license_file_missing' }
    [void](Resolve-Child $publish 'licenses/'$Relative)
    $hash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
    if ($ExpectedHash -and $hash -cne $ExpectedHash.ToUpperInvariant()) { throw 'attribution_license_hash_mismatch' }
    if ((Get-Item -LiteralPath $Source).Length -eq 0) { throw 'attribution_license_file_empty' }
    if ($copies.ContainsKey($Relative) -and $copies[$Relative].sha256 -cne $hash) { throw 'attribution_license_collision' }
    $copies[$Relative] = [pscustomobject]@{ source = $Source; sha256 = $hash }
    return [ordered]@{ path = 'licenses/' + $Relative; sha256 = $hash }
}

function Find-LicenseFiles([string]$Directory, [string]$DeclaredFile = '') {
    $found = @{}
    if ($DeclaredFile) {
        $file = Resolve-Child $Directory $DeclaredFile
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'attribution_declared_license_missing' }
        $found[$file] = $file
    }
    foreach ($file in Get-ChildItem -LiteralPath $Directory -File -Recurse) {
        if ($file.Name -match '^(LICENSE|LICENCE|COPYING|NOTICE|COPYRIGHT|UNLICENSE)([-_.]|$)') {
            $found[$file.FullName] = $file.FullName
        }
    }
    return @($found.Values | Sort-Object)
}

function Has-LicenseText([object[]]$Files) {
    return @($Files | Where-Object {
        [IO.Path]::GetFileName([string]$_) -match '^(LICENSE|LICENCE|COPYING|UNLICENSE)([-_.]|$)' -and
        (Get-Item -LiteralPath ([string]$_)).Length -ge 80
    }).Count -gt 0
}

function Add-CatalogComponent($Component, [string]$Ecosystem) {
    $files = [Collections.Generic.List[object]]::new()
    foreach ($file in $Component.licenseNoticeFiles) {
        $source = Resolve-Child $catalogRoot ([string]$file.path)
        $files.Add((Add-LicenseFile $source ('dotnet/' + [string]$file.path) ([string]$file.rawSha256)))
    }
    if ($files.Count -eq 0) { throw 'attribution_catalog_texts_missing' }
    $records.Add([ordered]@{
        ecosystem = $Ecosystem; name = [string]$Component.payloadComponentId; version = [string]$Component.version
        evidence = 'EXISTING_EXACT_VERSION_CATALOG'; files = $files.ToArray()
    })
}

try {
    if ($ReleaseVersion -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw 'attribution_version_invalid' }
    $repo = [IO.Path]::GetFullPath($RepositoryRoot)
    $publish = [IO.Path]::GetFullPath($PublishRoot)
    $output = [IO.Path]::GetFullPath($OutputRoot)
    if (-not (Test-Path -LiteralPath $publish -PathType Container)) { throw 'attribution_publish_missing' }
    [IO.Directory]::CreateDirectory($output) | Out-Null
    $catalogRoot = Resolve-Child $repo 'distribution/licenses'
    $catalogPath = Resolve-Child $catalogRoot 'bundle-manifest.json'
    $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
    if ($catalog.schemaVersion -ne 1 -or $catalog.bundleStatus -cne 'VERIFIED') { throw 'attribution_catalog_invalid' }
    $catalogPackages = @{}
    foreach ($component in $catalog.components | Where-Object { $_.payloadSourceKind -eq 'package' }) {
        $key = [string]$component.payloadComponentId + '/' + [string]$component.version
        if ($catalogPackages.ContainsKey($key)) { throw 'attribution_catalog_duplicate' }
        $catalogPackages[$key] = $component
    }

    # Only target libraries carrying runtime/native/resource assets need package
    # text. Build-only NuGet metapackages in libraries are not shipped payloads.
    $runtimeLibraries = @{}
    $depsInputs = [Collections.Generic.List[object]]::new()
    foreach ($name in @('ScreenGuide.DesktopClient.deps.json', 'ScreenGuide.DesktopHost.deps.json')) {
        $depsPath = Resolve-Child $publish $name
        if (-not (Test-Path -LiteralPath $depsPath -PathType Leaf)) { throw 'attribution_deps_missing' }
        $deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
        $depsInputs.Add([ordered]@{ path = $name; sha256 = (Get-FileHash -LiteralPath $depsPath -Algorithm SHA256).Hash })
        $targetProperty = @($deps.targets.PSObject.Properties | Where-Object Name -EQ $deps.runtimeTarget.name)
        if ($targetProperty.Count -ne 1) { throw 'attribution_deps_target_invalid' }
        foreach ($library in $targetProperty[0].Value.PSObject.Properties) {
            $assets = @($library.Value.PSObject.Properties | Where-Object Name -In @('runtime', 'native', 'resources', 'runtimeTargets'))
            if (@($assets | Where-Object { @($_.Value.PSObject.Properties).Count -gt 0 }).Count -eq 0) { continue }
            $identity = [string]$library.Name
            $declaration = $deps.libraries.PSObject.Properties[$identity]
            if (-not $declaration) { throw 'attribution_deps_library_missing' }
            $runtimeLibraries[$identity] = [string]$declaration.Value.type
        }
    }
    foreach ($identity in @($runtimeLibraries.Keys | Sort-Object)) {
        $parts = $identity.Split('/')
        if ($parts.Length -ne 2) { throw 'attribution_deps_identity_invalid' }
        $name = $parts[0] -replace '^runtimepack\.', ''
        $version = $parts[1]
        if ($runtimeLibraries[$identity] -eq 'project') {
            if (-not $name.StartsWith('ScreenGuide.', [StringComparison]::Ordinal) -or $version -cne $ReleaseVersion) {
                Add-Issue 'project' $name $version 'project_version_or_owner_mismatch'
            }
            continue
        }
        $key = $name + '/' + $version
        if (-not $catalogPackages.ContainsKey($key)) { Add-Issue 'nuget' $name $version 'exact_version_absent_from_catalog'; continue }
        Add-CatalogComponent $catalogPackages[$key] 'nuget'
    }
    foreach ($component in $catalog.components | Where-Object artifactScope -EQ 'installerContainer') {
        Add-CatalogComponent $component 'installer'
    }

    $island = Resolve-Child $repo 'src/Yuanshu.Island'
    $npmLockPath = Resolve-Child $island 'package-lock.json'
    $npmLock = Get-Content -LiteralPath $npmLockPath -Raw | ConvertFrom-Json -AsHashtable
    if ($npmLock.version -cne $ReleaseVersion -or $npmLock.lockfileVersion -ne 3) { throw 'attribution_npm_root_mismatch' }
    foreach ($packagePath in $npmLock.packages.Keys | Sort-Object) {
        $property = [pscustomobject]@{Name=$packagePath;Value=$npmLock.packages[$packagePath]}
        if (-not $property.Name -or $property.Value.dev) { continue }
        if ($property.Name -notmatch '^node_modules/' -or $property.Value.link) { throw 'attribution_npm_package_path_invalid' }
        $directory = Resolve-Child $island $property.Name
        $package = Get-Content -LiteralPath (Resolve-Child $directory 'package.json') -Raw | ConvertFrom-Json
        if ($package.version -cne $property.Value.version) { throw 'attribution_npm_installed_version_mismatch' }
        $files = @(Find-LicenseFiles $directory)
        if (-not (Has-LicenseText $files)) { Add-Issue 'npm' $package.name $package.version 'complete_license_text_missing'; continue }
        $included = @($files | ForEach-Object {
            Add-LicenseFile $_ ('npm/' + $package.name + '-' + $package.version + '/' + (Relative-Path $directory $_))
        })
        $records.Add([ordered]@{ ecosystem = 'npm'; name = $package.name; version = $package.version; declaredLicense = $package.license; evidence = 'LOCKED_INSTALLED_PRODUCTION_PACKAGE'; files = $included })
    }

    $cargoManifest = Resolve-Child $island 'src-tauri/Cargo.toml'
    $cargoCommand = Get-Command cargo -ErrorAction Stop
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $metadataText = @(& $cargoCommand.Source metadata --manifest-path $cargoManifest --locked --offline --format-version 1 --filter-platform x86_64-pc-windows-msvc 2>$null)
        $metadataExit = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previousPreference }
    if ($metadataExit -ne 0) { throw 'attribution_cargo_locked_offline_metadata_failed' }
    $metadata = ($metadataText -join "`n") | ConvertFrom-Json
    $sourceRoot = Resolve-Child $repo 'distribution/island-licenses'
    $sources = Get-Content -LiteralPath (Resolve-Child $sourceRoot 'sources.json') -Raw | ConvertFrom-Json
    $fallbacks = @{}
    foreach ($entry in $sources.packages) { $fallbacks[[string]$entry.name + '/' + [string]$entry.version] = $entry }
    $cargoRoot = @($metadata.packages | Where-Object id -EQ $metadata.resolve.root)
    if ($cargoRoot.Count -ne 1 -or $cargoRoot[0].name -cne 'yuanshu-island' -or $cargoRoot[0].version -cne $ReleaseVersion) { throw 'attribution_cargo_root_mismatch' }
    foreach ($package in $metadata.packages | Where-Object id -NE $metadata.resolve.root | Sort-Object name, version) {
        $directory = Split-Path -Parent $package.manifest_path
        $files = @(Find-LicenseFiles $directory ([string]$package.license_file))
        $included = [Collections.Generic.List[object]]::new()
        $evidence = 'LOCKED_WINDOWS_CARGO_PACKAGE'
        foreach ($file in $files) {
            $included.Add((Add-LicenseFile $file ('cargo/' + $package.name + '-' + $package.version + '/' + (Relative-Path $directory $file))))
        }
        if (-not (Has-LicenseText $files)) {
            $key = [string]$package.name + '/' + [string]$package.version
            if (-not $fallbacks.ContainsKey($key)) { Add-Issue 'cargo' $package.name $package.version 'complete_license_text_missing'; continue }
            $fallback = $fallbacks[$key]
            $vcs = Get-Content -LiteralPath (Join-Path $directory '.cargo_vcs_info.json') -Raw | ConvertFrom-Json
            if ($vcs.git.sha1 -cne $fallback.commit -or ([string]$package.repository).TrimEnd('/') -cne ([string]$fallback.repository).TrimEnd('/')) {
                Add-Issue 'cargo' $package.name $package.version 'upstream_source_binding_mismatch'; continue
            }
            $fallbackFiles = @($fallback.files | ForEach-Object { Resolve-Child $sourceRoot $_.path })
            if (-not (Has-LicenseText $fallbackFiles)) { Add-Issue 'cargo' $package.name $package.version 'upstream_license_text_missing'; continue }
            foreach ($file in $fallback.files) {
                $included.Add((Add-LicenseFile (Resolve-Child $sourceRoot $file.path) ('cargo/' + $package.name + '-' + $package.version + '/' + [IO.Path]::GetFileName($file.path)) $file.sha256))
            }
            $evidence = 'EXACT_CARGO_VCS_COMMIT_AND_RECORDED_UPSTREAM_TEXT'
        }
        $records.Add([ordered]@{
            ecosystem = 'cargo'; name = $package.name; version = $package.version; declaredLicense = $package.license
            evidence = $evidence; files = $included.ToArray()
        })
    }

    # Preserve the owner's existing declaration verbatim, including its historical
    # version. A generated cover identifies this build without changing grants.
    $ownerPath = Resolve-Child $repo 'distribution/release-candidates/v0.7.0/PROPRIETARY-NOTICE.txt'
    $ownerFile = Add-LicenseFile $ownerPath 'project/OWNER-DECLARATION.txt'
    $records.Add([ordered]@{ ecosystem = 'project'; name = 'Yuanshu including embedded Island'; version = $ReleaseVersion; evidence = 'UNCHANGED_OWNER_DECLARATION'; files = @($ownerFile) })
    [void](Add-LicenseFile (Resolve-Child $sourceRoot 'sources.json') 'cargo-upstream-sources.json')
    $report = [ordered]@{
        schemaVersion = 1; releaseVersion = $ReleaseVersion
        status = $(if ($issues.Count -eq 0) { 'COMPLETE_TEXTS_COLLECTED' } else { 'INCOMPLETE' })
        scope = 'Published .NET runtime libraries; locked npm production packages; Windows Cargo resolved graph including build dependencies; installer catalog'
        catalog = [ordered]@{ path = 'distribution/licenses/bundle-manifest.json'; sha256 = (Get-FileHash -LiteralPath $catalogPath -Algorithm SHA256).Hash }
        npmLock = [ordered]@{ path = 'src/Yuanshu.Island/package-lock.json'; sha256 = (Get-FileHash -LiteralPath $npmLockPath -Algorithm SHA256).Hash }
        cargoLock = [ordered]@{ path = 'src/Yuanshu.Island/src-tauri/Cargo.lock'; sha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $cargoManifest -Parent) 'Cargo.lock') -Algorithm SHA256).Hash }
        deps = $depsInputs.ToArray(); componentCount = $records.Count; fileCount = $copies.Count
        components = $records.ToArray(); issues = $issues.ToArray()
    }
    Write-Json (Resolve-Child $output 'unified-attribution-report.json') $report
    if ($issues.Count -ne 0) {
        [ordered]@{ passed = $false; errorCode = 'attribution_incomplete'; issues = $issues.ToArray(); report = 'unified-attribution-report.json' } | ConvertTo-Json -Depth 10 -Compress | Write-Output
        exit 1
    }

    # Do not publish a partial bundle. Existing user data and unrelated files are
    # never removed; only deterministic attribution outputs are replaced.
    foreach ($relative in @($copies.Keys | Sort-Object)) {
        $destination = Resolve-Child $publish ('licenses/' + $relative)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        Copy-Item -LiteralPath $copies[$relative].source -Destination $destination -Force
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -cne $copies[$relative].sha256) { throw 'attribution_copied_hash_mismatch' }
    }
    Write-Json (Resolve-Child $publish 'licenses/unified-manifest.json') $report
    $notice = [Collections.Generic.List[string]]::new()
    $notice.Add('元枢 ' + $ReleaseVersion + ' 内部合并版 — 第三方资料')
    $notice.Add('灵动岛为元枢内置功能。本包沿用 licenses/project/OWNER-DECLARATION.txt 中的现有所有者声明；历史版本号按原文保留。本次没有新增项目开源授权。')
    $notice.Add('各第三方组件的完整 LICENSE/NOTICE 文件如下。此清单记录实际收集的资料，不替代原文，也不表示已授权公开分发。')
    $notice.Add('Cargo 部分保守收集 Windows 解析依赖图，包含构建依赖；不声称所有列出包均进入最终二进制。')
    $notice.Add('')
    foreach ($record in $records | Sort-Object ecosystem, name, version) {
        $notice.Add('[' + $record.ecosystem + '] ' + $record.name + ' ' + $record.version)
        foreach ($file in $record.files) { $notice.Add('  ' + $file.path + '  SHA256=' + $file.sha256) }
    }
    [IO.File]::WriteAllText((Resolve-Child $publish 'THIRD-PARTY-NOTICES.txt'), (($notice.ToArray() -join "`n") + "`n"), $utf8)
    [ordered]@{ passed = $true; releaseVersion = $ReleaseVersion; componentCount = $records.Count; fileCount = $copies.Count; manifest = 'licenses/unified-manifest.json'; notices = 'THIRD-PARTY-NOTICES.txt'; report = 'unified-attribution-report.json' } | ConvertTo-Json -Compress | Write-Output
    exit 0
}
catch {
    $code = [string]$_.Exception.Message
    if ($code -notmatch '^attribution_[a-z0-9_]+$') { $code = 'attribution_generation_failed' }
    $failure = [ordered]@{ passed = $false; errorCode = $code; releaseVersion = $ReleaseVersion; issues = $issues.ToArray() }
    if ($output -and (Test-Path -LiteralPath $output -PathType Container)) {
        Write-Json (Resolve-Child $output 'unified-attribution-report.json') $failure
    }
    $failure | ConvertTo-Json -Depth 10 -Compress | Write-Output
    exit 1
}
