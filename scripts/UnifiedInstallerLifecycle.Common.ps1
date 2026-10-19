$ErrorActionPreference = 'Stop'

function Test-UnifiedReparsePoint([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if ((Test-Path -LiteralPath $current) -and
            (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { return $true }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $current = $parent.FullName
    }
    return $false
}

function Assert-UnifiedOwnedRoot([string]$Path) {
    $root = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $root.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($root) -cnotmatch '^YuanshuUnifiedLifecycle-[A-Za-z0-9-]+$' -or
        (Test-UnifiedReparsePoint $root)) { throw 'unified_lifecycle_output_path_invalid' }
    return $root
}

function Assert-UnifiedPayloadRelativePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [IO.Path]::IsPathRooted($Path) -or
        $Path.Contains(':') -or $Path.Contains('\') -or
        @($Path.Split('/') | Where-Object { $_ -eq '..' -or $_ -eq '.' -or $_ -eq '' }).Count -gt 0) {
        throw 'unified_lifecycle_payload_path_invalid'
    }
}

function Assert-UnifiedLifecyclePlan($Plan) {
    if ($Plan.schemaVersion -ne 1 -or $Plan.kind -cne 'yuanshu-unified-installer-lifecycle' -or
        $Plan.oldVersion -cne '0.7.0' -or $Plan.newVersion -cne '0.8.0' -or
        $Plan.oldInstaller.fileName -cne 'old-installer.exe' -or
        $Plan.newInstaller.fileName -cne 'unified-installer.exe' -or
        $Plan.oldInstaller.sha256 -cnotmatch '^[0-9A-F]{64}$' -or
        $Plan.newInstaller.sha256 -cnotmatch '^[0-9A-F]{64}$' -or
        $Plan.installerExecutions -ne 5 -or @($Plan.payload).Count -lt 3) {
        throw 'unified_lifecycle_plan_invalid'
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @($Plan.payload)) {
        Assert-UnifiedPayloadRelativePath ([string]$file.path)
        if (-not $seen.Add([string]$file.path) -or $file.sha256 -cnotmatch '^[0-9A-F]{64}$') {
            throw 'unified_lifecycle_payload_invalid'
        }
    }
    foreach ($required in @('ScreenGuide.DesktopClient.exe', 'ScreenGuide.DesktopHost.exe', 'island/yuanshu-island.exe')) {
        if (-not $seen.Contains($required)) { throw 'unified_lifecycle_required_component_missing' }
    }
    if ($null -ne $Plan.webView2Installer -and
        ($Plan.webView2Installer.fileName -cne 'webview2-offline-x64.exe' -or
         $Plan.webView2Installer.sha256 -cnotmatch '^[0-9A-F]{64}$' -or
         $Plan.webView2Installer.signatureStatus -cne 'Valid' -or
         $Plan.webView2Installer.signerSubject -notmatch '(^|, )O=Microsoft Corporation(,|$)')) {
        throw 'unified_lifecycle_webview2_dependency_invalid'
    }
}

function Get-UnifiedWebView2Version {
    $subkey = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    foreach ($hive in @([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryHive]::LocalMachine)) {
        foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
            try {
                $key = $base.OpenSubKey($subkey)
                if ($null -eq $key) { continue }
                try {
                    $parsed = $null
                    if ([version]::TryParse([string]$key.GetValue('pv'), [ref]$parsed) -and $parsed -gt [version]'0.0') {
                        return $parsed.ToString()
                    }
                } finally { $key.Dispose() }
            } finally { $base.Dispose() }
        }
    }
    return $null
}

function Write-UnifiedJson([string]$Path, $Value) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, (($Value | ConvertTo-Json -Depth 20).Replace("`r`n", "`n") + "`n"), [Text.UTF8Encoding]::new($false))
}
