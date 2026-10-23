Purpose: Add accurate password-age metadata and an actionable local Security Check to the existing offline PasswordTool desktop vault.

Assumptions: Keep .NET 10, WinForms, the local/offline architecture, English UI, current encryption and authentication boundaries, the existing 10-entry password history limit, and compatibility with existing vault and backup files. Phases 1 and 2 are complete. A password is considered old after 365 days, matching current behavior.

Final Prompt:

You are implementing **Phase 3 of 5: accurate password lifecycle metadata and an actionable Security Check** for the existing PasswordTool repository.

## Objective

Track when each current password actually became active, independently of ordinary item edits, and turn the existing local Security Check into a workflow from finding to remediation.

Implement **only Phase 3**. Do not begin installer, signing, update, publishing, or release-qualification work from Phases 4–5.

## Existing context

The solution targets .NET 10 and contains:

- `PasswordTool.Core`: encrypted vault state, item validation, password history, CSV import, backup/recovery, password-strength analysis, and security findings.
- `PasswordTool.WinForms`: the vault UI, item editor, password-history viewer, and local Security Check dialog.
- `PasswordTool.Api`: a local-development password-hashing API that must not expose vault operations.
- `PasswordTool.Core.Tests`: Core behavior and security regression tests.

Inspect the current source before changing it and preserve existing working-tree changes.

Today, `VaultItem.UpdatedAt` is general item metadata: it changes for note, tag, favorite, Trash, restore, and other edits. The current old-password finding incorrectly uses that timestamp. Password history already stores previous passwords with a `ChangedAt` value and is capped at 10 entries. The Security Check already detects weak, exactly reused, and one-year-old passwords locally, but its WinForms dialog is read-only.

## Scope

### 1. Accurate current-password lifecycle metadata

Add a dedicated nullable/version-tolerant timestamp, named `PasswordChangedAt` unless the current architecture strongly requires an equivalent name, to `VaultItem`.

Required semantics:

- New password items set `PasswordChangedAt` to the same captured UTC instant as `CreatedAt` and `UpdatedAt`.
- CSV-imported password items set it to the import instant.
- Changing a password to a different ordinal string sets `PasswordChangedAt` to the change instant.
- That same captured instant must be used for the previous password's `PasswordHistoryEntry.ChangedAt`; do not call the clock separately for the two values.
- Editing title, username, URL, notes, folder, favorite state, hidden-field flags, or website TOTP must not change `PasswordChangedAt`.
- Moving an item to Trash, restoring it, backup-health changes, Master Password rotation, KDF upgrades, Authenticator rotation, and other vault/config rewrites must not change it.
- Converting a recovery-code item into a password item initializes it to the conversion instant.
- Converting a password item into a recovery-code item clears password-only state, including password history and `PasswordChangedAt`, so obsolete secrets and metadata do not remain attached to that item type.
- Preserve the existing password-history limit of 10 and existing sensitive authorization required to view history or edit secrets.

Do not overload `UpdatedAt`; it must remain the general last-modified timestamp.

### 2. Legacy vault and backup compatibility

Older encrypted vaults and backups do not contain `PasswordChangedAt`. They must continue to load, inspect, import, recover, and open without a manual migration step.

Use this deterministic fallback for a password item whose dedicated timestamp is absent:

1. If password history has entries, use the greatest valid `ChangedAt`, because it represents when the current password replaced the previous one.
2. Otherwise use `UpdatedAt` as the best available legacy approximation.
3. If the legacy timestamp is unusable, fall back to `CreatedAt` rather than manufacturing a future date.

Centralize this normalization or effective-date rule in Core so UI code and security analysis cannot diverge. Do not rewrite or save the vault merely because it was opened. The next normal successful vault mutation may persist normalized metadata.

Ensure cloning, validation, encrypted persistence, external backup creation, backup inspection/import, full recovery, and snapshot/rollback paths preserve the field. Recovery-code items must not acquire password lifecycle metadata.

### 3. Correct local Security Check analysis

Keep Security Check fully local and continue requiring the existing sensitive-action authorization before scanning passwords.

Requirements:

- Determine one-year-old passwords from the effective `PasswordChangedAt`, not `UpdatedAt`.
- Keep the 365-day threshold unless the existing source defines it more precisely.
- Continue scanning only active password items; exclude Trash, recovery-code items, password history values, website TOTP secrets, and recovery codes.
- Keep reused-password comparison exact and ordinal. Do not normalize, log, hash for telemetry, or expose the shared password in a finding.
- Reuse the existing password-strength estimator and current weak-password thresholds unless a failing regression proves they are internally inconsistent.
- A finding may identify an item by ID and title and include safe explanatory metadata, but it must never contain the password, previous passwords, TOTP secrets, recovery codes, or derived secret material.
- Give finding types stable, user-friendly display text and a concrete recommendation. Avoid presenting raw enum names when a display label is clearer.
- Produce deterministic ordering: severity/type first and then item title, or another explicitly tested order that remains stable.

Do not add breach lookup, Have I Been Pwned integration, network calls, cloud services, telemetry, or a new security-scoring framework.

### 4. Actionable WinForms Security Check

