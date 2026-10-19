#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DataDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9._-]{1,160}$')][string]$PipeName,
    [string]$VoiceModelDirectory,
    [string]$TemporaryDirectory,
    [string]$BlenderUserConfig,
    [string]$BlenderUserScripts,
    [string]$BlenderUserExtensions
)
$ErrorActionPreference = 'Stop'
function Local-Path([string]$Path) {
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or $Path.StartsWith('\\') -or $Path -match '[\x00-\x1f]') { throw 'A full local disk path is required.' }
    [IO.Path]::GetFullPath($Path)
}
$data = Local-Path $DataDirectory
if (-not (Test-Path -LiteralPath (Join-Path $data 'state\tasking.db') -PathType Leaf)) { throw 'The existing database was not found; no profile was changed.' }
$profile = [ordered]@{version=1;dataDirectory=$data;pipeName=$PipeName;applicationEnvironment=@{}}
if ($VoiceModelDirectory) { $profile.voiceModelDirectory = Local-Path $VoiceModelDirectory }
if ($TemporaryDirectory) { $profile.temporaryDirectory = Local-Path $TemporaryDirectory }
foreach ($entry in @(@('BLENDER_USER_CONFIG',$BlenderUserConfig),@('BLENDER_USER_SCRIPTS',$BlenderUserScripts),@('BLENDER_USER_EXTENSIONS',$BlenderUserExtensions))) {
    if ($entry[1]) { $profile.applicationEnvironment[$entry[0]] = Local-Path $entry[1] }
}
$directory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Yuanshu'
$destination = Join-Path $directory 'runtime-profile.json'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
if (Test-Path -LiteralPath $destination) {
    Copy-Item -LiteralPath $destination -Destination ($destination + '.' + (Get-Date -Format 'yyyyMMddHHmmssfff') + '.bak')
}
$temporary = $destination + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
[IO.File]::WriteAllText($temporary, ($profile | ConvertTo-Json -Depth 5) + "`n", [Text.UTF8Encoding]::new($false))
[IO.File]::Move($temporary, $destination, $true)
[ordered]@{profile=$destination;sha256=(Get-FileHash -LiteralPath $destination).Hash;dataMoved=$false;credentialsRead=$false} | ConvertTo-Json
