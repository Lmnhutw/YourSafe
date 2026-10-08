[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [string]$CaptureLabel = 'current'
)

# Run only against the unlocked disposable vault seeded by Create_opt_in_disposable_ui_vault.
# Repeat at the required window sizes/themes/DPI; native screenshots need separate visual review.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$testProcess = Get-Process -Id $ProcessId
[IntPtr]$windowHandle = $testProcess.MainWindowHandle
if ($windowHandle -eq [IntPtr]::Zero) { throw 'The supplied process has no main window.' }
$outputDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'test-results'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$captureName = $CaptureLabel -replace '[^A-Za-z0-9_-]', '_'
$results = [System.Collections.Generic.List[object]]::new()
$originalTitle = 'Test account 00 with a long title'
$copyTitle = "UI check $([Guid]::NewGuid().ToString('N')) (copy)"

function Ui {
    $output = & winapp ui @args -w $windowHandle --json 2>&1
    $exitCode = $LASTEXITCODE
    $json = ($output -join [Environment]::NewLine) | ConvertFrom-Json
    if ($exitCode -ne 0 -and -not ($exitCode -eq 1 -and $args[0] -eq 'search' -and $json.matchCount -eq 0)) {
        throw "WinApp $($args[0]) failed: $output"
    }
    return $json
}

function Matches([string]$Selector) {
    return @((Ui search $Selector --max 100).matches | Where-Object { -not $_.isOffscreen })
}

function EnsureForeground {
    $output = & winapp ui list-windows --json 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Window inventory failed: $output" }
    $windows = ($output -join [Environment]::NewLine) | ConvertFrom-Json
    $target = $windows | Where-Object { $_.hwnd -eq $windowHandle.ToInt64() } | Select-Object -First 1
    if ($null -eq $target) { throw 'The test window was not found.' }
    if ($target.isForeground) { return }
    $taskbar = $windows | Where-Object { $_.className -eq 'Shell_TrayWnd' } | Select-Object -First 1
    if ($null -eq $taskbar) { throw 'The taskbar was not found.' }
    $output = & winapp ui invoke $testProcess.Path -w $taskbar.hwnd --json 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Foreground activation failed: $output" }
}

function WaitText([string]$Text) {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        if (@(Matches $Text).Count -gt 0) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Visible text '$Text' was not found."
}

function Button([string]$Name) {
    EnsureForeground
    if ($Name -in @('Close', 'Cancel')) {
        Ui invoke CloseButton | Out-Null
        Ui wait-for CloseButton --gone -t 5000 | Out-Null
        return
    }
    if ($Name -in @('Move', 'Move to Trash')) { Ui invoke PrimaryButton | Out-Null; return }
    Ui wait-for $Name -t 5000 | Out-Null
    $buttons = @(Matches $Name | Where-Object { $_.type -eq 'Button' -and $_.name -eq $Name })
    $button = $buttons | Select-Object -Last 1
    if ($null -eq $button) { throw "Visible button '$Name' was not found." }
    Ui invoke $button.selector | Out-Null
}

