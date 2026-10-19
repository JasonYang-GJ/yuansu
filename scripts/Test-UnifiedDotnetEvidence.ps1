#Requires -Version 7.0
param([Parameter(Mandatory)][string]$PreviousEvidenceRoot,
      [Parameter(Mandatory)][string]$CurrentSourceManifest,
      [Parameter(Mandatory)][string]$OutputRoot)
$ErrorActionPreference='Stop'
$previousManifest=Join-Path $PreviousEvidenceRoot 'source-manifest.json'
$previous=Get-Content -LiteralPath $previousManifest -Raw|ConvertFrom-Json
$current=Get-Content -LiteralPath $CurrentSourceManifest -Raw|ConvertFrom-Json
function Relevant($Files){
    @($Files|Where-Object{($_.path -match '^(src/|tests/|prompts/|assets/)' -and $_.path -notmatch '^src/Yuanshu.Island/') -or $_.path -match '^(Directory.Build.props|global.json|ScreenGuide.slnx|NuGet.Config)$'}|Sort-Object path|ForEach-Object{$_.path+'|'+$_.sha256})
}
if($previous.version -cne $current.version -or @(Compare-Object (Relevant $previous.files) (Relevant $current.files) -CaseSensitive).Count){throw 'dotnet_validation_sources_changed'}
$expected=@($current.files|Where-Object{$_.path -match '^tests/.+\.Tests.csproj$'}|ForEach-Object{[IO.Path]::GetFileNameWithoutExtension($_.path)})
$observed=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$reports=@(Get-ChildItem -LiteralPath (Join-Path $PreviousEvidenceRoot 'tests') -Filter '*.trx' -File)
$total=0
foreach($report in $reports){
    $xml=[xml](Get-Content -LiteralPath $report.FullName -Raw)
    $counts=$xml.TestRun.ResultSummary.Counters
    if([int]$counts.total -le 0 -or [int]$counts.passed -ne [int]$counts.total -or [int]$counts.failed -ne 0){throw 'dotnet_validation_contains_nonpassing_tests'}
    $assemblies=@($xml.TestRun.TestDefinitions.UnitTest|ForEach-Object{[IO.Path]::GetFileNameWithoutExtension([string]$_.storage)}|Sort-Object -Unique)
    if($assemblies.Count -ne 1 -or $assemblies[0] -notin $expected -or -not $observed.Add($assemblies[0])){throw 'dotnet_validation_assembly_coverage_invalid'}
    $total += [int]$counts.total
}
if($expected.Count -eq 0 -or $observed.Count -ne $expected.Count){throw 'dotnet_validation_assembly_missing'}
$destination=Join-Path $OutputRoot 'tests'
New-Item -ItemType Directory -Path $destination -Force|Out-Null
foreach($report in $reports){Copy-Item -LiteralPath $report.FullName -Destination $destination}
$receipt=[ordered]@{passed=$true;sourceComparison='All C# source, tests, assets, prompts, project and lock files unchanged';previousSourceManifestSha256=(Get-FileHash -LiteralPath $previousManifest).Hash;currentSourceManifestSha256=(Get-FileHash -LiteralPath $CurrentSourceManifest).Hash;assemblies=$observed.Count;passedTests=$total;reports=@($reports|ForEach-Object{@{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})}
$receipt|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $OutputRoot 'reused-dotnet-validation.json') -Encoding utf8
[pscustomobject]$receipt|Select-Object passed,assemblies,passedTests|ConvertTo-Json -Compress
