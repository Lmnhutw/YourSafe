# YourSafe browser autofill and TOTP

Chrome 127+, Edge and Brave integration for the running YourSafe desktop vault. TypeScript lives only here; the .NET solution contains a C# console NativeHost. Node/npm are build tools and are never shipped.

## Development

From the repository root on Windows x64, with the .NET 10 SDK, PowerShell 7 and Node/npm installed:

```powershell
pwsh .\scripts\Build-BrowserAutofill.ps1
```

The build installs locked dev dependencies with `npm.cmd ci --ignore-scripts`, runs TypeScript checks/mock tests, builds the development extension and Debug desktop, publishes the self-contained NativeHost, and copies it beside the desktop executable. It does not register the extension or launch the app. When ready to enable integration:

```powershell
pwsh .\scripts\Register-BrowserAutofill.ps1
pwsh .\scripts\Start-BrowserAutofill.ps1
```

Open `chrome://extensions`, `edge://extensions` or `brave://extensions`, enable Developer mode, choose **Load unpacked**, and select `browser-extension/dist/development`. The checked-in public development key fixes the extension ID to `cmfnnjellknkpnbooenlahijcmalbifg`; it contains no private key. Pin YourSafe in the browser toolbar.

Registration writes the current user's 32-bit Native Messaging keys for Chrome, Edge and Brave. Brave uses `Software\BraveSoftware\Brave-Browser\NativeMessagingHosts`; production reuses the Chrome manifest and Chrome Web Store extension ID. The app-only `-test` prerelease installer omits all browser registration: use the development build and registration commands above for unpacked-extension testing.

Save a password or TOTP item with a complete HTTPS URL in the desktop. Unlock the desktop vault, visit the matching site, and click **Fill with YourSafe** or its toolbar icon. The trusted popup shows the canonical origin and accounts. Clicking **Fill** is the final password consent; the page receives the username/password only then. The extension does not submit the form. Focus a specific username/password field first when multiple forms are ambiguous.

When locked, use **Show YourSafe**, unlock on the desktop, then refresh and choose the account again. YourSafe must already be running; the host never launches a second desktop or unlocks a vault.

Items with **No URL** (a null, empty or whitespace-only stored URL) appear alongside accounts matching the current website on every supported page. Selecting one authorizes filling that page. Discovery and secret retrieval use the same rule and recheck the stored URL, so assigning a different website after discovery prevents filling the old page.

```powershell
pwsh .\scripts\Register-BrowserAutofill.ps1 -Unregister
```

Unregister removes only development HKCU registrations whose value still points to this checkout. Development and production use different host names and pipe names. Debug NativeHost and desktop executables must be adjacent and built with the same configuration; peers reject different executable paths.

## Responsibilities and protocol

| Component | Responsibility |
|---|---|
| Content script | Top-level form detection, hint, native input setters and input/change events. No discovery or secret request authority. |
| Worker | Browser-derived tab/frame/document/URL context, short-lived metadata, trusted-popup authorization, final target checks and exact-document delivery. |
| Popup | Shows origin/accounts with separate Fill and View TOTP actions, one selected live code panel, UTC countdown, and desktop copy confirmation. Its live port owns the interaction. |
| NativeHost | Native Messaging framing/validation and named-pipe client. No Core, Presentation or WinUI reference, vault key, lock state or retries. |
| WinUI pipe server | Current-user pipe, Windows peer verification, DTO-to-application calls, last lifecycle check and window activation on the dispatcher. |
| Presentation | HTTPS exact-origin matching and application entrypoints through AppFlowCoordinator/RunVaultAsync. |
| Core | Password/TOTP capability projection with real URLs, credential lookup, configuration-aware local TOTP generation, session/lock checks and minimal secret projection. Imported null usernames become empty strings in metadata and secrets. Desktop reveal/edit continues to use the existing active sign-in session. |

V2 envelopes have `version: 2`, UUID `requestId`, allowlisted `action`, and typed `payload`. Requests allow only `ping`, `getStatus`, `findCredentials`, `getCredentialSecret`, `getCredentialTotp`, `copyCredentialTotp`, and `showApp`. Credential actions require a canonical HTTPS origin; retrieval additionally requires a nonempty credential UUID. Responses echo version/request ID and contain either `ok: true, result` or `ok: false, error`. Discovery results contain `{ credentials, truncated }`; complete account entries are included up to the 64 KiB envelope budget. When truncated, the popup directs users to the desktop for the full list. Rebuild the desktop, NativeHost and extension together after contract changes.

