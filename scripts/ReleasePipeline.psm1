Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'BrowserIntegration.psm1') -Force

function Assert-PathWithinRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Root,

        [switch]$AllowRoot
    )

    $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $comparison = [System.StringComparison]::OrdinalIgnoreCase

    if ($AllowRoot -and [string]::Equals($fullPath, $fullRoot, $comparison)) {
        return $fullPath
    }

    $rootPrefix = $fullRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($rootPrefix, $comparison)) {
        throw "Path '$fullPath' must be inside '$fullRoot'."
    }

    return $fullPath
}

function Assert-ReleaseVersion {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Version)

    if ($Version -notmatch '^\d+\.\d+\.\d+$') {
        throw "Release version '$Version' must use numeric major.minor.patch format."
    }
}

function Assert-PublishedPayload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$PublishDirectory,
        [Parameter(Mandatory)][string]$OutputRoot,
        [ValidateRange(1, 4096)][int]$MaximumPayloadSizeMB = 400
    )

    $publishPath = Assert-PathWithinRoot -Path $PublishDirectory -Root $OutputRoot
    if (-not (Test-Path -LiteralPath $publishPath -PathType Container)) {
        throw "Published payload directory '$publishPath' was not created."
    }

    $expectedExecutable = Join-Path $publishPath 'YourSafe.exe'
    if (-not (Test-Path -LiteralPath $expectedExecutable -PathType Leaf)) {
        throw "Expected published executable '$expectedExecutable' is missing."
    }
    $nativeHost = Join-Path $publishPath 'YourSafe.NativeHost.exe'
    if (-not (Test-Path -LiteralPath $nativeHost -PathType Leaf)) { throw 'Published NativeHost executable is missing.' }
    foreach ($manifestName in @('yoursafe-native-chrome.json', 'yoursafe-native-edge.json')) {
        Assert-NativeHostManifest -Path (Join-Path $publishPath $manifestName)
    }

    $files = @(Get-ChildItem -LiteralPath $publishPath -Recurse -Force -File)
    if ($files.Count -eq 0) {
        throw "Published payload directory '$publishPath' is empty."
    }

    $forbiddenFileNames = @('.config', '.storage', '.trusted-unlock')
    $forbiddenExtensions = @('.cs', '.csproj', '.sln', '.slnx', '.pdb', '.pfx', '.p12', '.snk', '.log', '.bak', '.backup', '.db', '.sqlite')
    $forbiddenSegments = @('.snapshots', 'PasswordTool.Api', 'tests', '.git')

    foreach ($file in $files) {
        if ($file.Length -le 0) {
            throw "Published payload contains an empty file: '$($file.FullName)'."
        }

        $relativePath = $file.FullName.Substring($publishPath.Length).TrimStart('\', '/')
        $segments = $relativePath -split '[\\/]'
        if ($forbiddenSegments | Where-Object { $segments -contains $_ }) {
            throw "Sensitive or non-desktop release input found in published payload: '$relativePath'."
        }

        if ($forbiddenFileNames -contains $file.Name -or $forbiddenExtensions -contains $file.Extension.ToLowerInvariant()) {
            throw "Sensitive or source release input found in published payload: '$relativePath'."
        }
    }

    $payloadBytes = ($files | Measure-Object -Property Length -Sum).Sum
    $maximumBytes = [int64]$MaximumPayloadSizeMB * 1MB
    if ($payloadBytes -gt $maximumBytes) {
        throw "Published payload is $payloadBytes bytes, which exceeds the $MaximumPayloadSizeMB MB limit."
    }

    return [pscustomobject]@{
        PublishDirectory = $publishPath
        ExecutablePath = $expectedExecutable
        NativeHostPath = $nativeHost
        FileCount = $files.Count
        PayloadBytes = $payloadBytes
    }
}

