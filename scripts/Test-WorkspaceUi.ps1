[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][string]$TestDirectory,
    [string]$WinAppPath = 'winapp',
    [string]$CaptureLabel = 'current'
)

# Unlock the disposable DEBUG vault before this focused UI check.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$testRoot = [System.IO.Path]::TrimEndingDirectorySeparator((Resolve-Path -LiteralPath $TestDirectory).Path)
$testProcess = Get-Process -Id $ProcessId
[IntPtr]$windowHandle = $testProcess.MainWindowHandle
if ($windowHandle -eq [IntPtr]::Zero) { throw 'The supplied process has no main window.' }
$null = Get-Command $WinAppPath -ErrorAction Stop
$results = [System.Collections.Generic.List[object]]::new()
$captures = [System.Collections.Generic.List[string]]::new()
$geometry = [System.Collections.Generic.List[object]]::new()
$captureName = $CaptureLabel -replace '[^A-Za-z0-9_-]', '_'
$outputDirectory = Join-Path $testRoot 'ui-results'
$hashPassword = 'synthetic-test-password'

function Ui {
    $target = if ($args[0] -eq 'screenshot') { @('-w', $windowHandle.ToInt64()) } else { @('-a', $ProcessId) }
    $output = & $WinAppPath ui @args @target --json 2>&1
    $exitCode = $LASTEXITCODE
    $json = ($output -join [Environment]::NewLine) | ConvertFrom-Json
    if ($args[0] -eq 'wait-for' -and $json.timedOut) { throw "UI condition timed out: $($args -join ' ')" }
    if ($exitCode -ne 0 -and -not ($exitCode -eq 1 -and $args[0] -eq 'search' -and $json.matchCount -eq 0)) {
        throw "WinApp $($args[0]) [$($args[1])] failed ($exitCode): $output"
    }
    return $json
}

function Matches([string]$Selector) {
    return @((Ui search $Selector --max 100).matches | Where-Object { -not $_.isOffscreen })
}

function WaitForNamedType([string]$Name, [string]$Type, [int]$TimeoutMs = 5000) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $element = @(Matches $Name | Where-Object { $_.name -eq $Name -and $_.type -eq $Type }) | Select-Object -Last 1
        if ($null -ne $element) {
            Ui wait-for $element.selector -p IsOffscreen --value False -t 1000 | Out-Null
            return $element
        }
        Start-Sleep -Milliseconds 100
    } while ($watch.ElapsedMilliseconds -lt $TimeoutMs)
    throw "Visible $Type named '$Name' was not found."
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
if ((Ui get-property TogglePaneButton -p Name).properties.Name -eq 'Close Navigation') { Ui invoke TogglePaneButton | Out-Null }
Ui wait-for BtnLockVault -p Name --value 'Lock vault' -t 5000 | Out-Null
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

