Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'BrowserIntegration.psm1') -Force
foreach ($id in @('', 'placeholder', 'cmfnnjellknkpnbooenlahijcmalbifg', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'abcdefghijklmnopabcdefghijklmnop')) {
    $rejected = $false
    try { Assert-ProductionExtensionId $id } catch { $rejected = $true }
    if (-not $rejected) { throw "Invalid production ID was accepted: '$id'." }
}
Assert-ProductionExtensionId 'lhffehnfgjhbipabkdgniaenehidnhfa'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$braveDevelopmentKey = 'Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.yoursafe.autofill.dev'
$registrationScript = Get-Content -Raw (Join-Path $PSScriptRoot 'Register-BrowserAutofill.ps1')
if (-not $registrationScript.Contains($braveDevelopmentKey)) { throw 'Brave development registration is missing.' }
$installer = Get-Content -Raw (Join-Path $repositoryRoot 'installer/PasswordTool.iss')
$braveProductionKey = 'Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.yoursafe.autofill'
if (-not $installer.Contains(('Subkey: "{0}"; ValueType: string; ValueName: ""; ValueData: "{{app}}\yoursafe-native-chrome.json"' -f $braveProductionKey)) -or
    -not $installer.Contains(("RegDeleteValue(HKCU32, '{0}', '');" -f $braveProductionKey))) {
    throw 'Brave must reuse the Chrome manifest and clean up its owned registration.'
}
Write-Output 'PASS: Brave development and installer registration use the expected host keys.'
$testDirectory = Join-Path $repositoryRoot ('.tmp/browser-contract-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
try {
    $path = Join-Path $testDirectory 'native.json'
    Write-NativeHostManifest -Path $path -ExecutablePath 'YourSafe.NativeHost.exe' -ExtensionId 'lhffehnfgjhbipabkdgniaenehidnhfa'
    Assert-NativeHostManifest $path
    Write-NativeHostManifest -Path $path -ExecutablePath 'YourSafe.NativeHost.exe' -ExtensionId 'cmfnnjellknkpnbooenlahijcmalbifg' -Development
    $rejected = $false
    try { Assert-NativeHostManifest $path } catch { $rejected = $true }
    if (-not $rejected) { throw 'Development manifest was accepted for production.' }
    Write-Output 'PASS: production IDs and manifest contract reject development and placeholder configuration.'
} finally {
    $fullTestPath = [IO.Path]::GetFullPath($testDirectory)
    $safeRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '.tmp')) + [IO.Path]::DirectorySeparatorChar
    if (-not $fullTestPath.StartsWith($safeRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup path.' }
    Remove-Item -LiteralPath $fullTestPath -Recurse -Force
}
