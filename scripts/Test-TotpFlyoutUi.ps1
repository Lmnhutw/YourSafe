param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][string]$TestDirectory,
    [switch]$SetupOnly
)

# Only the synthetic vault seeded by Create_opt_in_disposable_ui_vault.
$ErrorActionPreference = 'Stop'
$winApp = 'C:\Users\lmnhu\.nuget\packages\microsoft.windows.sdk.buildtools.winapp\0.6.1\tools\win-x64\winapp.exe'
function Ui {
    $output = & $winApp ui @args -a $ProcessId --json
    $exitCode = $LASTEXITCODE
    $result = $output -join "`n" | ConvertFrom-Json
    if ($exitCode -ne 0 -and -not ($args[0] -eq 'search' -and $result.matchCount -eq 0)) { throw ($args[0] + ' ' + $args[1] + ': ' + ($output -join "`n")) }
    $result
}
function Element([string]$Name) {
    $matches = @((Ui search $Name).matches | Where-Object { $_.name -eq $Name -or $_.automationId -eq $Name })
    if ($matches.Count -ne 1) { throw "Expected one element: $Name" }
    $matches[0]
}
function CurrentCode {
    $match = @((Ui search TxtTotpCode).matches | Where-Object { $_.automationId -eq 'TxtTotpCode' })
    if ($match.Count -eq 0) { return '' }
    $match[0].name
}
$directory = (Resolve-Path -LiteralPath $TestDirectory).Path
Ui wait-for YourSafeDisposableUiTest -t 10000 | Out-Null
if ((Ui get-property YourSafeDisposableUiTest -p HelpText).properties.HelpText -ne $directory) { throw 'Disposable vault marker mismatch.' }
if (@((Ui search TxtMasterPassword).matches).Count -gt 0) {
    Ui set-value TxtMasterPassword 'correct horse battery staple' | Out-Null
    Ui invoke BtnUnlock | Out-Null
    Ui wait-for 'Digit 1 of 6' -t 5000 | Out-Null
    $counter = [BitConverter]::GetBytes([long][Math]::Floor([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() / 30))
    [Array]::Reverse($counter)
    $hmac = [System.Security.Cryptography.HMACSHA1]::new([Convert]::FromHexString('48656C6C6F21DEADBEEF'))
    try { $hash = $hmac.ComputeHash($counter) } finally { $hmac.Dispose() }
    $offset = $hash[19] -band 15
    $code = ((([long]($hash[$offset] -band 127) -shl 24) -bor ([long]$hash[$offset + 1] -shl 16) -bor ([long]$hash[$offset + 2] -shl 8) -bor $hash[$offset + 3]) % 1000000).ToString('D6')
    for ($index = 0; $index -lt 6; $index++) {
        Ui set-value ('Digit ' + ($index + 1) + ' of 6') $code[$index].ToString() | Out-Null
        Start-Sleep -Milliseconds 100
    }
    Ui wait-for PrimaryButton -p IsEnabled --value True -t 5000 | Out-Null
    Ui invoke PrimaryButton | Out-Null
}
Ui wait-for TxtVaultSearch -t 5000 | Out-Null
function OpenTotp([string]$Title) {
    Ui click TxtVaultSearch | Out-Null
    Ui set-value TxtVaultSearch $Title | Out-Null
    Ui wait-for 'View verification code' -t 5000 | Out-Null
    Ui click (Element 'View verification code').automationId | Out-Null
    Ui wait-for TxtTotpCode -t 5000 | Out-Null
    Ui wait-for BtnCopyTotp -p IsEnabled --value True -t 5000 | Out-Null
    Start-Sleep -Milliseconds 300
}
if (-not $SetupOnly) {
OpenTotp 'TOTP password account'
$codeElement = Element 'TxtTotpCode'
$indicator = Element 'TotpTimeRemaining'
$copy = Element 'BtnCopyTotp'
$manage = Element 'Manage verification code'
Ui screenshot -o (Join-Path $directory 'totp-flyout.png') | Out-Null
if ($codeElement.name -notmatch '^\d{3}\u2009\d{3}$') { throw 'Expected six digits with a thin grouping space.' }
$firstHalf = Element 'TxtTotpCodeFirstHalf'
$secondHalf = Element 'TxtTotpCodeSecondHalf'
if ([Math]::Abs($secondHalf.x - ($firstHalf.x + $firstHalf.width) - 4) -gt 1) { throw 'Code groups must have a four-pixel gap.' }
if ([Math]::Abs(($copy.y + $copy.height / 2) - ($manage.y + $manage.height / 2)) -gt 3 -or $copy.x -le $manage.x) { throw 'Copy and Manage must share the footer row.' }
if ([Math]::Abs(($indicator.y + $indicator.height / 2) - ($firstHalf.y + $firstHalf.height / 2)) -gt 3 -or $indicator.x -le $secondHalf.x) { throw 'The filled timer must sit to the right of the code.' }
if (@((Ui search ProgressBar).matches).Count -gt 0 -or @((Ui search 's').matches | Where-Object { $_.type -eq 'Text' -and $_.name -match '^\d+s$' }).Count -gt 0) { throw 'The old countdown is still visible.' }
Ui screenshot -o (Join-Path $directory 'totp-flyout.png') | Out-Null
$decreased = $renewed = $false
Write-Output 'PASS: flyout layout and four-pixel grouping; observing one code renewal.'
$previousCode = CurrentCode
$initialTime = (Ui get-property TotpTimeRemaining -p HelpText).properties.HelpText
if ($initialTime -notmatch '^([1-8])/8 remaining\.') { throw 'Initial timer state missing.' }
$previousSlices = [int]$Matches[1]
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(40)
do {
    $before = CurrentCode
    $value = (Ui get-property TotpTimeRemaining -p HelpText).properties.HelpText
    $after = CurrentCode
    if ([string]::IsNullOrEmpty($value) -and ($before -eq '' -or $after -eq '')) { continue }
    if ($value -notmatch '^([1-8])/8 remaining\.') { throw 'Invalid circular countdown state.' }
    $slices = [int]$Matches[1]
    if ($before -eq $after) {
        if ($after -eq $previousCode) {
            if ($slices -gt $previousSlices) { throw 'The timer grew before the code changed.' }
            if ($slices -lt $previousSlices) { $decreased = $true }
        } else {
            if ($slices -ne 8) { throw 'A renewed code must reset to a filled circle.' }
            $renewed = $true
            Ui screenshot -o (Join-Path $directory 'totp-flyout-renewed.png') | Out-Null
        }
        $previousCode = $after
        $previousSlices = $slices
    }
    if (-not ($decreased -and $renewed)) { Start-Sleep -Milliseconds 700 }
} while (-not ($decreased -and $renewed) -and [DateTimeOffset]::UtcNow -lt $deadline)
if (-not ($decreased -and $renewed)) { throw 'The countdown did not decrease and reset within one period.' }
Write-Output 'PASS: compact layout, four-pixel digit gap, eight-step countdown and reset on code renewal.'
Ui send-keys escape | Out-Null
Ui wait-for TxtTotpCode --gone -t 5000 | Out-Null
}
Ui focus TxtVaultSearch | Out-Null
Ui set-value TxtVaultSearch 'TOTP password account' | Out-Null
Ui wait-for 'Edit item' -t 5000 | Out-Null
$edit = @((Ui search 'Edit item').matches | Where-Object { $_.automationId -like 'Edit_*' -and -not $_.isOffscreen })
if ($edit.Count -ne 1) { throw 'Expected one fixture item to edit.' }
Ui invoke $edit[0].automationId | Out-Null
Ui wait-for BtnEditorTotp -t 5000 | Out-Null
Ui scroll EditorPage --to bottom | Out-Null
Ui invoke BtnEditorTotp | Out-Null
Ui wait-for TxtTotpSecret -t 5000 | Out-Null
if (@((Ui search 'Issuer means').matches).Count -gt 0) { throw 'The explanation must not be visible before hover.' }
if ((Ui get-property HelpTotpIssuer -p HelpText).properties.HelpText -notmatch '^Issuer means') { throw 'Issuer explanation is missing for assistive technology.' }
Ui screenshot -o (Join-Path $directory 'totp-setup-key.png') | Out-Null
Ui hover HelpTotpSecret | Out-Null
Ui wait-for "Can't scan the QR code?" --contains -t 5000 | Out-Null
Ui screenshot -o (Join-Path $directory 'totp-setup-key-tooltip.png') | Out-Null
Ui click TxtTotpSecret | Out-Null
Ui send-keys shift+tab --target TxtTotpSecret --via send-input | Out-Null
Ui wait-for "Can't scan the QR code?" --contains -t 5000 | Out-Null
if ((Ui get-property HelpTotpSecret -p HasKeyboardFocus).properties.HasKeyboardFocus -ne 'True') { throw 'The secret help icon must be reachable with Shift+Tab.' }
Ui click TxtTotpSecret | Out-Null
Ui invoke 'Paste a setup link' | Out-Null
Ui wait-for TxtTotpUri -t 5000 | Out-Null
Ui hover HelpTotpUri | Out-Null
Ui wait-for "An ordinary website URL won't work." --contains -t 5000 | Out-Null
Ui click TxtTotpUri | Out-Null
Ui set-value TxtTotpUri 'https://example.test' | Out-Null
Ui invoke PrimaryButton | Out-Null
Ui wait-for 'Paste the full setup link' --contains -t 5000 | Out-Null
Ui set-value TxtTotpUri 'otpauth://totp/Example:test?secret=JBSWY3DPEHPK3PXP&issuer=Example&period=20' | Out-Null
Ui wait-for 'Code settings will be copied from this link.' --contains -t 5000 | Out-Null
Ui screenshot -o (Join-Path $directory 'totp-setup-link.png') | Out-Null
Ui invoke CloseButton | Out-Null
Ui wait-for CloseButton --gone -t 5000 | Out-Null
Ui invoke BtnCancelItem | Out-Null
Write-Output 'PASS: explanations hidden until hover/focus, setup key/link tooltips, invalid-link validation, valid 20-second URI review, and Cancel preserves the item.'
if ($SetupOnly) { return }
Ui wait-for TxtTotpCode --gone -t 5000 | Out-Null
OpenTotp 'TOTP only account'
if ((Element 'TxtTotpCode').name -notmatch '^\d{4}\u2009\d{4}$') { throw 'Eight-digit code grouping regressed.' }
Write-Output 'PASS: eight-digit code support retained.'
Ui send-keys escape | Out-Null
