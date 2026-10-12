[CmdletBinding(DefaultParameterSetName = 'Ui')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Ui')][int]$ProcessId,
    [Parameter(Mandatory, ParameterSetName = 'Ui')][string]$TestDirectory,
    [string]$WinAppPath = 'winapp',
    [Parameter(Mandatory, ParameterSetName = 'SelfCheck')][switch]$SelfCheck
)

# Launch the Debug app against a fresh SnapshotUiFixtureTests fixture first.
# This batch replaces only that marked synthetic vault; it never launches an app.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$results = [System.Collections.Generic.List[object]]::new()
$captures = [System.Collections.Generic.List[string]]::new()
$currentPassword = 'synthetic replacement master password'
$historicalPassword = 'synthetic original master password'
$currentError = 'The Master Password is incorrect.'
$historicalError = "The selected snapshot could not be verified or restored. If it used a different Master Password, enter that snapshot's Master Password and try again."

function Ui {
    $target = if ($args[0] -eq 'screenshot') { @('-w', $windowHandle) } else { @('-a', $ProcessId) }
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        $output = & $WinAppPath ui @args @target --json 2>&1
        $exitCode = $LASTEXITCODE
        try {
            try { $json = ($output -join [Environment]::NewLine) | ConvertFrom-Json -AsHashtable -NoEnumerate }
            catch { throw "Invalid JSON (exit $exitCode)." }
            if ($json -is [System.Collections.IDictionary] -and $json.Contains('error')) {
                $errorInfo = $json['error']
                $reason = if ($errorInfo -is [System.Collections.IDictionary]) { "$($errorInfo['code']): $($errorInfo['message'])" } else { 'WinApp returned an error.' }
                foreach ($secret in @($currentPassword, $historicalPassword, 'incorrect current fixture password', 'incorrect historical fixture password')) { $reason = $reason.Replace($secret, '[redacted]') }
                $reason = $reason -replace '\b[0-9]{6,8}\b', '[redacted number]'
                $failure = [InvalidOperationException]::new($reason.Substring(0, [Math]::Min(1000, $reason.Length)))
                if ($errorInfo -is [System.Collections.IDictionary]) { $failure.Data['WinAppCode'] = $errorInfo['code'] }
                throw $failure
            }
            if ($args[0] -eq 'list-windows') {
                if ($exitCode -ne 0 -or $json -isnot [array]) { throw 'Expected a window array from WinApp.' }
                return $json
            }
            if ($json -isnot [System.Collections.IDictionary]) { throw 'Expected a JSON object from WinApp.' }
            if ($args[0] -eq 'wait-for') {
                if (-not $json.Contains('timedOut') -or $json['timedOut'] -isnot [bool]) { throw 'WinApp returned no timeout status.' }
                if ($json['timedOut']) { throw 'UI condition timed out.' }
            }
            $noMatch = $args[0] -eq 'search' -and $json.Contains('matchCount') -and $json.Contains('matches') -and
                $json['matchCount'] -ceq 0 -and $json['matches'] -is [array] -and $json['matches'].Count -eq 0
            if ($exitCode -ne 0 -and -not ($exitCode -eq 1 -and $noMatch)) { throw "WinApp failed ($exitCode)." }
            if ($args[0] -eq 'search' -and (-not $json.Contains('matchCount') -or -not $json.Contains('matches') -or
                $json['matchCount'] -isnot [long] -and $json['matchCount'] -isnot [int] -or
                $json['matchCount'] -lt 0 -or $json['matches'] -isnot [array] -or $json['matchCount'] -ne $json['matches'].Count)) { throw 'Invalid match result from WinApp.' }
            return $json
        }
        catch {
            if ($attempt -eq 0 -and $args[0] -in 'wait-for', 'search', 'get-property', 'get-value' -and $_.Exception.Data['WinAppCode'] -ceq 'stale_element') { continue }
            $selector = if ($args.Count -gt 1) { " [$($args[1])]" } else { '' }
            throw "WinApp $($args[0])$selector`: $($_.Exception.Message)"
        }
    }
}

