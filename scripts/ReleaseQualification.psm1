Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'ReleasePipeline.psm1') -Force

function Assert-ExactPropertySet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$InputObject,
        [Parameter(Mandatory)][string[]]$ExpectedProperties,
        [Parameter(Mandatory)][string]$Context
    )

    $actualProperties = @($InputObject.PSObject.Properties.Name)
    $unexpected = @($actualProperties | Where-Object { $ExpectedProperties -cnotcontains $_ })
    $missing = @($ExpectedProperties | Where-Object { $actualProperties -cnotcontains $_ })
    if ($unexpected.Count -gt 0 -or $missing.Count -gt 0) {
        throw "$Context must contain exactly these public fields: $($ExpectedProperties -join ', ')."
    }
}

function ConvertTo-SafeRelativePath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Context
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or
        [System.IO.Path]::IsPathRooted($Path) -or
        $Path.StartsWith('/') -or
        $Path.StartsWith('\\') -or
        $Path.Contains(':')) {
        throw "$Context contains an unsafe path '$Path'."
    }

    $normalizedPath = $Path.Replace('\', '/')
    $segments = @($normalizedPath -split '/')
    if ($segments.Count -eq 0 -or $segments | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -in @('.', '..') }) {
        throw "$Context contains an unsafe path '$Path'."
    }

    return $normalizedPath
}

function Assert-SafePayloadPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][string]$Context
    )

    $normalizedPath = ConvertTo-SafeRelativePath -Path $RelativePath -Context $Context
    $segments = @($normalizedPath -split '/')
    $fileName = $segments[-1]
    $extension = [System.IO.Path]::GetExtension($fileName).ToLowerInvariant()
    $forbiddenFileNames = @(
        '.config',
        '.storage',
        '.trusted-unlock',
        '.state-transaction',
        '.env',
        'secrets.json'
    )
    $forbiddenExtensions = @(
        '.cs',
        '.csproj',
        '.sln',
        '.slnx',
        '.pfx',
        '.p12',
        '.pem',
        '.key',
        '.cer',
        '.crt',
        '.snk',
        '.log',
        '.bak',
        '.backup',
        '.db',
        '.sqlite',
        '.user'
    )
    $forbiddenSegments = @('.snapshots', 'PasswordTool.Api', 'test', 'tests', '.git')

    foreach ($segment in $segments) {
        if ($forbiddenSegments -contains $segment) {
            throw "$Context contains API, test, source, or sensitive vault content '$normalizedPath'."
        }
    }

    if ($forbiddenFileNames -contains $fileName -or $forbiddenExtensions -contains $extension) {
        throw "$Context contains API, test, source, or sensitive vault content '$normalizedPath'."
    }

    if ($fileName -match '(?i)PasswordTool\.Api|(^|[._-])Tests?([._-]|$)|^appsettings(?:\..+)?\.json$|secret|credential|private[._ -]?key') {
        throw "$Context contains API, test, source, or secret-bearing content '$normalizedPath'."
    }

    return $normalizedPath
}

function Get-RelativeFilePath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][System.IO.FileInfo]$File,
        [Parameter(Mandatory)][string]$Root
    )

    return $File.FullName.Substring($Root.Length).TrimStart('\', '/').Replace('\', '/')
}

