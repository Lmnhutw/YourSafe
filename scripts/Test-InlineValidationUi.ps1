[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][string]$TestDirectory,
    [string]$WinAppPath = 'winapp'
)

# Use only the unlocked Debug vault seeded by Create_opt_in_disposable_ui_vault.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$testRoot = [IO.Path]::TrimEndingDirectorySeparator((Resolve-Path -LiteralPath $TestDirectory).Path)
$testProcess = Get-Process -Id $ProcessId
$windowHandle = $testProcess.MainWindowHandle.ToInt64()
if ($windowHandle -eq 0) { throw 'The supplied process has no main window.' }
$null = Get-Command $WinAppPath -ErrorAction Stop
$outputDirectory = Join-Path $testRoot 'ui-results'
$groupName = "UI inline check $([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$results = [System.Collections.Generic.List[object]]::new()
$captures = [System.Collections.Generic.List[string]]::new()

function Ui {
    $target = if ($args[0] -eq 'screenshot') { @('-w', $windowHandle) } else { @('-a', $ProcessId) }
    $output = & $WinAppPath ui @args @target --json 2>&1
    $exitCode = $LASTEXITCODE
    try { $json = ($output -join [Environment]::NewLine) | ConvertFrom-Json -AsHashtable -NoEnumerate }
    catch { throw "WinApp $($args[0]) returned invalid JSON (exit $exitCode)." }
    if ($json -isnot [System.Collections.IDictionary]) { throw "WinApp $($args[0]) returned no JSON object." }
    if ($json.Contains('error')) { throw "WinApp $($args[0]) failed: $($json.error | ConvertTo-Json -Compress)" }
    if ($args[0] -eq 'wait-for' -and (-not $json.Contains('timedOut') -or $json.timedOut -isnot [bool] -or $json.timedOut)) {
        throw "UI condition failed: $($args -join ' ')"
    }
    if ($args[0] -eq 'search' -and (-not $json.Contains('matchCount') -or -not $json.Contains('matches') -or
        $json.matchCount -isnot [long] -and $json.matchCount -isnot [int] -or $json.matches -isnot [array] -or
        $json.matchCount -ne $json.matches.Count)) { throw 'WinApp returned invalid search results.' }
    $noMatch = $args[0] -eq 'search' -and $json.matchCount -eq 0
    if ($exitCode -ne 0 -and -not ($exitCode -eq 1 -and $noMatch)) { throw "WinApp $($args[0]) failed ($exitCode)." }
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
    $path = Join-Path $outputDirectory "inline-validation-$Name.png"
    Ui screenshot -o $path | Out-Null
    $captures.Add($path)
}

function PairHash {
    return "{0}/{1}" -f (Get-FileHash -LiteralPath (Join-Path $testRoot '.config')).Hash,
        (Get-FileHash -LiteralPath (Join-Path $testRoot '.storage')).Hash
}

# Fail closed before changing selection, entering input or capturing the marked vault.
Ui wait-for YourSafeDisposableUiTest -p HelpText --value $testRoot -t 5000 | Out-Null
if ((Ui get-property YourSafeDisposableUiTest -p HelpText).properties.HelpText -ne $testRoot) { throw 'Disposable vault marker mismatch.' }
Ui invoke NavVault | Out-Null
Ui wait-for BtnLockVault -p Name --value 'Lock vault' -t 5000 | Out-Null
Ui wait-for Work -t 5000 | Out-Null
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

try {
    Test 'Blank and duplicate names keep the same group dialog open without writing' {
        $before = PairHash
        Ui invoke BtnAddGroup | Out-Null
        Ui wait-for TxtNewGroupName -t 5000 | Out-Null
        Ui set-value TxtNewGroupName '   ' | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for TxtGroupNameError --value 'Enter a group name.' -t 5000 | Out-Null
        Ui wait-for TxtNewGroupName -p HasKeyboardFocus --value True -t 5000 | Out-Null
        Ui wait-for TxtNewGroupName --value '   ' -t 5000 | Out-Null
        if ((PairHash) -cne $before) { throw 'An invalid name changed persistent data.' }
        Capture blank-name
        Ui set-value TxtNewGroupName 'Work' | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for TxtGroupNameError --value 'A group with this name already exists.' -t 5000 | Out-Null
        Ui wait-for TxtNewGroupName --value Work -t 5000 | Out-Null
        if ((PairHash) -cne $before) { throw 'A duplicate name changed persistent data.' }
        Capture duplicate-name
        Ui set-value TxtNewGroupName $groupName | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for TxtNewGroupName --gone -t 5000 | Out-Null
        Ui wait-for $groupName -t 15000 | Out-Null
    }
    Test 'Wrong Master Password stays open and a corrected value unlocks in the same dialog' {
        Ui invoke BtnLockVault | Out-Null
        Ui wait-for BtnLockVault -p Name --value 'Unlock vault' -t 5000 | Out-Null
        $before = PairHash
        Ui invoke BtnLockVault | Out-Null
        Ui wait-for TxtUnlockMasterPassword -t 5000 | Out-Null
        Ui set-value TxtUnlockMasterPassword 'incorrect synthetic password' | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for TxtUnlockMasterPasswordError --value 'The Master Password is incorrect. Please try again.' -t 15000 | Out-Null
        Ui wait-for TxtUnlockMasterPassword -p HasKeyboardFocus --value True -t 5000 | Out-Null
        if ((PairHash) -cne $before) { throw 'An invalid password changed persistent data.' }
        Capture password-error
        # Review the captured selection visually; UIA avoids desktop-dependent SendInput delivery.
        Ui set-value TxtUnlockMasterPassword 'correct horse battery staple' | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for TxtUnlockMasterPassword --gone -t 15000 | Out-Null
        Ui wait-for BtnLockVault -p Name --value 'Lock vault' -t 15000 | Out-Null
    }
    Test 'Cancel after a rejected password closes the dialog and preserves the locked vault' {
        Ui invoke BtnLockVault | Out-Null
        Ui wait-for BtnLockVault -p Name --value 'Unlock vault' -t 5000 | Out-Null
        $before = PairHash
        Ui invoke BtnLockVault | Out-Null
        Ui wait-for TxtUnlockMasterPassword -t 5000 | Out-Null
        Ui set-value TxtUnlockMasterPassword 'incorrect synthetic password' | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for TxtUnlockMasterPasswordError -t 15000 | Out-Null
        Ui invoke CloseButton | Out-Null
        Ui wait-for TxtUnlockMasterPassword --gone -t 5000 | Out-Null
        Ui wait-for BtnLockVault -p Name --value 'Unlock vault' -t 5000 | Out-Null
        if ((PairHash) -cne $before) { throw 'Cancel changed persistent data.' }
        Capture cancelled
    }
}
catch { Write-Warning $_.Exception.Message }
finally {
    @{
        processId = $ProcessId
        testDirectory = $testRoot
        temporaryGroupName = $groupName
        results = @($results)
        screenshots = @($captures)
        residualLimitations = 'Leaves the batch-created empty synthetic group for inspection. Screenshots require visual review; Windows lifecycle and recovery wizard cases are covered separately.'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputDirectory 'inline-validation-ui.json') -Encoding utf8
}
if ($results.Count -ne 3 -or @($results | Where-Object { $_.status -eq 'FAIL' }).Count -gt 0) { exit 1 }
exit 0
