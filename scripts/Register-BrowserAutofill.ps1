[CmdletBinding()]
param([switch]$Unregister)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'BrowserIntegration.psm1') -Force
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$desktopDirectory = Join-Path $repositoryRoot 'src/PasswordTool.WinUI/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64'
$manifestPath = Join-Path $desktopDirectory 'yoursafe-native-dev.json'
$registrationKeys = @('Software\Google\Chrome\NativeMessagingHosts\com.yoursafe.autofill.dev', 'Software\Microsoft\Edge\NativeMessagingHosts\com.yoursafe.autofill.dev')
if (-not $Unregister) {
    foreach ($file in @('YourSafe.exe', 'YourSafe.NativeHost.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $desktopDirectory $file) -PathType Leaf)) { throw 'Build development integration first.' }
    }
    Write-NativeHostManifest -Path $manifestPath -ExecutablePath (Join-Path $desktopDirectory 'YourSafe.NativeHost.exe') -ExtensionId 'cmfnnjellknkpnbooenlahijcmalbifg' -Development
}
# Explicit Registry32 matches the browser's first lookup; production uses its separate host name.
$registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry32)
try {
    foreach ($path in $registrationKeys) {
        $existing = $registry.OpenSubKey($path)
        $current = if ($null -ne $existing) { $existing.GetValue('') } else { $null }
        if ($null -ne $existing) { $existing.Dispose() }
        if ($Unregister) {
            if ([string]::Equals($current, $manifestPath, [StringComparison]::OrdinalIgnoreCase)) { $registry.DeleteSubKey($path, $false) }
        } else {
            if ($null -ne $current -and -not [string]::Equals($current, $manifestPath, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Development registration belongs to a different checkout; unregister that checkout first.'
            }
            $key = $registry.CreateSubKey($path)
            try { $key.SetValue('', $manifestPath, [Microsoft.Win32.RegistryValueKind]::String) } finally { $key.Dispose() }
        }
    }
} finally { $registry.Dispose() }
Write-Output $(if ($Unregister) { 'Removed only development registrations still owned by this checkout.' } else { 'Registered YourSafe development host for Chrome and Edge.' })