function Get-SigningConfiguration {
    [CmdletBinding()]
    param([ValidateSet('Auto', 'Required', 'Disabled')][string]$SigningMode = 'Auto')

    $certificatePath = $env:PASSWORDTOOL_SIGN_CERT_PATH
    $certificatePassword = $env:PASSWORDTOOL_SIGN_CERT_PASSWORD
    $certificateThumbprint = $env:PASSWORDTOOL_SIGN_CERT_THUMBPRINT
    $timestampUrl = $env:PASSWORDTOOL_SIGN_TIMESTAMP_URL
    $configuredToolPath = $env:PASSWORDTOOL_SIGN_TOOL_PATH
    $hasConfiguration = -not [string]::IsNullOrWhiteSpace($certificatePath) -or
        -not [string]::IsNullOrWhiteSpace($certificatePassword) -or
        -not [string]::IsNullOrWhiteSpace($certificateThumbprint) -or
        -not [string]::IsNullOrWhiteSpace($timestampUrl) -or
        -not [string]::IsNullOrWhiteSpace($configuredToolPath)

    if ($SigningMode -eq 'Disabled') {
        return [pscustomobject]@{ Status = 'Unsigned'; Reason = 'Signing was explicitly disabled for this release.' }
    }

    if (-not $hasConfiguration -and $SigningMode -eq 'Auto') {
        return [pscustomobject]@{ Status = 'Unsigned'; Reason = 'No signing configuration was provided; this is an unsigned developer/test release.' }
    }

    if ([string]::IsNullOrWhiteSpace($timestampUrl)) {
        throw 'Signing was requested but PASSWORDTOOL_SIGN_TIMESTAMP_URL is missing.'
    }

    $parsedTimestampUrl = $null
    if (-not [Uri]::TryCreate($timestampUrl, [UriKind]::Absolute, [ref]$parsedTimestampUrl) -or $parsedTimestampUrl.Scheme -ne 'https') {
        throw 'PASSWORDTOOL_SIGN_TIMESTAMP_URL must be an absolute HTTPS URL.'
    }

    $useCertificateFile = -not [string]::IsNullOrWhiteSpace($certificatePath)
    $useThumbprint = -not [string]::IsNullOrWhiteSpace($certificateThumbprint)
    if ($useCertificateFile -eq $useThumbprint) {
        throw 'Signing requires exactly one of PASSWORDTOOL_SIGN_CERT_PATH or PASSWORDTOOL_SIGN_CERT_THUMBPRINT.'
    }

    if ($useCertificateFile) {
        if ([string]::IsNullOrWhiteSpace($certificatePassword)) {
            throw 'PASSWORDTOOL_SIGN_CERT_PASSWORD is required when PASSWORDTOOL_SIGN_CERT_PATH is used.'
        }

        $resolvedCertificatePath = Resolve-Path -LiteralPath $certificatePath -ErrorAction Stop
        if (-not (Test-Path -LiteralPath $resolvedCertificatePath -PathType Leaf) -or $resolvedCertificatePath.Path -notmatch '\.(pfx|p12)$') {
            throw 'PASSWORDTOOL_SIGN_CERT_PATH must resolve to a .pfx or .p12 certificate file.'
        }
    }
    else {
        if ($certificateThumbprint -notmatch '^[A-Fa-f0-9]{40}$') {
            throw 'PASSWORDTOOL_SIGN_CERT_THUMBPRINT must be a SHA-1 certificate thumbprint.'
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($configuredToolPath)) {
        $resolvedToolPath = Resolve-Path -LiteralPath $configuredToolPath -ErrorAction Stop
        if (-not (Test-Path -LiteralPath $resolvedToolPath -PathType Leaf)) {
            throw 'PASSWORDTOOL_SIGN_TOOL_PATH must resolve to a signing executable.'
        }
    }
    else {
        $signTool = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
        if ($null -eq $signTool) {
            throw 'Signing was requested but signtool.exe is unavailable. Install the Windows SDK on the controlled release machine or set PASSWORDTOOL_SIGN_TOOL_PATH.'
        }

        $resolvedToolPath = (Resolve-Path -LiteralPath $signTool.Source -ErrorAction Stop)
    }

    return [pscustomobject]@{
        Status = 'Requested'
        ToolPath = $resolvedToolPath.Path
        CertificatePath = if ($useCertificateFile) { $resolvedCertificatePath.Path } else { $null }
        CertificatePassword = if ($useCertificateFile) { $certificatePassword } else { $null }
        CertificateThumbprint = if ($useThumbprint) { $certificateThumbprint } else { $null }
        TimestampUrl = $parsedTimestampUrl.AbsoluteUri
    }
}

function Invoke-ReleaseSigning {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Configuration,
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string]$OutputRoot
    )

    if ($Configuration.Status -ne 'Requested') {
        return
    }

    $safeFilePath = Assert-PathWithinRoot -Path $FilePath -Root $OutputRoot
    if (-not (Test-Path -LiteralPath $safeFilePath -PathType Leaf) -or (Get-Item -LiteralPath $safeFilePath).Length -le 0) {
        throw "Cannot sign missing or empty release file '$safeFilePath'."
    }

    $arguments = @('sign', '/fd', 'SHA256')
    if ($null -ne $Configuration.CertificatePath) {
        $arguments += @('/f', $Configuration.CertificatePath, '/p', $Configuration.CertificatePassword)
    }
    else {
        $arguments += @('/sha1', $Configuration.CertificateThumbprint)
    }

    $arguments += @('/tr', $Configuration.TimestampUrl, '/td', 'SHA256', $safeFilePath)
    & $Configuration.ToolPath @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode signing failed for '$safeFilePath'."
    }

    & $Configuration.ToolPath 'verify' '/pa' '/tw' $safeFilePath
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode verification or timestamp verification failed for '$safeFilePath'."
    }
}

