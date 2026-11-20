# YourSafe dev log

## v0.1.9 — 2026-11-20

### Windows update checks

- **Settings → YourSafe updates** shows the installed version and provides a **Check for updates** button.
- Daily checks are off by default. When enabled, YourSafe checks while the app is open and saves the last attempt across restarts so it checks at most once every 24 hours.
- When GitHub has a newer published release with a Windows package, YourSafe shows a notice. **View release** opens the official release page so users can download and install it manually.
- YourSafe compares versions on the PC. The request fetches public release information without sending the installed version or vault data. The vault works offline.
- Updated both languages of the README, website FAQ, and Privacy Policy to explain optional update checks.

### Verification

- A versioned WinUI test build detected the published v0.1.8 release through both manual and scheduled checks. Verified that the daily preference survives a restart and that reopening within 24 hours does not repeat the check.
- All 198 .NET tests passed, including version comparison, unavailable releases, failed responses, and the saved daily schedule. Release qualification tests and website language checks also passed.

## v0.1.8 — 2026-11-20

### Vault and desktop

- Show the time remaining before the vault locks. Clearing the search field preserves the active filters.
- Show masked dots for saved passwords and 2FA codes, with empty cells for missing values. Add view and copy controls to 2FA and smooth the code countdown. Sensitive clipboard copies are cleared after 30 seconds or when the vault locks if YourSafe still owns the clipboard entry.
- Show the website origin eligible for autofill in the item editor, including when opening an existing item.
- Distinguish an empty Master Password from an incorrect one. Keep validation in the current form or dialog so users can retry there. Clarify group deletion and refine backup, import, and recovery flows.

### Browser integration

- **Open YourSafe** starts the Windows app when closed and restores its window when minimized. A fresh app session starts locked with browser integration off.
- Explain that the vault must be unlocked before enabling integration. Show distinct messages when a request is declined on the desktop or canceled after changing tabs. Filling passwords and viewing or copying verification codes continue to require desktop approval.

### Website and documentation

- Add English versions of the homepage, demo login, and privacy policy, plus a language selector and a portable ZIP download option.
- Refresh product screenshots with sample accounts. Update the autofill help, download troubleshooting, and both privacy policies in plain language.
- Add a Vietnamese README, update the English README and browser extension guide, and refresh the diagrams for vault, recovery, and browser flows.

### Verification

- Verified cold launch and minimized-window restore on Chrome with the real WinUI app, NativeHost, and a disposable test vault.
- Verified the editor’s saved URL status on WinUI and the bilingual website at widths from 320 to 1440 px.
