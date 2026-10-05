[CmdletBinding()]
param([Parameter(Mandatory)][string]$Version)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleasePipeline.psm1') -Force
Assert-ReleaseVersion $Version
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$output = Join-Path $repositoryRoot "artifacts/test-releases/$Version"
if (Test-Path -LiteralPath $output) { throw 'Test release already exists.' }
$publish = Join-Path $output 'publish'
& dotnet publish (Join-Path $repositoryRoot 'src/PasswordTool.WinUI/PasswordTool.WinUI.csproj') -c Release -r win-x64 --self-contained true -o $publish '-p:PasswordToolReleasePublish=true' '-p:Platform=x64' "-p:Version=$Version" '-p:ContinuousIntegrationBuild=true'
if ($LASTEXITCODE -ne 0) { throw 'Test release publish failed.' }
$executable = Join-Path $publish 'YourSafe.exe'
if (-not (Test-Path -LiteralPath $executable) -or (Get-Item $executable).Length -eq 0) { throw 'Missing application EXE.' }

# Test builds omit browser registration until real store extension IDs exist.
$template = [IO.File]::ReadAllText((Join-Path $repositoryRoot 'installer/PasswordTool.iss'))
$template = [regex]::Replace($template, '(?s)\[Registry\].*?(?=\[UninstallDelete\])', '')
$templatePath = Join-Path $output 'test-installer.iss'
[IO.File]::WriteAllText($templatePath, $template)
if (Get-Command ISCC.exe -ErrorAction SilentlyContinue) {
    $null = Invoke-InnoSetupBuild -InstallerScriptPath $templatePath -Version $Version -PublishDirectory $publish -OutputRoot $output
}
$zip = Join-Path $output "YourSafe-$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip
$artifacts = @(Get-Item $zip)
$installer = Join-Path $output "installer/YourSafe-$Version-win-x64-setup.exe"
if (Test-Path -LiteralPath $installer) { $artifacts += Get-Item $installer }
$checksums = foreach ($artifact in $artifacts) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $artifact.Name
}
[IO.File]::WriteAllLines((Join-Path $output 'checksums.sha256'), [string[]]$checksums)
[IO.File]::WriteAllText((Join-Path $output 'release-status.txt'), 'UNSIGNED TEST PRERELEASE. Browser autofill registration omitted. Production release qualification and interactive smoke tests have not been performed.')
Write-Output $output
