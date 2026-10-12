[CmdletBinding(DefaultParameterSetName = 'Integration')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Integration')][int]$ProcessId,
    [Parameter(Mandatory, ParameterSetName = 'Integration')][string]$TestDirectory,
    [Parameter(Mandatory, ParameterSetName = 'Integration')][string]$BrowserExecutable,
    [Parameter(Mandatory, ParameterSetName = 'Integration')][string]$PlaywrightPackage,
    [ValidateRange(1024, 65535)][int]$FixturePort = 49173,
    [string]$CasePrefix = '',
    [switch]$AllowExistingRegistrationOverride,
    [switch]$NativeHostOnly,
    [Parameter(Mandatory, ParameterSetName = 'SelfCheck')][switch]$SelfCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Import-Module (Join-Path $PSScriptRoot 'BrowserIntegration.psm1') -Force

function Write-TestNativeManifest([string]$ManifestPath, [string]$ExecutablePath) {
    $targetPath = [IO.Path]::GetFullPath($ManifestPath)
    if (-not $targetPath.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($targetPath) -cne 'yoursafe-native-dev.json') { throw 'The test manifest must be the development manifest inside this checkout.' }
    $directory = [IO.Path]::GetDirectoryName($targetPath)
    $stagePath = [IO.Path]::GetFullPath((Join-Path $directory ('yoursafe-native-dev-' + [Guid]::NewGuid().ToString('N') + '.tmp')))
    if (-not $stagePath.StartsWith($directory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test manifest staging path.' }
    try {
        Write-NativeHostManifest -Path $stagePath -ExecutablePath $ExecutablePath -ExtensionId 'cmfnnjellknkpnbooenlahijcmalbifg' -Development
        $hash = (Get-FileHash -LiteralPath $stagePath -Algorithm SHA256).Hash
        [IO.File]::Move($stagePath, $targetPath, $true)
        return $hash
    } finally { if (Test-Path -LiteralPath $stagePath -PathType Leaf) { Remove-Item -LiteralPath $stagePath } }
}

if ($SelfCheck) {
    $fixtureRoot = [IO.Path]::GetFullPath((Join-Path $repository ('.tmp/native-manifest-self-check-' + [Guid]::NewGuid().ToString('N'))))
    $safeRoot = [IO.Path]::GetFullPath((Join-Path $repository '.tmp')) + [IO.Path]::DirectorySeparatorChar
    if (-not $fixtureRoot.StartsWith($safeRoot, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $fixtureRoot)) { throw 'Unsafe manifest self-check directory.' }
    New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
    $originalWriter = (Get-Command Write-NativeHostManifest).ScriptBlock
    try {
        $path = Join-Path $fixtureRoot 'yoursafe-native-dev.json'
        [IO.File]::WriteAllText($path, 'synthetic manifest baseline')
        $baseline = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        function Write-NativeHostManifest {
            param($Path, $ExecutablePath, $ExtensionId, [switch]$Development)
            [IO.File]::WriteAllText($Path, 'partial staged manifest')
            throw 'Injected staging write failure.'
        }
        $failed = $false
        try { Write-TestNativeManifest $path 'synthetic-native-host.exe' | Out-Null }
        catch { $failed = $_.Exception.Message.Contains('Injected staging write failure.') }
        if (-not $failed -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $baseline -or @(Get-ChildItem -LiteralPath $fixtureRoot -File).Count -ne 1) { throw 'A staging failure changed the live manifest or left a temporary file.' }
        Set-Item -Path Function:\Write-NativeHostManifest -Value $originalWriter
        $hash = Write-TestNativeManifest $path 'synthetic-native-host.exe'
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $hash -or @(Get-ChildItem -LiteralPath $fixtureRoot -File).Count -ne 1) { throw 'Staged manifest publication failed.' }
        $invalidPath = Join-Path $fixtureRoot 'yoursafe-native-chrome.json'
        $denied = $false
        try { Write-TestNativeManifest $invalidPath 'synthetic-native-host.exe' | Out-Null } catch { $denied = $true }
        if (-not $denied -or (Test-Path -LiteralPath $invalidPath)) { throw 'A production manifest path was accepted.' }
        Write-Output 'PASS: manifest staging failure preserves baseline; successful publication matches its hash; production paths are rejected. No app, browser or registry touched.'
    } finally {
        Set-Item -Path Function:\Write-NativeHostManifest -Value $originalWriter
        if (-not [IO.Path]::GetFullPath($fixtureRoot).StartsWith($safeRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe manifest self-check cleanup path.' }
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
    exit 0
}

$testRoot = (Resolve-Path -LiteralPath $TestDirectory).Path
$control = Join-Path $PSScriptRoot 'AutofillTestControl.ps1'
& $control -Action validate -ProcessId $ProcessId -TestDirectory $testRoot | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Only the marked disposable desktop can run this test.' }
if (-not $NativeHostOnly) {
    & $control -Action validateInteractive -ProcessId $ProcessId -TestDirectory $testRoot
    if ($LASTEXITCODE -ne 0) { throw 'Interactive desktop unavailable. No registration was changed.' }
}
$desktopDirectory = Join-Path $repository 'src/PasswordTool.WinUI/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64'
$manifestPath = Join-Path $desktopDirectory 'yoursafe-native-dev.json'
$nativeHost = Join-Path $desktopDirectory 'YourSafe.NativeHost.exe'
foreach ($file in @($nativeHost, (Join-Path $desktopDirectory 'YourSafe.exe'))) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'Build the adjacent Debug desktop and NativeHost first.' }
}
$keys = @('Software\Google\Chrome\NativeMessagingHosts\com.yoursafe.autofill.dev',
    'Software\Microsoft\Edge\NativeMessagingHosts\com.yoursafe.autofill.dev',
    'Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.yoursafe.autofill.dev')
$registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry32)
$previous = [System.Collections.Generic.List[object]]::new()
$registered = [System.Collections.Generic.List[string]]::new()
$hadManifest = Test-Path -LiteralPath $manifestPath -PathType Leaf
$originalManifest = $null
if ($hadManifest) { $originalManifest = [IO.File]::ReadAllBytes($manifestPath) }
$testManifestHash = $null
$environment = @{}
$environmentNames = @('YOURSAFE_APP_PID', 'YOURSAFE_E2E_DIRECTORY', 'YOURSAFE_BROWSER_EXECUTABLE', 'YOURSAFE_PLAYWRIGHT_PACKAGE', 'YOURSAFE_E2E_CONTROL_SCRIPT', 'YOURSAFE_E2E_PORT', 'YOURSAFE_E2E_CASE_PREFIX')
foreach ($name in $environmentNames) { $environment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$exitCode = 1
try {
    # Validate every key before changing any; never silently replace another checkout.
    foreach ($path in $keys) {
        $key = $registry.OpenSubKey($path)
        try {
            $hadValue = $null -ne $key -and '' -in $key.GetValueNames()
            $value = if ($hadValue) { $key.GetValue('', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } else { $null }
            $kind = if ($hadValue) { $key.GetValueKind('') } else { [Microsoft.Win32.RegistryValueKind]::String }
            $otherCheckout = $hadValue -and -not [string]::Equals($value, $manifestPath, [StringComparison]::OrdinalIgnoreCase)
            if ($otherCheckout -and -not $AllowExistingRegistrationOverride) {
                throw 'Development registration belongs to another checkout. Explicit approval is required for a temporary override.'
            }
            $previous.Add(@{ path = $path; existed = $null -ne $key; hadValue = $hadValue; value = $value; kind = [int]$kind })
        } finally { if ($key) { $key.Dispose() } }
    }
    New-Item -ItemType Directory -Path (Join-Path $testRoot 'ui-results') -Force | Out-Null
    $previous | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $testRoot 'ui-results/native-registration-before.json') -Encoding utf8
    $testManifestHash = Write-TestNativeManifest $manifestPath $nativeHost
    foreach ($record in $previous) {
        $key = $registry.CreateSubKey($record.path)
        try { $key.SetValue('', $manifestPath, [Microsoft.Win32.RegistryValueKind]::String) }
        finally { $key.Dispose() }
        $registered.Add($record.path)
    }
    Push-Location (Join-Path $repository 'browser-extension')
    try {
        if ($NativeHostOnly) {
            & node test/native-host-process.mjs --host $nativeHost --app-pid $ProcessId --test-directory $testRoot --origin "http://127.0.0.1:$FixturePort"
        } else {
            $env:YOURSAFE_APP_PID = $ProcessId.ToString()
            $env:YOURSAFE_E2E_DIRECTORY = $testRoot
            $env:YOURSAFE_BROWSER_EXECUTABLE = $BrowserExecutable
            $env:YOURSAFE_PLAYWRIGHT_PACKAGE = $PlaywrightPackage
            $env:YOURSAFE_E2E_CONTROL_SCRIPT = $control
            $env:YOURSAFE_E2E_PORT = $FixturePort.ToString()
            $env:YOURSAFE_E2E_CASE_PREFIX = $CasePrefix
            & node test/extension-e2e.mjs
        }
        $exitCode = $LASTEXITCODE
    } finally { Pop-Location }
} finally {
    $cleanupFailures = [System.Collections.Generic.List[string]]::new()
    foreach ($record in $previous) {
        if ($record.path -notin $registered) { continue }
        try {
            $key = $registry.OpenSubKey($record.path, $true)
            $removeEmptyKey = $false
            try {
                if ($null -eq $key) { continue }
                if ('' -notin $key.GetValueNames() -or $key.GetValueKind('') -ne [Microsoft.Win32.RegistryValueKind]::String -or
                    -not [string]::Equals($key.GetValue('', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames), $manifestPath, [StringComparison]::OrdinalIgnoreCase)) {
                    Write-Warning 'Development registration changed externally; preserving the new value and type.'
                    continue
                }
                if ($record.hadValue) { $key.SetValue('', $record.value, [Microsoft.Win32.RegistryValueKind]$record.kind) }
                else { $key.DeleteValue('', $false) }
                $removeEmptyKey = -not $record.existed -and $key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0
            } finally { if ($key) { $key.Dispose() } }
            if ($removeEmptyKey) { $registry.DeleteSubKey($record.path, $false) }
        } catch { $cleanupFailures.Add("Development registration cleanup failed for $($record.path): $($_.Exception.Message)") }
    }
    try {
        if ($testManifestHash -and (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            if ((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -ne $testManifestHash) {
                Write-Warning 'Development manifest changed externally; preserving the new file.'
            } elseif ($hadManifest) { [IO.File]::WriteAllBytes($manifestPath, $originalManifest) }
            else { Remove-Item -LiteralPath $manifestPath }
        }
    } catch { $cleanupFailures.Add("Development manifest cleanup failed: $($_.Exception.Message)") }
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process') }
    try {
        $registrationChecks = foreach ($record in $previous) {
            $key = $registry.OpenSubKey($record.path)
            try {
                $hasValue = $null -ne $key -and '' -in $key.GetValueNames()
                $matchesBaseline = ($null -ne $key) -eq $record.existed -and $hasValue -eq $record.hadValue
                if ($matchesBaseline -and $hasValue) {
                    $actualValue = $key.GetValue('', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                    $matchesBaseline = [int]$key.GetValueKind('') -eq $record.kind -and
                        ($actualValue | ConvertTo-Json -Compress) -ceq ($record.value | ConvertTo-Json -Compress)
                }
                @{ path = $record.path; matchesBaseline = $matchesBaseline }
            } finally { if ($key) { $key.Dispose() } }
        }
        $hasManifest = Test-Path -LiteralPath $manifestPath -PathType Leaf
        $manifestMatchesBaseline = $hasManifest -eq $hadManifest
        if ($manifestMatchesBaseline -and $hadManifest) {
            $manifestMatchesBaseline = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -ceq [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($originalManifest))
        }
        $environmentChecks = foreach ($name in $environmentNames) {
            @{ name = $name; matchesBaseline = [string]::Equals([Environment]::GetEnvironmentVariable($name, 'Process'), $environment[$name], [StringComparison]::Ordinal) }
        }
        $reportPath = Join-Path $testRoot 'ui-results/native-registration-cleanup.json'
        if (Test-Path -LiteralPath (Split-Path $reportPath) -PathType Container) {
            @{ registrations = @($registrationChecks); manifestMatchesBaseline = $manifestMatchesBaseline; environment = @($environmentChecks); cleanupFailures = @($cleanupFailures) } |
                ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
        }
    } catch { $cleanupFailures.Add("Cleanup verification report failed: $($_.Exception.Message)") }
    finally { $registry.Dispose() }
    if ($cleanupFailures.Count -ne 0) { throw ($cleanupFailures -join [Environment]::NewLine) }
}
exit $exitCode
