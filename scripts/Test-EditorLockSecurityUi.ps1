[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][string]$TestDirectory,
    [string]$WinAppPath = 'winapp'
)

# Start the disposable Debug app with a one-minute vault duration, then sign in.
# This exercises the real timeout; it never locks or suspends Windows.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$testRoot = [IO.Path]::TrimEndingDirectorySeparator((Resolve-Path -LiteralPath $TestDirectory).Path)
$testProcess = Get-Process -Id $ProcessId
$null = Get-Command $WinAppPath -ErrorAction Stop
$windows = & $WinAppPath ui list-windows -a $ProcessId --json | ConvertFrom-Json
$windowHandle = @($windows | Where-Object { $_.processId -eq $ProcessId -and $_.className -eq 'WinUIDesktopWin32WindowClass' })[0].hwnd
if (-not $windowHandle) { throw 'The supplied process has no YourSafe main window.' }
$outputDirectory = Join-Path $testRoot 'ui-results'
$results = [System.Collections.Generic.List[object]]::new()
$captures = [System.Collections.Generic.List[string]]::new()
$skippedCaptures = [System.Collections.Generic.List[string]]::new()
$settingsPromptEvidence = @{}
$password = 'correct horse battery staple'
$draftRecovery = 'DEMO-R1-RECOVERY-ONLY'
$draftNotes = 'DEMO-R1-PRIVATE-NOTE-ONLY'

