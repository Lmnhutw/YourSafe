# YourSafe browser autofill

Chrome 127+ and Edge integration for the running YourSafe desktop vault. TypeScript lives only here; the .NET solution contains a C# console NativeHost. Node/npm are build tools and are never shipped.

## Development

From the repository root, with .NET 10 and Node/npm installed:

```powershell
pwsh .\scripts\Build-BrowserAutofill.ps1
pwsh .\scripts\Register-BrowserAutofill.ps1
pwsh .\scripts\Start-BrowserAutofill.ps1
```

Open `chrome://extensions` or `edge://extensions`, enable Developer mode, choose **Load unpacked**, and select `browser-extension/dist/development`. The checked-in public development key fixes the extension ID to `cmfnnjellknkpnbooenlahijcmalbifg`; it contains no private key. Pin YourSafe in the browser toolbar.

Save a password item with a complete HTTPS URL in the desktop. Unlock the desktop vault, visit the matching site, and click **Fill with YourSafe** or its toolbar icon. The trusted popup shows the canonical origin and accounts. Clicking an account is the final consent; the page receives the username/password only then. The extension does not submit the form. Focus a specific username/password field first when multiple forms are ambiguous.

When locked, use **Open YourSafe**, unlock on the desktop, then refresh and choose the account again. YourSafe must already be running; the host never launches a second desktop or unlocks a vault.

```powershell
pwsh .\scripts\Register-BrowserAutofill.ps1 -Unregister
```

Unregister removes only development HKCU registrations whose value still points to this checkout. Development and production use different host names and pipe names. Debug NativeHost and desktop executables must be adjacent and built with the same configuration; peers reject different executable paths.

## Responsibilities and protocol

| Component | Responsibility |
|---|---|
| Content script | Top-level form detection, hint, native input setters and input/change events. No discovery or secret request authority. |
| Worker | Browser-derived tab/frame/document/URL context, short-lived metadata, trusted-popup authorization, final target checks and exact-document delivery. |
| Popup | Shows origin/accounts using text nodes; account selection supplies one-use consent. |
| NativeHost | Native Messaging framing/validation and named-pipe client. No Core, Presentation or WinUI reference, vault key, lock state or retries. |
| WinUI pipe server | Current-user pipe, Windows peer verification, DTO-to-application calls, last lifecycle check and window activation on the dispatcher. |
| Presentation | HTTPS exact-origin matching and application entrypoints through AppFlowCoordinator/RunVaultAsync. |
| Core | Active-password projection with real URLs, credential lookup, session/lock checks and minimal secret projection. Existing reveal/edit TOTP policy is preserved. |

V1 envelopes have `version: 1`, UUID `requestId`, allowlisted `action`, and typed `payload`. Requests allow only `ping`, `getStatus`, `findCredentials`, `getCredentialSecret`, and `showApp`. Both credential actions require a canonical HTTPS origin; retrieval additionally requires a nonempty credential UUID. Responses echo version/request ID and contain either `ok: true, result` or `ok: false, error`. Discovery results contain `{ credentials, truncated }`; complete account entries are included up to the 64 KiB envelope budget. When truncated, the popup directs users to the desktop for the full list. Rebuild the desktop, NativeHost and extension together after contract changes.

Native Messaging and named pipes use UTF-8 JSON preceded by a 4-byte little-endian length, capped at 64 KiB before allocating the body. Partial/coalesced reads, malformed UTF-8/JSON, unknown fields/actions, duplicate JSON properties, oversized frames, timeout and disconnect fail closed. Secret requests are never automatically retried. Discovery returns only ID/title/username, including for hidden-URL items. Matching requires identical normalized host and effective port; paths do not matter and subdomains are distinct. Missing schemes, HTTP, userinfo and ambiguous numeric/escaped hosts are rejected.

The pipe name includes a SID hash, Windows session and dev/prod channel. Both endpoints use `CurrentUserOnly` and verify the OS-reported peer PID, Windows session and exact adjacent executable path. No JSON PID/path is trusted. These controls reduce simple impersonation; they do not establish protection against malware running as the same user. The desktop trusts context checked by the official worker.

