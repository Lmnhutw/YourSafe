[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][string]$TestDirectory
)

# Only drive the marked Debug process seeded by Create_opt_in_disposable_landing_vault.
$ErrorActionPreference = 'Stop'
$winApp = 'C:/Users/lmnhu/.nuget/packages/microsoft.windows.sdk.buildtools.winapp/0.6.1/tools/win-x64/winapp.exe'
$testRoot = (Resolve-Path -LiteralPath $TestDirectory).Path
$windowHandle = (Get-Process -Id $ProcessId).MainWindowHandle.ToInt64()
$outputDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'website/assets/captures-inbox'
$results = [System.Collections.Generic.List[object]]::new()
function Ui {
    $scope = if ($args[0] -eq 'screenshot') { @('-w', $windowHandle) } else { @('-a', $ProcessId) }
    $output = & $winApp ui @args @scope --json 2>&1
    $code = $LASTEXITCODE
    $result = ($output -join "`n") | ConvertFrom-Json
    if ($code -ne 0 -and -not ($args[0] -eq 'search' -and $result.matchCount -eq 0)) { throw "UI command failed: $($args[0]) $($args[1]): $output" }
    $result
}
function Capture([string]$Name) {
    Start-Sleep -Milliseconds 400
    $path = Join-Path $outputDirectory "$Name.png"
    Ui screenshot -o $path | Out-Null
    if ((Get-Item -LiteralPath $path).Length -lt 1000) { throw 'Empty capture.' }
    $results.Add(@{ name = $Name; status = 'CAPTURED'; path = $path })
    Write-Output "CAPTURED: $Name"
}
function Step([string]$Name, [scriptblock]$Action) {
    try { & $Action }
    catch {
        $results.Add(@{ name = $Name; status = 'PENDING'; reason = $_.Exception.Message })
        Write-Output "PENDING: $Name — $($_.Exception.Message)"
    }
}
Ui wait-for YourSafeDisposableUiTest -p HelpText --value $testRoot -t 5000 | Out-Null
if ((Ui get-property YourSafeDisposableUiTest -p HelpText).properties.HelpText -ne $testRoot) { throw 'Disposable vault marker mismatch.' }
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class LandingCaptureWindow {
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
'@
if (-not [LandingCaptureWindow]::SetWindowPos([IntPtr]$windowHandle, [IntPtr]::Zero, 0, 0, 1384, 960, 0x0016)) { throw 'Could not resize the disposable capture window.' }
if (@((Ui search TxtMasterPassword).matches).Count -gt 0) {
    Ui set-value TxtMasterPassword 'correct horse battery staple' | Out-Null
    Ui invoke BtnUnlock | Out-Null
    Ui wait-for 'Digit 1 of 6' -t 5000 | Out-Null
    $counter = [BitConverter]::GetBytes([long][Math]::Floor([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() / 30))
    [Array]::Reverse($counter)
    $hmac = [System.Security.Cryptography.HMACSHA1]::new([Convert]::FromHexString('48656C6C6F21DEADBEEF'))
    try { $hash = $hmac.ComputeHash($counter) } finally { $hmac.Dispose() }
    $offset = $hash[19] -band 15
    $otp = ((([long]($hash[$offset] -band 127) -shl 24) -bor ([long]$hash[$offset + 1] -shl 16) -bor ([long]$hash[$offset + 2] -shl 8) -bor $hash[$offset + 3]) % 1000000).ToString('D6')
    for ($index = 0; $index -lt 6; $index++) { Ui set-value ('Digit ' + ($index + 1) + ' of 6') $otp[$index].ToString() | Out-Null }
    Ui wait-for PrimaryButton -p IsEnabled --value True -t 5000 | Out-Null
    Ui invoke PrimaryButton | Out-Null
}
Ui invoke NavVault | Out-Null
Ui wait-for TxtVaultSearch -t 5000 | Out-Null
Ui set-value TxtVaultSearch '' | Out-Null
Ui focus TxtVaultSearch | Out-Null
Step '01-desktop-vault' {
    Ui wait-for GitHub -t 5000 | Out-Null
    Ui wait-for Netflix -t 5000 | Out-Null
    Capture '01-desktop-vault'
}
Step '02-desktop-editor' {
    Ui set-value TxtVaultSearch GitHub | Out-Null
    Ui focus BtnAddItem | Out-Null
    $edit = @((Ui search 'Edit item').matches | Where-Object { $_.automationId -like 'Edit_*' -and -not $_.isOffscreen })
    if ($edit.Count -ne 1) { throw 'Expected one GitHub editor action.' }
    Ui invoke $edit[0].automationId | Out-Null
    Ui wait-for TxtEditorTitle --value GitHub -t 5000 | Out-Null
    Ui focus TxtEditorTitle | Out-Null
    Capture '02-desktop-editor'
}
Step '04-password-generator' {
    Ui invoke BtnGeneratePassword | Out-Null
    Ui wait-for 'Generate another' -t 5000 | Out-Null
    Capture '04-password-generator'
    Ui invoke CloseButton | Out-Null
}
if (@((Ui search BtnCancelItem).matches).Count -gt 0) { Ui invoke BtnCancelItem | Out-Null }
Step '05-security-check' {
    Ui invoke NavSecurityCheck | Out-Null
    Ui wait-for 'Run check' -t 5000 | Out-Null
    Ui invoke 'Run check' | Out-Null
    Ui wait-for 'Security Check results' -t 5000 | Out-Null
    Start-Sleep -Milliseconds 600
    Capture '05-security-check'
}
Step '06-encrypted-backup' {
    Ui invoke NavBackup | Out-Null
    Ui wait-for 'Create backup' -t 5000 | Out-Null
    Capture '06-encrypted-backup'
}
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $testRoot 'capture-results.json') -Encoding utf8
Write-Output 'Capture results saved in the disposable vault directory.'
