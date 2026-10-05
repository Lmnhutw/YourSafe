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