function Invoke-InnoSetupBuild {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$InstallerScriptPath,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$PublishDirectory,
        [Parameter(Mandatory)][string]$OutputRoot
    )

    $iscc = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($null -eq $iscc) {
        return $null
    }

    $safePublishDirectory = Assert-PathWithinRoot -Path $PublishDirectory -Root $OutputRoot
    $installerDirectory = Join-Path $OutputRoot 'installer'
    New-Item -ItemType Directory -Path $installerDirectory -ErrorAction Stop | Out-Null
    $safeInstallerDirectory = Assert-PathWithinRoot -Path $installerDirectory -Root $OutputRoot

    & $iscc.Source "/DAppVersion=$Version" "/DSourceDir=$safePublishDirectory" "/DOutputDir=$safeInstallerDirectory" $InstallerScriptPath | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'Inno Setup compilation failed.'
    }

    $expectedInstaller = Join-Path $safeInstallerDirectory "YourSafe-$Version-win-x64-setup.exe"
    if (-not (Test-Path -LiteralPath $expectedInstaller -PathType Leaf) -or (Get-Item -LiteralPath $expectedInstaller).Length -le 0) {
        throw "Inno Setup did not create the expected installer '$expectedInstaller'."
    }

    return $expectedInstaller
}

function New-ReleaseArchive {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$PublishDirectory,
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$OutputRoot
    )

    $safePublishDirectory = Assert-PathWithinRoot -Path $PublishDirectory -Root $OutputRoot
    $safeArchivePath = Assert-PathWithinRoot -Path $ArchivePath -Root $OutputRoot
    if (Test-Path -LiteralPath $safeArchivePath) {
        throw "Release archive '$safeArchivePath' already exists."
    }

    Assert-PublishedPayload -PublishDirectory $safePublishDirectory -OutputRoot $OutputRoot | Out-Null
    Compress-Archive -Path (Join-Path $safePublishDirectory '*') -DestinationPath $safeArchivePath -CompressionLevel Optimal -ErrorAction Stop
    if (-not (Test-Path -LiteralPath $safeArchivePath -PathType Leaf) -or (Get-Item -LiteralPath $safeArchivePath).Length -le 0) {
        throw "Release archive '$safeArchivePath' was not created or is empty."
    }

    return $safeArchivePath
}