function Get-FileSha256 {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Read-StaticReleaseManifest {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ManifestPath)

    try {
        $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Release manifest '$ManifestPath' is not valid JSON. $($_.Exception.Message)"
    }

    Assert-ExactPropertySet -InputObject $manifest -ExpectedProperties @('version', 'artifacts', 'releaseNotes') -Context 'Release manifest'
    if ($manifest.version -isnot [string]) {
        throw 'Release manifest version must be a string.'
    }
    Assert-ReleaseVersion -Version $manifest.version

    if ($manifest.releaseNotes -isnot [string] -or [string]::IsNullOrWhiteSpace($manifest.releaseNotes) -or $manifest.releaseNotes.Length -gt 4096) {
        throw 'Release manifest releaseNotes must be non-empty public text or a URL no longer than 4,096 characters.'
    }

    $artifacts = @($manifest.artifacts)
    if ($artifacts.Count -eq 0) {
        throw 'Release manifest must declare at least the portable ZIP artifact.'
    }

    $seenArtifacts = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($artifact in $artifacts) {
        Assert-ExactPropertySet -InputObject $artifact -ExpectedProperties @('fileName', 'sha256') -Context 'Each release manifest artifact'
        if ($artifact.fileName -isnot [string]) {
            throw 'Release manifest artifact fileName must be a string.'
        }
        $artifact.fileName = ConvertTo-SafeRelativePath -Path $artifact.fileName -Context 'Release manifest'
        if (-not $seenArtifacts.Add($artifact.fileName)) {
            throw "Release manifest contains duplicate artifact '$($artifact.fileName)'."
        }
        if ($artifact.sha256 -isnot [string] -or $artifact.sha256 -cnotmatch '^[a-f0-9]{64}$') {
            throw "Release manifest artifact '$($artifact.fileName)' must use a lowercase SHA-256 value."
        }
    }

    return [pscustomobject]@{
        Version = $manifest.version
        Artifacts = $artifacts
        ReleaseNotes = $manifest.releaseNotes
    }
}

function Assert-ReleaseDirectoryShape {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ReleaseDirectory,
        [Parameter(Mandatory)][string]$Version
    )

    $releaseInfo = Get-Item -LiteralPath $ReleaseDirectory -Force -ErrorAction Stop
    if (-not $releaseInfo.PSIsContainer) {
        throw "Release path '$ReleaseDirectory' must be a directory."
    }
    if ($releaseInfo.Name -cne $Version) {
        throw "Release directory name '$($releaseInfo.Name)' does not match manifest version '$Version'."
    }
    if ($releaseInfo.Parent.Name -eq '.staging') {
        throw 'Release qualification requires a finalized release directory, not mutable staging output.'
    }

    $zipName = "YourSafe-$Version-win-x64.zip"
    $requiredFileNames = @('checksums.sha256', 'release-manifest.json', 'release-status.txt', $zipName)
    $topLevelFiles = @(Get-ChildItem -LiteralPath $ReleaseDirectory -Force -File)
    $actualFileNames = @($topLevelFiles.Name)
    $missingFiles = @($requiredFileNames | Where-Object { $actualFileNames -cnotcontains $_ })
    $unexpectedFiles = @($actualFileNames | Where-Object { $requiredFileNames -cnotcontains $_ })
    if ($missingFiles.Count -gt 0 -or $unexpectedFiles.Count -gt 0) {
        throw "Release directory must contain only the expected metadata and portable ZIP for version '$Version'."
    }

    $topLevelDirectories = @(Get-ChildItem -LiteralPath $ReleaseDirectory -Force -Directory)
    $allowedDirectories = @('publish', 'installer')
    $unexpectedDirectories = @($topLevelDirectories.Name | Where-Object { $allowedDirectories -cnotcontains $_ })
    if ($unexpectedDirectories.Count -gt 0 -or $topLevelDirectories.Name -cnotcontains 'publish') {
        throw 'Release directory contains an unexpected directory or is missing the publish directory.'
    }

    $reparsePoints = @(Get-ChildItem -LiteralPath $ReleaseDirectory -Recurse -Force | Where-Object {
            ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
        })
    if ($reparsePoints.Count -gt 0) {
        throw 'Release directories may not contain links or reparse points.'
    }

    $emptyFiles = @(Get-ChildItem -LiteralPath $ReleaseDirectory -Recurse -Force -File | Where-Object Length -le 0)
    if ($emptyFiles.Count -gt 0) {
        throw "Release directory contains an empty file '$($emptyFiles[0].FullName)'."
    }
}