if ($SelfCheck) {
    function MockWinApp {
        $script:mockCalls++
        if ($script:mockStaleFirst -and $script:mockCalls -eq 1) { $global:LASTEXITCODE = 1; return '{"error":{"code":"stale_element","message":"Mock UI failure"}}' }
        $global:LASTEXITCODE = $script:mockCode
        return $script:mockJson
    }
    $WinAppPath = 'MockWinApp'
    $cases = @(
        @{ verb = 'list-windows'; json = '[{"processId":123,"hwnd":"0x1"}]'; code = 0; stale = $false; fails = $false; calls = 1 },
        @{ verb = 'search'; json = '{"matchCount":0,"matches":[]}'; code = 1; stale = $false; fails = $false; calls = 1 },
        @{ verb = 'wait-for'; json = '{"timedOut":false}'; code = 0; stale = $false; fails = $false; calls = 1 },
        @{ verb = 'wait-for'; json = '{"timedOut":true}'; code = 0; stale = $false; fails = $true; calls = 1 },
        @{ verb = 'wait-for'; json = '{"timedOut":true}'; code = 1; stale = $false; fails = $true; calls = 1 },
        @{ verb = 'wait-for'; json = '{}'; code = 0; stale = $false; fails = $true; calls = 1 },
        @{ verb = 'wait-for'; json = '{"error":{"code":"invalid_request","message":"synthetic original master password 123456"}}'; code = 0; stale = $false; fails = $true; calls = 1 },
        @{ verb = 'wait-for'; json = '{"timedOut":false}'; code = 0; stale = $true; fails = $false; calls = 2 },
        @{ verb = 'search'; json = '{"matchCount":0,"matches":[]}'; code = 0; stale = $true; fails = $false; calls = 2 },
        @{ verb = 'get-property'; json = '{"properties":{"Name":"Fixture"}}'; code = 0; stale = $true; fails = $false; calls = 2 },
        @{ verb = 'get-value'; json = '{"text":"Fixture"}'; code = 0; stale = $true; fails = $false; calls = 2 },
        @{ verb = 'invoke'; json = '{}'; code = 0; stale = $true; fails = $true; calls = 1 },
        @{ verb = 'set-value'; json = '{}'; code = 0; stale = $true; fails = $true; calls = 1 },
        @{ verb = 'wait-for'; json = '{"error":{"code":"stale_element","message":"Still stale"}}'; code = 1; stale = $true; fails = $true; calls = 2 }
    )
    foreach ($case in $cases) {
        $script:mockCalls = 0; $script:mockJson = $case.json; $script:mockCode = $case.code; $script:mockStaleFirst = $case.stale
        $failure = ''
        try { Ui $case.verb FixtureSelector | Out-Null } catch { $failure = $_.Exception.Message }
        if (($failure.Length -gt 0) -ne $case.fails -or $script:mockCalls -ne $case.calls -or $failure.Contains($historicalPassword) -or $failure.Contains('123456')) { throw "Snapshot UI self-check failed for $($case.verb)." }
    }
    Write-Output "PASS: $($cases.Count) snapshot UI JSON/timeout/window-array/retry/redaction checks; no desktop or vault touched."
    exit 0
}

$testRoot = [IO.Path]::TrimEndingDirectorySeparator((Resolve-Path -LiteralPath $TestDirectory).Path)
$null = Get-Process -Id $ProcessId
$null = Get-Command $WinAppPath -ErrorAction Stop
$fixture = Get-Content -LiteralPath (Join-Path $testRoot 'snapshot-ui-fixture.json') -Raw | ConvertFrom-Json
if ($fixture.fixture -ne 'YourSafe historical snapshot UI test v1' -or
    [IO.Path]::GetFileName($fixture.snapshotId) -ne $fixture.snapshotId) { throw 'Invalid disposable snapshot fixture.' }
