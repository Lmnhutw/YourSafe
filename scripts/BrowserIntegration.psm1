Set-StrictMode -Version Latest

function Assert-ProductionExtensionId {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Id)
    if ($Id -cnotmatch '^[a-p]{32}$' -or $Id -in @('cmfnnjellknkpnbooenlahijcmalbifg', 'abcdefghijklmnopabcdefghijklmnop') -or
        $Id -match '^([a-p])\1{31}$') {
        throw 'Production extension ID must be a real store ID, not missing, a placeholder, or the development ID.'
    }
}

function Write-NativeHostManifest {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExecutablePath,
        [Parameter(Mandatory)][string]$ExtensionId,
        [switch]$Development
    )
    if ($ExtensionId -cnotmatch '^[a-p]{32}$') { throw 'Invalid extension ID.' }
    if (-not $Development) { Assert-ProductionExtensionId $ExtensionId }
    $manifest = [ordered]@{
        name = if ($Development) { 'com.yoursafe.autofill.dev' } else { 'com.yoursafe.autofill' }
        description = 'YourSafe local browser autofill'
        path = $ExecutablePath
        type = 'stdio'
        allowed_origins = @("chrome-extension://$ExtensionId/")
    }
    [IO.File]::WriteAllText($Path, ($manifest | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
}

function Assert-NativeHostManifest {
    param([Parameter(Mandatory)][string]$Path)
    $manifest = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
    if ($manifest.name -cne 'com.yoursafe.autofill' -or $manifest.type -cne 'stdio' -or
        $manifest.path -cne 'YourSafe.NativeHost.exe' -or @($manifest.allowed_origins).Count -ne 1 -or
        $manifest.allowed_origins[0] -cnotmatch '^chrome-extension://([a-p]{32})/$') {
        throw "Invalid production Native Messaging manifest '$Path'."
    }
    Assert-ProductionExtensionId $Matches[1]
}

Export-ModuleMember -Function Assert-ProductionExtensionId, Write-NativeHostManifest, Assert-NativeHostManifest
