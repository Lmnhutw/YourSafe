param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][string]$TestDirectory
)
$ErrorActionPreference = 'Stop'
function Ui {
    $output = & winapp ui @args -a $ProcessId --json
    $exitCode = $LASTEXITCODE
    $json = $output -join "`n" | ConvertFrom-Json
    if ($exitCode -ne 0 -and -not ($args[0] -eq 'search' -and $json.matchCount -eq 0)) { throw "UI command failed: $args" }
    return $json
}
function Element([string]$Name, [string]$Type) {
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    do {
        $matches = @((Ui search $Name).matches | Where-Object { $_.name -eq $Name -and $_.type -eq $Type -and -not $_.isOffscreen } | Group-Object selector | ForEach-Object { $_.Group[0] })
        if ($matches.Count -eq 0) { Start-Sleep -Milliseconds 100 }
    } while ($matches.Count -eq 0 -and [DateTime]::UtcNow -lt $deadline)
    if ($matches.Count -ne 1) { throw "Expected one visible $Type '$Name'." }
    return $matches[0]
}
$marker = Ui get-property YourSafeDisposableUiTest -p HelpText
if ($marker.properties.HelpText -ne (Resolve-Path -LiteralPath $TestDirectory).Path) { throw 'Disposable vault marker mismatch.' }
$null = Ui invoke NavVault
foreach ($value in @('Recovery codes', 'Favorites', 'Passwords', 'All items', 'Recovery codes', 'All items')) {
    $combo = Element 'Item type filter' ComboBox
    $null = Ui invoke $combo.selector
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    do {
        $first = Element 'All items' ListItem
        $below = $first.y -ge $combo.y + $combo.height - 2
        if (-not $below) { Start-Sleep -Milliseconds 100 }
    } while (-not $below -and [DateTime]::UtcNow -lt $deadline)
    if (-not $below) { throw "Dropdown at $($first.y) must be below field bottom $($combo.y + $combo.height)." }
    $option = Element $value ListItem
    $null = Ui focus $option.selector
    $null = Ui click $option.selector
    $closed = Ui wait-for $combo.selector -p ExpandCollapseState --value Collapsed -t 5000
    if ($closed.timedOut) { throw 'Dropdown did not close after the first click.' }
    $null = Ui wait-for $combo.selector --value $value -t 5000
    Write-Output "PASS: downward popup and selection '$value'."
}
$null = Ui invoke NavSettings
foreach ($value in @('Dark', 'Light', 'System')) {
    $combo = Element 'App background' ComboBox
    $null = Ui invoke $combo.selector
    $option = Element $value ListItem
    $null = Ui focus $option.selector
    $null = Ui click $option.selector
    $closed = Ui wait-for CmbAppTheme -p ExpandCollapseState --value Collapsed -t 5000
    if ($closed.timedOut) { throw 'Background dropdown did not close after the first click.' }
    $result = Ui wait-for CmbAppTheme --value $value -t 5000
    if (-not $result.found) { throw "First selection did not update background to '$value'." }
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    do {
        $theme = (Get-Content -Raw -LiteralPath (Join-Path $TestDirectory 'appearance.json') | ConvertFrom-Json).Theme
    } while ($theme -ne $value -and [DateTime]::UtcNow -lt $deadline)
    if ($theme -ne $value) { throw "Background '$value' was not saved." }
    Write-Output "PASS: first selection applies and saves background '$value'."
}
$null = Ui invoke NavVault
