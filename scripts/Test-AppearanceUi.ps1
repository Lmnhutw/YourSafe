[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][string]$TestDirectory,
    [string]$WinAppPath = 'winapp',
    [string]$CaptureLabel = 'current'
)

# Run against an already unlocked DEBUG app using Create_opt_in_disposable_ui_vault.
# The DEBUG title-bar marker must match TestDirectory before any UI interaction.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$testRoot = [System.IO.Path]::TrimEndingDirectorySeparator((Resolve-Path -LiteralPath $TestDirectory).Path)
$testProcess = Get-Process -Id $ProcessId
[IntPtr]$windowHandle = $testProcess.MainWindowHandle
if ($windowHandle -eq [IntPtr]::Zero) { throw 'The supplied process has no main window.' }
$null = Get-Command $WinAppPath -ErrorAction Stop
$results = [System.Collections.Generic.List[object]]::new()
$captures = [System.Collections.Generic.List[string]]::new()
$captureName = $CaptureLabel -replace '[^A-Za-z0-9_-]', '_'
$outputDirectory = Join-Path $testRoot 'ui-results'
$preferencesPath = Join-Path $testRoot 'appearance.json'
$draftTitle = "Appearance UI draft $([Guid]::NewGuid().ToString('N'))"
$savedTitle = "Appearance UI saved $([Guid]::NewGuid().ToString('N'))"