No secret persistence, payload logging, clipboard fill, HTTP listener or network service is added. Serialized transport buffers are cleared after use; managed strings and JavaScript strings are not guaranteed to be zeroized. Once a password reaches a website, locking the vault cannot revoke it. Interaction state expires after a minute and is invalidated by navigation, SPA/hash changes, active tab/window changes, document replacement and failure.

## Verification

```powershell
dotnet test .\tests\PasswordTool.Core.Tests
dotnet test .\tests\PasswordTool.Presentation.Tests
pwsh .\scripts\Test-ReleasePipeline.ps1
pwsh .\scripts\Test-ReleaseQualification.ps1
pwsh .\scripts\Test-BrowserIntegration.ps1
cd browser-extension
npm.cmd run typecheck
npm.cmd test
npm.cmd run build
npm.cmd run build:production
npm.cmd run test:browser:build
```

`test/contract-fixtures.json` is consumed by C# and TS. C# tests cover hidden URLs, item filtering, exact origin, lock/logout/expiry while pending, framing and OS peer checks. TS tests cover contract/result validation, popup versus content authorization, one-use selection, document/navigation changes, and failure without retries.

Open `test/browser-smoke.html` in a browser after building the smoke bundle; it must report PASS. Alternatively use an already-installed Playwright and browser:

```powershell
$env:YOURSAFE_PLAYWRIGHT_PACKAGE = 'absolute-path-to-installed-playwright-package'
$env:YOURSAFE_BROWSER_EXECUTABLE = 'absolute-path-to-installed-chromium.exe'
node test/browser-smoke.mjs
```

No browser or automation dependency is downloaded by this smoke runner. This isolated DOM test is separate from live extension/desktop E2E.

Complete the following manual checklist separately on Chrome and Edge, using a disposable vault and synthetic credentials:

- Happy path for a normal, password-only and dynamically rendered form; input/change fire and submit does not.
- Hidden URL matches; HTTP, a different subdomain/port and malformed/missing-scheme URLs do not.
- Multiple login forms require focus; hidden/disabled/readonly/new-password fields never receive secrets.
- The hint opens the trusted popup; if `action.openPopup()` fails, toolbar fallback works. Qualify Edge explicitly.
- Navigate, replace the document, switch tabs/windows, change SPA routes or hashes during discovery/retrieval: no fill.
- Lock/logout/expire after discovery or while retrieval waits: no secret is delivered; after unlock select again.
- Disconnect NativeHost, close desktop, or send malformed/oversized frames: bounded failure without logging secrets or retrying retrieval.
- Clean install, upgrade, uninstall and portable ZIP: registrations target the installed manifests, unregister affects only owned values, portable launch never edits the registry, vault storage survives unchanged.

## Distribution

Build the store extension with `pwsh scripts/Build-BrowserAutofill.ps1 -Production`; distribute `dist/production` through the browser stores. Release publishing requires the actual Chrome and Edge store extension IDs:

```powershell
pwsh .\scripts\Publish-WindowsRelease.ps1 -Version 1.0.0 -ChromeExtensionId $chromeStoreId -EdgeExtensionId $edgeStoreId -SigningMode Required
```

Missing, malformed, repeated-letter placeholders and the known development ID are rejected. The self-contained `win-x64` NativeHost is published beside `YourSafe.exe`. Per-browser manifests contain only the corresponding store origin and a relative NativeHost path. The per-user installer registers HKCU32 entries for Chrome/Edge; its uninstall cleanup removes a value only if it still points to that installation. The portable ZIP never auto-registers. Signing and qualification verify both executables plus the installer when present. Signing credentials and Inno Setup remain external controlled-release inputs.

V1 defers iframe/shadow DOM support, multi-step login, desktop hotkeys, UI Automation, Auto-Type and clipboard autofill. No additional Contracts project, runtime Node package or frontend framework is needed.

Primary API references: [Chrome action/openPopup](https://developer.chrome.com/docs/extensions/reference/api/action), [Native Messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging), [document targeting](https://developer.chrome.com/docs/extensions/reference/api/tabs), [Windows pipe peer PID](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid).
