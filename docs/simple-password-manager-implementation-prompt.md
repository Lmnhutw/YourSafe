# Offline vault usability implementation prompt

Purpose: Add a focused set of everyday password-manager features to the existing local Windows vault.

Assumptions: Keep the current English WinForms UI, .NET 10 solution structure, encrypted local-file storage, Master Password flow, application TOTP protection, and backward compatibility with existing vaults and encrypted backups.

Final Prompt:

## Objective

Turn PasswordTool into a useful single-user offline Windows vault for passwords, website TOTP secrets, and recovery codes without trying to become a hosted Bitwarden or 1Password replacement.

## Context

- `PasswordTool.Core` owns cryptography, validation, vault persistence, import parsing, password generation, and TOTP behavior.
- `PasswordTool.WinForms` owns interaction, clipboard access, dialogs, filtering, and presentation.
- `PasswordTool.Api` is an unauthenticated local-development password-hashing API. It must not expose vault data or receive vault secrets.
- Existing `.config`, `.storage`, and encrypted backup files must continue to load.

## Scope

1. Add cryptographically secure password and passphrase generation with strength feedback.
2. Add local search, favorites, folders, and groups for vault items.
3. Allow password entries to store an optional website TOTP secret and generate its current six-digit code.
4. Import common plaintext CSV exports from browsers and password managers through a preview-and-confirm workflow.
5. Add intentional copy actions for username, password, and website TOTP code. Clear the Windows clipboard after 30 seconds only when it still contains the exact value copied by PasswordTool.
6. Reuse one successful application-TOTP verification for a five-minute sensitive-action session, clear it when the vault session closes, and provide an explicit Lock action.

## Constraints

- Preserve offline-only operation. Add no account, database, sync, network request, telemetry, browser extension, mobile client, passkey, sharing, or attachment feature.
- Use cryptographically secure randomness; never use `Random` for secrets.
- Website TOTP secrets, imported passwords, notes, folders, and groups must remain inside the encrypted vault payload and encrypted backups.
- Treat CSV content and all persisted JSON as untrusted. Bound file size, row count, field lengths, and parsing complexity.
- Do not log, place in exception messages, or display in vault list rows any password, recovery code, TOTP secret, or generated secret.
- Preserve the distinction between the application authenticator secret and each website's optional TOTP secret.
- Preserve the user's existing uncommitted changes and avoid unrelated refactors.

## What to change

- Extend the version-tolerant `VaultItem` model and all clone, validation, backup, and import paths.
- Add focused Core services/models for generation, strength estimation, website TOTP, and CSV import.
- Extend the WinForms editor and vault list with the new fields and actions.
- Keep hidden URL and hidden Notes behavior intact.
- Update product and architecture documentation to describe the delivered behavior and security limits.

## What not to change

- Do not expose vault endpoints from `PasswordTool.Api`.
- Do not replace the current vault cryptography or storage envelope in this feature set.
- Do not weaken Master Password, AES-GCM, DPAPI, backup-passphrase, or import validation rules.
- Do not silently overwrite existing items during import.

## Verification criteria

- Existing vault and backup tests continue to pass.
- New unit tests cover password/passphrase generation, website TOTP validation/code generation, CSV quoting/header mapping/limits, metadata persistence, backup round trips, and the five-minute sensitive session.
- A full solution build succeeds.
- `git diff --check` succeeds.
- Documentation and UI accurately distinguish application TOTP from website TOTP and plaintext CSV import from encrypted backup import.