function Get-ExpectedDistributionArtifacts {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ReleaseDirectory,
        [Parameter(Mandatory)][string]$Version
    )

    $artifacts = [System.Collections.Generic.List[object]]::new()
    $zipName = "YourSafe-$Version-win-x64.zip"
    $zipPath = Join-Path $ReleaseDirectory $zipName
    if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
        throw "Expected portable ZIP '$zipPath' is missing."
    }
    $artifacts.Add([pscustomobject]@{ RelativePath = $zipName; FullName = $zipPath })

    $installerDirectory = Join-Path $ReleaseDirectory 'installer'
    if (Test-Path -LiteralPath $installerDirectory -PathType Container) {
        $expectedInstallerName = "YourSafe-$Version-win-x64-setup.exe"
        $installerFiles = @(Get-ChildItem -LiteralPath $installerDirectory -Force -File)
        $installerDirectories = @(Get-ChildItem -LiteralPath $installerDirectory -Force -Directory)
        if ($installerDirectories.Count -gt 0 -or $installerFiles.Count -ne 1 -or $installerFiles[0].Name -cne $expectedInstallerName) {
            throw "Installer directory must contain only '$expectedInstallerName'."
        }
        $artifacts.Add([pscustomobject]@{
                RelativePath = "installer/$expectedInstallerName"
                FullName = $installerFiles[0].FullName
            })
    }

    return @($artifacts)
}

function Read-ReleaseChecksums {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ChecksumPath)

    $entries = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $lines = @(Get-Content -LiteralPath $ChecksumPath)
    if ($lines.Count -eq 0) {
        throw 'Release checksum file is empty.'
    }

    foreach ($line in $lines) {
        if ($line -cnotmatch '^([a-f0-9]{64}) \*([^\r\n]+)$') {
            throw "Release checksum line is invalid: '$line'."
        }
        $relativePath = ConvertTo-SafeRelativePath -Path $Matches[2] -Context 'Release checksum file'
        if (-not $entries.TryAdd($relativePath, $Matches[1])) {
            throw "Release checksum file contains duplicate path '$relativePath'."
        }
    }

    return ,$entries
}

function Assert-ReleaseChecksums {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ReleaseDirectory,
        [Parameter(Mandatory)][System.Collections.Generic.Dictionary[string, string]]$ChecksumEntries,
        [Parameter(Mandatory)][object[]]$DistributionArtifacts
    )

    $publishDirectory = Join-Path $ReleaseDirectory 'publish'
    $expectedFiles = [System.Collections.Generic.List[object]]::new()
    foreach ($file in @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -Force -File)) {
        $payloadRelativePath = Get-RelativeFilePath -File $file -Root $publishDirectory
        Assert-SafePayloadPath -RelativePath $payloadRelativePath -Context 'Published payload' | Out-Null
        $expectedFiles.Add([pscustomobject]@{
                RelativePath = "publish/$payloadRelativePath"
                FullName = $file.FullName
            })
    }
    foreach ($artifact in $DistributionArtifacts) {
        $expectedFiles.Add($artifact)
    }

    if ($ChecksumEntries.Count -ne $expectedFiles.Count) {
        throw 'Release checksum file must declare every payload and distribution artifact exactly once, and no other file.'
    }

    foreach ($expectedFile in $expectedFiles) {
        if (-not $ChecksumEntries.ContainsKey($expectedFile.RelativePath)) {
            throw "Release checksum file is missing '$($expectedFile.RelativePath)'."
        }
        $actualHash = Get-FileSha256 -Path $expectedFile.FullName
        if ($actualHash -cne $ChecksumEntries[$expectedFile.RelativePath]) {
            throw "SHA-256 checksum mismatch for '$($expectedFile.RelativePath)'."
        }
    }
}

function Assert-ManifestArtifacts {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Manifest,
        [Parameter(Mandatory)][object[]]$DistributionArtifacts,
        [Parameter(Mandatory)][System.Collections.Generic.Dictionary[string, string]]$ChecksumEntries
    )

    if ($Manifest.Artifacts.Count -ne $DistributionArtifacts.Count) {
        throw 'Release manifest must declare every distribution artifact exactly once, and no payload or private file.'
    }

    foreach ($distributionArtifact in $DistributionArtifacts) {
        $manifestArtifact = @($Manifest.Artifacts | Where-Object fileName -CEQ $distributionArtifact.RelativePath)
        if ($manifestArtifact.Count -ne 1) {
            throw "Release manifest is missing distribution artifact '$($distributionArtifact.RelativePath)'."
        }
        $actualHash = Get-FileSha256 -Path $distributionArtifact.FullName
        if ($manifestArtifact[0].sha256 -cne $actualHash -or $ChecksumEntries[$distributionArtifact.RelativePath] -cne $actualHash) {
            throw "Release manifest SHA-256 does not match '$($distributionArtifact.RelativePath)'."
        }
    }
}

