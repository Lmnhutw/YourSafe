[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][string]$TestDirectory,
    [Parameter(Mandatory)][ValidateSet('Setup', 'Recovery')][string]$Scenario,
    [string]$WinAppPath = 'winapp'
)

# Setup needs an empty marked Debug vault; Recovery needs Create_opt_in_disposable_ui_vault.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$testRoot = [IO.Path]::TrimEndingDirectorySeparator((Resolve-Path -LiteralPath $TestDirectory).Path)
$testProcess = Get-Process -Id $ProcessId
$windowHandle = $testProcess.MainWindowHandle.ToInt64()
if ($windowHandle -eq 0) { throw 'The supplied process has no main window.' }
$outputDirectory = Join-Path $testRoot 'ui-results'
$captures = [System.Collections.Generic.List[string]]::new()
$passed = $false
$failure = $null

function Ui {
    $target = if ($args[0] -eq 'screenshot') { @('-w', $windowHandle) } else { @('-a', $ProcessId) }
    $output = & $WinAppPath ui @args @target --json 2>&1
    $code = $LASTEXITCODE
    try { $json = ($output -join [Environment]::NewLine) | ConvertFrom-Json -AsHashtable }
    catch { throw "WinApp $($args[0]) returned invalid JSON." }
    if ($json -isnot [System.Collections.IDictionary] -or $json.Contains('error') -or $code -ne 0) {
        throw "WinApp $($args[0]) failed. Input values are omitted."
    }
    if ($args[0] -eq 'wait-for' -and (-not $json.Contains('timedOut') -or $json.timedOut -isnot [bool] -or $json.timedOut)) {
        throw "WinApp wait-for failed for $($args[1])."
    }
    return $json
}

function Capture([string]$Name) {
    $path = Join-Path $outputDirectory "authentication-$Scenario-$Name.png"
    Ui screenshot -o $path | Out-Null
    $captures.Add($path)
}

function PairHash {
    return '{0}/{1}' -f (Get-FileHash -LiteralPath (Join-Path $testRoot '.config')).Hash,
        (Get-FileHash -LiteralPath (Join-Path $testRoot '.storage')).Hash
}

# Fail closed before entering credentials, changing state or capturing the window.
Ui wait-for YourSafeDisposableUiTest -p HelpText --value $testRoot -t 10000 | Out-Null
if ((Ui get-property YourSafeDisposableUiTest -p HelpText).properties.HelpText -cne $testRoot) { throw 'Disposable vault marker mismatch.' }
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

