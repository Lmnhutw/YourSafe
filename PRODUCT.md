# Product brief

## Product

PasswordTool is a local Windows vault for passwords, website TOTP codes, and recovery codes, with an accompanying password-hash utility.

## Users and purpose

For individuals who want to keep personal credentials, website TOTP secrets, and recovery codes on their own Windows device, without cloud sync, a hosted account, or an external database. The core workflow is to unlock, find, copy, review, and safely update a credential with minimal friction.

## Experience principles

- **Calm and minimal:** prioritize the vault task over decorative dashboards or security theatrics.
- **Explicit security state:** explain the required Master Password + TOTP sign-in and when a backup passphrase is required.
- **Lifecycle-aware locking:** lock the vault after one minute of inactivity by default, on Windows lock/disconnect/suspend/resume, and after a five-hour sign-in session.
- **Low-friction actions:** do not ask for TOTP again for ordinary vault actions during the active sign-in session.
- **Recoverable without an account:** make encrypted external backup creation, verification, and first-launch recovery understandable without implying that local snapshots protect against disk loss.
- **Practical daily use:** make search, password generation, short-lived copy actions, and common CSV migration easy without adding an online service.
- **Actionable local review:** Security Check findings must remain secret-free, distinguish general item edits from password-age changes, and route remediation through the existing protected editor.
- **Familiar desktop behavior:** preserve keyboard navigation, visible focus, and standard Windows control patterns.
- **No colour-only meaning:** pair status colour with concise visible text or an icon.

## Security posture in the interface

The interface should help users understand that the Master Password cannot be recovered, that opening the vault requires Master Password + Google Authenticator, and that backup passphrases are separate from the Master Password. New-machine recovery authenticates an encrypted backup, then creates a new Master Password and a new PasswordTool Authenticator. Internal snapshots stay on the same disk and must not be presented as disaster-recovery protection. Avoid claims that clipboard clearing, hidden files, or TOTP make data safe from malware in an unlocked Windows session.

Security timeout changes require the current Master Password. Inactivity locking defaults to one minute and may be configured from 1–120 minutes. The sign-in session has a fixed five-hour maximum. Do not offer a “never lock” option or a separate sensitive-action TOTP timeout.

## Intentional constraints

- No cloud sync, online account, reset flow, or backdoor.
- No server, telemetry, mobile client, passkeys, sharing, or attachments. The local browser extension supports explicit password fill and viewing/copying credential TOTP while the desktop vault is unlocked.
- Copy actions are explicit. Passwords, recovery codes, and other sensitive copies clear unchanged clipboard values after 30 seconds; copied credential TOTP remains until replaced. This is not protection from a compromised Windows session.
- Hidden URL and Notes fields remain encrypted and should be shown as `Hidden` in lists, not silently omitted.