function Ui {
    # ComboBox/context-menu popups have their own HWND; keep them in the marked PID scope.
    $target = if ($args[0] -eq 'screenshot') { @('-w', $windowHandle.ToInt64()) } else { @('-a', $ProcessId) }
    $output = & $WinAppPath ui @args @target --json 2>&1
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

function EnsureForeground([string]$Id = 'TogglePaneButton') {
    Ui focus $Id | Out-Null
}

function VisibleItem([string]$Name, [string]$Type) {
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $item = @(Matches $Name | Where-Object { $_.name -eq $Name -and $_.type -eq $Type }) | Select-Object -Last 1
        if ($null -ne $item) { return $item }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Visible $Type '$Name' was not found."
}

function SelectCombo([string]$Id, [string]$Value) {
    EnsureForeground $Id
    if ((Ui get-value $Id).text -eq $Value) { return }
    Ui invoke $Id | Out-Null
    $option = VisibleItem $Value ListItem
    Ui invoke $option.selector | Out-Null
    Ui wait-for $Id --value $Value -t 5000 | Out-Null
}

function Reveal([string]$Id) {
    Ui scroll-into-view $Id | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        if (@(Matches $Id | Where-Object { $_.width -gt 0 -and $_.height -gt 0 }).Count -gt 0) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Element '$Id' has no visible, nonzero bounds after scrolling."
}

function Capture([string]$Name, [switch]$Popup) {
    $path = Join-Path $outputDirectory "$captureName-$Name.png"
    [string[]]$options = @(if ($Popup) { '--capture-screen' })
    Ui screenshot -o $path @options | Out-Null
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

function AssertPreferences([string]$Theme, [bool]$Vertical) {
    $preferences = Get-Content -LiteralPath $preferencesPath -Raw | ConvertFrom-Json
    if ($preferences.Theme -ne $Theme -or $preferences.IsVerticalTabs -ne $Vertical) {
        throw 'Persisted appearance preferences do not match the applied controls.'
    }
}

function AssertTabPreference([string]$Property, [AllowNull()][object]$Expected) {
    $preferences = Get-Content -LiteralPath $preferencesPath -Raw | ConvertFrom-Json
    if ($preferences.$Property -ne $Expected) { throw "Persisted $Property does not match the applied tab setting." }
}

function OpenTabMenu([string]$Id, [string]$Action) {
    EnsureForeground
    Ui invoke NavVault | Out-Null
    Ui wait-for $Id -t 5000 | Out-Null
    Ui click $Id --right | Out-Null
    $item = VisibleItem $Action MenuItem
    Ui invoke $item.selector | Out-Null
}

function OpenTabColor([string]$Id) {
    OpenTabMenu $Id 'Change tab color'
    Ui wait-for GroupColorPicker -t 5000 | Out-Null
}

function RenameTab([string]$Id, [string]$Name) {
    OpenTabMenu $Id 'Rename tab'
    Ui wait-for TxtTabName -t 5000 | Out-Null
    Ui set-value TxtTabName $Name | Out-Null
    Ui invoke PrimaryButton | Out-Null
    Ui wait-for TxtTabName --gone -t 5000 | Out-Null
}

# Fail closed: a real vault/app without the matching DEBUG marker is never driven.
Ui wait-for YourSafeDisposableUiTest -p HelpText --value $testRoot -t 5000 | Out-Null
Ui invoke NavVault | Out-Null
Ui wait-for BtnLockVault -t 5000 | Out-Null
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

try {
    Test 'Background changes apply and persist while buttons remain fixed blue' {
        Ui invoke NavSettings | Out-Null
        Ui wait-for CmbAppTheme -t 5000 | Out-Null
        SelectCombo CmbAppTheme Light
        SelectCombo CmbGroupTabPlacement Horizontal
        Ui wait-for TxtAppearanceStatus -t 5000 | Out-Null
        AssertPreferences Light $false
        Capture '01-light-blue'
        SelectCombo CmbAppTheme Dark
        AssertPreferences Dark $false
        Capture '02-dark-blue'
    }
    Test 'All tab color validates, persists exact RGB, and Cancel preserves it' {
        OpenTabColor Group_All
        Ui set-value TxtGroupColorHex '#xyzxyz' | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for GroupColorError -t 5000 | Out-Null
        Ui wait-for GroupColorPicker -t 5000 | Out-Null
        Reveal GroupColorError
        Capture '03-tab-invalid' -Popup
        Ui set-value TxtGroupColorHex '#808080' | Out-Null
        Ui wait-for PrimaryButton -p IsEnabled --value True -t 5000 | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for GroupColorPicker --gone -t 5000 | Out-Null
        AssertTabPreference AllTabColor '#808080'
        Ui invoke Group_All | Out-Null
        Capture '04-all-custom-buttons-blue'
        OpenTabColor Group_All
        Ui wait-for TxtGroupColorHex --value '#808080' -t 5000 | Out-Null
        Ui set-value TxtGroupColorHex '#FF0000' | Out-Null
        Ui invoke CloseButton | Out-Null
        Ui wait-for GroupColorPicker --gone -t 5000 | Out-Null
        AssertTabPreference AllTabColor '#808080'
        OpenTabColor Group_All
        Ui wait-for TxtGroupColorHex --value '#808080' -t 5000 | Out-Null
        Ui invoke CloseButton | Out-Null
        Ui wait-for GroupColorPicker --gone -t 5000 | Out-Null
        Capture '05-all-cancel-buttons-blue'
    }
    Test 'Vertical and horizontal layout choices persist across page navigation' {
        Ui invoke NavSettings | Out-Null
        SelectCombo CmbGroupTabPlacement Vertical
        AssertPreferences Dark $true
        Ui invoke NavVault | Out-Null
        Ui wait-for Group_All -t 5000 | Out-Null
        Capture '06-vertical-tabs'
        Ui invoke NavSettings | Out-Null
        Ui wait-for CmbGroupTabPlacement --value Vertical -t 5000 | Out-Null
        SelectCombo CmbGroupTabPlacement Horizontal
        AssertPreferences Dark $false
    }
    Test 'Ungrouped tab color persists independently and Reset clears it' {
        OpenTabColor Group_Ungrouped
        Ui set-value TxtGroupColorHex '#00AAFF' | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for GroupColorPicker --gone -t 5000 | Out-Null
        AssertTabPreference UngroupedTabColor '#00AAFF'
        AssertTabPreference AllTabColor '#808080'
        Ui invoke Group_Ungrouped | Out-Null
        Capture '07-ungrouped-custom-buttons-blue'
        OpenTabColor Group_Ungrouped
        Ui wait-for TxtGroupColorHex --value '#00AAFF' -t 5000 | Out-Null
        Ui invoke SecondaryButton | Out-Null
        Ui wait-for GroupColorPicker --gone -t 5000 | Out-Null
        AssertTabPreference UngroupedTabColor $null
        OpenTabColor Group_All
        Ui invoke SecondaryButton | Out-Null
        Ui wait-for GroupColorPicker --gone -t 5000 | Out-Null
        AssertTabPreference AllTabColor $null
        Capture '08-system-tabs-reset'
    }
    Test 'Custom tab color uses its right-click menu and persists through encrypted refresh' {
        Ui invoke Group_All | Out-Null
        $group = @(Matches Work | Where-Object { $_.name -eq 'Work' -and $_.type -eq 'Button' -and $_.automationId -like 'Group_*' }) | Select-Object -First 1
        if ($null -eq $group) { throw 'The disposable Work tab was not found.' }
        $script:workTabId = $group.automationId
        OpenTabColor $script:workTabId
        Ui set-value TxtGroupColorHex '#FF0000' | Out-Null
        Ui wait-for PrimaryButton -p IsEnabled --value True -t 5000 | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for GroupColorPicker --gone -t 5000 | Out-Null
        Ui invoke $script:workTabId | Out-Null
        Capture '09-work-red-buttons-blue'
        OpenTabColor $script:workTabId
        Ui wait-for TxtGroupColorHex --value '#FF0000' -t 5000 | Out-Null
        Ui set-value TxtGroupColorHex '#00AAFF' | Out-Null
        Ui invoke CloseButton | Out-Null
        Ui wait-for GroupColorPicker --gone -t 5000 | Out-Null
        OpenTabColor $script:workTabId
        Ui wait-for TxtGroupColorHex --value '#FF0000' -t 5000 | Out-Null
        Ui invoke SecondaryButton | Out-Null
        Ui wait-for GroupColorPicker --gone -t 5000 | Out-Null
        OpenTabColor $script:workTabId
        if ((Ui get-value TxtGroupColorHex).text -eq '#FF0000') { throw 'Reset retained the custom tab color.' }
        Ui invoke CloseButton | Out-Null
        Ui wait-for GroupColorPicker --gone -t 5000 | Out-Null
        Capture '10-work-reset'
    }
    Test 'Right-click Rename tab updates custom and built-in labels' {
        RenameTab $script:workTabId 'Work renamed'
        OpenTabMenu $script:workTabId 'Rename tab'
        Ui wait-for TxtTabName --value 'Work renamed' -t 5000 | Out-Null
        Ui invoke CloseButton | Out-Null
        Ui wait-for TxtTabName --gone -t 5000 | Out-Null
        Capture '11-work-renamed'
        RenameTab $script:workTabId Work
        RenameTab Group_All 'All renamed'
        AssertTabPreference AllTabName 'All renamed'
        OpenTabMenu Group_All 'Rename tab'
        Ui wait-for TxtTabName --value 'All renamed' -t 5000 | Out-Null
        Ui invoke CloseButton | Out-Null
        Ui wait-for TxtTabName --gone -t 5000 | Out-Null
        RenameTab Group_All All
    }
    Test 'Trash Back to Vault synchronizes navigation selection' {
        Ui invoke NavTrash | Out-Null
        Ui wait-for BtnCloseTrash -t 5000 | Out-Null
        Ui wait-for NavTrash -p IsSelected --value True -t 5000 | Out-Null
        Ui invoke BtnCloseTrash | Out-Null
        Ui wait-for BtnAddItem -t 5000 | Out-Null
        Ui wait-for NavVault -p IsSelected --value True -t 5000 | Out-Null
    }
    Test 'Editor validation stays visible and Ctrl+N preserves the draft' {
        Ui invoke BtnAddItem | Out-Null
        Ui wait-for TxtEditorTitle -t 5000 | Out-Null
        Ui invoke BtnSaveItem | Out-Null
        Ui wait-for StatusWorkspace -t 5000 | Out-Null
        Ui wait-for TxtEditorTitle -t 5000 | Out-Null
        Capture '12-editor-error'
        Ui set-value TxtEditorTitle $draftTitle | Out-Null
        EnsureForeground
        Ui send-keys 'ctrl+n' --target TxtEditorTitle --via send-input | Out-Null
        Ui wait-for TxtEditorTitle --value $draftTitle -t 5000 | Out-Null
    }
    Test 'Discard confirmation protects drafts and cancel returns to selected Vault' {
        Ui invoke BtnCancelItem | Out-Null
        Ui wait-for PrimaryButton -t 5000 | Out-Null
        Capture '13-discard-confirmation' -Popup
        Ui invoke CloseButton | Out-Null
        Ui wait-for TxtEditorTitle --value $draftTitle -t 5000 | Out-Null
        Ui invoke BtnCancelItem | Out-Null
        Ui wait-for PrimaryButton -t 5000 | Out-Null
        Ui invoke PrimaryButton | Out-Null
        Ui wait-for BtnAddItem -t 5000 | Out-Null
        Ui wait-for NavVault -p IsSelected --value True -t 5000 | Out-Null
    }
    Test 'Rapid double-click Save creates one item and selects Vault' {
        Ui invoke BtnAddItem | Out-Null
        Ui wait-for TxtEditorTitle -t 5000 | Out-Null
        Ui set-value TxtEditorTitle $savedTitle | Out-Null
        Ui set-value TxtEditorPassword 'synthetic-test-password' | Out-Null
        Reveal BtnSaveItem
        EnsureForeground BtnSaveItem
        Ui click BtnSaveItem --double | Out-Null
        Ui wait-for TxtVaultSearch -t 10000 | Out-Null
        Ui wait-for NavVault -p IsSelected --value True -t 5000 | Out-Null
        Ui set-value TxtVaultSearch $savedTitle | Out-Null
        Ui wait-for $savedTitle -t 5000 | Out-Null
        $items = @(Matches 'Edit item' | Where-Object { $_.type -eq 'Button' -and $_.automationId -like 'Edit_*' })
        if ($items.Count -ne 1) { throw "Rapid Save produced $($items.Count) matching items instead of one." }
        Capture '14-saved-item'
        Ui invoke BtnClearVaultFilters | Out-Null
    }
    Test 'System theme remains selectable and preferences survive navigation' {
        Ui invoke NavSettings | Out-Null
        SelectCombo CmbAppTheme System
        AssertPreferences System $false
        Ui invoke NavVault | Out-Null
        Ui invoke NavSettings | Out-Null
        Ui wait-for CmbAppTheme --value System -t 5000 | Out-Null
        Capture '15-system-background-buttons-blue'
    }
}
catch { Write-Warning $_.Exception.Message }
finally {
    @{
        processId = $ProcessId
        testDirectory = $testRoot
        results = @($results)
        screenshots = @($captures)
        visualReviewRequired = 'Confirm filled buttons remain #204BDB before and after each individual tab color change. Inspect tab previews, text contrast, clipping and hover/focus/disabled states. Repeat at small widths, 125/150 percent DPI, Win11 Snap and a Windows Contrast theme.'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputDirectory "appearance-ui-$captureName.json") -Encoding utf8
}
if (@($results | Where-Object { $_.status -eq 'FAIL' }).Count -gt 0) { exit 1 }
exit 0