function Get-StreamSha256 {
    [CmdletBinding()]
    param([Parameter(Mandatory)][System.IO.Stream]$Stream)

    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [Convert]::ToHexString($algorithm.ComputeHash($Stream)).ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Assert-PortableArchiveMatchesPayload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$PublishDirectory
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $expectedFiles = [System.Collections.Generic.Dictionary[string, System.IO.FileInfo]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @(Get-ChildItem -LiteralPath $PublishDirectory -Recurse -Force -File)) {
        $relativePath = Get-RelativeFilePath -File $file -Root $PublishDirectory
        Assert-SafePayloadPath -RelativePath $relativePath -Context 'Published payload' | Out-Null
        $expectedFiles.Add($relativePath, $file)
    }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $seenEntries = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        $fileEntries = @($archive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) })
        if ($fileEntries.Count -ne $expectedFiles.Count) {
            throw 'Portable ZIP content does not exactly match the validated publish payload.'
        }

        foreach ($entry in $fileEntries) {
            $relativePath = Assert-SafePayloadPath -RelativePath $entry.FullName -Context 'Portable ZIP'
            if (-not $seenEntries.Add($relativePath) -or -not $expectedFiles.ContainsKey($relativePath)) {
                throw "Portable ZIP contains an unexpected or duplicate entry '$relativePath'."
            }
            if ($entry.Length -le 0 -or $entry.Length -ne $expectedFiles[$relativePath].Length) {
                throw "Portable ZIP entry '$relativePath' is empty or differs from the publish payload."
            }

            $stream = $entry.Open()
            try {
                $archiveHash = Get-StreamSha256 -Stream $stream
            }
            finally {
                $stream.Dispose()
            }
            if ($archiveHash -cne (Get-FileSha256 -Path $expectedFiles[$relativePath].FullName)) {
                throw "Portable ZIP entry '$relativePath' differs from the publish payload."
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-ReleaseBuildAndOfflineContract {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RepositoryRoot)

    $buildPropsPath = Join-Path $RepositoryRoot 'Directory.Build.props'
    [xml]$buildProps = Get-Content -LiteralPath $buildPropsPath -Raw
    $releaseProperties = @($buildProps.Project.PropertyGroup | Where-Object { $_.Condition -match 'PasswordToolReleasePublish' })
    if ($releaseProperties.Count -ne 1 -or
        $releaseProperties[0].RuntimeIdentifier -cne 'win-x64' -or
        $releaseProperties[0].SelfContained -cne 'true' -or
        $releaseProperties[0].PublishSingleFile -cne 'true' -or
        $releaseProperties[0].IncludeAllContentForSelfExtract -cne 'true' -or
        $releaseProperties[0].PublishTrimmed -cne 'false' -or
        $releaseProperties[0].DebugSymbols -cne 'false') {
        throw 'Release build properties no longer define the expected self-contained, single-file win-x64 payload.'
    }

    $winUiProjectPath = Join-Path $RepositoryRoot 'src\PasswordTool.WinUI\PasswordTool.WinUI.csproj'
    [xml]$winUiProject = Get-Content -LiteralPath $winUiProjectPath -Raw
    $targetFrameworks = @($winUiProject.SelectNodes('/Project/PropertyGroup/TargetFramework') | ForEach-Object InnerText)
    $targetPlatformMinimums = @($winUiProject.SelectNodes('/Project/PropertyGroup/TargetPlatformMinVersion') | ForEach-Object InnerText)
    $useWinUi = @($winUiProject.SelectNodes('/Project/PropertyGroup/UseWinUI') | ForEach-Object InnerText)
    $enableMsixTooling = @($winUiProject.SelectNodes('/Project/PropertyGroup/EnableMsixTooling') | ForEach-Object InnerText)
    $projectReferences = @($winUiProject.SelectNodes('/Project/ItemGroup/ProjectReference') | ForEach-Object { $_.GetAttribute('Include') })
    if ($targetFrameworks.Count -ne 1 -or $targetFrameworks[0] -cne 'net10.0-windows10.0.26100.0' -or
        $targetPlatformMinimums.Count -ne 1 -or $targetPlatformMinimums[0] -cne '10.0.17763.0' -or
        $useWinUi.Count -ne 1 -or $useWinUi[0] -cne 'true' -or
        $enableMsixTooling.Count -ne 1 -or $enableMsixTooling[0] -cne 'true' -or
        $projectReferences.Count -ne 1 -or $projectReferences[0] -cne '..\PasswordTool.Presentation\PasswordTool.Presentation.csproj') {
        throw 'WinUI release project must remain .NET 10 Windows, target the supported Windows baseline, and reference only PasswordTool.Presentation.'
    }

    $publishScript = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'scripts\Publish-WindowsRelease.ps1') -Raw
    if ($publishScript -notmatch [regex]::Escape("src\PasswordTool.WinUI\PasswordTool.WinUI.csproj") -or
        $publishScript -notmatch [regex]::Escape("src\PasswordTool.NativeHost\PasswordTool.NativeHost.csproj") -or
        $publishScript -match 'PasswordTool\.Api' -or
        @([regex]::Matches($publishScript, '(?im)^\s*&\s*dotnet\s+publish\b')).Count -ne 2) {
        throw 'Release publishing must target only PasswordTool.WinUI and PasswordTool.NativeHost.'
    }

    [xml]$nativeProject = Get-Content -Raw -LiteralPath (Join-Path $RepositoryRoot 'src\PasswordTool.NativeHost\PasswordTool.NativeHost.csproj')
    if ($nativeProject.Project.PropertyGroup.TargetFramework -cne 'net10.0' -or
        $nativeProject.Project.PropertyGroup.AssemblyName -cne 'YourSafe.NativeHost' -or
        $nativeProject.SelectNodes('/Project/ItemGroup/ProjectReference').Count -ne 0) {
        throw 'NativeHost must remain a standalone .NET 10 transport executable without project references.'
    }

    $sourceRoots = @(
        (Join-Path $RepositoryRoot 'src\PasswordTool.Core'),
        (Join-Path $RepositoryRoot 'src\PasswordTool.Presentation'),
        (Join-Path $RepositoryRoot 'src\PasswordTool.NativeHost'),
        (Join-Path $RepositoryRoot 'src\PasswordTool.WinUI')
    )
    $networkOrLoggingPattern = '(?i)using\s+System\.Net|\bHttpClient\b|\bIHttpClientFactory\b|\bWebRequest\b|\bWebClient\b|\bClientWebSocket\b|\bTcpClient\b|\bUdpClient\b|\bGrpcChannel\b|\bApplicationInsights\b|\bSentry\b|\bAutoUpdater\b|\bUpdateManager\b|\bILogger(?:<|\b)|\bLogInformation\s*\(|\bLogDebug\s*\(|\bTrace\.Write|\bDebug\.Write|\bConsole\.Write'
    foreach ($sourceRoot in $sourceRoots) {
        foreach ($sourceFile in @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Filter '*.cs' | Where-Object {
                    $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]'
                })) {
            if ((Get-Content -LiteralPath $sourceFile.FullName -Raw) -match $networkOrLoggingPattern) {
                throw "Offline release source verification found a network, updater, telemetry, or logging surface in '$($sourceFile.FullName)'."
            }
        }
    }

    $storageSource = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'src\PasswordTool.Core\Services\VaultStorageService.cs') -Raw
    if ($storageSource -notmatch 'Environment\.SpecialFolder\.LocalApplicationData' -or
        $storageSource -notmatch 'Path\.Combine\([^\r\n]+"PasswordTool"\)') {
        throw 'Vault storage no longer has the expected %LocalAppData%\PasswordTool source contract.'
    }
}

