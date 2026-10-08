# PasswordTool.Core

The core owns Argon2id/PBKDF2 key derivation, AES-GCM vault persistence, paired snapshots, encrypted-backup inspection and new-machine recovery, bounded security timers, Master Password and Authenticator rotation, password history, Trash retention, and local security findings. UI and API layers must call these workflows rather than reproduce cryptographic or persistence logic.

The reusable domain and security layer for PasswordTool. The WinUI desktop UI and HTTP projects depend on this project; Core must never depend on UI-framework or ASP.NET request/response types.

## Responsibilities

- Master Password validation, Argon2id KEK derivation, AES-GCM DEK wrapping, and legacy PBKDF2 compatibility
- AES-256-GCM encryption/decryption and encrypted local-vault persistence
- Vault sign-in TOTP generation/verification and legacy Windows-DPAPI trusted-unlock token compatibility
- Master-Password-authorized vault durations and fixed five-hour login deadlines
- Encrypted website TOTP configuration, strict Base32/otpauth parsing, and local current-code generation for desktop and native browser requests
- Vault item validation, CRUD, recovery-code parsing, and sensitive-action verification
- Credential groups, tags, favorites, and migration of legacy folders to groups
- Autofill metadata/secret projections with lock/session checks and null username normalization; origin policy and browser consent belong to Presentation and the worker
- Encrypted, versioned backup creation, safe authenticated inspection, import planning, and atomic new-machine recovery
- Cryptographically secure password/passphrase generation and strength estimates
- Bounded common-format CSV parsing and duplicate-aware import planning
- Password hash implementations, inspection, registry metadata, and constant-time comparisons
- Password lifecycle metadata and local, secret-free weak/reused/old Security Check analysis

## Storage and sign-in contract

For a new v4 config, `.config` contains Master and Recovery key slots wrapping the same random DEK, a credential revision, and the DEK-encrypted Authenticator secret. Vault ciphertext retains the v3 context. Recovery keys are 32 random bytes encoded as eight hex groups with an RK1 prefix. Only the AES-GCM wrapper is persisted. Trusted tokens bind to the credential revision as part of the configuration fingerprint. TOTP remains an application gate rather than an independent encryption key.

Current desktop sign-in requires both the Master Password and Authenticator code. Re-unlock during the fixed five-hour login requires only the Master Password. Sensitive actions use the active unlocked session without requesting another code. Legacy Authenticator-only token methods remain in Core for compatibility and are not used by the current desktop sign-in.

Backup restoration accepts only uninitialized storage and requires Recovery Key confirmation. RecoveryKeyResetRequest is separate: it validates the current Recovery Key and vault, new password, saved replacement key, and new Authenticator OTP before a verified paired commit. It preserves ciphertext and clears sessions/tokens. SaveRecoveryKey requires Master Password and a verified login; it completes v3 enrollment without rewriting ciphertext and migrates legacy payloads once. Failed or cancelled enrollment leaves storage unchanged and denies workspace access. Snapshots retain their original credentials and revisions.

File names and Hidden/System attributes are obfuscation only. The security boundary is the Master Password-derived KEK, wrapped random DEK, authenticated encryption, DPAPI scope, and the Windows user account.

## Change rules

- Do not persist or log raw passwords, recovery codes, Master Passwords, TOTP secrets, or unprotected vault keys.
- New `Password`-type credentials may contain a password, TOTP, recovery codes, or a combination. Recovery-code lists require at least two valid unique codes. Legacy `RecoveryCodes`-type entries cannot contain a password, website TOTP configuration, or password-history metadata. Preserve these rules on add, update, import, and export.
- Treat backups as untrusted input: retain schema, size, depth, field-length, version, KDF, and authentication checks before mutating the vault.
- Keep inspection results metadata-only; never return decrypted backup payloads to a UI.
- `GetAutofillCredentials` returns active entries with a password or website TOTP and their real URLs, including hidden URLs. Every secret/code retrieval rechecks item availability and the matched stored URL. Normalize imported null usernames to `""` in the projections. Discovery transports only ID/title/username and password/TOTP capability flags; selected password responses contain username/password, and TOTP responses contain only the code and its time window. URL-bearing Core models stay inside the application.
- Keep UI and API layers thin. They may choose dialogs, HTTP status codes, and DTOs, but Core owns cryptography and domain validation.
- Maintain backward compatibility for vault items that predate recovery codes: their missing `Type` defaults to `Password`.
- New optional item metadata must keep safe defaults so older encrypted vault and backup payloads continue to deserialize.
- UpdatedAt is not password age. PasswordChangedAt is nullable for legacy payloads and is resolved in Core from valid history, UpdatedAt, then CreatedAt; future dates are ignored. The 365-day Security Check threshold is inclusive and findings never contain a secret.
- Older configs migrate to a one-minute vault duration. Supported durations are 1, 2, 5, 10, 30, 60, 120, and 300 minutes. Neither activity nor unlocking extends the login deadline; duration edits apply on the next unlock.