Native Messaging and named pipes use UTF-8 JSON preceded by a 4-byte little-endian length, capped at 64 KiB before allocating the body. Partial/coalesced reads, malformed UTF-8/JSON, unknown fields/actions, duplicate JSON properties, oversized frames, timeout and disconnect fail closed. Secret requests are never automatically retried. Discovery returns only ID/title/username and boolean hasPassword/hasTotp capabilities, including for hidden-URL items. Matching requires identical normalized host and effective port; paths do not matter and subdomains are distinct. Missing schemes, HTTP, userinfo and ambiguous numeric/escaped hosts are rejected.

The pipe name includes a SID hash, Windows session and dev/prod channel. Both endpoints use `CurrentUserOnly` and verify the OS-reported peer PID, Windows session and exact adjacent executable path. No JSON PID/path is trusted. These controls reduce simple impersonation; they do not establish protection against malware running as the same user. The desktop trusts context checked by the official worker.

No secret persistence, payload logging, clipboard fill, HTTP listener or network service is added. Serialized transport buffers are cleared after use; managed strings and JavaScript strings are not guaranteed to be zeroized. Once a password reaches a website, locking the vault cannot revoke it. Interaction state expires 60 seconds after discovery begins, bounded by UTC and a monotonic deadline. Successful code refreshes never extend it. A live official popup port owns one native connection with serialized request IDs and ten-second timeouts; popup closure, native disconnect, interaction expiry and invalidation close the connection and discard outstanding responses. Target navigation, refresh, SPA/hash changes, document replacement, active-tab changes in the target window, focus changes to another window, and failure invalidate it. Background tab navigation/reload/closure leaves the target interaction intact. Selection checks interaction identity, expiry and target after asynchronous target lookups; discovery checks its generation after awaits before releasing metadata or creating consent.

Field revalidation retains the selected username and password. Inputs disabled through a fieldset are excluded via the browser's `:disabled` state. Username input/change events can alter the form, so the selected fields are checked again before writing the password. The pipe server waits 250 ms after handled failures, including pipe-constructor `IOException`, and shutdown cancels that wait.

## Verification codes

Discovery never retrieves codes. **View TOTP** replaces the account list with one selected account panel, grouped six- or eight-digit code, and countdown derived from desktop expiry. **Choose account** immediately clears the panel, cancels a pending read, and refreshes the list so another account can be selected. Focus moves into the live panel and returns to the selected account. Codes outside their UTC time window disappear while a refresh is pending. The worker permits at most one code read per second, checks the browser-derived origin/tab/window/document before and after asynchronous work, and accepts only an eligible ID returned by the current discovery.

**Copy** sends `copyCredentialTotp`; the desktop regenerates the current code and writes its numeric value through the existing sensitive clipboard service. Copied credential TOTP codes stay in the clipboard until replaced; timer and lock cleanup remain for copied passwords and recovery codes. The extension adds no clipboard permission and never receives TOTP configuration, otpauth URIs, or recovery codes. Content scripts and websites cannot retrieve or receive codes.

Both code actions require the actual desktop Unlocked state and current lifecycle, and recheck the credential's stored URL and capability on each request. After any failure the panel clears and stops refreshing; use an explicit Refresh/View action to resume. Unlock remains **Show YourSafe → unlock desktop → Refresh**.

## Verification

Unit and static checks do not require browser registration. Run from the repository root:

```powershell
dotnet test .\tests\PasswordTool.Core.Tests
dotnet test .\tests\PasswordTool.Presentation.Tests --filter "FullyQualifiedName!~Windows_pipe_peers"
pwsh .\scripts\Test-ReleasePipeline.ps1
pwsh .\scripts\Test-ReleaseQualification.ps1
pwsh .\scripts\Test-BrowserIntegration.ps1
cd browser-extension
npm.cmd ci --ignore-scripts
npm.cmd run typecheck
npm.cmd test
npm.cmd run build
npm.cmd run build:production
npm.cmd run test:browser:build
```

`test/contract-fixtures.json` is consumed by C# and TS. C# unit tests cover hidden URLs, item filtering, exact origin, lock/logout/expiry while pending, in-memory framing, discovery byte budgeting/truncation, and imported null usernames. TS mock tests cover version-2 request/result validation, popup versus content authorization, one-use password selection, TOTP-only accounts, selected-account authorization, stale target/expiry during awaits, code grouping/rotation, expiry while fetching, clock changes, native connection reuse/serialization, disconnect/timeout/malformed frames, closure/late responses, background-tab isolation, and failure without retries.

The commands above build the DOM smoke bundle but do not run a browser, register Native Messaging or perform live IPC/install checks. The separate OS pipe peer check opens real named pipes; run it explicitly from the repository root when qualifying IPC:

```powershell
dotnet test .\tests\PasswordTool.Presentation.Tests --filter "FullyQualifiedName~Windows_pipe_peers"
```

