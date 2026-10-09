[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][string]$TestDirectory,
    [string]$CaptureLabel = 'current'
)

# Unlock the disposable DEBUG vault before this focused UI check.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$testRoot = [System.IO.Path]::TrimEndingDirectorySeparator((Resolve-Path -LiteralPath $TestDirectory).Path)
$testProcess = Get-Process -Id $ProcessId
[IntPtr]$windowHandle = $testProcess.MainWindowHandle
if ($windowHandle -eq [IntPtr]::Zero) { throw 'The supplied process has no main window.' }
$null = Get-Command winapp -ErrorAction Stop
$results = [System.Collections.Generic.List[object]]::new()
$captures = [System.Collections.Generic.List[string]]::new()
$geometry = [System.Collections.Generic.List[object]]::new()
$captureName = $CaptureLabel -replace '[^A-Za-z0-9_-]', '_'
$outputDirectory = Join-Path $testRoot 'ui-results'
$hashPassword = 'synthetic-test-password'

function Ui {
    $target = if ($args[0] -eq 'screenshot') { @('-w', $windowHandle.ToInt64()) } else { @('-a', $ProcessId) }
    $output = & winapp ui @args @target --json 2>&1
    $exitCode = $LASTEXITCODE
    $json = ($output -join [Environment]::NewLine) | ConvertFrom-Json
    if ($exitCode -ne 0 -and -not ($exitCode -eq 1 -and $args[0] -eq 'search' -and $json.matchCount -eq 0)) {
        throw "WinApp $($args[0]) [$($args[1])] failed ($exitCode): $output"
    }
    return $json
}

function Matches([string]$Selector) {
    return @((Ui search $Selector --max 100).matches | Where-Object { -not $_.isOffscreen })
}

function Element([string]$Selector) {
    $matches = @(Matches $Selector | Where-Object { $_.width -gt 0 -and $_.height -gt 0 })
    if ($matches.Count -ne 1) { throw "Expected one visible element with nonzero bounds: $Selector" }
    return $matches[0]
}

function Reveal([string]$Selector) {
    Ui scroll-into-view $Selector | Out-Null
    Ui wait-for $Selector -p IsOffscreen --value False -t 5000 | Out-Null
}

function Capture([string]$Name) {
    $path = Join-Path $outputDirectory "$captureName-$Name.png"
    Start-Sleep -Milliseconds 150
    Ui screenshot --capture-screen -o $path | Out-Null
    $captures.Add($path)
}

function Test([string]$Name, [scriptblock]$Action) {
    try {
        & $Action | Out-Null
        $results.Add(@{ name = $Name; status = 'PASS' })
        Write-Host "PASS: $Name"
    }
    catch {
        $results.Add(@{ name = $Name; status = 'FAIL'; detail = $_.Exception.Message })
        throw
    }
}

function AssertHorizontal([string]$Name, [string]$First, [string]$Second) {
    Reveal $Second
    $a = Element $First
    $b = Element $Second
    $geometry.Add(@{ name = $Name; first = $a; second = $b })
    if ([Math]::Abs(($a.y + $a.height / 2) - ($b.y + $b.height / 2)) -gt 2 -or
        -not ($a.x + $a.width -le $b.x -or $b.x + $b.width -le $a.x)) {
        throw "$Name must share a row without overlapping."
    }
}

function AssertVerification([string]$Expected) {
    if ($Expected.Length -eq 0) {
        if (@(Matches TxtHashVerificationResult | Where-Object { $_.PSObject.Properties['name'] -and $_.name }).Count -ne 0) {
            throw 'An old verification result is still visible.'
        }
        return
    }
    Ui wait-for TxtHashVerificationResult -p Name --value $Expected -t 5000 | Out-Null
}