function Menu([string]$MoreId, [string]$Action) {
    EnsureForeground
    WaitText $MoreId
    Start-Sleep -Milliseconds 200
    Ui invoke $MoreId | Out-Null
    Ui wait-for $Action -t 5000 | Out-Null
    $item = @(Matches $Action | Where-Object { $_.type -eq 'MenuItem' -and $_.name -eq $Action }) | Select-Object -First 1
    if ($null -eq $item) { throw "Menu item '$Action' was not found." }
    Ui invoke $item.name | Out-Null
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

function MoveTestItem([string]$ExpectedGroup, [string]$NextGroup) {
    Menu $script:originalMore 'Move to group'
    WaitText 'Move to group'
    $picker = @(Matches 'Group' | Where-Object { $_.type -eq 'ComboBox' }) | Select-Object -Last 1
    if ($null -eq $picker) { throw 'Move dialog group picker was not found.' }
    Ui wait-for $picker.selector --value $ExpectedGroup -t 5000 | Out-Null
    Ui invoke $picker.selector | Out-Null
    $option = @(Matches $NextGroup | Where-Object { $_.name -eq $NextGroup -and $_.type -eq 'ListItem' }) | Select-Object -Last 1
    if ($null -eq $option) { throw "Group option '$NextGroup' was not found." }
    Ui invoke $option.selector | Out-Null
    Button 'Move'
    Ui wait-for $script:originalMore -t 5000 | Out-Null
}

function SelectTrashItem {
    EnsureForeground
    $item = @(Matches $copyTitle | Where-Object { $_.type -in @('ListItem', 'DataItem') }) | Select-Object -First 1
    if ($null -eq $item) { throw 'The Trash item was not found.' }
    Ui invoke $item.selector | Out-Null
    Start-Sleep -Milliseconds 200
}

try {
    Test 'Default Clear is disabled; search and Clear restore defaults' {
        EnsureForeground
        Ui invoke 'NavVault' | Out-Null
        Ui wait-for 'BtnClearVaultFilters' -p IsEnabled --value False -t 5000 | Out-Null
        Ui set-value 'TxtVaultSearch' $originalTitle | Out-Null
        Ui wait-for 'BtnClearVaultFilters' -p IsEnabled --value True -t 5000 | Out-Null
        $script:originalMore = @(Matches 'More item actions' | Where-Object { $_.automationId -like 'More_*' })[0].automationId
        Ui invoke 'BtnClearVaultFilters' | Out-Null
        Ui wait-for 'BtnClearVaultFilters' -p IsEnabled --value False -t 5000 | Out-Null
        Ui wait-for 'Item type filter' --value 'All items' -t 5000 | Out-Null
        Ui wait-for 'Sort order' --value 'Title A–Z' -t 5000 | Out-Null
        Ui wait-for 'ChkFilterByGroup' --value Off -t 5000 | Out-Null
        $titleHeader = @(Matches 'Title' | Where-Object { $_.type -eq 'Text' -and $_.name -eq 'Title' })[0]
        $titleRow = @(Matches $originalTitle | Where-Object { $_.type -eq 'Text' -and $_.name -eq $originalTitle })[0]
        if ([Math]::Abs($titleHeader.x - $titleRow.x) -gt 1 -or [Math]::Abs($titleHeader.width - $titleRow.width) -gt 1) {
            throw 'Title header and row are not aligned.'
        }
        Ui screenshot -o (Join-Path $outputDirectory "vault-$captureName.png") | Out-Null
        Ui set-value 'TxtVaultSearch' $originalTitle | Out-Null
    }
    Test 'View details is read-only and reveals no password automatically' {
        Menu $script:originalMore 'View details'
        Ui wait-for 'Reveal password' -t 5000 | Out-Null
        if (@(Matches 'TxtEditorTitle').Count -ne 0 -or @(Matches 'synthetic-test-password').Count -ne 0) {
            throw 'View details opened an editor or exposed a password without reveal.'
        }
        Ui screenshot -o (Join-Path $outputDirectory "details-$captureName.png") | Out-Null
        Button 'Close'
    }
    Test 'Password History is available in the menu' {
        Menu $script:originalMore 'History'
        WaitText 'No previous'
        Button 'Close'
    }
    Test 'Duplicate cancellation creates no item' {
        Menu $script:originalMore 'Duplicate'
        Ui wait-for 'TxtEditorTitle' --value "$originalTitle (copy)" -t 5000 | Out-Null
        Ui set-value 'TxtEditorTitle' $copyTitle | Out-Null
        Ui invoke 'BtnCancelItem' | Out-Null
        Ui set-value 'TxtVaultSearch' $copyTitle | Out-Null
        if (@(Matches 'More item actions').Count -ne 0) { throw 'Cancelled duplicate was saved.' }
        Ui set-value 'TxtVaultSearch' $originalTitle | Out-Null
    }
    Test 'Duplicate Save creates a distinct item ID' {
        Menu $script:originalMore 'Duplicate'
        Ui wait-for 'TxtEditorTitle' --value "$originalTitle (copy)" -t 5000 | Out-Null
        Ui set-value 'TxtEditorTitle' $copyTitle | Out-Null
        Ui invoke 'BtnSaveItem' | Out-Null
        Ui wait-for 'TxtVaultSearch' -t 5000 | Out-Null
        Ui set-value 'TxtVaultSearch' $copyTitle | Out-Null
        Ui wait-for $copyTitle -t 5000 | Out-Null
        $script:copyMore = @(Matches 'More item actions' | Where-Object { $_.automationId -like 'More_*' })[0].automationId
        if ($script:copyMore -eq $script:originalMore) { throw 'Duplicate reused the original item ID.' }
        Ui set-value 'TxtVaultSearch' $originalTitle | Out-Null
    }
    Test 'Move to Ungrouped and back to Work persists the selected group' {
        MoveTestItem 'Work' 'Ungrouped'
        MoveTestItem 'Ungrouped' 'Work'
        Menu $script:originalMore 'Move to group'
        $picker = @(Matches 'Group' | Where-Object { $_.type -eq 'ComboBox' }) | Select-Object -Last 1
        Ui wait-for $picker.selector --value Work -t 5000 | Out-Null
        Button 'Cancel'
    }
    Test 'Delete moves to Trash and Restore returns the same item' {
        Ui set-value 'TxtVaultSearch' $copyTitle | Out-Null
        Menu $script:copyMore 'Delete'
        Button 'Move to Trash'
        Ui wait-for $script:copyMore --gone -t 5000 | Out-Null
        Ui invoke 'NavTrash' | Out-Null
        Ui wait-for $copyTitle -t 5000 | Out-Null
        SelectTrashItem
        Button 'Restore selected item'
        Ui wait-for $copyTitle --gone -t 5000 | Out-Null
        Ui invoke 'NavVault' | Out-Null
        Ui wait-for $script:copyMore -t 5000 | Out-Null
    }
    Test 'Permanent delete requires confirmation and cancellation preserves Trash' {
        Menu $script:copyMore 'Delete'
        Button 'Move to Trash'
        Ui invoke 'NavTrash' | Out-Null
        Ui wait-for $copyTitle -t 5000 | Out-Null
        SelectTrashItem
        Button 'Delete permanently'
        WaitText 'This cannot be undone'
        Button 'Cancel'
        Ui wait-for $copyTitle -t 5000 | Out-Null
        Button 'Delete permanently'
        WaitText 'This cannot be undone'
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for $copyTitle --gone -t 5000 | Out-Null
        Ui invoke 'NavVault' | Out-Null
        Ui invoke 'BtnClearVaultFilters' | Out-Null
    }
    Test 'Manual lock preserves Login and re-unlock needs no OTP' {
        Ui invoke 'BtnLockVault' | Out-Null
        Ui wait-for 'Unlock Vault' -t 5000 | Out-Null
        Ui set-value 'TxtMasterPassword' 'correct horse battery staple' | Out-Null
        Ui invoke 'BtnUnlock' | Out-Null
        Ui wait-for 'BtnLockVault' -t 15000 | Out-Null
        Ui wait-for 'BtnClearVaultFilters' -p IsEnabled --value False -t 5000 | Out-Null
    }
}
catch { Write-Warning $_.Exception.Message }
finally {
    $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputDirectory "vault-ui-$captureName.json") -Encoding utf8
}
if (@($results | Where-Object { $_.status -eq 'FAIL' }).Count -gt 0) { exit 1 }