function Get-InnoSectionLines {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string[]]$Lines,
        [Parameter(Mandatory)][string]$SectionName
    )

    $insideSection = $false
    $result = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $Lines) {
        $trimmedLine = $line.Trim()
        if ($trimmedLine -match '^\[(.+)\]$') {
            $insideSection = $Matches[1] -ceq $SectionName
            continue
        }
        if ($insideSection -and -not [string]::IsNullOrWhiteSpace($trimmedLine) -and -not $trimmedLine.StartsWith(';')) {
            $result.Add($trimmedLine)
        }
    }

    return @($result)
}

function Assert-InstallerTemplateContract {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$InstallerScriptPath)

    $lines = @(Get-Content -LiteralPath $InstallerScriptPath)
    $setupLines = @(Get-InnoSectionLines -Lines $lines -SectionName 'Setup')
    $setup = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($line in $setupLines) {
        $parts = $line -split '=', 2
        if ($parts.Count -ne 2 -or -not $setup.TryAdd($parts[0].Trim(), $parts[1].Trim())) {
            throw "Inno Setup [Setup] entry is invalid or duplicated: '$line'."
        }
    }

    $expectedSetup = [ordered]@{
        AppId = '{{8DFF6D6D-6678-4455-9B24-CEB32A1D854A}'
        AppName = 'YourSafe'
        AppVersion = '{#AppVersion}'
        DefaultDirName = '{localappdata}\Programs\YourSafe'
        DefaultGroupName = 'YourSafe'
        PrivilegesRequired = 'lowest'
        ArchitecturesAllowed = 'x64compatible'
        ArchitecturesInstallIn64BitMode = 'x64compatible'
        OutputDir = '{#OutputDir}'
        OutputBaseFilename = 'YourSafe-{#AppVersion}-win-x64-setup'
        UsePreviousAppDir = 'yes'
    }
    foreach ($entry in $expectedSetup.GetEnumerator()) {
        if (-not $setup.ContainsKey($entry.Key) -or $setup[$entry.Key] -cne $entry.Value) {
            throw "Inno Setup must keep $($entry.Key)=$($entry.Value)."
        }
    }
    if (-not $setup.ContainsKey('AppPublisher') -or [string]::IsNullOrWhiteSpace($setup['AppPublisher'])) {
        throw 'Inno Setup must contain a configured publisher placeholder or value.'
    }

    $fileLines = @(Get-InnoSectionLines -Lines $lines -SectionName 'Files')
    if ($fileLines.Count -ne 1 -or $fileLines[0] -cne 'Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs') {
        throw 'Inno Setup must contain only the validated SourceDir payload.'
    }
    $iconLines = @(Get-InnoSectionLines -Lines $lines -SectionName 'Icons')
    if ($iconLines.Count -ne 1 -or $iconLines[0] -cne 'Name: "{group}\YourSafe"; Filename: "{app}\YourSafe.exe"') {
        throw 'Inno Setup must create the expected YourSafe Start Menu entry.'
    }
    $runLines = @(Get-InnoSectionLines -Lines $lines -SectionName 'Run')
    if ($runLines.Count -ne 1 -or $runLines[0] -cne 'Filename: "{app}\YourSafe.exe"; Description: "Launch YourSafe"; Flags: nowait postinstall skipifsilent') {
        throw 'Inno Setup must launch only the expected YourSafe WinUI executable.'
    }
    if (@(Get-InnoSectionLines -Lines $lines -SectionName 'UninstallDelete').Count -ne 0) {
        throw 'Inno Setup must not delete vault data or any path outside the application binaries.'
    }
    $registryLines = @(Get-InnoSectionLines -Lines $lines -SectionName 'Registry')
    $expectedRegistry = @(
        'Root: HKCU32; Subkey: "Software\Google\Chrome\NativeMessagingHosts\com.yoursafe.autofill"; ValueType: string; ValueName: ""; ValueData: "{app}\yoursafe-native-chrome.json"',
        'Root: HKCU32; Subkey: "Software\Microsoft\Edge\NativeMessagingHosts\com.yoursafe.autofill"; ValueType: string; ValueName: ""; ValueData: "{app}\yoursafe-native-edge.json"'
    )
    if ($registryLines.Count -ne 2 -or @($registryLines | Where-Object { $expectedRegistry -cnotcontains $_ }).Count -gt 0) {
        throw 'Installer must register only the production host in HKCU for Chrome and Edge.'
    }
}

