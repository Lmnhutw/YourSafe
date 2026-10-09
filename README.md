# YourSafe (PasswordTool)

> A local Windows vault for passwords and account recovery codes, plus a password-hash utility and optional Chrome/Edge autofill. Vault data stays on the device; the desktop application has no database, cloud sync, online account system, telemetry, or runtime network dependency.

YourSafe is the desktop product name; repository/project names and the vault directory retain `PasswordTool`. The desktop application uses WinUI 3. The former WinForms client and its UI-preview project were removed during the migration cutover.

## What is included

| Area | What it does |
| --- | --- |
| **Encrypted vault** | Stores credentials with a password, account recovery codes, or both in an AES-256-GCM encrypted local vault. |
| **Sign-in** | Requires a Master Password and a current six-digit Google Authenticator code. |
| **Session** | Fixed five-hour login; vault locks after a configurable duration from unlock, one minute by default. |
| **Recovery Key** | Resets the Master Password and Authenticator for the current vault, with a fresh key and verified replacement Authenticator. |
| **Backup & recovery** | Creates and verifies encrypted external backups and can recover a vault on a new Windows installation. |
| **Everyday organization** | Searches locally and organizes entries with favorites, groups displayed as tabs, and tags. |
| **Password generation** | Generates cryptographically random passwords and readable passphrases with strength feedback. |
| **Website TOTP** | Add or edit TOTP in a credential draft, then Save item. View current codes from the vault's 2FA action, Details, or the browser popup while the desktop vault is unlocked. |
| **Browser autofill** | Session opt-in Chrome/Edge integration fills an account on its exact stored origin only after a separate one-shot desktop approval. |
| **Migration** | Reviews and imports common browser or password-manager CSV exports without overwriting matching accounts. |
| **Safety lifecycle** | Keeps password history, a 30-day Trash, paired encrypted snapshots, timed vault lock, and a local weak/reused/old-password check. |
| **Hash utility** | Generates, verifies, and inspects password hashes, including clearly marked educational-only algorithms. |

## Download and install

