[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][string]$TestDirectory,
    [Parameter(Mandatory)][string]$BrowserExecutable,
    [Parameter(Mandatory)][string]$PlaywrightPackage,
    [ValidateRange(1024, 65535)][int]$FixturePort = 49173,
    [string]$CasePrefix = '',
    [switch]$AllowExistingRegistrationOverride,
    [switch]$NativeHostOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
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
Import-Module (Join-Path $PSScriptRoot 'BrowserIntegration.psm1') -Force
Write-NativeHostManifest -Path $manifestPath -ExecutablePath $nativeHost -ExtensionId 'cmfnnjellknkpnbooenlahijcmalbifg' -Development
$keys = @('Software\Google\Chrome\NativeMessagingHosts\com.yoursafe.autofill.dev',
    'Software\Microsoft\Edge\NativeMessagingHosts\com.yoursafe.autofill.dev')
$registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry32)
$previous = [System.Collections.Generic.List[object]]::new()
$registered = [System.Collections.Generic.List[string]]::new()
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
    foreach ($record in $previous) {
        if ($record.path -notin $registered) { continue }
        $key = $registry.OpenSubKey($record.path, $true)
        $removeEmptyKey = $false
        try {
            if ($null -eq $key) { continue }
            if (-not [string]::Equals($key.GetValue(''), $manifestPath, [StringComparison]::OrdinalIgnoreCase)) {
                Write-Warning 'Development registration changed externally; preserving the new value.'
                continue
            }
            if ($record.hadValue) { $key.SetValue('', $record.value, [Microsoft.Win32.RegistryValueKind]$record.kind) }
            else { $key.DeleteValue('', $false) }
            $removeEmptyKey = -not $record.existed -and $key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0
        } finally { if ($key) { $key.Dispose() } }
        if ($removeEmptyKey) { $registry.DeleteSubKey($record.path, $false) }
    }
    $registry.Dispose()
}
exit $exitCode