function Resolve-QualificationTool {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CommandName,
        [AllowEmptyString()][string]$ConfiguredPath,
        [Parameter(Mandatory)][bool]$WasConfigured
    )

    if ($WasConfigured) {
        if ([string]::IsNullOrWhiteSpace($ConfiguredPath)) {
            return $null
        }
        $resolvedPath = Resolve-Path -LiteralPath $ConfiguredPath -ErrorAction Stop
        if (-not (Test-Path -LiteralPath $resolvedPath.Path -PathType Leaf)) {
            throw "Configured qualification tool '$ConfiguredPath' is not a file."
        }
        return $resolvedPath.Path
    }

    $command = Get-Command $CommandName -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        return $null
    }
    return $command.Source
}

function Assert-AuthenticodeAndTimestamp {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string]$SignToolPath
    )

    $verificationOutput = @(& $SignToolPath 'verify' '/pa' '/all' '/tw' '/v' $FilePath 2>&1)
    $exitCode = $LASTEXITCODE
    $verificationText = $verificationOutput | Out-String
    if ($exitCode -ne 0 -or $verificationText -match '(?i)SignTool Warning:|Number of warnings:\s*[1-9]|Number of errors:\s*[1-9]') {
        throw "Authenticode or timestamp verification failed for '$FilePath'."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $FilePath
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or $null -eq $signature.TimeStamperCertificate) {
        throw "Authenticode signature or trusted timestamp is not valid for '$FilePath'."
    }
}

