[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'ReleaseQualification.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'ReleasePipeline.psm1') -Force

function Assert-Throws {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$Message,
        [string]$ExpectedMessage
    )

    try {
        & $Action
    }
    catch {
        if (-not [string]::IsNullOrWhiteSpace($ExpectedMessage) -and $_.Exception.Message -notmatch $ExpectedMessage) {
            throw "Expected error matching '$ExpectedMessage', but received '$($_.Exception.Message)'."
        }
        return
    }

    throw $Message
}

function Invoke-ReleaseQualificationTest {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Action
    )

    & $Action
    Write-Host "PASS: $Name"
}

function New-TestRelease {
    param(
        [Parameter(Mandatory)][string]$Root,
        [string]$Version = '1.2.3'
    )

    $releaseDirectory = Join-Path $Root $Version
    $publishDirectory = Join-Path $releaseDirectory 'publish'
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $publishDirectory 'YourSafe.exe'), [byte[]](1, 2, 3, 4, 5, 6))
    [System.IO.File]::WriteAllBytes((Join-Path $publishDirectory 'YourSafe.NativeHost.exe'), [byte[]](1, 2, 3, 4, 5, 6))
    Import-Module (Join-Path $PSScriptRoot 'BrowserIntegration.psm1') -Force
    foreach ($browser in @('chrome', 'edge')) {
        Write-NativeHostManifest -Path (Join-Path $publishDirectory "yoursafe-native-$browser.json") -ExecutablePath 'YourSafe.NativeHost.exe' -ExtensionId 'lhffehnfgjhbipabkdgniaenehidnhfa'
    }
    [System.IO.File]::WriteAllBytes((Join-Path $publishDirectory 'PasswordTool.Core.dll'), [byte[]](7, 8, 9, 10))

    $archivePath = Join-Path $releaseDirectory "YourSafe-$Version-win-x64.zip"
    New-ReleaseArchive -PublishDirectory $publishDirectory -ArchivePath $archivePath -OutputRoot $releaseDirectory | Out-Null
    Write-ReleaseChecksums -OutputRoot $releaseDirectory -PublishDirectory $publishDirectory | Out-Null
    Write-ReleaseManifest -OutputRoot $releaseDirectory -Version $Version -PublishDirectory $publishDirectory | Out-Null
    [System.IO.File]::WriteAllText(
        (Join-Path $releaseDirectory 'release-status.txt'),
        "Release signing status: UNSIGNED`r`nUnsigned developer/test fixture.",
        [System.Text.UTF8Encoding]::new($false))

    return $releaseDirectory
}

