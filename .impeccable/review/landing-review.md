# YourSafe landing review — 2026-10-08

Implemented in the existing static HTML/CSS/JavaScript site. Preserves the packaged logo, local Be Vietnam Pro fonts, cobalt actions, paper surfaces and Vietnamese editorial hierarchy. Uses four unique product PNGs with demo data, native aspect ratios and direct image corner rounding.

## Review evidence

- Assessment A reviewed existing source/product/release contracts before implementation. Its main findings were excessive text, lack of product visuals, misleading recovery-code grouping, ambiguous signing guidance and absent development-extension availability guidance. These are addressed in the revised content, native disclosures and product visuals.
- Assessment B exercised the implementation in installed Microsoft Edge via Playwright. It detected incorrect editor height metadata, initial enhancement layout shift, swipe click suppression affecting keyboard activation and mobile extension caption overlap. All four were fixed and verified.
- Independence caveat: the agent-status listing unexpectedly surfaced Assessment A's completed text to B during standby. B's functional findings were independently measured, but this was not a fully blind assessment.
- The prescribed detector ran exactly once and returned `[]`: zero findings, rules, file locations and false positives. No second detector run.

## Validation

- `node website/verify.cjs`: PASS at 1440, 768, 390 and 320 px, with JavaScript and with JavaScript disabled.
- Tests cover all carousel controls and wrapping, Arrow/Home/End keys, hidden/inert inactive articles, stable focus, horizontal swipe thresholds/vertical/cancel rejection, emulated CDP touch, pointer click suppression with keyboard/tap allowance, no autoplay, native image metadata/ratios/radii, no image-caption overlap, menu, five FAQ disclosures, checksum clipboard success/failure, reduced motion, reveal, local assets/logo/font and project base path.
- Observed cumulative layout shifts at those widths: 0.0024, 0.0442, 0.0581 and 0.0583. Below 0.1; gallery geometry stays stable across user-controlled slide changes.
- `node --check website/script.js`, `node --check scripts/Capture-WebsiteExtension.cjs`, `git diff --check`: PASS.
- Desktop/mobile full-page and individual gallery screenshots are in this folder. Inspected visually after the fixes.

## Limits and delivery

No production deployment or Git commit. Preview remains at http://127.0.0.1:4173. Firefox/Safari, physical touch devices, assistive technology, real download/install and live Native Messaging/autofill were not tested in this website change. Extension images render actual popup source with synthetic transport responses, as stated in captions and asset provenance.

Computer Use refused to read YourSafe with “not approved to use yoursafe”, including the separately launched Debug demo. The opt-in disposable-vault seed test passed. The created Debug process was stopped without touching the user's existing app. A combined cleanup command was rejected with “blocked by policy”; its disposable demo directory was preserved and excluded from local Git status.

Replacement screenshots: `website/assets/captures-inbox/README.md`. Visible copy before/after: `website/CONTENT-CHANGES.md`.

## Follow-up: native screenshots and realistic demo data

At the user's request, switched to the repository's native UI test harness (`winapp ui screenshot`), without using Computer Use. Added a separate opt-in landing fixture so existing UI test names remain intact. Seed test passed with eight service records and fake example.test usernames. Real service URLs: YouTube, Google, GitHub, Facebook, Netflix, Spotify, Microsoft, Discord. Public synthetic TOTP and DEMO recovery codes only; Netflix intentionally has a weak fake password for the Security Check screenshot.

Captured and visually inspected five fresh full-window Debug PNGs: vault, GitHub editor, password generator, Security Check with a Netflix finding, and Backup. Window size 1384×960 yields native PNGs 1370×953, with no image editing/cropping. Vault and editor now replace old displayed captures, retaining image aspect ratio and direct corner rounding. Extension popup captures were refreshed to GitHub/fake usernames, still using synthetic transport and not proving live IPC. Seven PNGs are in captures-inbox. The real-browser login context image remains pending per the user's instruction to leave unavailable captures for later.