function Get-ReleaseChecksumTargets {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$OutputRoot,
        [Parameter(Mandatory)][string]$PublishDirectory
    )

    $safeOutputRoot = Assert-PathWithinRoot -Path $OutputRoot -Root $OutputRoot -AllowRoot
    $safePublishDirectory = Assert-PathWithinRoot -Path $PublishDirectory -Root $safeOutputRoot
    $targets = @(Get-ChildItem -LiteralPath $safePublishDirectory -Recurse -Force -File)
    $targets += @(Get-ChildItem -LiteralPath $safeOutputRoot -Recurse -Force -File | Where-Object {
            $_.Extension -in @('.zip', '.msi', '.msix') -or ($_.Directory.Name -eq 'installer' -and $_.Extension -eq '.exe')
        })

    return @($targets | Sort-Object -Property FullName -Unique)
}

function Write-ReleaseChecksums {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$OutputRoot,
        [Parameter(Mandatory)][string]$PublishDirectory
    )

    $safeOutputRoot = Assert-PathWithinRoot -Path $OutputRoot -Root $OutputRoot -AllowRoot
    $targets = @(Get-ReleaseChecksumTargets -OutputRoot $safeOutputRoot -PublishDirectory $PublishDirectory)
    if ($targets.Count -eq 0) {
        throw 'No distributable release files were available for checksum generation.'
    }

    $checksumLines = foreach ($target in $targets) {
        if ($target.Length -le 0) {
            throw "Cannot checksum empty release file '$($target.FullName)'."
        }

        $hash = (Get-FileHash -LiteralPath $target.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $relativePath = $target.FullName.Substring($safeOutputRoot.Length).TrimStart('\', '/').Replace('\', '/')
        "$hash *$relativePath"
    }

    $checksumPath = Join-Path $safeOutputRoot 'checksums.sha256'
    [System.IO.File]::WriteAllLines($checksumPath, [string[]]$checksumLines, [System.Text.UTF8Encoding]::new($false))
    return $checksumPath
}

function Write-ReleaseManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$OutputRoot,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$PublishDirectory
    )

    $safeOutputRoot = Assert-PathWithinRoot -Path $OutputRoot -Root $OutputRoot -AllowRoot
    $safePublishDirectory = Assert-PathWithinRoot -Path $PublishDirectory -Root $safeOutputRoot
    $artifacts = @(Get-ChildItem -LiteralPath $safeOutputRoot -Recurse -Force -File | Where-Object {
            $_.Extension -in @('.zip', '.msi', '.msix') -or ($_.Directory.Name -eq 'installer' -and $_.Extension -eq '.exe')
        } | Sort-Object -Property FullName)

    if ($artifacts.Count -eq 0) {
        throw 'No portable archive or installer was created for the release manifest.'
    }

    $manifest = [ordered]@{
        version = $Version
        artifacts = @($artifacts | ForEach-Object {
                [ordered]@{
                    fileName = $_.FullName.Substring($safeOutputRoot.Length).TrimStart('\', '/').Replace('\', '/')
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            })
        releaseNotes = 'Manual update only: download an approved newer release, verify its SHA-256 checksum and signature when present, then run its installer or replace the portable files. PasswordTool makes no runtime update check or network request.'
    }

    $manifestPath = Join-Path $safeOutputRoot 'release-manifest.json'
    [System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 4), [System.Text.UTF8Encoding]::new($false))
    return $manifestPath
}

Export-ModuleMember -Function @(
    'Assert-PathWithinRoot',
    'Assert-ReleaseVersion',
    'Assert-PublishedPayload',
    'Get-SigningConfiguration',
    'Invoke-ReleaseSigning',
    'Invoke-InnoSetupBuild',
    'New-ReleaseArchive',
    'Get-ReleaseChecksumTargets',
    'Write-ReleaseChecksums',
    'Write-ReleaseManifest'
)
