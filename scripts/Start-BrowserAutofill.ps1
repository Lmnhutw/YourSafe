Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$executable = Join-Path $repositoryRoot 'src/PasswordTool.WinUI/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/YourSafe.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Run Build-BrowserAutofill.ps1 first.' }
Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) -WindowStyle Hidden
