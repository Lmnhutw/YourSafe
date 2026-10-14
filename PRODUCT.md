# Product brief

## Product

YourSafe is a local Windows vault for passwords, website TOTP codes, and account recovery codes, with an accompanying password-hash utility. A credential can combine a password, website TOTP, and account recovery codes.

## Users and purpose

For individuals who want to keep personal credentials, website TOTP secrets, and recovery codes on their own Windows device, without cloud sync, a hosted account, or an external database. The core workflow is to unlock, find, copy, review, and safely update a credential with minimal friction.

## Experience principles

- **Calm and minimal:** prioritize the vault task over decorative dashboards or security theatrics.
- **Explicit security state:** explain the required Master Password + TOTP sign-in and when a backup passphrase is required.
- **Lifecycle-aware locking:** lock the vault one minute after unlock by default, regardless of activity. Table locking keeps the sign-in session and cached list metadata; Windows lock/disconnect/suspend/resume clears the workspace. Sign-in expires after five hours.
- **Low-friction actions:** do not ask for TOTP again for ordinary vault actions during the active sign-in session.
- **Recoverable without an account:** make encrypted external backup creation, verification, and first-launch recovery understandable without implying that local snapshots protect against disk loss.
- **Practical daily use:** make search, password generation, short-lived copy actions, and common CSV migration easy without adding an online service.
- **Actionable local review:** Security Check findings must remain secret-free, distinguish general item edits from password-age changes, and route remediation through the existing protected editor.
- **Familiar desktop behavior:** preserve keyboard navigation, visible focus, and standard Windows control patterns.
- **No colour-only meaning:** pair status colour with concise visible text or an icon.

## Security posture in the interface

The interface should help users understand that the Master Password cannot be retrieved, but a saved Recovery Key can reset both the Master Password and the YourSafe sign-in Authenticator. Initial sign-in requires Master Password + a compatible Authenticator's TOTP code; reopening the vault within that session requires the Master Password. TOTP authorizes sign-in and is not an input to vault encryption. Backup passphrases are separate from the Master Password. New-machine recovery authenticates an encrypted entry backup, then creates a new Master Password, sign-in Authenticator, and Recovery Key. External backups preserve entry data, not group names, colors, ordering, or appearance settings. Internal snapshots stay on the same disk and must not be presented as disaster-recovery protection. Avoid claims that clipboard clearing, hidden files, or TOTP make data safe from malware in an unlocked Windows session.

Vault-duration changes require the current Master Password and apply after restarting the app. Supported durations from unlock are 1, 2, 5, 10, 30, 60, 120, or 300 minutes, capped by the fixed five-hour sign-in deadline. Activity extends neither deadline. After a table lock, a desktop action can request the Master Password once and relock immediately; explicit **Unlock vault** opens it for the selected duration. Neither path requests TOTP again during an active sign-in session. Browser requests require an explicitly unlocked vault. Do not offer a “never lock” option or a separate sensitive-action TOTP timeout.

## Intentional constraints

- No cloud sync, online account, server-assisted password reset, or support bypass. Recovery requires the saved Recovery Key or an encrypted backup with its separate passphrase.
- No server, telemetry, mobile client, passkeys, sharing, or attachments. The local browser extension supports explicit password fill and viewing/copying credential TOTP while the desktop vault is unlocked.
- Copy actions are explicit. Passwords, website TOTP codes, recovery codes, and other sensitive copies use best-effort cleanup after 30 seconds and on lock while YourSafe still owns the clipboard value. Cleanup preserves replacement content and cannot erase copies already read or pasted elsewhere.
- Hidden URL and Notes fields remain encrypted and should be shown as `Hidden` in lists, not silently omitted.
