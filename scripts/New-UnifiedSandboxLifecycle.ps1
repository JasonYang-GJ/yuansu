param(
    [Parameter(Mandatory = $true)][string]$OldInstallerPath,
    [Parameter(Mandatory = $true)][string]$CandidateInstallerPath,
    [Parameter(Mandatory = $true)][string]$PublishRoot,
    [string]$WebView2InstallerPath,
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UnifiedInstallerLifecycle.Common.ps1')
$ownedRoot = if ($OutputRoot) { Assert-UnifiedOwnedRoot $OutputRoot } else {
    Assert-UnifiedOwnedRoot (Join-Path ([IO.Path]::GetTempPath()) ('YuanshuUnifiedLifecycle-' + [guid]::NewGuid().ToString('N')))
}
if (Test-Path -LiteralPath $ownedRoot) { throw 'unified_lifecycle_output_already_exists' }
$sandbox = Join-Path $env:WINDIR 'System32\WindowsSandbox.exe'
$feature = Get-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM
if (-not (Test-Path -LiteralPath $sandbox -PathType Leaf) -or [string]$feature.State -ne 'Enabled') {
    throw 'unified_lifecycle_sandbox_unavailable'
}
$oldInstaller = [IO.Path]::GetFullPath($OldInstallerPath)
$newInstaller = [IO.Path]::GetFullPath($CandidateInstallerPath)
$publish = [IO.Path]::GetFullPath($PublishRoot).TrimEnd('\')
foreach ($file in @($oldInstaller, $newInstaller)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Test-UnifiedReparsePoint $file)) {
        throw 'unified_lifecycle_installer_missing'
    }
}
if (([Diagnostics.FileVersionInfo]::GetVersionInfo($oldInstaller).ProductVersion).Trim() -cne '0.7.0' -or
    ([Diagnostics.FileVersionInfo]::GetVersionInfo($newInstaller).ProductVersion).Trim() -cne '0.8.0') {
    throw 'unified_lifecycle_installer_version_mismatch'
}
if (-not (Test-Path -LiteralPath $publish -PathType Container) -or (Test-UnifiedReparsePoint $publish)) {
    throw 'unified_lifecycle_publish_missing'
}
$payload = @(Get-ChildItem -LiteralPath $publish -File -Recurse | Sort-Object FullName | ForEach-Object {
    if (Test-UnifiedReparsePoint $_.FullName) { throw 'unified_lifecycle_payload_reparse_point' }
    [ordered]@{ path = $_.FullName.Substring($publish.Length + 1).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$plan = [ordered]@{
    schemaVersion = 1; kind = 'yuanshu-unified-installer-lifecycle'; oldVersion = '0.7.0'; newVersion = '0.8.0'
    oldInstaller = [ordered]@{fileName = 'old-installer.exe'; sha256 = (Get-FileHash -LiteralPath $oldInstaller -Algorithm SHA256).Hash}
    newInstaller = [ordered]@{fileName = 'unified-installer.exe'; sha256 = (Get-FileHash -LiteralPath $newInstaller -Algorithm SHA256).Hash}
    installerExecutions = 5; payload = $payload
}
$runtimeInstaller = $null
if ($WebView2InstallerPath) {
    $runtimeInstaller = [IO.Path]::GetFullPath($WebView2InstallerPath)
    if (-not (Test-Path -LiteralPath $runtimeInstaller -PathType Leaf) -or (Test-UnifiedReparsePoint $runtimeInstaller)) {
        throw 'unified_lifecycle_webview2_dependency_missing'
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $runtimeInstaller
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|, )O=Microsoft Corporation(,|$)') {
        throw 'unified_lifecycle_webview2_signature_invalid'
    }
    $plan.webView2Installer = [ordered]@{ fileName = 'webview2-offline-x64.exe'; sha256 = (Get-FileHash -LiteralPath $runtimeInstaller -Algorithm SHA256).Hash;
        signatureStatus = [string]$signature.Status; signerSubject = $signature.SignerCertificate.Subject }
}
Assert-UnifiedLifecyclePlan $plan
$inputRoot = Join-Path $ownedRoot 'input'
$evidenceRoot = Join-Path $ownedRoot 'evidence'
New-Item -ItemType Directory -Path $inputRoot, $evidenceRoot -Force | Out-Null
Copy-Item -LiteralPath $oldInstaller -Destination (Join-Path $inputRoot 'old-installer.exe')
Copy-Item -LiteralPath $newInstaller -Destination (Join-Path $inputRoot 'unified-installer.exe')
if ($runtimeInstaller) { Copy-Item -LiteralPath $runtimeInstaller -Destination (Join-Path $inputRoot 'webview2-offline-x64.exe') }
foreach ($scriptName in @('Invoke-UnifiedSandboxLifecycle.ps1', 'UnifiedInstallerLifecycle.Common.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $scriptName) -Destination (Join-Path $inputRoot $scriptName)
}
Write-UnifiedJson (Join-Path $inputRoot 'plan.json') $plan
$inputEscaped = [Security.SecurityElement]::Escape($inputRoot)
$evidenceEscaped = [Security.SecurityElement]::Escape($evidenceRoot)
$wsb = @"
<Configuration>
  <Networking>Disable</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <PrinterRedirection>Disable</PrinterRedirection>
  <vGPU>Disable</vGPU>
  <MappedFolders>
    <MappedFolder><HostFolder>$inputEscaped</HostFolder><SandboxFolder>C:\YuanshuUnifiedInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$evidenceEscaped</HostFolder><SandboxFolder>C:\YuanshuUnifiedEvidence</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\YuanshuUnifiedInput\Invoke-UnifiedSandboxLifecycle.ps1</Command></LogonCommand>
</Configuration>
"@
$wsbPath = Join-Path $ownedRoot 'yuanshu-unified-lifecycle.wsb'
[IO.File]::WriteAllText($wsbPath, $wsb, [Text.UTF8Encoding]::new($false))
[ordered]@{ passed = $true; wsbPath = $wsbPath; evidenceRoot = $evidenceRoot; payloadCount = $payload.Count; hostInstallerExecutions = 0 } | ConvertTo-Json -Compress