try {
    Test 'Password Tool shows inline defaults and only Generate produces a copyable result' {
        Ui invoke NavHashTool | Out-Null
        Ui invoke HashModeGenerate | Out-Null
        Ui wait-for TxtPasswordToolTitle --value 'Password Tool' -t 5000 | Out-Null
        Ui wait-for ChkGenerateHash --value Off -t 5000 | Out-Null
        Ui wait-for BtnToolGeneratePassword -t 5000 | Out-Null
        Ui wait-for CmbHashAlgorithm --gone -t 5000 | Out-Null
        Ui wait-for CmbToolPasswordType --value 'Password' -t 5000 | Out-Null
        Ui wait-for 'Number of characters Minimum8 Maximum128' --value '24' -t 5000 | Out-Null
        Ui wait-for BtnCopyToolGeneratedPassword -p IsEnabled --value False -t 5000 | Out-Null
        Capture 'tool-default'
        Ui invoke BtnToolGeneratePassword | Out-Null
        Ui wait-for BtnCopyToolGeneratedPassword -p IsEnabled --value True -t 5000 | Out-Null
        $generated = (Ui get-value TxtToolGeneratedPassword).text
        if ($generated.Length -ne 24 -or $generated -cnotmatch '[A-Z]' -or $generated -cnotmatch '[a-z]' -or
            $generated -notmatch '[0-9]' -or $generated -notmatch '[^A-Za-z0-9]') {
            throw 'Password generator did not return the shared default password options.'
        }
        Capture 'tool-password'
        Ui invoke BtnCopyToolGeneratedPassword | Out-Null
        Ui wait-for 'Generated password copied. YourSafe will try to clear it after 30 seconds.' -t 5000 | Out-Null
        if ((Get-Clipboard -Raw) -cne $generated) { throw 'Copy did not place the current generated password on the clipboard.' }
        Set-Clipboard -Value 'YOURSAFE-DISPOSABLE-GENERATOR-SENTINEL'
        Ui send-keys 'ctrl+a' --target TxtToolGeneratedPassword --via send-input | Out-Null
        Ui send-keys 'ctrl+c' --target TxtToolGeneratedPassword --via send-input | Out-Null
        if ((Get-Clipboard -Raw) -cne 'YOURSAFE-DISPOSABLE-GENERATOR-SENTINEL') { throw 'Native Ctrl+C bypassed secure Copy.' }
        Ui send-keys 'ctrl+insert' --target TxtToolGeneratedPassword --via send-input | Out-Null
        if ((Get-Clipboard -Raw) -cne 'YOURSAFE-DISPOSABLE-GENERATOR-SENTINEL') { throw 'Native Ctrl+Insert bypassed secure Copy.' }
        Ui invoke BtnCopyToolGeneratedPassword | Out-Null
        if ((Get-Clipboard -Raw) -cne $generated) { throw 'Secure Copy did not replace the test sentinel.' }
    }
    Test 'Generator option and type changes discard old output and require Generate again' {
        Ui invoke ChkToolSymbols | Out-Null
        Ui wait-for BtnCopyToolGeneratedPassword -p IsEnabled --value False -t 5000 | Out-Null
        if ((Ui get-property TxtToolGeneratedPassword -p Value).properties.Value -ne '') { throw 'Options retained a stale generated password.' }
        Ui invoke BtnToolGeneratePassword | Out-Null
        if ((Ui get-value TxtToolGeneratedPassword).text -match '[^A-Za-z0-9]') { throw 'Generator ignored the disabled symbols option.' }
        Ui invoke CmbToolPasswordType | Out-Null
        $type = WaitForNamedType 'Passphrase' 'ListItem'
        Ui invoke $type.selector | Out-Null
        Ui wait-for 'Number of word pairs Minimum4 Maximum12' --value '6' -t 5000 | Out-Null
        Ui wait-for BtnCopyToolGeneratedPassword -p IsEnabled --value False -t 5000 | Out-Null
        Ui invoke BtnToolGeneratePassword | Out-Null
        if (((Ui get-value TxtToolGeneratedPassword).text -split '-').Count -ne 6) { throw 'Passphrase did not use six word pairs.' }
        Capture 'tool-inline-passphrase'
    }
    Test 'Hash generation validates missing input and accepts a password' {
        Ui invoke NavHashTool | Out-Null
        Ui invoke HashModeGenerate | Out-Null
        Ui invoke ChkGenerateHash | Out-Null
        Ui wait-for CmbHashAlgorithm -t 5000 | Out-Null
        Ui wait-for BtnToolGeneratePassword --gone -t 5000 | Out-Null
        Ui invoke BtnGenerateHash | Out-Null
        Ui wait-for 'Enter a password and select an algorithm.' -t 5000 | Out-Null
        Ui set-value TxtHashGeneratePassword $hashPassword | Out-Null
        Ui invoke BtnGenerateHash | Out-Null
        Ui wait-for TxtGeneratedHash --value '$' --contains -t 15000 | Out-Null
        $script:storedHash = (Ui get-value TxtGeneratedHash).text
        if ([string]::IsNullOrWhiteSpace($script:storedHash)) { throw 'Generated hash is empty.' }
        Capture 'tool-hash'
    }
    Test 'Verify starts empty and shows only a submitted result beside its button' {
        Ui invoke HashModeVerify | Out-Null
        Ui wait-for TxtHashVerifyPassword -t 5000 | Out-Null
        AssertVerification ''
        Capture '01-verify-initial'
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
    Test 'Inspect accepts a generated hash; changing modes clears passwords and hashes' {
        Ui invoke HashModeInspect | Out-Null
        Ui set-value TxtHashInspectStored $script:storedHash | Out-Null
        Ui invoke BtnInspectHash | Out-Null
        Ui wait-for TxtHashInspectionResult --value 'Algorithm:' --contains -t 5000 | Out-Null
        Ui invoke HashModeGenerate | Out-Null
        Ui invoke ChkGenerateHash | Out-Null
        Ui wait-for TxtToolGeneratedPassword -t 5000 | Out-Null
        if ((Ui get-property TxtToolGeneratedPassword -p Value).properties.Value -ne '') { throw 'Mode switch retained an old plaintext password.' }
        Ui invoke ChkGenerateHash | Out-Null
        if ((Ui get-property TxtGeneratedHash -p Value).properties.Value -ne '') { throw 'Mode switch retained an old hash.' }
        Ui invoke ChkGenerateHash | Out-Null
        Capture 'tool-reset'
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
    Test 'Editor required fields validate inline and optional recovery codes clear their error' {
        Ui invoke BtnAddItem | Out-Null
        AssertEditor
        Ui wait-for CmbEditorGroup --value 'All' -t 5000 | Out-Null
        Reveal BtnSaveItem
        Ui invoke BtnSaveItem | Out-Null
        Ui wait-for TxtEditorTitleError --value 'Enter a title.' -t 5000 | Out-Null
        Ui wait-for TxtEditorTitle -p HasKeyboardFocus --value True -t 5000 | Out-Null
        Ui set-value TxtEditorTitle 'Inline validation fixture' | Out-Null
        Ui wait-for TxtEditorTitleError --gone -t 5000 | Out-Null
        Ui set-value TxtEditorUsername 'synthetic-user' | Out-Null
        Ui wait-for TxtEditorUsernameError --gone -t 5000 | Out-Null
        Ui wait-for TxtEditorPasswordError --value 'Enter a password.' -t 5000 | Out-Null
        Ui set-value TxtEditorPassword 'synthetic-test-password' | Out-Null
        Ui wait-for TxtEditorPasswordError --gone -t 5000 | Out-Null
        Reveal ChkEditorNoUrl
        Ui invoke ChkEditorNoUrl | Out-Null
        Ui wait-for TxtEditorUrlError --gone -t 5000 | Out-Null
        Reveal TxtEditorRecoveryCodes
        Ui set-value TxtEditorRecoveryCodes 'only-one-code' | Out-Null
        Ui wait-for TxtEditorRecoveryCodesError -t 5000 | Out-Null
        Ui set-value TxtEditorRecoveryCodes '' | Out-Null
        Ui wait-for TxtEditorRecoveryCodesError --gone -t 5000 | Out-Null
        Reveal BtnEditorRecoveryHelp
        Ui send-keys enter --target BtnEditorRecoveryHelp --via send-input | Out-Null
        $help = WaitForNamedType 'Use the recovery codes supplied by the website. Enter at least two different codes, one per line or separated by commas. For example: 1234-5678, 8765-4321. You can leave this field empty.' 'ToolTip'
        Ui wait-for $help.selector -p Name --value 'Use the recovery codes supplied by the website. Enter at least two different codes, one per line or separated by commas. For example: 1234-5678, 8765-4321. You can leave this field empty.' -t 5000 | Out-Null
        Capture 'editor-recovery-help'
        Ui focus TxtEditorRecoveryCodes | Out-Null
        Capture 'editor-inline-validation'
        Reveal BtnCancelItem
        Ui invoke BtnCancelItem | Out-Null
        Ui wait-for PrimaryButton -t 5000 | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for TxtEditorTitle --gone -t 5000 | Out-Null
    }
    Test 'Security settings prompt uses the action label and Cancel preserves the chosen duration' {
        Ui invoke NavSettings | Out-Null
        Reveal CmbVaultDuration
        Ui invoke CmbVaultDuration | Out-Null
        Ui wait-for CmbVaultDuration -p ExpandCollapseState --value Expanded -t 5000 | Out-Null
        $duration = @(Matches '2 minutes' | Where-Object { $_.name -eq '2 minutes' -and $_.type -eq 'ListItem' }) | Select-Object -Last 1
        if ($null -eq $duration) { throw 'Two minute duration was not found.' }
        Ui invoke $duration.selector | Out-Null
        Reveal BtnSaveSecuritySettings
        Ui invoke BtnSaveSecuritySettings | Out-Null
        Ui wait-for TxtUnlockMasterPassword -t 5000 | Out-Null
        Ui wait-for PrimaryButton -p Name --value 'Save security settings' -t 5000 | Out-Null
        Capture 'settings-single-prompt'
        Ui invoke CloseButton | Out-Null
        Ui wait-for TxtUnlockMasterPassword --gone -t 5000 | Out-Null
        Ui wait-for CmbVaultDuration --value '2 minutes' -t 5000 | Out-Null
        Ui invoke NavVault | Out-Null
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
    Test 'Creating a group reports success after persistence' {
        Ui invoke BtnAddGroup | Out-Null
        Ui wait-for TxtNewGroupName -t 5000 | Out-Null
        Ui set-value TxtNewGroupName 'UI flow created group' | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for 'Group “UI flow created group” created.' -t 15000 | Out-Null
        Capture 'group-success-feedback'
    }
    Test 'Locked settings save verifies one password, rejects wrong input and relocks after success' {
        Ui invoke BtnLockVault | Out-Null
        Ui invoke NavSettings | Out-Null
        Reveal BtnSaveSecuritySettings
        $before = Get-Content -LiteralPath (Join-Path $testRoot '.config') -Raw
        Ui invoke BtnSaveSecuritySettings | Out-Null
        Ui wait-for TxtUnlockMasterPassword -t 5000 | Out-Null
        Ui set-value TxtUnlockMasterPassword 'incorrect synthetic password' | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for TxtUnlockMasterPassword --gone -t 15000 | Out-Null
        Ui wait-for BtnSaveSecuritySettings -p IsEnabled --value True -t 15000 | Out-Null
        if ((Get-Content -LiteralPath (Join-Path $testRoot '.config') -Raw) -cne $before) { throw 'Wrong settings password changed persistent configuration.' }
        Ui invoke BtnSaveSecuritySettings | Out-Null
        Ui wait-for TxtUnlockMasterPassword -t 5000 | Out-Null
        Ui set-value TxtUnlockMasterPassword 'correct horse battery staple' | Out-Null
        Ui invoke PrimaryButton | Out-Null
        $saved = WaitForNamedType 'Settings saved' 'Window' 15000
        Ui wait-for TxtUnlockMasterPassword --gone -t 5000 | Out-Null
        Ui invoke CloseButton | Out-Null
        Ui invoke NavVault | Out-Null
        Ui wait-for BtnLockVault -p Name --value 'Unlock vault' -t 5000 | Out-Null
        Capture 'settings-relocked'
        Ui invoke BtnLockVault | Out-Null
        Ui wait-for TxtUnlockMasterPassword -t 5000 | Out-Null
        Ui set-value TxtUnlockMasterPassword 'correct horse battery staple' | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for BtnLockVault -p Name --value 'Lock vault' -t 15000 | Out-Null
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
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputDirectory "workspace-ui-$captureName.json") -Encoding utf8
}
if (@($results | Where-Object { $_.status -eq 'FAIL' }).Count -gt 0) { exit 1 }
exit 0