function Get-ReleaseFingerprint {
    param([Parameter(Mandatory)][string]$ReleaseDirectory)

    $root = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
    return @(Get-ChildItem -LiteralPath $root -Recurse -Force -File | Sort-Object FullName | ForEach-Object {
            $relativePath = $_.FullName.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
            "$relativePath|$($_.Length)|$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
        }) -join "`n"
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("PasswordTool.ReleaseQualification.{0}" -f [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null

    Invoke-ReleaseQualificationTest -Name 'A valid unsigned developer release is explicitly qualified only as unsigned' -Action {
        $caseRoot = Join-Path $temporaryRoot 'unsigned'
        $releaseDirectory = New-TestRelease -Root $caseRoot
        $before = Get-ReleaseFingerprint -ReleaseDirectory $releaseDirectory
        $report = Invoke-ReleaseQualification -ReleaseDirectory $releaseDirectory -RepositoryRoot $repositoryRoot -SignToolPath '' -InstallerCompilerPath ''
        $after = Get-ReleaseFingerprint -ReleaseDirectory $releaseDirectory
        if ($report.SigningQualification -cne 'UNSIGNED_DEVELOPER_TEST_ONLY' -or
            $report.AutomatedQualification -cne 'PASSED_WITH_LIMITATIONS' -or
            $report.ReleaseReadiness -notmatch '^NOT_ESTABLISHED' -or
            ($report.Limitations -join ' ') -notmatch 'UNSIGNED' -or
            $before -cne $after) {
            throw 'Unsigned qualification was mislabeled, treated as release-ready, or mutated the finalized directory.'
        }
    }

    Invoke-ReleaseQualificationTest -Name 'A release cannot qualify as signed without independent signing verification' -Action {
        $caseRoot = Join-Path $temporaryRoot 'signed-without-tool'
        $releaseDirectory = New-TestRelease -Root $caseRoot
        [System.IO.File]::WriteAllText(
            (Join-Path $releaseDirectory 'release-status.txt'),
            "Release signing status: SIGNED`r`nClaimed fixture signature.",
            [System.Text.UTF8Encoding]::new($false))
        Assert-Throws -Action {
            Invoke-ReleaseQualification -ReleaseDirectory $releaseDirectory -RepositoryRoot $repositoryRoot -SignToolPath '' -InstallerCompilerPath '' | Out-Null
        } -Message 'Expected a signed claim without signtool to fail.' -ExpectedMessage 'signtool\.exe is unavailable'
    }

    Invoke-ReleaseQualificationTest -Name 'A release cannot qualify as signed when signature verification fails' -Action {
        $caseRoot = Join-Path $temporaryRoot 'signed-failed-verification'
        $releaseDirectory = New-TestRelease -Root $caseRoot
        [System.IO.File]::WriteAllText(
            (Join-Path $releaseDirectory 'release-status.txt'),
            "Release signing status: SIGNED`r`nClaimed fixture signature.",
            [System.Text.UTF8Encoding]::new($false))
        $failingSignTool = Join-Path $caseRoot 'failing-signtool.cmd'
        Set-Content -LiteralPath $failingSignTool -Value @('@echo off', 'exit /b 1') -Encoding ascii
        Assert-Throws -Action {
            Invoke-ReleaseQualification -ReleaseDirectory $releaseDirectory -RepositoryRoot $repositoryRoot -SignToolPath $failingSignTool -InstallerCompilerPath '' | Out-Null
        } -Message 'Expected failed signing verification to fail qualification.' -ExpectedMessage 'Authenticode or timestamp verification failed'
    }

    Invoke-ReleaseQualificationTest -Name 'Checksum verification detects a modified distribution artifact' -Action {
        $caseRoot = Join-Path $temporaryRoot 'modified-artifact'
        $releaseDirectory = New-TestRelease -Root $caseRoot
        $archivePath = Join-Path $releaseDirectory 'YourSafe-1.2.3-win-x64.zip'
        $archiveStream = [System.IO.File]::Open($archivePath, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
            $archiveStream.WriteByte(99)
        }
        finally {
            $archiveStream.Dispose()
        }
        Assert-Throws -Action {
            Invoke-ReleaseQualification -ReleaseDirectory $releaseDirectory -RepositoryRoot $repositoryRoot -SignToolPath '' -InstallerCompilerPath '' | Out-Null
        } -Message 'Expected a modified distribution artifact to fail qualification.' -ExpectedMessage 'SHA-256 checksum mismatch'
    }

    Invoke-ReleaseQualificationTest -Name 'An unsigned status cannot also describe the release as signed' -Action {
        $caseRoot = Join-Path $temporaryRoot 'contradictory-unsigned-status'
        $releaseDirectory = New-TestRelease -Root $caseRoot
        [System.IO.File]::WriteAllText(
            (Join-Path $releaseDirectory 'release-status.txt'),
            "Release signing status: UNSIGNED`r`nThis release is signed and verified.",
            [System.Text.UTF8Encoding]::new($false))
        Assert-Throws -Action {
            Invoke-ReleaseQualification -ReleaseDirectory $releaseDirectory -RepositoryRoot $repositoryRoot -SignToolPath '' -InstallerCompilerPath '' | Out-Null
        } -Message 'Expected a contradictory unsigned status to fail qualification.' -ExpectedMessage 'must never describe the release as signed'
    }

    $forbiddenCases = @(
        @{ Name = 'vault storage'; Path = '.storage' },
        @{ Name = 'vault snapshot'; Path = '.snapshots\snapshot-1\.config' },
        @{ Name = 'API content'; Path = 'PasswordTool.Api.dll' },
        @{ Name = 'source content'; Path = 'Unexpected.cs' },
        @{ Name = 'test content'; Path = 'PasswordTool.Tests.dll' }
    )
    foreach ($forbiddenCase in $forbiddenCases) {
        Invoke-ReleaseQualificationTest -Name "Qualification rejects $($forbiddenCase.Name) in the release payload" -Action {
            $caseRoot = Join-Path $temporaryRoot ("forbidden-{0}" -f [Guid]::NewGuid().ToString('N'))
            $releaseDirectory = New-TestRelease -Root $caseRoot
            $forbiddenPath = Join-Path (Join-Path $releaseDirectory 'publish') $forbiddenCase.Path
            New-Item -ItemType Directory -Path (Split-Path -Parent $forbiddenPath) -Force | Out-Null
            [System.IO.File]::WriteAllBytes($forbiddenPath, [byte[]](42))
            Assert-Throws -Action {
                Invoke-ReleaseQualification -ReleaseDirectory $releaseDirectory -RepositoryRoot $repositoryRoot -SignToolPath '' -InstallerCompilerPath '' | Out-Null
            } -Message "Expected $($forbiddenCase.Name) to be rejected."
        }
    }

    Invoke-ReleaseQualificationTest -Name 'The static release manifest rejects private or unsupported fields' -Action {
        $caseRoot = Join-Path $temporaryRoot 'private-manifest-field'
        $releaseDirectory = New-TestRelease -Root $caseRoot
        $manifestPath = Join-Path $releaseDirectory 'release-manifest.json'
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $manifest | Add-Member -NotePropertyName 'certificatePath' -NotePropertyValue 'C:\private\release.pfx'
        [System.IO.File]::WriteAllText(
            $manifestPath,
            ($manifest | ConvertTo-Json -Depth 4),
            [System.Text.UTF8Encoding]::new($false))
        Assert-Throws -Action {
            Invoke-ReleaseQualification -ReleaseDirectory $releaseDirectory -RepositoryRoot $repositoryRoot -SignToolPath '' -InstallerCompilerPath '' | Out-Null
        } -Message 'Expected a private manifest field to fail qualification.' -ExpectedMessage 'exactly these public fields'
    }

    Invoke-ReleaseQualificationTest -Name 'A missing installer compiler is a manual qualification requirement, not an installer pass' -Action {
        $caseRoot = Join-Path $temporaryRoot 'missing-installer-tooling'
        $releaseDirectory = New-TestRelease -Root $caseRoot
        $report = Invoke-ReleaseQualification -ReleaseDirectory $releaseDirectory -RepositoryRoot $repositoryRoot -SignToolPath '' -InstallerCompilerPath ''
        if ($report.InstallerQualification -cne 'MANUAL_QUALIFICATION_REQUIRED' -or
            ($report.Limitations -join ' ') -notmatch 'ISCC\.exe is unavailable') {
            throw 'Missing installer tooling was not reported as a qualification limitation.'
        }
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
