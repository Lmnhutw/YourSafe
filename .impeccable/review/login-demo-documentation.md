# YourSafe login demo documentation

2026-10-13. Documentation verdict: **incumbent website identity preserved**.

This is an ordinary surface extension. No new brand decision, global token change or DESIGN.md/sidecar refresh was needed. Existing drift between the incumbent artifacts and newer landing content remains unchanged by request.

## Evidence checked

- Read PRODUCT.md, DESIGN.md and `.impeccable/design.json`, the Impeccable document reference, the demo HTML/JavaScript and shared CSS, landing CTA/image markup, website README, asset provenance and capture-inbox README.
- Inspected `demo-1440.png`, `demo-390.png`, `demo-success.png`, `desktop.png`, `mobile.png` and `08-extension-login-context.png`.
- Read `website/verify.cjs`'s demo assertions and the recorded independent finish review. The root agent supplied passing browser results; this documentation pass did not rerun them.

## Comparison with the incumbent system

The demo uses the same cool paper, cobalt, ink/muted text, locally hosted Be Vietnam Pro and packaged eye logo. Shared action styles and 3px visible focus remain intact. Generous desktop gutters and the existing 680px stacking breakpoint continue the manual's layout. The white form panel uses the incumbent larger-corner surface vocabulary; inputs retain small corners and clear labels. Sequential guide numbers describe real steps.

The desktop capture places instructions left of the form. The mobile capture places the form before the guide, with a wrapping page address and desktop-browser note. Success replaces only the form content and pairs its cobalt icon with explicit text. The landing screenshots show the new illustration in the extension workflow and the demo link within the existing cobalt closing section. Source confirms same-tab navigation.

## Confirmed behavior and qualification

Source confirms explicit local submission of arbitrary/empty values, cleared values on submit/retry/page exit, success announcement and heading focus, and username focus on retry. Fill does not submit. CSP blocks connections/form navigation; fields are disabled without the handler. Copy-address feedback covers success and failure.

Root browser verification passed at 1440/768/390/320 widths, including no overflow, root/project paths, actual extension `fields.ts` discovery/fill, Enter submission, arbitrary/empty values, reset/focus, no credential requests or storage, clipboard failure, and no-JavaScript/script-failure guards. Both demo files are included in the Pages artifact workflow.

Image08 contains the actual demo and popup source rendered with synthetic responses, a matching illustrated origin and fake masked credentials. Its image label, landing caption and provenance disclose simulation. It is UI evidence, not live Native Messaging proof.

Live Desktop/Native Messaging integration was not tested because no YourSafe process was running. This pass did not run browser/tests, measure computed contrast, or publish the website. Documentation writes are limited to this report and `surface-briefs/yoursafe-login-demo.md`; code, existing design artifacts and backend files were untouched.
