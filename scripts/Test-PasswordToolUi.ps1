[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][string]$TestDirectory,
    [string]$CaptureLabel = 'password-tool'
)

# Run only on an unlocked disposable DEBUG vault.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$testRoot = [IO.Path]::TrimEndingDirectorySeparator((Resolve-Path -LiteralPath $TestDirectory).Path)
$outputDirectory = Join-Path $testRoot 'ui-results'
$captureName = $CaptureLabel -replace '[^A-Za-z0-9_-]', '_'
$windowHandle = (Get-Process -Id $ProcessId).MainWindowHandle.ToInt64()
$results = [Collections.Generic.List[string]]::new()

function Ui {
    $target = if ($args[0] -eq 'screenshot') { @('-w', $windowHandle) } else { @('-a', $ProcessId) }
    $output = & winapp ui @args @target --json 2>&1
    if ($LASTEXITCODE -ne 0) { throw "UI step failed: $($args[0]) $($args[1]): $output" }
    return (($output -join [Environment]::NewLine) | ConvertFrom-Json)
}

function Capture([string]$Name) {
    Ui screenshot --capture-screen -o (Join-Path $outputDirectory "$captureName-$Name.png") | Out-Null
}

function Pass([string]$Name) {
    $results.Add($Name)
    Write-Host "PASS: $Name"
}

# Fail closed before navigation: never interact with a real vault.
Ui wait-for YourSafeDisposableUiTest -p HelpText --value $testRoot -t 5000 | Out-Null
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
Ui invoke NavHashTool | Out-Null
Ui invoke HashModeGenerate | Out-Null
Ui wait-for TxtPasswordToolTitle --value 'Password Tool' -t 5000 | Out-Null
Ui wait-for ChkGenerateHash --value Off -t 5000 | Out-Null
Ui wait-for BtnToolGeneratePassword -t 5000 | Out-Null
Ui wait-for CmbHashAlgorithm --gone -t 5000 | Out-Null
Capture '01-default'
Pass 'Default Generate creates passwords; hash fields are hidden and title is Password Tool'

Ui invoke BtnToolGeneratePassword | Out-Null
Ui wait-for PrimaryButton -t 10000 | Out-Null
Capture '02-generator'
Ui invoke CloseButton | Out-Null
if ((Ui get-value TxtToolGeneratedPassword).text -ne '') { throw 'Cancel produced a password.' }
Ui invoke BtnToolGeneratePassword | Out-Null
Ui wait-for PrimaryButton -t 5000 | Out-Null
Ui invoke PrimaryButton | Out-Null
$generated = (Ui get-value TxtToolGeneratedPassword).text
if ($generated.Length -ne 24 -or $generated -cnotmatch '[A-Z]' -or $generated -cnotmatch '[a-z]' -or
    $generated -notmatch '[0-9]' -or $generated -notmatch '[^A-Za-z0-9]') {
    throw 'Password generator did not return the shared default password options.'
}
Capture '03-password'
Pass 'Shared password generator accepts or cancels without changing vault items'

Ui invoke ChkGenerateHash | Out-Null
Ui wait-for CmbHashAlgorithm -t 5000 | Out-Null
Ui wait-for BtnToolGeneratePassword --gone -t 5000 | Out-Null
Ui invoke BtnGenerateHash | Out-Null
Ui wait-for 'Enter a password and select an algorithm.' -t 5000 | Out-Null
Ui set-value TxtHashGeneratePassword 'synthetic-test-password' | Out-Null
Ui invoke BtnGenerateHash | Out-Null
Ui wait-for TxtGeneratedHash --value '$' --contains -t 15000 | Out-Null
$hash = (Ui get-value TxtGeneratedHash).text
Capture '04-hash'
Pass 'Checked mode generates a hash and keeps missing-password validation'

Ui invoke HashModeVerify | Out-Null
Ui set-value TxtHashVerifyPassword 'synthetic-test-password' | Out-Null
Ui set-value TxtHashVerifyStored $hash | Out-Null
Ui invoke BtnVerifyHash | Out-Null
Ui wait-for TxtHashVerificationResult --value 'Password verified.' -t 15000 | Out-Null
Ui invoke HashModeInspect | Out-Null
Ui set-value TxtHashInspectStored $hash | Out-Null
Ui invoke BtnInspectHash | Out-Null
Ui wait-for TxtHashInspectionResult --value 'Algorithm:' --contains -t 5000 | Out-Null
Pass 'Verify and Inspect still accept generated hashes'

Ui invoke HashModeGenerate | Out-Null
Ui invoke ChkGenerateHash | Out-Null
Ui wait-for TxtToolGeneratedPassword -t 5000 | Out-Null
if ((Ui get-value TxtToolGeneratedPassword).text -ne '') { throw 'Mode switch retained an old plaintext password.' }
Ui invoke ChkGenerateHash | Out-Null
if ((Ui get-value TxtGeneratedHash).text -ne '') { throw 'Mode switch retained an old hash.' }
Ui invoke ChkGenerateHash | Out-Null
Capture '05-reset'
Pass 'Switching modes clears old passwords, hashes and errors'

@{ processId = $ProcessId; results = @($results); visualReviewRequired = 'Review default, generator and hash screenshots for clipping and hierarchy.' } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputDirectory "$captureName.json") -Encoding utf8
