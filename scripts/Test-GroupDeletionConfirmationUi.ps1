[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][string]$TestDirectory,
    [string]$WinAppPath = 'winapp'
)

# Use the unlocked Debug app seeded by Create_opt_in_disposable_ui_vault.
# Only the unique empty group created by this batch can be deleted.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$testRoot = [IO.Path]::TrimEndingDirectorySeparator((Resolve-Path -LiteralPath $TestDirectory).Path)
$testProcess = Get-Process -Id $ProcessId
$windowHandle = $testProcess.MainWindowHandle.ToInt64()
if ($windowHandle -eq 0) { throw 'The supplied process has no main window.' }
$null = Get-Command $WinAppPath -ErrorAction Stop
$outputDirectory = Join-Path $testRoot 'ui-results'
$groupName = "UI delete check $([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$confirmationText = "Confirm delete all data in `"$groupName`""
$results = [System.Collections.Generic.List[object]]::new()
$captures = [System.Collections.Generic.List[string]]::new()
$groupId = $null

function Ui {
    $target = if ($args[0] -eq 'screenshot') { @('-w', $windowHandle) } else { @('-a', $ProcessId) }
    $output = & $WinAppPath ui @args @target --json 2>&1
    $exitCode = $LASTEXITCODE
    try { $json = ($output -join [Environment]::NewLine) | ConvertFrom-Json -AsHashtable -NoEnumerate }
    catch { throw "WinApp $($args[0]) returned invalid JSON (exit $exitCode)." }
    if ($json -isnot [System.Collections.IDictionary]) { throw "WinApp $($args[0]) returned no JSON object." }
    if ($json.Contains('error')) { throw "WinApp $($args[0]) failed: $($json.error | ConvertTo-Json -Compress)" }
    if ($args[0] -eq 'wait-for' -and (-not $json.Contains('timedOut') -or $json.timedOut -isnot [bool] -or $json.timedOut)) {
        throw "UI condition failed: $($args[0]) $($args[1])"
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
    $path = Join-Path $outputDirectory "group-deletion-$Name.png"
    Ui screenshot -o $path | Out-Null
    $captures.Add($path)
}

function SingleElement([string]$Name, [string]$TypePattern) {
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $matches = @((Ui search $Name --max 100).matches | Where-Object { -not $_.isOffscreen -and $_.type -match $TypePattern -and $_.name -eq $Name })
        if ($matches.Count -eq 1) { return $matches[0] }
        if ($matches.Count -gt 1) { throw "Expected one visible $TypePattern named '$Name'." }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Visible $TypePattern named '$Name' was not found."
}

function GroupIds {
    return @((Ui search Group_ --max 100).matches | Where-Object { $_.automationId -like 'Group_*' } |
        Select-Object -ExpandProperty automationId -Unique | Sort-Object)
}

function OpenDeletion {
    Ui scroll-into-view $groupId | Out-Null
    Ui click $groupId --right | Out-Null
    $delete = SingleElement 'Delete group' '^MenuItem$'
    Ui invoke $delete.selector | Out-Null
    Ui wait-for PrimaryButton -p Name --value 'Delete all data' -t 5000 | Out-Null
    Ui wait-for PrimaryButton -p IsEnabled --value False -t 5000 | Out-Null
    SingleElement $confirmationText '^Text$' | Out-Null
    return (SingleElement 'Type the exact confirmation below' '^(Edit|TextBox)$').selector
}

# Fail closed before creating groups, changing selection, or taking screenshots.
Ui wait-for YourSafeDisposableUiTest -p HelpText --value $testRoot -t 5000 | Out-Null
if ((Ui get-property YourSafeDisposableUiTest -p HelpText).properties.HelpText -ne $testRoot) { throw 'Disposable vault marker mismatch.' }
Ui invoke NavVault | Out-Null
Ui wait-for BtnLockVault -p Name --value 'Lock vault' -t 5000 | Out-Null
Ui wait-for Work -t 5000 | Out-Null
$work = SingleElement Work '^Button$'
if ($work.automationId -notlike 'Group_*') { throw 'The Work fixture group was not found.' }
$originalGroups = @(GroupIds)
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

try {
    Test 'Create a unique empty group without changing the existing fixture groups' {
        Ui invoke BtnAddGroup | Out-Null
        Ui wait-for PrimaryButton -p Name --value Save -t 5000 | Out-Null
        $input = SingleElement 'Group name' '^(Edit|TextBox)$'
        Ui set-value $input.selector $groupName | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for PrimaryButton --gone -t 5000 | Out-Null
        $script:groupId = (SingleElement $groupName '^Button$').automationId
        if ($groupId -notlike 'Group_*' -or $groupId -in $originalGroups) { throw 'The new group must have a distinct group ID.' }
        if (@(Compare-Object $originalGroups @(GroupIds | Where-Object { $_ -ne $groupId })).Count -ne 0) { throw 'Creating the group changed an existing group.' }
    }
    Test 'Exact displayed confirmation enables Delete; Cancel preserves the group and persisted pair' {
        $configHash = (Get-FileHash -LiteralPath (Join-Path $testRoot '.config')).Hash
        $vaultHash = (Get-FileHash -LiteralPath (Join-Path $testRoot '.storage')).Hash
        $input = OpenDeletion
        Ui set-value $input 'Delete group' | Out-Null
        Ui wait-for PrimaryButton -p IsEnabled --value False -t 5000 | Out-Null
        Ui set-value $input $confirmationText | Out-Null
        Ui wait-for PrimaryButton -p IsEnabled --value True -t 5000 | Out-Null
        Capture confirmation
        Ui invoke CloseButton | Out-Null
        Ui wait-for PrimaryButton --gone -t 5000 | Out-Null
        Ui wait-for $groupId -t 5000 | Out-Null
        if ((Get-FileHash -LiteralPath (Join-Path $testRoot '.config')).Hash -ne $configHash -or
            (Get-FileHash -LiteralPath (Join-Path $testRoot '.storage')).Hash -ne $vaultHash) { throw 'Cancelling changed the persisted pair.' }
    }
    Test 'Confirmed deletion removes only the new empty group and keeps Work credentials' {
        $input = OpenDeletion
        Ui set-value $input $confirmationText | Out-Null
        Ui wait-for PrimaryButton -p IsEnabled --value True -t 5000 | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for PrimaryButton --gone -t 5000 | Out-Null
        Ui wait-for $groupId --gone -t 15000 | Out-Null
        if (@(Compare-Object $originalGroups @(GroupIds)).Count -ne 0) { throw 'Deleting the group changed an existing group.' }
        Ui invoke Group_All | Out-Null
        Ui set-value TxtVaultSearch 'Test account 00 with a long title' | Out-Null
        Ui wait-for 'Test account 00 with a long title' -t 5000 | Out-Null
        Ui wait-for $work.automationId -t 5000 | Out-Null
        Capture preserved-fixture
        Ui invoke BtnClearVaultFilters | Out-Null
    }
}
catch { Write-Warning $_.Exception.Message }
finally {
    @{
        processId = $ProcessId
        testDirectory = $testRoot
        temporaryGroupName = $groupName
        temporaryGroupId = $groupId
        results = @($results)
        screenshots = @($captures)
        residualLimitations = 'Uses one empty synthetic group. Core tests cover deletion of active/Trash group data. A failed batch can leave the marked temporary group for inspection.'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputDirectory 'group-deletion-confirmation-ui.json') -Encoding utf8
}
if ($results.Count -ne 3 -or @($results | Where-Object { $_.status -eq 'FAIL' }).Count -gt 0) { exit 1 }
exit 0