The recommended way to use PasswordTool is to download an approved Windows x64 release from the [GitHub Releases page](https://github.com/Lmnhutw/PasswordTool.Core/releases). A release may provide either or both of these packages:

- **Installer (`.exe`)** — the easiest option. It installs PasswordTool for the current Windows user, adds a Start Menu shortcut, and does not require administrator access.
- **Portable ZIP** — extract the complete ZIP to a folder you control, then run `YourSafe.exe`. Do not run the executable from inside the ZIP or copy only the `.exe` out of its folder.

Before running a downloaded build:

1. Read `release-status.txt`. Use a `SIGNED` release for normal use. `UNSIGNED` means developer/test evaluation only; Windows may show an unknown-publisher warning.
2. Compare the package SHA-256 value with the matching entry in `checksums.sha256`:

   ```powershell
   Get-FileHash .\YourSafe-<version>-win-x64.zip -Algorithm SHA256
   ```

3. For a signed executable, open **Properties → Digital Signatures** and confirm the signature is valid, or verify it with `signtool verify /pa /tw <file>` when Windows SDK tools are available.

PasswordTool releases are self-contained: an end user does not need to install .NET separately. The application runs locally and does not require an account, browser extension, cloud service, or internet connection. New versions are installed manually; PasswordTool has no auto-updater.

### Upgrade, uninstall, and portable use

- Installing a newer approved installer over an older one keeps the vault in `%LocalAppData%\PasswordTool`.
- Uninstalling removes application binaries but intentionally keeps the vault. Delete the vault directory manually only when you are certain you have a usable encrypted backup and intend to erase local data.
- A portable executable is portable; the vault is not. By default, it still uses `%LocalAppData%\PasswordTool` on the current Windows profile.
- Never copy only `.config` or only `.storage` between machines. Use **Backup & Recovery Center** and an encrypted backup instead.

## First-time setup

1. Start PasswordTool and choose **Create a new vault**. If you already have a PasswordTool encrypted backup, choose **Recover from encrypted backup** instead.
2. Create a strong, unique Master Password of at least 12 characters. A long passphrase that you do not reuse elsewhere is recommended.
3. Scan the displayed QR code with Google Authenticator or another compatible TOTP application.
4. Enter the current 6-digit code to confirm setup.
5. Save the displayed Recovery Key outside this device and confirm that you saved it. Setup does not commit before confirmation. Protect it separately from the Master Password and Authenticator: it can reset both credentials.
6. Add a test item, lock the vault, unlock it again, and create an encrypted external backup before relying on the vault for important data.

Sign in with both the Master Password and the current 6-digit Google Authenticator code. Within that fixed five-hour session, unlocking a locked vault requires only the Master Password and never extends the session. Restarting the application requires a fresh sign-in. PasswordTool does not offer Authenticator-only sign-in. Enter codes in the six digit boxes; pasting a six-digit code is supported.

## Everyday use

1. **Sign in / unlock:** initial sign-in requires Master Password and Authenticator code; subsequent unlocks within the same session require Master Password only.
2. **Add and organize:** create a credential with a password, at least two account recovery codes, or both. Assign favorites, groups, tags, URLs, or notes. Right-click a group tab to rename it or change its color; Settings offers horizontal/vertical tabs and System/Light/Dark themes.
3. **Reveal or copy a secret:** no second Authenticator prompt is needed while the sign-in session is active.
4. **Lock:** Lock keeps the login active and clears decrypted vault state. The vault locks after its duration from unlock (1, 2, 5, 10, or 30 minutes; 1, 2, or 5 hours), or when Windows locks/disconnects, suspends, or resumes. Activity does not extend either deadline. Settings changes apply on the next unlock.
5. **Back up:** regularly export an encrypted backup using a separate strong backup passphrase, store it away from the PC, and verify it in **Backup & Recovery Center**.
6. **Check safety:** run **Local Security Check** to find weak, exactly reused, or old passwords without sending values to an online service.

When recovering on another Windows installation, the backup passphrase decrypts the backup. You then create a new Master Password and a new PasswordTool Authenticator for that installation. Legacy trusted-device tokens are not used by the current sign-in flow.

### What must be kept safe

| If this is lost | Result |
| --- | --- |
| Master Password | Choose Forgot Master Password and use the Recovery Key to reset credentials. |
| Authenticator entry/device | Use the Recovery Key to replace the Master Password and Authenticator. |
| Master Password and Authenticator access | The Recovery Key can reset both. Without it, recover an encrypted backup on a fresh installation using its separate passphrase. |
| Backup passphrase | That backup cannot be decrypted. The live vault is unaffected while its own Master Password remains available. |
| Computer or Windows profile | Restore an encrypted external backup on the new installation. Create a new Master Password and Authenticator during recovery. |

## Build from source

### Requirements

- Windows x64, Windows 10 version 1809 or later
- .NET 10 SDK
- PowerShell 7 (`pwsh`) for repository scripts
- Node.js/npm only when building the optional browser extension
- Git (only when cloning)

Confirm the installed SDK:

```powershell
dotnet --version
```

### Clone, restore, and run

Clone the repository, or extract a ZIP so that `PasswordTool.slnx` is in the current folder. Package references are already committed to the project files; do **not** run `dotnet add` to set up the solution.

```powershell
git clone https://github.com/Lmnhutw/PasswordTool.Core.git
cd PasswordTool.Core
dotnet restore PasswordTool.slnx
dotnet run --project src\PasswordTool.WinUI\PasswordTool.WinUI.csproj --configuration Debug -p:Platform=x64
```

Building from source is intended for developers. It does not establish that a local build is an approved, signed release. On first launch, follow the setup flow above.

### Verify the solution

```powershell
dotnet test PasswordTool.slnx
dotnet build PasswordTool.slnx
```

These commands do not build the TypeScript extension. See [browser-extension/README.md](browser-extension/README.md) for its build and registration steps. For unit checks without browser registration or live IPC, run:

```powershell
dotnet test tests\PasswordTool.Core.Tests
dotnet test tests\PasswordTool.Presentation.Tests --filter "FullyQualifiedName!~Windows_pipe_peers"
cd browser-extension
npm.cmd ci --ignore-scripts
npm.cmd run typecheck
npm.cmd test
npm.cmd run build
```

Native action checks use [scripts/Test-VaultUi.ps1](scripts/Test-VaultUi.ps1) against an unlocked disposable test vault. Debug builds support `PASSWORDTOOL_UI_TEST_DIRECTORY` for isolated storage and a separate single-instance mutex; `PASSWORDTOOL_UI_TEST_WIDTH` and `PASSWORDTOOL_UI_TEST_THEME` select test geometry and theme. Release builds ignore these overrides. Seed a new test directory with the opt-in `Create_opt_in_disposable_ui_vault` Core test, then run the script with that app's process ID. Never target a user vault. See [the verification record](docs/recovery-session-verification.md) for results and outstanding visual checks.

## How encryption and unlocking work

PasswordTool does not store the Master Password and does not merely hide password text behind the Google Authenticator screen. The durable files contain authenticated ciphertext. To display a saved password, the application must obtain the vault encryption key, authenticate and decrypt the vault, then authorize the reveal action.

```text
Random 256-bit DEK
  ├─ AES-256-GCM encrypts vault items in .storage
  └─ AES-256-GCM encrypts the PasswordTool Authenticator secret in .config

Master Password
  └─ Argon2id (3 passes, 64 MiB, parallelism 2 + random 32-byte salt)
       └─ KEK wraps the DEK with AES-256-GCM

Current desktop sign-in
  └─ Master Password unwraps the DEK; a valid Authenticator code authorizes a five-hour login
       └─ re-unlock within that login uses the Master Password without another code
```

The terms in that flow mean:

- **DEK (Data Encryption Key):** a random 256-bit key created independently for each new vault. It encrypts `.storage` and the PasswordTool Authenticator secret with AES-256-GCM.
- **KEK (Key Encryption Key):** a key derived from the Master Password and a random salt using Argon2id. It is not stored. It wraps, or encrypts, the DEK.
- **Envelope encryption:** vault data is encrypted with the random DEK, while the Master Password-derived KEK encrypts only that DEK. Therefore, changing the Master Password or strengthening KDF parameters re-wraps the same DEK without decrypting and re-encrypting the entire vault file.
- **AES-256-GCM:** authenticated encryption. A wrong key, modified ciphertext, or ciphertext used in the wrong context fails authentication instead of returning unchecked plaintext. PasswordTool binds the wrapped DEK, vault payload, and Authenticator secret to different purposes.
- **DPAPI CurrentUser:** Windows protection used by compatibility trusted-device tokens. It binds protected data to the Windows user, not to this application; arbitrary code running as that user can also unprotect it.

Core maintains a one-day DPAPI-protected compatibility token with separate Authenticator and DEK blobs. Version 3 protects creation time, expiry and vault fingerprint inside both blobs; older tokens require a fresh Master Password sign-in. The current desktop sign-in does not use that token to offer Authenticator-only login.

### What happens when a password is shown

1. The encrypted vault remains in `.storage`; the UI masking characters are not the security boundary.
2. The vault must already be unlocked with both factors. The Master Password derives the KEK and unwraps the DEK; the Authenticator code verifies the sign-in session.
3. PasswordTool uses the DEK to authenticate and decrypt the vault into the running process's session memory.
4. The successful sign-in authorizes vault actions for at most five hours. Actions do not request another Authenticator code during that session.
5. Only after that check does the UI display or explicitly copy the requested value. Passwords, OTP codes, recovery codes and setup/recovery keys use best-effort clipboard cleanup after 30 seconds and on lock. Cleanup preserves a value replaced by the user; it cannot retract a paste or a copy read by another process.
6. Locking or ending the session clears application-held key material where the runtime permits. The on-disk vault remains encrypted throughout.

Possession of a current 6-digit code alone is insufficient to open a copied vault on another computer: the current desktop requires the Master Password as well. Legacy trusted tokens are tied to the original Windows user. TOTP does not protect against malware already controlling that same unlocked Windows account or reading the process while the vault is open.

### Vault-format migration

Config v4 adds a Recovery Key wrapper around the same DEK and a credential revision. Existing v3 vaults must complete Master Password and OTP sign-in and save a Recovery Key before workspace access; the ciphertext and v3 crypto contexts are preserved. Legacy v1/v2 payloads are re-encrypted once, only after authentication and key confirmation, through the existing staged, verified transaction. Cancellation or a write failure preserves the old pair.

Forgot Master Password verifies the Recovery Key, collects a new Master Password, requires confirmation of a fresh Recovery Key, and verifies a replacement Authenticator before one commit. It grants no workspace session and returns to Login. Old keys, passwords, OTP secrets, and trusted tokens are invalid for the current state afterward. Settings can rotate the Recovery Key with the current Master Password. Exported snapshots retain their old credentials; snapshots with a different credential revision are labeled as old security state and are never an automatic fallback.

### Sign-in choices

- **Sign-in:** the Master Password unwraps the DEK, then a valid six-digit PasswordTool Authenticator code completes the sign-in. Authenticator-only unlock is unavailable; stored login-mode preferences are ignored for compatibility.
- Existing vaults migrate atomically to v4 after sign-in and Recovery Key confirmation. Vaults without an Authenticator secret cannot complete sign-in.
- Sensitive actions do not request a second code. Login expires after five hours; the configurable vault deadline is capped by that login deadline. Core checks expiration before protected reads and writes.
- Manual Lock keeps the app open and clears decrypted vault state. It shows Unlock Vault while the five-hour Login remains valid, otherwise Login.

### Vault use and backups

- New credentials can contain a password, website TOTP, account recovery codes, or a combination. Recovery-code lists require at least two valid unique codes. Legacy `RecoveryCodes`-type entries remain supported and cannot contain password or website TOTP data.
- Website TOTP configuration remains encrypted and supports SHA1/SHA256/SHA512, 6/8 digits, and custom periods. Older secrets default to SHA1/6 digits/30 seconds. Add/Edit/Remove changes stay in the editor draft until Save item; Cancel discards them. Desktop live panels require an unlocked vault and stop on closure or lock. Copy regenerates the current code and uses best-effort 30-second clipboard cleanup. Browser View and Copy each require a new desktop approval; browser countdown ticks never retrieve another code. The separate vault sign-in Authenticator remains unchanged.
- Favorites, groups, tags, and local search help organize entries without a server or online account. Legacy folders migrate to groups.
- URL and Notes may be hidden in the list; the encrypted stored value is unchanged and can be accessed only through the protected edit workflow.
- General copy/cut shortcuts remain disabled in sensitive fields. Explicit sensitive copy actions attempt to clear a still-owned clipboard value after 30 seconds, with bounded attempts per cleanup and later retries during transient contention. Windows and other applications may read it first; history exclusion is a hint, not guaranteed erasure.
- Export uses a separate backup passphrase of at least 12 characters. The encrypted envelope contains vault entries, including any stored website TOTP secrets; it excludes the Master Password configuration, vault sign-in Authenticator secret, and trusted token.
- The **Backup & Recovery Center** records the last successful external backup and authenticated verification time and warns when no external backup is recorded or the latest is older than 30 days.
- On a new PC, choose **Recover from encrypted backup**, enter the backup passphrase, review safe item counts, then create a new Master Password and Authenticator. Recovery preserves supported item data but deliberately creates a fresh Argon2id salt, trusted token, and application Authenticator secret.
- Import validates the full backup before changing the vault, shows new/duplicate/conflicting IDs, and saves only new entries. Existing entries are never overwritten.
- CSV import supports common headers from browsers and password managers, previews new and duplicate accounts, and adds only new accounts. CSV exports contain plaintext secrets; protect and securely remove them after import.
- Changing a password keeps its latest 10 previous values inside the encrypted vault. Deleted items remain in Trash for 30 days unless restored or permanently deleted.
- UpdatedAt records any item edit; PasswordChangedAt records only when the current password became active. Older vaults derive the latter from the newest valid password-history change, then UpdatedAt, then CreatedAt; impossible future dates are ignored.
- Local Security Check is local-only: it scans active password entries for weak, exactly reused, and passwords at least 365 days old without returning a secret. Its dialog names affected items, supports protected **Edit selected item**, then rescans after the edit. It performs no network request and does not expose password values in its result.
- State changes preserve up to five paired `.config` + `.storage` snapshots. Restore restores the pair and ends the current sign-in session; sign in again using the restored credentials.
- Internal snapshots remain on the same disk. They can undo local changes but are not an external backup and do not protect against disk loss.

## Security model and limits

PasswordTool protects data at rest and requires a local second factor to open the vault. It is not a substitute for securing Windows itself.

| Protected by the application | Not protected by the application |
| --- | --- |
| Vault entries and TOTP secret are encrypted with AES-256-GCM. | Malware, a compromised running Windows session, screen capture, or memory inspection while the vault is open. |
| Every new vault uses an independent random DEK and KDF salt; cryptographic key buffers are cleared when sessions end where the runtime permits. | Loss of all credentials, Recovery Key, and usable backups; there is no server or cloud copy. |
| Initial sign-in requires the Master Password and a valid six-digit Authenticator code; re-unlock within that session requires the Master Password. | Malware or another process already acting as the same Windows user; TOTP does not protect an already-unlocked session. |
| The in-memory sign-in session authorizes vault actions for up to five hours. | A weak Master Password or an unlocked device left accessible to another person. |
| Sensitive clipboard values, including credential TOTP, use best-effort 30-second cleanup while still owned by YourSafe. | Another process reading the clipboard, clipboard history, an already-pasted copy, remote-control software, or malware. |

Keep Windows patched, use a strong unique Master Password, lock the PC when away, protect the authenticator and backup passphrase separately, and keep encrypted backups in a location you control. Hidden/System file attributes are only concealment; encryption and Windows account security are the actual boundaries.

## Local files

The desktop app stores its files in:

```text
%LocalAppData%\PasswordTool
```

| File | Contents |
| --- | --- |
| `.config` | v4 Master and Recovery key slots wrapping the same DEK, credential revision, encrypted Authenticator secret, session settings, and backup-health timestamps. |
| `.storage` | AES-256-GCM encrypted vault payload. |
| `.trusted-unlock` | Legacy trusted-unlock token file; Authenticator-only sign-in is no longer offered. |
| `.snapshots` | Up to five previous paired config/vault states, retaining the same encrypted-at-rest representation. |
| `appearance.json` | Local theme, tab placement, and built-in tab names/colors; separate from the encrypted vault payload. |

Do not manually edit, mix, or partially restore these files. If only `.config` or `.storage` is present, the app stops rather than overwriting partial storage.

## Password hashing utility and API

The desktop **Hash Tool** and the optional API share `PasswordTool.Core` implementations. Argon2id is the default recommendation. bcrypt, PBKDF2, scrypt, and ASP.NET Core Identity formats are supported; MD5/SHA family educational options are intentionally labeled unsafe and must never be used for production password storage.

The API currently exposes local development endpoints:

- `POST /api/password/hash`
- `POST /api/password/verify`
- `POST /api/password/inspect`
- `GET /api/password/algorithms`

It has no authentication, rate limiting, or production hardening today. Do not expose it to an untrusted network. See [the API README](src/PasswordTool.Api/README.md) before running it.

## Windows releases

PasswordTool supports Windows x64. The release workflow publishes deterministic .NET 10, unpackaged, self-contained binaries: `YourSafe.exe` and the adjacent `YourSafe.NativeHost.exe`, plus browser host manifests and required content. End-user machines do not need a separately installed .NET runtime or Windows App SDK runtime. The desktop has no runtime network, update, telemetry, online account, or cloud dependency.

Create an unsigned developer/test release with a new numeric `major.minor.patch` version. Set both variables to actual production Chrome/Edge store extension IDs; missing IDs, placeholders and the development ID are rejected, including when the installer is skipped:

```powershell
pwsh .\scripts\Publish-WindowsRelease.ps1 -Version 1.0.0 -ChromeExtensionId $chromeStoreId -EdgeExtensionId $edgeStoreId -SigningMode Disabled
```

The script writes a non-overwriting versioned directory under `artifacts\releases\1.0.0`. It validates the published payload, excludes vault and source inputs, produces a portable ZIP, and writes SHA-256 checksums, a public static release manifest, and an explicit `release-status.txt`. Use `-SigningMode Required` for a production release with configured signing. The TypeScript extension is built separately and distributed through the browser stores. Do not distribute a release whose status says `UNSIGNED` as a production-signed release.

Qualify the newly generated, finalized directory without modifying it:

```powershell
pwsh .\scripts\Test-ReleasePipeline.ps1
pwsh .\scripts\Test-ReleaseQualification.ps1
pwsh .\scripts\Test-BrowserIntegration.ps1
pwsh .\scripts\Invoke-ReleaseQualification.ps1 -ReleaseDirectory .\artifacts\releases\1.0.0
```

Qualification independently re-hashes the payload and distribution artifacts, compares ZIP entries with the published files, enforces the public manifest schema, rejects API/source/test/vault/secret content, and verifies the offline release and installer source contracts. An unsigned developer release, unavailable signing or installer tools, and pending manual installer/application smoke checks are reported as limitations, not as production release readiness.

An Inno Setup template is included for a per-user x64 installer. When `ISCC.exe` is already installed, the release script compiles it and adds the installer to the checksums. When it is unavailable, the script intentionally produces only the portable ZIP and leaves the documented installer handoff; it never downloads a compiler or packaging dependency.

For signing configuration, checksum/signature verification, qualification outcomes, installer data-retention scenarios, the controlled-machine smoke checklist, manual updates, and the operator checklist, read [docs/release-operations.md](docs/release-operations.md).

## Repository layout

```text
src/
  PasswordTool.Core/       # cryptography, vault workflows, hash implementations
  PasswordTool.Presentation/ # platform-neutral MVVM state and orchestration
  PasswordTool.WinUI/      # local WinUI 3 Windows interface
  PasswordTool.NativeHost/ # C# Native Messaging and named-pipe client
  PasswordTool.Api/        # optional Minimal API for hash operations
tests/
  PasswordTool.Core.Tests/ # core behavior and security-rule tests
  PasswordTool.Presentation.Tests/ # MVVM and orchestration tests
docs/
  architecture.md          # boundaries, data flows, and developer rules
browser-extension/         # standalone TypeScript Chrome/Edge extension
website/                   # static Vietnamese landing page
```

For implementation details and rules for future changes, read [docs/architecture.md](docs/architecture.md).

## Browser autofill

YourSafe includes a local C# NativeHost and a standalone TypeScript extension for Chrome and Edge. Integration starts disabled for each app session; enabling it in Settings starts the local pipe listeners and disabling it cancels their connections. Each password/OTP read or OTP copy requires a separate one-shot approval on the running, unlocked desktop. Stored URLs must match the exact supported origin; URL-empty items are denied. The extension uses activeTab access after explicit interaction, validates the destination fields and form, and never submits it. Discovery stays within a 64 KiB envelope and reports when the list is incomplete. See [browser-extension/README.md](browser-extension/README.md) and [the security requirements](docs/security-hardening-requirements.md) for boundaries and deferred qualification.
