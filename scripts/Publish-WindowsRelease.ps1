[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [string]$ArtifactsRoot,

    [string]$ChromeExtensionId = '',
    [string]$EdgeExtensionId = '',

    [ValidateSet('Auto', 'Required', 'Disabled')]
    [string]$SigningMode = 'Auto',

    [ValidateRange(1, 4096)]
    [int]$MaximumPayloadSizeMB = 400,

    [switch]$SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'ReleasePipeline.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'BrowserIntegration.psm1') -Force
Assert-ProductionExtensionId $ChromeExtensionId
Assert-ProductionExtensionId $EdgeExtensionId

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($ArtifactsRoot)) {
    $ArtifactsRoot = Join-Path $repositoryRoot 'artifacts\releases'
}

$safeArtifactsRoot = Assert-PathWithinRoot -Path $ArtifactsRoot -Root $repositoryRoot
Assert-ReleaseVersion -Version $Version

$finalReleaseDirectory = Join-Path $safeArtifactsRoot $Version
if (Test-Path -LiteralPath $finalReleaseDirectory) {
    throw "Release version '$Version' already exists at '$finalReleaseDirectory'. Refusing to overwrite it."
}

$stagingRoot = Join-Path $safeArtifactsRoot '.staging'
New-Item -ItemType Directory -Path $stagingRoot -Force -ErrorAction Stop | Out-Null
$workingDirectory = Join-Path $stagingRoot ("{0}-{1}" -f $Version, [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workingDirectory -ErrorAction Stop | Out-Null
$safeWorkingDirectory = Assert-PathWithinRoot -Path $workingDirectory -Root $safeArtifactsRoot
$publishDirectory = Join-Path $safeWorkingDirectory 'publish'

$projectPath = Join-Path $repositoryRoot 'src\PasswordTool.WinUI\PasswordTool.WinUI.csproj'
& dotnet publish $projectPath --configuration Release --runtime win-x64 --self-contained true --output $publishDirectory `
    '-p:PasswordToolReleasePublish=true' `
    '-p:Platform=x64' `
    "-p:Version=$Version" `
    '-p:ContinuousIntegrationBuild=true' `
    '-p:Deterministic=true'
if ($LASTEXITCODE -ne 0) {
    throw 'dotnet publish failed; no release directory was finalized.'
}

$nativeProject = Join-Path $repositoryRoot 'src\PasswordTool.NativeHost\PasswordTool.NativeHost.csproj'
# Publish the single-file host directly alongside the desktop; no Node runtime is shipped.
& dotnet publish $nativeProject -c Release -r win-x64 --self-contained true -o $publishDirectory `
    '-p:PublishSingleFile=true' '-p:DebugType=embedded' '-p:DebugSymbols=false' "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'NativeHost publish failed; no release directory was finalized.' }
Write-NativeHostManifest -Path (Join-Path $publishDirectory 'yoursafe-native-chrome.json') -ExecutablePath 'YourSafe.NativeHost.exe' -ExtensionId $ChromeExtensionId
Write-NativeHostManifest -Path (Join-Path $publishDirectory 'yoursafe-native-edge.json') -ExecutablePath 'YourSafe.NativeHost.exe' -ExtensionId $EdgeExtensionId

# Symbols are useful in CI output but are not part of the portable single-file
# distribution and can reveal local build paths. Remove only generated PDBs from
# this newly-created staging directory before payload qualification.
Get-ChildItem -LiteralPath $publishDirectory -Recurse -Force -File -Filter '*.pdb' |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop }

$payload = Assert-PublishedPayload -PublishDirectory $publishDirectory -OutputRoot $safeWorkingDirectory -MaximumPayloadSizeMB $MaximumPayloadSizeMB
$signingConfiguration = Get-SigningConfiguration -SigningMode $SigningMode
Invoke-ReleaseSigning -Configuration $signingConfiguration -FilePath $payload.ExecutablePath -OutputRoot $safeWorkingDirectory
Invoke-ReleaseSigning -Configuration $signingConfiguration -FilePath $payload.NativeHostPath -OutputRoot $safeWorkingDirectory

$installerPath = $null
if (-not $SkipInstaller) {
    $installerTemplate = Join-Path $repositoryRoot 'installer\PasswordTool.iss'
    $installerPath = Invoke-InnoSetupBuild -InstallerScriptPath $installerTemplate -Version $Version -PublishDirectory $publishDirectory -OutputRoot $safeWorkingDirectory
    if ($null -ne $installerPath) {
        Invoke-ReleaseSigning -Configuration $signingConfiguration -FilePath $installerPath -OutputRoot $safeWorkingDirectory
    }
}

$archivePath = Join-Path $safeWorkingDirectory "YourSafe-$Version-win-x64.zip"
New-ReleaseArchive -PublishDirectory $publishDirectory -ArchivePath $archivePath -OutputRoot $safeWorkingDirectory | Out-Null

$releaseStatus = if ($signingConfiguration.Status -eq 'Requested') {
    "Release signing status: SIGNED`r`nAuthenticode and timestamp verification completed for each executable release artifact."
}
else {
    "Release signing status: UNSIGNED`r`n$($signingConfiguration.Reason)"
}
[System.IO.File]::WriteAllText((Join-Path $safeWorkingDirectory 'release-status.txt'), $releaseStatus, [System.Text.UTF8Encoding]::new($false))

$checksumPath = Write-ReleaseChecksums -OutputRoot $safeWorkingDirectory -PublishDirectory $publishDirectory
$manifestPath = Write-ReleaseManifest -OutputRoot $safeWorkingDirectory -Version $Version -PublishDirectory $publishDirectory

if (Test-Path -LiteralPath $finalReleaseDirectory) {
    throw "Release version '$Version' appeared at '$finalReleaseDirectory' while staging. Refusing to overwrite it."
}

Move-Item -LiteralPath $safeWorkingDirectory -Destination $finalReleaseDirectory -ErrorAction Stop

[pscustomobject]@{
    Version = $Version
    ReleaseDirectory = $finalReleaseDirectory
    PortableArchive = Join-Path $finalReleaseDirectory (Split-Path -Leaf $archivePath)
    Installer = if ($null -ne $installerPath) { Join-Path (Join-Path $finalReleaseDirectory 'installer') (Split-Path -Leaf $installerPath) } else { $null }
    SigningStatus = if ($signingConfiguration.Status -eq 'Requested') { 'Signed and verified' } else { 'Unsigned developer/test release' }
    Checksums = Join-Path $finalReleaseDirectory (Split-Path -Leaf $checksumPath)
    Manifest = Join-Path $finalReleaseDirectory (Split-Path -Leaf $manifestPath)
}
