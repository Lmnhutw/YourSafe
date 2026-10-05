# Windows release operations

## Release boundary

This is the only supported release-publish path for `PasswordTool.WinUI`:

```powershell
pwsh .\scripts\Publish-WindowsRelease.ps1 -Version 1.0.0 -ChromeExtensionId $chromeStoreId -EdgeExtensionId $edgeStoreId
```

It targets `win-x64` and uses the repository's .NET 10 release properties: self-contained, single-file, non-trimmed, deterministic publish with embedded debug information disabled. Output is staged under `artifacts\releases\.staging` and finalized only as `artifacts\releases\<version>`. An existing version directory is always rejected rather than overwritten.

The release script publishes `PasswordTool.WinUI` and the standalone `PasswordTool.NativeHost`; it does not publish `PasswordTool.Api`, test projects, source files, or local user data. Both executables are self-contained. The WinUI payload includes the Windows App SDK runtime content for first-run extraction. Before archiving, the script rejects source files, logs, certificates, database files, and the vault paths `.config`, `.storage`, `.trusted-unlock`, and `.snapshots`. It also rejects missing or empty files, output outside its configured staging root, and payloads above the configured size limit.

Supply the actual Chrome/Edge store extension IDs. Missing, malformed, known placeholder and development IDs are rejected. `YourSafe.NativeHost.exe` and per-browser Native Messaging manifests are required payload files; both executables are signed and independently qualified. The installer registers the production host in HKCU32 and removes only registration values still owned by that installation. The portable ZIP never changes the registry. See [browser integration instructions](../browser-extension/README.md) for development scripts and the separate Chrome/Edge manual E2E checklist.

The resulting directory contains:

- `publish\` — the validated self-contained application payload.
- `YourSafe-<version>-win-x64.zip` — the portable distribution archive.
- `checksums.sha256` — SHA-256 entries for the application payload and every ZIP/installer distribution artifact.
- `release-manifest.json` — public version, artifact name, SHA-256, and manual-update release notes only.
- `release-status.txt` — `UNSIGNED` developer/test status or `SIGNED` only after signing and verification succeed.

No vault, password, password history, TOTP secret, recovery code, key, backup passphrase, trusted-unlock token, snapshot, or local storage location is packaged, signed, checksummed, or serialized by this flow.

## Phase 5 release qualification

Qualification is a separate, read-only step over a finalized release directory. Generate a new version with the publish command above, then run:

```powershell
pwsh .\scripts\Invoke-ReleaseQualification.ps1 -ReleaseDirectory .\artifacts\releases\1.0.0
```

The command does not trust the publish script's success output and does not write into the release directory. It independently:

- verifies the finalized versioned directory shape, unpackaged self-contained WinUI release contract, required payload, ZIP, checksums, manifest, and status file;
- compares every portable ZIP entry byte-for-byte with the validated `publish\` payload;
- recalculates every declared SHA-256 value and rejects missing, extra, duplicate, modified, or undeclared payload/distribution files;
- accepts only the public manifest fields `version`, `artifacts` (`fileName` and `sha256`), and `releaseNotes`;
- rejects API/test/source content, vault files and paths, logs, certificates, keys, likely secret material, links, and reparse points;
- verifies that WinUI remains the only published project, `PasswordTool.Api` remains excluded, the WinUI/Presentation/Core sources have no runtime networking, updater, telemetry, or logging surface, and the vault storage source contract remains `%LocalAppData%\PasswordTool`;
- checks the Inno Setup template's per-user x64 identity, publisher field, upgrade identity, install location, payload source, Start Menu entry, and non-deletion contract.

A successful command means the automated artifact and source-contract checks passed. Read every status field and limitation in the result:

- `UNSIGNED_DEVELOPER_TEST_ONLY` is valid only for local developer/test evaluation. It is not a signed-release pass.
- `SIGNED_AND_TIMESTAMP_VERIFIED` is emitted only after independent `signtool verify /pa /all /tw /v` verification and a valid Authenticode timestamp certificate check succeed for every executable artifact.
- `MANUAL_QUALIFICATION_REQUIRED` means no installer was available to qualify. It is not an installer pass.
- `STATIC_CONTRACT_PASSED_MANUAL_SMOKE_REQUIRED` means the installer artifact and template passed static checks, but real install/upgrade/uninstall behavior still requires the controlled-machine smoke tests below.
- `ReleaseReadiness` remains `NOT_ESTABLISHED_UNTIL_REQUIRED_MANUAL_CHECKS_COMPLETE`. Do not infer production readiness from an automated script result alone.

Run the focused script checks before qualifying an artifact:

```powershell
pwsh .\scripts\Test-ReleasePipeline.ps1
pwsh .\scripts\Test-ReleaseQualification.ps1
```

## Installer

`installer\PasswordTool.iss` is a standard Inno Setup template. The release script compiles it only if `ISCC.exe` is already available on the controlled release machine. This repository does not download or add an installer compiler. If the compiler is absent, the ZIP remains the supported reproducible developer artifact and the template is the explicit installer handoff.

When compiled, the installer is per-user and x64 scoped. It installs the validated release payload to `%LocalAppData%\Programs\PasswordTool`, creates a `PasswordTool` Start Menu entry, and keeps the same Inno Setup application identifier for in-place upgrades. It does not request administrator privileges.

Vault data remains in `%LocalAppData%\PasswordTool`, outside the install directory. Upgrade and uninstall do not remove `.config`, `.storage`, `.trusted-unlock`, `.snapshots`, encrypted backups, logs, or any other vault data. The uninstaller removes application binaries only; users must consciously delete local vault material themselves if that is their intent.

When `ISCC.exe` is available and an installer is produced, qualification verifies the static installer contract and that the installer is the only file in `installer\`, is declared in both the checksums and public manifest, and is signed/timestamp-verified when the release claims `SIGNED`. On the controlled release machine, also perform a clean per-user install, launch from the Start Menu, upgrade over the prior approved version, and uninstall. Confirm throughout that `%LocalAppData%\PasswordTool` is neither the installation directory nor created, bundled, migrated, exposed, or removed by the installer.

When `ISCC.exe` is unavailable, do not download it as part of this workflow and do not report installer qualification as passed. Record the emitted `MANUAL_QUALIFICATION_REQUIRED` limitation and repeat installer build plus qualification on a controlled release machine where Inno Setup is already installed.

## Signing on a controlled release machine

Ordinary development does not require signing. With no signing environment variables, `-SigningMode Auto` produces a deliberately labeled unsigned developer/test release. `-SigningMode Required` fails if credentials or the toolchain are incomplete. `-SigningMode Disabled` is an explicit unsigned mode.

Use one of these certificate inputs, never both:

- `PASSWORDTOOL_SIGN_CERT_THUMBPRINT` — 40-hex-character thumbprint for a certificate available to the controlled release machine.
- `PASSWORDTOOL_SIGN_CERT_PATH` and `PASSWORDTOOL_SIGN_CERT_PASSWORD` — absolute path to a `.pfx`/`.p12` and its secure process environment value.

Also set:

- `PASSWORDTOOL_SIGN_TIMESTAMP_URL` — an approved HTTPS RFC 3161 timestamp service.
- `PASSWORDTOOL_SIGN_TOOL_PATH` — optional absolute path to `signtool.exe`; otherwise the script discovers `signtool.exe` from the installed Windows SDK.

Do not commit a certificate, private key, password, timestamp credential, token, a populated environment file, or a command line containing a real secret. Supply them only through the controlled release process's secure environment. Requested certificate paths must exist and be `.pfx`/`.p12`; signing targets are constrained to the staged release directory. The script signs application and installer executables, then runs `signtool verify /pa /tw`. Any unavailable tool, signing failure, verification failure, or timestamp verification failure stops the release rather than claiming it is signed.

Example, with secret values supplied by the release environment rather than pasted into source:

```powershell
$env:PASSWORDTOOL_SIGN_CERT_THUMBPRINT = '<40-hex-thumbprint>'
$env:PASSWORDTOOL_SIGN_TIMESTAMP_URL = 'https://<approved-timestamp-service>'
pwsh .\scripts\Publish-WindowsRelease.ps1 -Version 1.0.0 -ChromeExtensionId $chromeStoreId -EdgeExtensionId $edgeStoreId -SigningMode Required
```

## Verify a received release

Verify the checksum file from the release directory:

```powershell
Get-Content .\artifacts\releases\1.0.0\checksums.sha256 | ForEach-Object {
    $hash, $relativePath = $_ -split ' \*', 2
    $actual = (Get-FileHash (Join-Path .\artifacts\releases\1.0.0 $relativePath) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $hash) { throw "Checksum mismatch: $relativePath" }
}
```

For a signed executable, verify its Authenticode signature and timestamp:

```powershell
signtool verify /pa /tw .\artifacts\releases\1.0.0\publish\YourSafe.exe
```

Verify the installer too when one was produced. A successful verification is the only basis for describing the release as signed.

Inspect the public manifest directly:

```powershell
Get-Content .\artifacts\releases\1.0.0\release-manifest.json -Raw | ConvertFrom-Json | Format-List
```

It may contain only the release version, distribution artifact names and SHA-256 values, and public release-notes text or URL. It must not contain certificate paths, credentials, machine paths, signing configuration, environment values, vault metadata, or other private fields. Match each manifest SHA-256 to the corresponding entry in `checksums.sha256` and to a fresh `Get-FileHash` result.

## Manual updates

PasswordTool has no auto-updater, polling service, runtime update check, silent download, or runtime network dependency. Users obtain a newer approved signed installer or portable ZIP through the approved release location, verify it, and run it manually. Normal installer upgrades keep `%LocalAppData%\PasswordTool` intact.

## Controlled-machine manual smoke checklist

Perform these checks against the exact qualified artifact. Record failures and unavailable tooling/environment steps as limitations; never convert a skipped or blocked check into a pass.

- **First launch:** Start with no PasswordTool vault files, create a vault, and confirm no network access is required.
- **Existing-vault unlock:** Launch against a protected test vault at `%LocalAppData%\PasswordTool` and unlock with its Master Password.
- **Lock/relock:** Exercise manual lock and the fixed vault deadline, then unlock again and confirm sensitive state was cleared while locked. Activity must not extend either deadline.
- **Session authorization:** Verify Login requires Master Password + TOTP and re-unlock during that Login requires only the Master Password. Reveal/copy and other vault actions do not request another code; Login ends after five hours.
- **Manual lock:** Verify Lock keeps the app open and clears decrypted vault state. It shows Unlock Vault during a valid Login and Login after expiration.
- **Backup verification:** Export an encrypted backup to an external test path and complete authenticated verification with the separate backup passphrase; confirm no passphrase or plaintext secrets appear in release files or logs.
- **Security Check:** Run the local Security Check against known weak/reused/old test entries, edit through the protected workflow, and confirm the rescan updates without displaying password values.
- **Offline launch:** Disconnect networking before launch and exercise unlock plus normal vault use. Confirm no update, telemetry, cloud, account, API, or other network prompt/dependency appears.
- **Upgrade preserves vault data:** Install the prior approved version, create a disposable test vault, upgrade using the candidate installer, and confirm the same `%LocalAppData%\PasswordTool` vault unlocks unchanged.
- **Uninstall preserves vault data:** Uninstall the application and confirm `%LocalAppData%\PasswordTool` and its test vault remain. Reinstall and confirm the preserved test vault still unlocks.

Use disposable qualification data, never a real user vault. These scenarios validate the qualified behavior; they do not change the vault format, KDF, encrypted backup/recovery, password lifecycle, or Security Check rules.

## Release operator checklist

1. Run `pwsh .\scripts\Test-ReleasePipeline.ps1` and `pwsh .\scripts\Test-ReleaseQualification.ps1`.
2. Run the solution verification sequence from the root README/task requirements.
3. Choose a new numeric `major.minor.patch` version that does not already exist under `artifacts\releases`.
4. Set signing inputs only on the controlled release machine, or intentionally create an unsigned developer/test release.
5. Run `Publish-WindowsRelease.ps1`; do not manually copy files into its staging/output directory.
6. Run `Invoke-ReleaseQualification.ps1` against the newly finalized version directory.
7. Inspect the reported signing/installer status and every limitation. Independently verify checksums, the public manifest, Authenticode signatures, and timestamps.
8. Complete and record every applicable controlled-machine smoke scenario above. Unavailable tools and blocked environment checks remain explicit limitations, never passes.
9. Publish nothing from this repository automatically. A commit, tag, external release, upload, or other distribution action remains separate work requiring explicit authorization.