function Ui {
    $target = if ($args[0] -eq 'screenshot') { @('-w', $windowHandle) } else { @('-a', $ProcessId) }
    $output = & $WinAppPath ui @args @target --json 2>&1
    $exitCode = $LASTEXITCODE
    $json = ($output -join [Environment]::NewLine) | ConvertFrom-Json
    if ($args[0] -eq 'wait-for' -and $json.timedOut) { throw "UI condition timed out: $($args -join ' ')" }
    if ($exitCode -ne 0 -and -not ($exitCode -eq 1 -and $args[0] -eq 'search' -and $json.matchCount -eq 0)) {
        throw "WinApp $($args[0]) failed ($exitCode): $output"
    }
    return $json
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

function Capture([string]$Name) {
    if (-not @((Ui list-windows) | Where-Object isForeground).Count) {
        $skippedCaptures.Add($Name)
        return
    }
    $path = Join-Path $outputDirectory "editor-security-$Name.png"
    Ui screenshot -o $path --capture-screen | Out-Null
    $captures.Add($path)
}

function Reveal([string]$Selector) {
    Ui scroll-into-view $Selector | Out-Null
    Ui wait-for $Selector -p IsOffscreen --value False -t 5000 | Out-Null
}

function AssertNoDraft {
    Ui wait-for TxtEditorTitle --gone -t 5000 | Out-Null
    Ui wait-for NavVault -p IsSelected --value True -t 5000 | Out-Null
    foreach ($secret in @($draftRecovery, $draftNotes)) {
        if (@((Ui search $secret --max 100).matches | Where-Object { -not $_.isOffscreen }).Count -ne 0) {
            throw 'A draft secret is still exposed through UI Automation.'
        }
    }
}

function UnlockTable {
    Ui invoke BtnLockVault | Out-Null
    Ui wait-for TxtUnlockMasterPassword -t 5000 | Out-Null
    Ui set-value TxtUnlockMasterPassword $password | Out-Null
    Ui invoke PrimaryButton | Out-Null
    Ui wait-for TxtUnlockMasterPassword --gone -t 15000 | Out-Null
    Ui wait-for BtnLockVault -p Name --value 'Lock vault' -t 15000 | Out-Null
    AssertNoDraft
}

function AssertFreshEditor {
    Ui invoke BtnAddItem | Out-Null
    Ui wait-for TxtEditorTitle -p IsEnabled --value True -t 15000 | Out-Null
    foreach ($selector in @('TxtEditorTitle', 'TxtEditorUsername', 'TxtEditorRecoveryCodes', 'TxtEditorNotes')) {
        Reveal $selector
        if ((Ui get-property $selector -p Value).properties.Value -ne '') { throw "The new editor retained data: $selector" }
    }
    Reveal BtnEditorTotp
    Ui wait-for BtnEditorTotp -p Name --value 'Add verification code' -t 5000 | Out-Null
    Ui wait-for BtnRemoveTotp --gone -t 5000 | Out-Null
    Reveal BtnCancelItem
    Ui invoke BtnCancelItem | Out-Null
    # An empty editor must leave immediately, without a stale baseline/discard dialog.
    AssertNoDraft
    Ui wait-for PrimaryButton --gone -t 5000 | Out-Null
}

# Fail closed before any navigation, mutation or screenshot.
Ui wait-for YourSafeDisposableUiTest -p HelpText --value $testRoot -t 5000 | Out-Null
if ((Ui get-property YourSafeDisposableUiTest -p HelpText).properties.HelpText -ne $testRoot) {
    throw 'Disposable vault marker mismatch.'
}
$configPath = Join-Path $testRoot '.config'
if ((Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).VaultOpenDurationMinutes -ne 1) {
    throw 'This batch requires a fresh disposable fixture seeded with a one-minute vault duration before app launch.'
}
if ((Ui get-property BtnLockVault -p Name).properties.Name -eq 'Unlock vault') { UnlockTable }
Ui wait-for BtnLockVault -p Name --value 'Lock vault' -t 5000 | Out-Null
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

try {
    foreach ($hasTotp in @($false, $true)) {
        $case = if ($hasTotp) { 'with-totp' } else { 'without-totp' }
        Test "Timeout clears the $case draft before further input" {
            Ui invoke BtnAddItem | Out-Null
            Ui wait-for TxtEditorTitle -p IsEnabled --value True -t 15000 | Out-Null
            Ui set-value TxtEditorTitle "R1 $case draft" | Out-Null
            Ui set-value TxtEditorUsername 'demo@example.test' | Out-Null
            Ui set-value TxtEditorPassword 'DEMO-R1-PASSWORD-ONLY' | Out-Null
            Reveal TxtEditorRecoveryCodes
            Ui set-value TxtEditorRecoveryCodes $draftRecovery | Out-Null
            Reveal TxtEditorNotes
            Ui set-value TxtEditorNotes $draftNotes | Out-Null
            if ($hasTotp) {
                Reveal BtnEditorTotp
                Ui invoke BtnEditorTotp | Out-Null
                Ui wait-for TxtTotpSecret -t 5000 | Out-Null
                Ui set-value TxtTotpSecret 'JBSWY3DPEHPK3PXP' | Out-Null
                Ui invoke PrimaryButton | Out-Null
                Ui wait-for TxtTotpSecret --gone -t 5000 | Out-Null
            }
            # Short bounded waits keep WinApp timeout semantics explicit.
            $deadline = [DateTime]::UtcNow.AddSeconds(75)
            do {
                if ((Ui search TxtEditorTitle).matchCount -eq 0) { break }
                Start-Sleep -Milliseconds 500
            } while ([DateTime]::UtcNow -lt $deadline)
            Ui wait-for BtnLockVault -p Name --value 'Unlock vault' -t 1000 | Out-Null
            AssertNoDraft
            Ui wait-for TxtUnlockMasterPassword --gone -t 5000 | Out-Null
            Capture "$case-timeout"
        }
        Test "Cancel and a failed re-unlock keep the $case draft discarded" {
            Ui invoke BtnLockVault | Out-Null
            Ui wait-for TxtUnlockMasterPassword -t 5000 | Out-Null
            Capture "$case-unlock-prompt"
            if ($hasTotp) {
                Ui set-value TxtUnlockMasterPassword 'incorrect-demo-password' | Out-Null
                Ui invoke PrimaryButton | Out-Null
                Ui wait-for TxtUnlockMasterPasswordError --value 'The Master Password is incorrect. Please try again.' -t 15000 | Out-Null
                Ui wait-for TxtUnlockMasterPassword -p IsEnabled --value True -t 5000 | Out-Null
                AssertNoDraft
            }
            Ui invoke CloseButton | Out-Null
            Ui wait-for TxtUnlockMasterPassword --gone -t 5000 | Out-Null
            Ui wait-for BtnLockVault -p Name --value 'Unlock vault' -t 5000 | Out-Null
            AssertNoDraft
            UnlockTable
            AssertFreshEditor
        }
    }
    Test 'Natural vault timeout dismisses the settings password prompt without saving the selected duration' {
        # Re-unlock for a fresh real deadline after the editor cases.
        Ui invoke BtnLockVault | Out-Null
        Ui wait-for BtnLockVault -p Name --value 'Unlock vault' -t 5000 | Out-Null
        UnlockTable
        Ui invoke NavSettings | Out-Null
        Reveal CmbVaultDuration
        Ui wait-for CmbVaultDuration --value '1 minute' -t 5000 | Out-Null
        Ui invoke CmbVaultDuration | Out-Null
        Ui wait-for CmbVaultDuration -p ExpandCollapseState --value Expanded -t 5000 | Out-Null
        $duration = @((Ui search '2 minutes' --max 100).matches | Where-Object {
            -not $_.isOffscreen -and $_.name -eq '2 minutes' -and $_.type -eq 'ListItem'
        }) | Select-Object -Last 1
        if ($null -eq $duration) { throw 'Two minute duration was not found.' }
        Ui invoke $duration.selector | Out-Null
        Ui wait-for CmbVaultDuration --value '2 minutes' -t 5000 | Out-Null
        $settingsPromptEvidence.selectedDuration = '2 minutes'
        $settingsPromptEvidence.configBeforeHash = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash
        Reveal BtnSaveSecuritySettings
        Ui invoke BtnSaveSecuritySettings | Out-Null
        Ui wait-for TxtUnlockMasterPassword -t 5000 | Out-Null
        Ui wait-for PrimaryButton -p Name --value 'Save security settings' -t 5000 | Out-Null
        Ui set-value TxtUnlockMasterPassword $password | Out-Null
        Capture 'settings-password-pending'
        $deadline = [DateTime]::UtcNow.AddSeconds(75)
        do {
            if ((Ui search TxtUnlockMasterPassword).matchCount -eq 0) { break }
            Start-Sleep -Milliseconds 500
        } while ([DateTime]::UtcNow -lt $deadline)
        Ui wait-for TxtUnlockMasterPassword --gone -t 1000 | Out-Null
        Ui wait-for BtnLockVault -p Name --value 'Unlock vault' -t 1000 | Out-Null
        AssertNoDraft
        $settingsPromptEvidence.configAfterHash = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash
        if ($settingsPromptEvidence.configAfterHash -cne $settingsPromptEvidence.configBeforeHash) {
            throw 'The expired settings prompt changed persistent configuration.'
        }
        Ui invoke NavSettings | Out-Null
        Reveal CmbVaultDuration
        Ui wait-for CmbVaultDuration --value '1 minute' -t 5000 | Out-Null
        Ui wait-for BtnSaveSecuritySettings -p IsEnabled --value True -t 5000 | Out-Null
        $settingsPromptEvidence.persistedDurationMinutes = (Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).VaultOpenDurationMinutes
        if ($settingsPromptEvidence.persistedDurationMinutes -ne 1) { throw 'The expired settings selection was committed.' }
        Capture 'settings-timeout-not-saved'
        Ui invoke NavVault | Out-Null
    }
}
catch { Write-Warning $_.Exception.Message }
finally {
    @{
        processId = $ProcessId
        testDirectory = $testRoot
        testLayer = 'Real WinUI controls and natural table-lock timer; synthetic vault'
        results = @($results)
        screenshots = @($captures)
        skippedScreenCapturesWhenNotForeground = @($skippedCaptures)
        settingsPromptTimeout = $settingsPromptEvidence
        residualLimitations = 'PasswordBox content and managed references are not readable through UIA. Review screenshots. Windows lock/suspend and late callbacks require separate lifecycle tests.'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputDirectory 'editor-lock-security-ui.json') -Encoding utf8
}
if ($results.Count -ne 5 -or @($results | Where-Object { $_.status -eq 'FAIL' }).Count -gt 0) { exit 1 }
exit 0