function AssertEditor {
    Ui wait-for TxtEditorTitle -p IsEnabled --value True -t 15000 | Out-Null
    Ui wait-for TxtEditorPassword -p IsEnabled --value True -t 5000 | Out-Null
    $heading = Element TxtEditorPageTitle
    $titleBar = Element YourSafeDisposableUiTest
    if ($heading.y -lt $titleBar.y + $titleBar.height) { throw 'The new-item page title is clipped by the old scroll position.' }
    if (@(Matches TxtUnlockMasterPassword).Count -ne 0) { throw 'The editor still has an unlock prompt.' }
}

function CancelEditor {
    Reveal BtnCancelItem
    Ui invoke BtnCancelItem | Out-Null
    Ui wait-for TxtEditorTitle --gone -t 5000 | Out-Null
    Ui wait-for NavVault -p IsSelected --value True -t 5000 | Out-Null
}

# Fail closed before navigation or writing screenshots: never drive a real vault.
Ui wait-for YourSafeDisposableUiTest -p HelpText --value $testRoot -t 5000 | Out-Null
if ((Ui get-property YourSafeDisposableUiTest -p HelpText).properties.HelpText -ne $testRoot) {
    throw 'Disposable vault marker mismatch.'
}
Ui invoke NavVault | Out-Null
Ui wait-for BtnLockVault -p Name --value 'Lock vault' -t 5000 | Out-Null
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

