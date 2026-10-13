# YourSafe login demo

Confirmed extension of the existing website, documented 2026-10-13. Mode: Operate.

Target: `website/demo-login.html`, `website/demo-login.js`, shared `website/styles.css`, the closing CTA and extension illustration in `website/index.html`.

Audience: Vietnamese-speaking visitors who want to try explicit extension password fill on a disposable login form.

Success: fill or manually enter any values, explicitly submit, see local demo success, and retry with cleared fields. No real account or authentication service is required.

## Direction contract

THESIS: A short practical exercise within the existing software field manual.

OWN-WORLD: Preserve DESIGN.md's cool paper, cobalt actions, ink text, self-hosted Be Vietnam Pro and unchanged packaged eye logo. Reuse existing buttons, links, gutters and visible focus. The white form panel and small-corner fields are surface components, not a new token system or brand direction.

STORY: Open YourSafe and enable browser integration; save a disposable credential for this page; select Fill, approve in YourSafe, then explicitly submit the demo. Keep the privacy notice beside the form and the installation-help link beside the instructions.

FIRST VIEWPORT: Desktop places concise introduction/instructions left and the form right. At the existing 680px breakpoint, the introduction is followed by the form, then the guide. Mobile includes a note that extension testing uses a desktop browser. The page address wraps and is selectable; copying is an optional secure-context enhancement with success/failure feedback.

FORM: Same-tab `demo-login.html` entry from the closing CTA. Native labeled username/password fields accept arbitrary or empty values. Fill only populates fields. Submit clears values and reveals explicit local success, announces it and focuses its heading. Retry clears again and focuses username. Leaving the page clears values. No credential requests, logs, cookies or storage writes. Inputs remain disabled until the submit handler is attached; CSP blocks connection requests and form navigation.

PROOF: Image08 shows the actual demo page beside actual popup source, with labeled synthetic transport responses and fake masked credentials. Preserve its caption, provenance and full-size access. It demonstrates UI context; it does not establish a live Desktop/Native Messaging connection.

FINISH: Source and supplied screenshots match the confirmed direction. Root browser verification passed at 1440/768/390/320 widths, including the extension's actual field discovery/fill code and privacy/reset guards. Live Desktop integration remains untested because no app was running. Existing DESIGN.md/sidecar drift is outside this change; preserve both files.