function Get-ReleaseSigningStatus {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$StatusPath)

    $lines = @((Get-Content -LiteralPath $StatusPath -Raw).Trim() -split '\r?\n')
    if ($lines.Count -eq 0) {
        throw 'Release status file is empty.'
    }

    if ($lines[0] -ceq 'Release signing status: UNSIGNED') {
        $remainingText = ($lines | Select-Object -Skip 1) -join "`n"
        if ($remainingText -match '(?i)\bsigned\b|signature\s+(?:was\s+)?verified|authenticode\s+(?:and\s+timestamp\s+)?verification\s+completed') {
            throw 'Unsigned release status must never describe the release as signed or verified.'
        }
        return 'UNSIGNED'
    }
    if ($lines[0] -ceq 'Release signing status: SIGNED') {
        if (($lines -join "`n") -match '(?i)\bUNSIGNED\b') {
            throw 'Signed release status is contradictory.'
        }
        return 'SIGNED'
    }

    throw 'Release status must explicitly begin with either Release signing status: UNSIGNED or Release signing status: SIGNED.'
}

function Invoke-ReleaseQualification {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ReleaseDirectory,
        [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
        [AllowEmptyString()][string]$SignToolPath,
        [AllowEmptyString()][string]$InstallerCompilerPath
    )

    $releasePath = (Resolve-Path -LiteralPath $ReleaseDirectory -ErrorAction Stop).Path
    $repositoryPath = (Resolve-Path -LiteralPath $RepositoryRoot -ErrorAction Stop).Path
    $manifestPath = Join-Path $releasePath 'release-manifest.json'
    $checksumPath = Join-Path $releasePath 'checksums.sha256'
    $statusPath = Join-Path $releasePath 'release-status.txt'
    foreach ($requiredPath in @($manifestPath, $checksumPath, $statusPath)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Required release file '$requiredPath' is missing."
        }
    }

    $manifest = Read-StaticReleaseManifest -ManifestPath $manifestPath
    Assert-ReleaseDirectoryShape -ReleaseDirectory $releasePath -Version $manifest.Version
    Assert-ReleaseBuildAndOfflineContract -RepositoryRoot $repositoryPath
    Assert-InstallerTemplateContract -InstallerScriptPath (Join-Path $repositoryPath 'installer\PasswordTool.iss')

    $publishDirectory = Join-Path $releasePath 'publish'
    $payload = Assert-PublishedPayload -PublishDirectory $publishDirectory -OutputRoot $releasePath
    $distributionArtifacts = @(Get-ExpectedDistributionArtifacts -ReleaseDirectory $releasePath -Version $manifest.Version)
    $portableArchive = @($distributionArtifacts | Where-Object RelativePath -CEQ "YourSafe-$($manifest.Version)-win-x64.zip")
    Assert-PortableArchiveMatchesPayload -ArchivePath $portableArchive[0].FullName -PublishDirectory $publishDirectory

    $checksumEntries = Read-ReleaseChecksums -ChecksumPath $checksumPath
    Assert-ReleaseChecksums -ReleaseDirectory $releasePath -ChecksumEntries $checksumEntries -DistributionArtifacts $distributionArtifacts
    Assert-ManifestArtifacts -Manifest $manifest -DistributionArtifacts $distributionArtifacts -ChecksumEntries $checksumEntries

    $signToolWasConfigured = $PSBoundParameters.ContainsKey('SignToolPath')
    $installerCompilerWasConfigured = $PSBoundParameters.ContainsKey('InstallerCompilerPath')
    $resolvedSignTool = Resolve-QualificationTool -CommandName 'signtool.exe' -ConfiguredPath $SignToolPath -WasConfigured $signToolWasConfigured
    $resolvedInstallerCompiler = Resolve-QualificationTool -CommandName 'ISCC.exe' -ConfiguredPath $InstallerCompilerPath -WasConfigured $installerCompilerWasConfigured
    $signingStatus = Get-ReleaseSigningStatus -StatusPath $statusPath
    $limitations = [System.Collections.Generic.List[string]]::new()

    if ($signingStatus -eq 'SIGNED') {
        if ($null -eq $resolvedSignTool) {
            throw 'Release claims SIGNED, but signtool.exe is unavailable. Authenticode and timestamp qualification did not pass.'
        }
        Assert-AuthenticodeAndTimestamp -FilePath $payload.ExecutablePath -SignToolPath $resolvedSignTool
        Assert-AuthenticodeAndTimestamp -FilePath $payload.NativeHostPath -SignToolPath $resolvedSignTool
        foreach ($installerArtifact in @($distributionArtifacts | Where-Object RelativePath -CLike 'installer/*')) {
            Assert-AuthenticodeAndTimestamp -FilePath $installerArtifact.FullName -SignToolPath $resolvedSignTool
        }
        $signingQualification = 'SIGNED_AND_TIMESTAMP_VERIFIED'
    }
    else {
        $signingQualification = 'UNSIGNED_DEVELOPER_TEST_ONLY'
        $limitations.Add('The release is explicitly UNSIGNED and is not eligible to be described as a signed controlled release.')
        if ($null -eq $resolvedSignTool) {
            $limitations.Add('signtool.exe is unavailable; signed-release verification requires a controlled release machine with the Windows SDK signing tools.')
        }
    }

    $installerArtifacts = @($distributionArtifacts | Where-Object RelativePath -CLike 'installer/*')
    if ($installerArtifacts.Count -eq 0) {
        $installerQualification = 'MANUAL_QUALIFICATION_REQUIRED'
        if ($null -eq $resolvedInstallerCompiler) {
            $limitations.Add('ISCC.exe is unavailable and no installer was produced. Build and qualify the installer on a controlled release machine with an already-installed Inno Setup compiler.')
        }
        else {
            $limitations.Add('No installer artifact is present. Produce and qualify it on the controlled release machine before an installer-based release.')
        }
    }
    else {
        $installerQualification = 'STATIC_CONTRACT_PASSED_MANUAL_SMOKE_REQUIRED'
        if ($null -eq $resolvedInstallerCompiler) {
            $limitations.Add('The installer artifact and template passed static checks, but ISCC.exe is unavailable to reproduce the installer build on this machine.')
        }
        $limitations.Add('Installer first-install, upgrade, Start Menu, uninstall, and vault-retention behavior still require manual verification on a controlled Windows release machine.')
    }

    $limitations.Add('Application first launch, existing-vault unlock, lock/relock, TOTP-protected action, backup verification, Security Check, and offline launch require the documented manual smoke test.')

    return [pscustomobject]@{
        Version = $manifest.Version
        ReleaseDirectory = $releasePath
        AutomatedQualification = if ($limitations.Count -eq 0) { 'PASSED' } else { 'PASSED_WITH_LIMITATIONS' }
        ArtifactQualification = 'PASSED'
        SigningQualification = $signingQualification
        InstallerQualification = $installerQualification
        OfflineSecurityQualification = 'SOURCE_CONTRACT_PASSED'
        ReleaseReadiness = 'NOT_ESTABLISHED_UNTIL_REQUIRED_MANUAL_CHECKS_COMPLETE'
        DistributionArtifacts = @($distributionArtifacts.RelativePath)
        Limitations = @($limitations)
    }
}

Export-ModuleMember -Function @(
    'Invoke-ReleaseQualification'
)