try {
    if ($Scenario -eq 'Setup') {
        if ((Test-Path -LiteralPath (Join-Path $testRoot '.config')) -or (Test-Path -LiteralPath (Join-Path $testRoot '.storage'))) {
            throw 'Setup requires an empty synthetic vault.'
        }
        Ui invoke BtnCreateVault | Out-Null
        Ui set-value TxtSetupMasterPassword short | Out-Null
        Ui set-value TxtSetupConfirmMasterPassword short | Out-Null
        Ui invoke BtnContinueMasterPassword | Out-Null
        Ui wait-for TxtNewMasterPasswordError -t 5000 | Out-Null
        Ui set-value TxtSetupMasterPassword 'synthetic setup master password' | Out-Null
        Ui set-value TxtSetupConfirmMasterPassword 'incorrect confirmation' | Out-Null
        Ui invoke BtnContinueMasterPassword | Out-Null
        Ui wait-for TxtConfirmMasterPasswordError -t 5000 | Out-Null
        Ui set-value TxtSetupConfirmMasterPassword 'synthetic setup master password' | Out-Null
        Ui invoke BtnContinueMasterPassword | Out-Null
        Ui wait-for TxtAuthenticatorSetupSecret -t 10000 | Out-Null
        $secret = (Ui get-property TxtAuthenticatorSetupSecret -p Name).properties.Name
        Ui invoke BtnCompleteAuthenticator | Out-Null
        Ui wait-for TxtAuthenticatorSetupError -t 10000 | Out-Null
        if ((Ui get-property TxtAuthenticatorSetupSecret -p Name).properties.Name -cne $secret) { throw 'A rejected code changed the prepared Authenticator.' }
        Capture code-error
        Add-Type -Path (Join-Path (Split-Path -Parent $testProcess.Path) 'Otp.NET.dll')
        $totp = [OtpNet.Totp]::new([OtpNet.Base32Encoding]::ToBytes($secret), 30, [OtpNet.OtpHashMode]::Sha1, 6, $null)
        $code = $totp.ComputeTotp([DateTime]::UtcNow)
        for ($index = 0; $index -lt 6; $index++) { Ui set-value "Digit $($index + 1) of 6" $code[$index].ToString() | Out-Null }
        Ui invoke BtnCompleteAuthenticator | Out-Null
        Ui wait-for PrimaryButton -p Name --value "I've saved it" -t 10000 | Out-Null
        Ui invoke CloseButton | Out-Null
        Ui wait-for BtnCompleteAuthenticator -t 10000 | Out-Null
        if ((Ui get-property TxtAuthenticatorSetupSecret -p Name).properties.Name -cne $secret) { throw 'Cancel discarded the prepared Authenticator.' }
        if ((Test-Path -LiteralPath (Join-Path $testRoot '.config')) -or (Test-Path -LiteralPath (Join-Path $testRoot '.storage'))) {
            throw 'Incomplete setup wrote vault files.'
        }
        Capture after-key-cancel
    }
    else {
        $receipt = Get-Content -LiteralPath (Join-Path $testRoot 'ui-test-credentials.json') -Raw | ConvertFrom-Json
        if ($receipt.fixture -cne 'YourSafe disposable UI test credentials v1') { throw 'Synthetic recovery fixture missing.' }
        $before = PairHash
        Ui invoke 'Forgot Master Password?' | Out-Null
        Ui wait-for PrimaryButton -p Name --value 'Enter Recovery Key' -t 5000 | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for TxtRecoveryKey -t 10000 | Out-Null
        Ui set-value TxtRecoveryKey 'invalid synthetic recovery key' | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for TxtRecoveryKeyError -t 15000 | Out-Null
        Capture key-error
        if ((PairHash) -cne $before) { throw 'Rejected key changed storage.' }
        Ui set-value TxtRecoveryKey $receipt.recoveryKey | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for TxtRecoveryNewPassword -t 15000 | Out-Null
        Ui set-value TxtRecoveryNewPassword short | Out-Null
        Ui set-value TxtRecoveryConfirmPassword short | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for TxtRecoveryNewPasswordError -t 5000 | Out-Null
        Ui set-value TxtRecoveryNewPassword 'correct horse battery staple' | Out-Null
        Ui set-value TxtRecoveryConfirmPassword 'correct horse battery staple' | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for TxtRecoveryNewPasswordError --value 'Choose a different Master Password from your current one.' -t 15000 | Out-Null
        Ui set-value TxtRecoveryNewPassword 'new synthetic replacement password' | Out-Null
        Ui set-value TxtRecoveryConfirmPassword 'not the same synthetic password' | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for TxtRecoveryConfirmationError -t 5000 | Out-Null
        Capture password-error
        Ui set-value TxtRecoveryConfirmPassword 'new synthetic replacement password' | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for PrimaryButton -p Name --value "I've saved it" -t 15000 | Out-Null
        Ui invoke 'I saved this key in a safe place outside this device.' | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for 'Digit 1 of 6' -t 15000 | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for TxtRecoveryAuthenticatorError -t 10000 | Out-Null
        Capture code-error
        if ((PairHash) -cne $before) { throw 'Rejected recovery code changed storage.' }
        Ui invoke CloseButton | Out-Null
        Ui wait-for TxtMasterPassword -t 10000 | Out-Null
        if ((PairHash) -cne $before) { throw 'Cancelled recovery changed storage.' }
    }
    $passed = $true
    Write-Output "PASS: $Scenario validation retries preserve the current step and Cancel preserves storage."
}
catch { $failure = $_.Exception.Message; Write-Warning $failure }
finally {
    @{
        scenario = $Scenario; passed = $passed; failure = $failure; screenshots = @($captures)
        processId = $ProcessId; testDirectory = $testRoot
        residualLimitations = 'Uses only synthetic credentials; no Windows lock/suspend injection or successful recovery commit. Review screenshots.'
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputDirectory "authentication-$Scenario-ui.json") -Encoding utf8
}
if (-not $passed) { exit 1 }
exit 0