Extend the existing dialog rather than replacing the desktop architecture.

The dialog must:

- show a useful empty state when no findings exist;
- show total findings and affected-item count without implying that one item equals one finding;
- show item, finding label, recommendation, and password age/change date when applicable;
- preserve read-only secret handling: no password values may be bound to the grid or copied into display models;
- provide an **Edit selected item** action, enabled only when a finding is selected;
- support double-clicking a finding as the same edit action;
- return only the selected item ID to `VaultForm`; the dialog must not receive `VaultService`, decrypt secrets, or reproduce authorization logic;
- let `VaultForm` reuse its existing protected item-edit workflow for the selected item;
- after an edit completes, rerun the local scan and refresh the dialog so resolved findings disappear and remaining findings are current;
- handle an item that was deleted or changed while transitioning to edit without crashing or displaying stale secrets;
- retain standard WinForms keyboard navigation and provide an accessible name or useful text for controls.

Refactor the existing edit flow only as much as needed to edit a specified item ID from either the main grid or Security Check. Avoid duplicating the editor, TOTP prompt, or save logic.

### 5. Core ownership and time consistency

- Lifecycle rules, legacy fallback, security-analysis rules, and secret-free finding construction belong in `PasswordTool.Core`.
- WinForms owns presentation and navigation only.
- Capture `utcNow()` once per logical mutation or scan where boundary consistency matters.
- Compare timestamps in UTC and define the threshold boundary in tests; a password exactly 365 days old should have one unambiguous result.
- Reject or safely normalize impossible future lifecycle dates so clock anomalies cannot indefinitely hide an old password. Keep the policy small and documented.
- Do not introduce repository, mediator, event-bus, migration-framework, or general workflow abstractions for this phase.

## Constraints

- Keep WinForms and .NET 10.
- Keep the application offline.
- Preserve the current encrypted local-file design, backup encryption, KDF behavior, TOTP algorithms, trusted-unlock-token behavior, inactivity locking, Windows lifecycle locking, and sensitive-action timeout behavior.
- Preserve current public behavior unless this prompt explicitly changes it.
- Do not expose vault items, lifecycle metadata, or Security Check through `PasswordTool.Api`.
- Do not add accounts, cloud sync, databases, browser integration, background services, telemetry, or new third-party packages.
- Do not display, log, serialize into findings, or include in exception messages any password, password-history value, TOTP secret, recovery code, encryption key, or backup passphrase.
- Keep changes focused and avoid unrelated refactors.
- Do not create a commit unless explicitly requested.

## Required tests

Add focused automated tests proving:

1. Adding a password item sets `CreatedAt`, `UpdatedAt`, and `PasswordChangedAt` from one logical timestamp.
2. Editing only non-password fields changes `UpdatedAt` but leaves `PasswordChangedAt` unchanged.
3. Changing the password updates `PasswordChangedAt`, creates one history entry at that exact same instant, and preserves the 10-entry cap.
4. Converting between password and recovery-code item types initializes or clears password-only lifecycle state correctly.
5. CSV import initializes `PasswordChangedAt`.
6. Clone, encrypted save/reopen, external backup round-trip, backup import, and full recovery preserve the timestamp.
7. A legacy item with history derives its effective date from the newest valid history timestamp; a legacy item without history falls back deterministically.
8. Old-password detection uses password age rather than general item `UpdatedAt`; editing notes cannot clear an old-password finding.
9. The exact 365-day boundary and future-date policy are deterministic.
10. Reused-password analysis is exact/ordinal and excludes deleted items, recovery-code items, and history values.
11. Findings and list-facing models never contain secret values.
12. Finding ordering and user-facing labels are stable.
13. Existing sensitive-action authorization, history, backup/recovery, Trash, CSV duplicate handling, and Phase 2 security timer tests remain green.

WinForms navigation and refresh behavior may be verified by source/build plus documented manual scenarios when an automated UI harness is not already present. Do not add a UI automation framework solely for this phase.

## Documentation

Update:

- root `README.md`;
- `PRODUCT.md`;
- `docs/architecture.md`;
- relevant Core and WinForms README files.

Document the difference between `UpdatedAt` and `PasswordChangedAt`, the legacy fallback, the 365-day rule, local-only weak/reused/old analysis, secret-free findings, and the edit-and-rescan workflow.

## Verification

Run in sequence:

```powershell
dotnet restore PasswordTool.slnx
dotnet test PasswordTool.slnx --no-restore
dotnet build PasswordTool.slnx --no-restore
dotnet format PasswordTool.slnx --verify-no-changes --no-restore
dotnet list src\PasswordTool.Api\PasswordTool.Api.csproj package --vulnerable --include-transitive
git diff --check
git status --short
```

If stale MSBuild workers cause the known coverage temp-file access denial, shut down the build servers or rerun test/build with `--disable-build-servers`, and report that variation exactly.

## Completion report

Return:

- concise summary of implemented behavior;
- important files changed;
- lifecycle migration/fallback decisions;
- security and compatibility guarantees;
- exact restore/test/build/format/package-scan results;
- manual Security Check UI scenarios that remain unverified;
- confirmation that only Phase 3 was implemented.