try {
    Test 'Verify starts empty and shows only a submitted result beside its button' {
        Ui invoke NavHashTool | Out-Null
        Ui invoke HashModeVerify | Out-Null
        Ui wait-for TxtHashVerifyPassword -t 5000 | Out-Null
        AssertVerification ''
        Capture '01-verify-initial'
        Ui invoke HashModeGenerate | Out-Null
        Ui set-value TxtHashGeneratePassword $hashPassword | Out-Null
        Ui invoke BtnGenerateHash | Out-Null
        Ui wait-for TxtGeneratedHash --value '$' --contains -t 15000 | Out-Null
        $script:storedHash = (Ui get-value TxtGeneratedHash).text
        if ([string]::IsNullOrWhiteSpace($script:storedHash)) { throw 'Generated hash is empty.' }
        Ui invoke HashModeVerify | Out-Null
        Ui set-value TxtHashVerifyPassword $hashPassword | Out-Null
        Ui set-value TxtHashVerifyStored $script:storedHash | Out-Null
        Ui invoke BtnVerifyHash | Out-Null
        AssertVerification 'Password verified.'
        AssertHorizontal 'Verification result and action' TxtHashVerificationResult BtnVerifyHash
        Capture '02-verify-success'
    }
    Test 'Editing either input clears the old result; mismatch and missing input do not retain success' {
        Ui set-value TxtHashVerifyPassword 'incorrect synthetic password' | Out-Null
        AssertVerification ''
        Ui invoke BtnVerifyHash | Out-Null
        AssertVerification 'Password does not match.'
        Capture '03-verify-mismatch'
        Ui set-value TxtHashVerifyStored "$script:storedHash " | Out-Null
        AssertVerification ''
        Ui set-value TxtHashVerifyStored $script:storedHash | Out-Null
        Ui set-value TxtHashVerifyPassword $hashPassword | Out-Null
        Ui invoke BtnVerifyHash | Out-Null
        AssertVerification 'Password verified.'
        # Verification clears the submitted password; repeating now exercises missing input.
        Ui invoke BtnVerifyHash | Out-Null
        AssertVerification ''
        Ui wait-for 'Enter both a password and stored hash.' -t 5000 | Out-Null
        Capture '04-verify-missing-input'
    }
    Test 'Backup and CSV actions stay horizontal without overlap' {
        Ui invoke NavBackup | Out-Null
        Ui wait-for BtnCreateBackup -t 10000 | Out-Null
        AssertHorizontal 'Create and verify backup' BtnCreateBackup BtnVerifyBackup
        AssertHorizontal 'Preview and import backup' BtnPreviewBackup BtnImportBackup
        Capture '05-backup'
        AssertHorizontal 'Preview and import CSV' BtnPreviewCsv BtnImportCsv
        Capture '06-csv'
        Reveal TxtLocalSnapshotsTitle
        Capture '06-local-snapshots'
    }
    Test 'Session explanation is a tooltip on the security heading row' {
        Ui invoke NavSettings | Out-Null
        Reveal BtnSessionHelp
        AssertHorizontal 'Security heading and session help' TxtSignInVaultLockTitle BtnSessionHelp
        if (@(Matches 'YourSafe session: 5 hours').Count -ne 0) { throw 'Session information is still an inline row.' }
        Capture '07-security-settings'
        Ui focus BtnSessionHelp | Out-Null
        Ui hover BtnSessionHelp --dwell-time 1500 | Out-Null
        $help = @(Matches 'YourSafe session lasts 5 hours' | Where-Object { $_.type -eq 'Text' })
        if ($help.Count -eq 0 -or $help[0].name -notlike '*Vault lock has a separate timer*') {
            throw 'Session tooltip does not explain the separate vault-lock timer.'
        }
        Capture '07-session-tooltip'
        Ui hover CmbVaultDuration | Out-Null
    }
    Test 'Master Password actions remain visible in one row' {
        Ui invoke NavSettings | Out-Null
        Ui wait-for BtnUpgradePasswordProtection -t 5000 | Out-Null
        AssertHorizontal 'Master Password actions' BtnUpgradePasswordProtection BtnChangeMasterPassword
        Capture '07-master-password'
    }
    Test 'Unlocked Add opens an enabled editor without asking for a password' {
        Ui invoke NavVault | Out-Null
        Ui wait-for BtnAddItem -t 5000 | Out-Null
        Capture '08-vault-headers'
        Ui invoke BtnAddItem | Out-Null
        AssertEditor
        Capture '09-unlocked-add'
        CancelEditor
    }
    Test 'Locked Add asks to unlock first; Cancel stays on Vault' {
        Ui invoke BtnLockVault | Out-Null
        Ui wait-for BtnLockVault -p Name --value 'Unlock vault' -t 10000 | Out-Null
        Ui invoke BtnAddItem | Out-Null
        Ui wait-for TxtUnlockMasterPassword -t 5000 | Out-Null
        if (@(Matches TxtEditorTitle).Count -ne 0) { throw 'Locked Add opened the editor before unlocking.' }
        Capture '10-locked-add-prompt'
        Ui invoke CloseButton | Out-Null
        Ui wait-for TxtUnlockMasterPassword --gone -t 5000 | Out-Null
        Ui wait-for NavVault -p IsSelected --value True -t 5000 | Out-Null
        if (@(Matches TxtEditorTitle).Count -ne 0) { throw 'Cancel opened the editor.' }
    }
    Test 'Locked Ctrl+N follows the same unlock flow and then enables the editor' {
        Ui focus BtnAddItem | Out-Null
        Ui send-keys 'ctrl+n' --target BtnAddItem --via send-input | Out-Null
        Ui wait-for TxtUnlockMasterPassword -t 5000 | Out-Null
        if (@(Matches TxtEditorTitle).Count -ne 0) { throw 'Ctrl+N opened the editor before unlocking.' }
        Ui set-value TxtUnlockMasterPassword 'correct horse battery staple' | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for TxtUnlockMasterPassword --gone -t 15000 | Out-Null
        AssertEditor
        Capture '11-unlocked-keyboard-add'
        CancelEditor
    }
}
catch { Write-Warning $_.Exception.Message }
finally {
    @{
        processId = $ProcessId
        testDirectory = $testRoot
        results = @($results)
        geometry = @($geometry)
        screenshots = @($captures)
        visualReviewRequired = 'Review backup hierarchy, tooltips, header dividers, clipping and secondary text contrast in both themes. Check the minimum window width separately.'
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputDirectory "requested-layout-ui-$captureName.json") -Encoding utf8
}
if (@($results | Where-Object { $_.status -eq 'FAIL' }).Count -gt 0) { exit 1 }
exit 0
