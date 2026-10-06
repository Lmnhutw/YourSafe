param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][string]$TestDirectory,
    [switch]$ExpectSecondary
)

# Run with a two- or three-button modal open in the disposable DEBUG app.
$ErrorActionPreference = 'Stop'
function Ui {
    $output = & winapp ui @args -a $ProcessId --json
    if ($LASTEXITCODE -ne 0) { throw "UI command failed: $args" }
    return ($output -join "`n" | ConvertFrom-Json)
}
function Button([string]$Id) {
    $matches = @((Ui search $Id).matches | Where-Object { $_.automationId -eq $Id -and -not $_.isOffscreen })
    if ($matches.Count -ne 1) { throw "Expected one visible modal button: $Id" }
    return $matches[0]
}

$marker = Ui get-property YourSafeDisposableUiTest -p HelpText
if ($marker.properties.HelpText -ne (Resolve-Path -LiteralPath $TestDirectory).Path) {
    throw 'Disposable vault marker mismatch.'
}
$deadline = [DateTime]::UtcNow.AddSeconds(3)
do {
    $primary = Button PrimaryButton
    $cancel = Button CloseButton
    $ordered = $cancel.x + $cancel.width -le $primary.x -and [Math]::Abs($cancel.y - $primary.y) -le 2
    if (-not $ordered) { Start-Sleep -Milliseconds 100 }
} while (-not $ordered -and [DateTime]::UtcNow -lt $deadline)
if (-not $ordered) {
    throw 'Cancel must be left of Primary in the same footer row.'
}
if ($ExpectSecondary) {
    $secondary = Button SecondaryButton
    if ($secondary.x -lt $cancel.x + $cancel.width -or $secondary.x + $secondary.width -gt $primary.x) {
        throw 'Secondary must be between Cancel and Primary.'
    }
}
Write-Output "PASS: $($cancel.name) -> $(if ($ExpectSecondary) { $secondary.name + ' -> ' })$($primary.name)"
