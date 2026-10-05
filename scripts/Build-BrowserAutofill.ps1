[CmdletBinding()]
param([switch]$Production)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location (Join-Path $repositoryRoot 'browser-extension')
try {
    & npm.cmd ci --ignore-scripts
    if ($LASTEXITCODE -ne 0) { throw 'Extension dependencies failed.' }
    & npm.cmd run typecheck
    if ($LASTEXITCODE -ne 0) { throw 'Extension typecheck failed.' }
    & npm.cmd test
    if ($LASTEXITCODE -ne 0) { throw 'Extension tests failed.' }
    $buildScript = if ($Production) { 'build:production' } else { 'build' }
    & npm.cmd run $buildScript
    if ($LASTEXITCODE -ne 0) { throw 'Extension build failed.' }
} finally { Pop-Location }
if (-not $Production) {
    & dotnet build (Join-Path $repositoryRoot 'src/PasswordTool.WinUI/PasswordTool.WinUI.csproj') -c Debug -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
    $desktopDirectory = Join-Path $repositoryRoot 'src/PasswordTool.WinUI/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64'
    $hostOutput = Join-Path $repositoryRoot 'artifacts/autofill/development/host'
    & dotnet publish (Join-Path $repositoryRoot 'src/PasswordTool.NativeHost/PasswordTool.NativeHost.csproj') -c Debug -r win-x64 --self-contained true -o $hostOutput -p:PublishSingleFile=true -p:DebugType=embedded -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'NativeHost development publish failed.' }
    Copy-Item -LiteralPath (Join-Path $hostOutput 'YourSafe.NativeHost.exe') -Destination $desktopDirectory
    Write-Output "Load unpacked: $(Join-Path $repositoryRoot 'browser-extension/dist/development')"
    Write-Output "Register: .\scripts\Register-BrowserAutofill.ps1"
    Write-Output "Run: .\scripts\Start-BrowserAutofill.ps1"
}
