[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleasePipeline.psm1') -Force

function Assert-Throws {
    param([Parameter(Mandatory)][scriptblock]$Action, [Parameter(Mandatory)][string]$Message)

    try {
        & $Action
    }
    catch {
        return
    }

    throw $Message
}

function Invoke-ReleasePipelineTest {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][scriptblock]$Action)

    & $Action
    Write-Host "PASS: $Name"
}

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("PasswordTool.ReleasePipeline.{0}" -f [Guid]::NewGuid().ToString('N'))
$environmentNames = @(
    'PASSWORDTOOL_SIGN_CERT_PATH',
    'PASSWORDTOOL_SIGN_CERT_PASSWORD',
    'PASSWORDTOOL_SIGN_CERT_THUMBPRINT',
    'PASSWORDTOOL_SIGN_TIMESTAMP_URL',
    'PASSWORDTOOL_SIGN_TOOL_PATH'
)
$originalEnvironment = @{}

try {
    foreach ($name in $environmentNames) {
        $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        Remove-Item "Env:$name" -ErrorAction SilentlyContinue
    }

    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    $stagingRoot = Join-Path $temporaryRoot 'staging'
    $publishDirectory = Join-Path $stagingRoot 'publish'
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $publishDirectory 'YourSafe.exe'), [byte[]](1, 2, 3, 4))
    [System.IO.File]::WriteAllBytes((Join-Path $publishDirectory 'YourSafe.NativeHost.exe'), [byte[]](1, 2, 3, 4))
    Import-Module (Join-Path $PSScriptRoot 'BrowserIntegration.psm1') -Force
    foreach ($browser in @('chrome', 'edge')) {
        Write-NativeHostManifest -Path (Join-Path $publishDirectory "yoursafe-native-$browser.json") -ExecutablePath 'YourSafe.NativeHost.exe' -ExtensionId 'lhffehnfgjhbipabkdgniaenehidnhfa'
    }
    [System.IO.File]::WriteAllBytes((Join-Path $publishDirectory 'PasswordTool.Core.dll'), [byte[]](5, 6, 7, 8))

    Invoke-ReleasePipelineTest -Name 'Release staging rejects output outside its configured root' -Action {
        Assert-Throws -Action { Assert-PathWithinRoot -Path (Join-Path $temporaryRoot 'outside') -Root $stagingRoot | Out-Null } -Message 'Expected outside staging path to be rejected.'
    }

    Invoke-ReleasePipelineTest -Name 'Required files and deterministic checksums are generated' -Action {
        Assert-PublishedPayload -PublishDirectory $publishDirectory -OutputRoot $stagingRoot -MaximumPayloadSizeMB 1 | Out-Null
        $first = Get-Content -LiteralPath (Write-ReleaseChecksums -OutputRoot $stagingRoot -PublishDirectory $publishDirectory) -Raw
        $second = Get-Content -LiteralPath (Write-ReleaseChecksums -OutputRoot $stagingRoot -PublishDirectory $publishDirectory) -Raw
        if ($first -ne $second -or $first -notmatch 'YourSafe\.exe') {
            throw 'Checksums were not deterministic or did not include the published executable.'
        }
    }

    Invoke-ReleasePipelineTest -Name 'Sensitive local vault files are excluded from published payloads' -Action {
        $sensitivePath = Join-Path $publishDirectory '.storage'
        [System.IO.File]::WriteAllBytes($sensitivePath, [byte[]](9))
        try {
            Assert-Throws -Action { Assert-PublishedPayload -PublishDirectory $publishDirectory -OutputRoot $stagingRoot | Out-Null } -Message 'Expected local vault storage to be rejected.'
        }
        finally {
            Remove-Item -LiteralPath $sensitivePath -Force
        }
    }

    Invoke-ReleasePipelineTest -Name 'Missing signing configuration is explicitly unsigned' -Action {
        $configuration = Get-SigningConfiguration -SigningMode Auto
        if ($configuration.Status -ne 'Unsigned' -or $configuration.Reason -notmatch 'unsigned') {
            throw 'Missing signing configuration was not labeled as unsigned.'
        }
    }

    Invoke-ReleasePipelineTest -Name 'Requested signing fails closed when verification fails' -Action {
        $failingSignTool = Join-Path $temporaryRoot 'failing-signtool.cmd'
        Set-Content -LiteralPath $failingSignTool -Value @('@echo off', 'if "%1"=="verify" exit /b 1', 'exit /b 0') -Encoding ascii
        $env:PASSWORDTOOL_SIGN_TOOL_PATH = $failingSignTool
        $env:PASSWORDTOOL_SIGN_CERT_THUMBPRINT = '0123456789ABCDEF0123456789ABCDEF01234567'
        $env:PASSWORDTOOL_SIGN_TIMESTAMP_URL = 'https://timestamp.example.invalid'
        $configuration = Get-SigningConfiguration -SigningMode Required
        Assert-Throws -Action { Invoke-ReleaseSigning -Configuration $configuration -FilePath (Join-Path $publishDirectory 'YourSafe.exe') -OutputRoot $stagingRoot } -Message 'Expected failed signature verification to stop the release.'
    }
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], 'Process')
    }

    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