Open `test/browser-smoke.html` in a browser after building the smoke bundle; it must report PASS. Alternatively use an already-installed Playwright and browser:

```powershell
$env:YOURSAFE_PLAYWRIGHT_PACKAGE = 'absolute-path-to-installed-playwright-package'
$env:YOURSAFE_BROWSER_EXECUTABLE = 'absolute-path-to-installed-chromium.exe'
node test/browser-smoke.mjs
```

No browser or automation dependency is downloaded by this smoke runner. This isolated DOM test covers selected username/password preservation, fieldset-disabled and first-legend behavior, and password reclassification after username events. It is separate from live extension/desktop E2E. Building its bundle alone does not verify browser behavior.

Complete the following manual checklist separately on Chrome, Edge and Brave, using a disposable vault and synthetic credentials:

- Happy path for a normal, password-only and dynamically rendered form; input/change fire and submit does not.
- Discover password-only, password+TOTP and TOTP-only items; no code retrieval until View TOTP, and no Fill for a TOTP-only item.
- View/copy six- and eight-digit codes with leading zeros, non-default periods and algorithms; rotation hides expired codes, and copy at a boundary uses the current desktop code.
- Switch selected accounts, close the popup, expire its minute, lock desktop, disconnect the host, or change the stored URL: code clears, refreshing stops, and explicit consent is required again.
- Hidden URL matches; HTTP, a different subdomain/port and malformed/missing-scheme URLs do not.
- Multiple login forms require focus; hidden/disabled/readonly/new-password fields never receive secrets.
- The hint opens the trusted popup; if `action.openPopup()` fails, toolbar fallback works. Qualify Edge explicitly.
- Navigate, replace the document, switch tabs/windows, change SPA routes or hashes during discovery/retrieval: no fill.
- Reload, navigate or close a background tab during discovery/selection: the target account selection remains usable.
- Large matching-account lists: the popup lists complete entries within 64 KiB and clearly directs users to the desktop when truncated.
- Lock/logout/expire after discovery or while retrieval waits: no secret is delivered; after unlock select again.
- Disconnect NativeHost, close desktop, or send malformed/oversized frames: bounded failure without logging secrets or retrying retrieval.
- Start two Debug desktops with separate disposable test directories: an occupied pipe must not freeze the second desktop; closing it must cancel any retry wait.
- Clean install, upgrade, uninstall and portable ZIP: registrations target the installed manifests, unregister affects only owned values, portable launch never edits the registry, vault storage survives unchanged.

## Popup user stories

- Unlocked desktop: show the current origin and separate account title/username; selection fills once without submitting the form.
- Locked table: direct the user to Show YourSafe, Unlock Vault, then Refresh. Never request the Master Password in the extension.
- No matching account: explain how to add an account with this exact origin, then refresh.
- Desktop disconnected: explain how to start the matching desktop and check registration; keep recovery buttons available.
- Unsupported page or changed target: refuse filling and explain how to retry on the intended HTTPS page.
- Pending request: disable duplicate requests; success consumes account selection, and failure clears stale accounts.

`npm test` checks these popup states with a minimal DOM and synthetic responses, plus worker authorization races. This does not replace live Chrome/Edge/Brave Native Messaging qualification.

## Release distribution

Build the store extension with `pwsh scripts/Build-BrowserAutofill.ps1 -Production`; distribute `dist/production` through the browser stores. Release publishing requires the actual Chrome and Edge store extension IDs:

```powershell
pwsh .\scripts\Publish-WindowsRelease.ps1 -Version 1.0.0 -ChromeExtensionId $chromeStoreId -EdgeExtensionId $edgeStoreId -SigningMode Required
```

Missing, malformed, repeated-letter placeholders and the known development ID are rejected. The self-contained `win-x64` NativeHost is published beside `YourSafe.exe`. Per-browser manifests contain only the corresponding store origin and a relative NativeHost path. The per-user installer registers HKCU32 entries for Chrome/Edge/Brave; its uninstall cleanup removes a value only if it still points to that installation. The portable ZIP never auto-registers. Signing and qualification verify both executables plus the installer when present. Signing credentials and Inno Setup remain external controlled-release inputs.

This version defers iframe/shadow DOM support, multi-step login, desktop hotkeys, UI Automation, Auto-Type and clipboard autofill. No additional Contracts project, runtime Node package or frontend framework is needed.

Primary API references: [Chrome action/openPopup](https://developer.chrome.com/docs/extensions/reference/api/action), [Native Messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging), [document targeting](https://developer.chrome.com/docs/extensions/reference/api/tabs), [Windows pipe peer PID](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid).
