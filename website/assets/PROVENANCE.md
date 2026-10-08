# Asset sources

## Landing product images

- `homepage/Main-vault-YourSafe.png`: existing repository screenshot supplied for the website. Actual vault with synthetic Test account entries, example.test usernames and masked passwords; 1384×712.
- `homepage/desktop-vault.png`, `homepage/desktop-editor.png`: unchanged copies of `captures-inbox/01-desktop-vault.png` and `02-desktop-editor.png`, freshly captured from the separately marked YourSafe Debug process by `winapp ui screenshot`. Full native window captures, 1370×953, from a 1384×960 window. Synthetic landing vault includes YouTube, Google, GitHub, Facebook, Netflix, Spotify, Microsoft and Discord; real service URLs, fake example.test usernames/passwords/recovery codes/TOTP. Editor shows GitHub with masked password and explicitly marked DEMO recovery codes. Reproduce by seeding `Create_opt_in_disposable_landing_vault` with `PASSWORDTOOL_LANDING_TEST_DIRECTORY`, launching Debug with the same `PASSWORDTOOL_UI_TEST_DIRECTORY`, then running `scripts/Capture-WebsiteDesktop.ps1 -ProcessId <PID> -TestDirectory <directory>`. The script checks the Debug marker before any interaction. The original `Main-vault-YourSafe.png` is retained, no longer displayed.
- `captures-inbox/04-password-generator.png`, `05-security-check.png`, `06-encrypted-backup.png`: full native Debug captures from the same synthetic vault. Generator output is unused; Security Check intentionally finds Netflix's weak demo password; backup password fields are blank. Available as evidence/replacement candidates, not displayed in the landing layout.
- `homepage/extension-accounts.png`, `homepage/extension-code.png`: captured from actual extension popup HTML, CSS and compiled `browser-extension/src/popup.ts` in Microsoft Edge. Runtime transport uses synthetic GitHub discovery/account/TOTP responses; no real vault, secret or external website was accessed. Sizes 720×1150 and 720×1098 (360 CSS px at 2× scale). Copies are also in captures-inbox as `03` and `07`. These show actual UI rendering, not live Native Messaging/autofill qualification. Captions identify demo data and manual development installation. Reproduce with `scripts/Capture-WebsiteExtension.cjs` using existing Playwright and extension esbuild dependencies.
- Storage diagram: inline SVG authored for this page, showing the encrypted local Windows vault and manual external backup with a separate password. No cloud or automatic USB sync is depicted.
- `captures-inbox/`: replacement screenshot inbox for the user, with requirements in its README. Keep real credentials and recovery keys out of this folder.

Product images retain their full native aspect ratio. CSS rounds each image's own edges; no square crop or cover fitting is used. Full-size image links are available.

## Existing assets

- `unlock-migration.png`: pixel-identical copy of repository `docs/screenshots/winui-migration-unlock.png` with origin recorded in PNG metadata. Historical WinUI migration capture; blank password input, no credential values. Caption explicitly distinguishes it from current release UI.
- `BeVietnamPro-Regular.ttf`, `BeVietnamPro-Bold.ttf`: Google Fonts, https://github.com/google/fonts/tree/main/ofl/bevietnampro. Licensed under SIL Open Font License 1.1; license is included in `OFL.txt`. These local fonts make no external requests.
- `app-logo.png`: pixel-identical copy of `src/PasswordTool.WinUI/Assets/Square44x44Logo.scale-200.png`, the app's actual packaged 88x88 logo, with origin recorded in PNG metadata. Used in header, footer and favicon at the user's request.