$outputDirectory = Join-Path $testRoot ('ui-results/snapshot-restore-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))

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

function Reveal([string]$Selector) {
    Ui scroll-into-view $Selector | Out-Null
    Ui wait-for $Selector -p IsOffscreen --value False -t 5000 | Out-Null
}

function Capture([string]$Name) {
    $path = Join-Path $outputDirectory "snapshot-security-$Name.png"
    Ui screenshot -o $path | Out-Null
    $captures.Add($path)
}

function AssertPair([string]$ConfigHash, [string]$VaultHash) {
    if ((Get-FileHash -LiteralPath (Join-Path $testRoot '.config') -Algorithm SHA256).Hash -ne $ConfigHash -or
        (Get-FileHash -LiteralPath (Join-Path $testRoot '.storage') -Algorithm SHA256).Hash -ne $VaultHash) {
        throw 'The live vault/config pair differs from the expected fixture state.'
    }
}

function AssertUnchanged {
    AssertPair $fixture.currentConfigHash $fixture.currentVaultHash
    Ui wait-for NavBackup -p IsSelected --value True -t 5000 | Out-Null
    Ui wait-for TxtMasterPassword --gone -t 1000 | Out-Null
}

function Credentials([string]$Current, [string]$Historical) {
    Reveal TxtSnapshotMasterPassword
    Ui set-value TxtSnapshotMasterPassword $Current | Out-Null
    Reveal TxtSnapshotPreviousMasterPassword
    Ui set-value TxtSnapshotPreviousMasterPassword $Historical | Out-Null
}

function Restore([bool]$Confirm) {
    Reveal BtnRestoreSnapshot
    Ui invoke BtnRestoreSnapshot | Out-Null
    Ui wait-for PrimaryButton -p Name --value Restore -t 5000 | Out-Null
    Ui wait-for CloseButton -p Name --value Cancel -t 5000 | Out-Null
    $button = if ($Confirm) { 'PrimaryButton' } else { 'CloseButton' }
    Ui invoke $button | Out-Null
    Ui wait-for PrimaryButton --gone -t 15000 | Out-Null
}

function AssertFailure([string]$Expected) {
    Ui wait-for BtnRestoreSnapshot -p IsEnabled --value True -t 15000 | Out-Null
    Reveal StatusBackup
    Ui wait-for $Expected -t 5000 | Out-Null
    AssertUnchanged
}

function SignIn([string]$Password) {
    Ui wait-for TxtMasterPassword -t 5000 | Out-Null
    $needsOtp = (Ui get-property BtnUnlock -p Name).properties.Name -eq 'Sign in'
    Ui set-value TxtMasterPassword $Password | Out-Null
    Ui invoke BtnUnlock | Out-Null
    if ($needsOtp) {
        Ui wait-for 'Digit 1 of 6' -t 10000 | Out-Null
        # Public fixture secret JBSWY3DPEHPK3PXP, never an existing user's code.
        $counter = [BitConverter]::GetBytes([long][Math]::Floor([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() / 30))
        [Array]::Reverse($counter)
        $hmac = [Security.Cryptography.HMACSHA1]::new([Convert]::FromHexString('48656C6C6F21DEADBEEF'))
        try { $hash = $hmac.ComputeHash($counter) } finally { $hmac.Dispose() }
        $offset = $hash[19] -band 15
        $otp = ((([long]($hash[$offset] -band 127) -shl 24) -bor ([long]$hash[$offset + 1] -shl 16) -bor
            ([long]$hash[$offset + 2] -shl 8) -bor $hash[$offset + 3]) % 1000000).ToString('D6')
        for ($index = 0; $index -lt 6; $index++) {
            Ui set-value ('Digit ' + ($index + 1) + ' of 6') $otp[$index].ToString() | Out-Null
        }
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for 'Digit 1 of 6' --gone -t 15000 | Out-Null
    }
    Ui wait-for BtnLockVault -p Name --value 'Lock vault' -t 15000 | Out-Null
}

# Fail closed before navigation, mutation, or screenshots; the fixture is single-use.
Ui wait-for YourSafeDisposableUiTest -p HelpText --value $testRoot -t 5000 | Out-Null
if ((Ui get-property YourSafeDisposableUiTest -p HelpText).properties.HelpText -ne $testRoot) {
    throw 'Disposable vault marker mismatch.'
}
$mainWindows = @(Ui list-windows | Where-Object { $_.processId -eq $ProcessId -and $_.className -eq 'WinUIDesktopWin32WindowClass' })
if ($mainWindows.Count -ne 1) { throw 'The supplied process must have exactly one YourSafe main window.' }
$windowHandle = $mainWindows[0].hwnd
AssertPair $fixture.currentConfigHash $fixture.currentVaultHash
$snapshotIds = @(Get-ChildItem -LiteralPath (Join-Path $testRoot '.snapshots') -Directory |
    Sort-Object -Property Name -Descending | Select-Object -ExpandProperty Name)
if ($fixture.snapshotIndex -lt 0 -or $fixture.snapshotIndex -ge $snapshotIds.Count -or
    $snapshotIds[$fixture.snapshotIndex] -ne $fixture.snapshotId) { throw 'The snapshot fixture order changed.' }
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
Write-Output "EVIDENCE: $outputDirectory"

try {
    Test 'Current and historical password inputs use the guarded historical snapshot' {
        if ((Ui search TxtMasterPassword).matchCount -ne 0) { SignIn $currentPassword }
        Ui invoke NavBackup | Out-Null
        Reveal ListLocalSnapshots
        $snapshotItems = @((Ui search $fixture.snapshotId).matches | Where-Object { $_.type -in 'ListItem', 'DataItem' })
        if ($snapshotItems.Count -ne 1) { throw 'Expected exactly one list item for the historical snapshot ID.' }
        Ui invoke $snapshotItems[0].selector | Out-Null
        Ui wait-for $snapshotItems[0].selector -p IsSelected --value True -t 5000 | Out-Null
        Reveal TxtSnapshotMasterPassword
        Ui wait-for TxtSnapshotMasterPassword -p Name --value 'Master Password for snapshot restore' -t 5000 | Out-Null
        Reveal TxtSnapshotPreviousMasterPassword
        Ui wait-for TxtSnapshotPreviousMasterPassword -p Name --value 'Snapshot Master Password if different' -t 5000 | Out-Null
        Capture inputs
        AssertUnchanged
    }
    Test 'Cancel clears the current password and preserves the live vault/session' {
        Credentials $currentPassword $historicalPassword
        Restore $false
        AssertUnchanged
        # Refill only the historical input: a retained current password would restore.
        Reveal TxtSnapshotPreviousMasterPassword
        Ui set-value TxtSnapshotPreviousMasterPassword $historicalPassword | Out-Null
        Restore $true
        AssertFailure $currentError
        Capture cancel-current-cleared
    }
    Test 'Cancel clears the historical password and preserves the live vault/session' {
        Credentials $currentPassword $historicalPassword
        Restore $false
        AssertUnchanged
        Reveal TxtSnapshotMasterPassword
        Ui set-value TxtSnapshotMasterPassword $currentPassword | Out-Null
        Restore $true
        AssertFailure $historicalError
        Capture cancel-historical-cleared
    }
    Test 'Failed historical verification clears the current password' {
        Credentials $currentPassword 'incorrect historical fixture password'
        Restore $true
        AssertFailure $historicalError
        Reveal TxtSnapshotPreviousMasterPassword
        Ui set-value TxtSnapshotPreviousMasterPassword $historicalPassword | Out-Null
        Restore $true
        AssertFailure $currentError
        Capture failure-current-cleared
    }
    Test 'Failed current authorization clears the historical password' {
        Credentials 'incorrect current fixture password' $historicalPassword
        Restore $true
        AssertFailure $currentError
        Reveal TxtSnapshotMasterPassword
        Ui set-value TxtSnapshotMasterPassword $currentPassword | Out-Null
        Restore $true
        AssertFailure $historicalError
        Capture failure-historical-cleared
    }
    Test 'Successful restore replaces the pair and ends the sign-in session' {
        Credentials $currentPassword $historicalPassword
        Restore $true
        Ui wait-for TxtMasterPassword -t 15000 | Out-Null
        Ui wait-for NavBackup --gone -t 5000 | Out-Null
        Ui wait-for TxtSnapshotMasterPassword --gone -t 5000 | Out-Null
        AssertPair $fixture.snapshotConfigHash $fixture.snapshotVaultHash
        Capture restored-sign-in
    }
    Test 'Restored credentials work and the replaced credentials no longer work' {
        Ui set-value TxtMasterPassword $currentPassword | Out-Null
        Ui invoke BtnUnlock | Out-Null
        Ui wait-for 'The Master Password is incorrect. Please try again.' -t 15000 | Out-Null
        Ui wait-for 'Digit 1 of 6' --gone -t 1000 | Out-Null
        SignIn $historicalPassword
        Ui invoke NavVault | Out-Null
        Ui wait-for 'Historical snapshot account' -t 5000 | Out-Null
        Ui wait-for 'Live-only snapshot account' --gone -t 5000 | Out-Null
        AssertPair $fixture.snapshotConfigHash $fixture.snapshotVaultHash
        Capture restored-vault
        Ui invoke NavBackup | Out-Null
        Reveal BtnRestoreSnapshot
        Capture restored-inputs
    }
}
catch { Write-Warning $_.Exception.Message }
finally {
    @{
        processId = $ProcessId
        testDirectory = $testRoot
        testLayer = 'Actual WinUI restore, sign-in, and file pair; synthetic historical credentials'
        results = @($results)
        screenshots = @($captures)
        residualLimitations = 'PasswordBox clearing is verified through rejected reuse, not readable plaintext or physical memory zeroization. Review screenshots. Windows lock/suspend and filesystem fault injection are separate tests.'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputDirectory 'snapshot-restore-security-ui.json') -Encoding utf8
}
if ($results.Count -ne 7 -or @($results | Where-Object { $_.status -eq 'FAIL' }).Count -gt 0) { exit 1 }
exit 0
