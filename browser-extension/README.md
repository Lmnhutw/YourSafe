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

Enable browser integration in the desktop Settings; it starts disabled each app session, with no pipe listener. Enabling permits matching account names/usernames and capability metadata to be shared without another prompt; disabling closes connections and cancels pending requests. Save a password or TOTP item with a complete HTTPS URL, unlock the desktop vault, visit the matching site, and open the toolbar popup. **Fill**, **View TOTP**, and **Copy** each request a separate approval on the desktop before accessing the secret. The extension does not submit the form. Focus a specific username/password field first when multiple forms are ambiguous.

The extension uses `activeTab` and `scripting` instead of persistent website permissions. It injects its content script only into the selected top-level document after an explicit Fill action. It adds no automatic page hint. Its extension-page CSP forbids network connections, remote scripts, frames, and form submission.

When locked, use **Show YourSafe**, unlock on the desktop, then refresh and choose the account again. YourSafe must already be running; the host never launches a second desktop or unlocks a vault.

Items with **No URL** (a null, empty or whitespace-only stored URL), invalid URLs, or unrelated origins are excluded from browser discovery and all secret actions. Add the exact supported website in the desktop before using browser integration. Discovery and secret retrieval recheck the stored URL, so changing it after discovery invalidates the old request.

```powershell
pwsh .\scripts\Register-BrowserAutofill.ps1 -Unregister
```

Unregister removes only development HKCU registrations whose value still points to this checkout. Development and production use different host names and pipe names. Debug NativeHost and desktop executables must be adjacent and built with the same configuration; peers reject different executable paths.

## Responsibilities and protocol

| Component | Responsibility |
|---|---|
| Content script | Explicitly injected top-level form detection, native input setters and input/change events. No discovery or secret request authority. |
| Worker | Browser-derived tab/frame/document/URL context, short-lived metadata, one bounded pending desktop approval, final target checks and exact-document delivery. Reopening a popup can recover that operation without replaying a native request. |
| Popup | Shows origin/accounts with separate Fill and View TOTP actions, one approved code panel, UTC countdown, desktop copy confirmation, and explicit cancellation. |
| NativeHost | Native Messaging framing/validation and named-pipe client. No Core, Presentation or WinUI reference, vault key, lock state or retries. |
| WinUI pipe server | Current-user pipe, Windows peer verification, bounded request handling, same-pipe cancellation, final lifecycle checks and dispatcher work. |
| Presentation | Integration opt-in, per-request desktop approval, HTTPS exact-origin matching, and application entrypoints through AppFlowCoordinator/RunVaultAsync. |
| Core | Password/TOTP capability projection with real URLs, credential lookup, configuration-aware local TOTP generation, session/lock checks and minimal secret projection. Imported null usernames become empty strings in metadata and secrets. Desktop reveal/edit continues to use the existing active sign-in session. |

V2 envelopes have `version: 2`, UUID `requestId`, allowlisted `action`, and typed `payload`. Requests allow only `ping`, `getStatus`, `findCredentials`, `getCredentialSecret`, `getCredentialTotp`, `copyCredentialTotp`, and `showApp`. Credential actions require a canonical HTTPS origin; retrieval additionally requires a nonempty credential UUID. Responses echo version/request ID and contain either `ok: true, result` or `ok: false, error`. Discovery results contain `{ credentials, truncated }`; complete account entries are included up to the 64 KiB envelope budget. When truncated, the popup directs users to the desktop for the full list. Rebuild the desktop, NativeHost and extension together after contract changes.

Native Messaging and named pipes use UTF-8 JSON preceded by a 4-byte little-endian length, capped at 64 KiB before allocating the body. Partial/coalesced reads, malformed UTF-8/JSON, unknown fields/actions, duplicate JSON properties, oversized frames, timeout and disconnect fail closed. Secret requests are never automatically retried. Discovery returns only ID/title/username and boolean hasPassword/hasTotp capabilities, including for hidden-URL items. Matching requires identical normalized host and effective port; paths do not matter and subdomains are distinct. Missing schemes, HTTP, userinfo and ambiguous numeric/escaped hosts are rejected.

The pipe name includes a SID hash, Windows session and dev/prod channel. Both endpoints use `CurrentUserOnly` and verify the OS-reported peer PID, Windows session and exact adjacent executable path. No JSON PID/path is trusted. These controls reduce simple impersonation; they do not establish protection against malware running as the same user. Browser registration allowlists the extension, but extension identity and the actual visited origin are not cryptographically attested to the desktop. The origin received by the desktop is a claim from the client. Desktop approval displays that claim and the selected credential; it does not prove which page or extension made the request. A modified client can enumerate plausible origins for metadata within the desktop rate limit while integration is enabled. The official worker independently checks the actual browser target before delivery.

No secret persistence, payload logging, clipboard fill, HTTP listener or network service is added to the product. Serialized transport buffers are cleared after use; managed strings and JavaScript strings are not guaranteed to be zeroized. Once a password or code has been disclosed, locking the vault cannot revoke that copy. Discovery metadata expires after 60 seconds, bounded by UTC and a monotonic deadline. An explicit secret action starts one bounded 70-second pending lifetime, including up to 60 seconds for desktop approval. Reopening the popup and countdown ticks never renew it. The worker owns the native connection with serialized request IDs and 70-second timeouts. Popup closure and focus loss to the desktop preserve an explicitly pending approval; password approval can fill the original document once even if the popup closed. No password is cached for recovery. Only bounded status and a code until its expiry remain in memory, and worker restart loses them without retrying a secret request.

Explicit cancellation, native disconnect, interaction expiry, target navigation/refresh, SPA/hash changes, document replacement, active-tab changes in the target window, a different browser window, and failure invalidate the operation. The worker also invalidates the content token, so cancellation received during a visibility probe prevents a later field write. Background tab navigation/reload/closure leaves the target interaction intact. Selection checks interaction identity, expiry and target after asynchronous target lookups; discovery checks its generation after awaits before releasing metadata or creating consent.

Field revalidation retains the selected username and password. Inputs disabled through a fieldset are excluded via the browser's `:disabled` state. Readonly, inert, hidden, transparent, offscreen, and covered inputs are excluded. A bounded Intersection Observer visibility probe is required during preparation and before each actual field write; it checks paint occlusion, including covers with `pointer-events:none`, and denies on unsupported/error/timeout. Browser visibility tracking is conservative: partially visible, filtered, transformed or translucent fields may be refused. The form action and associated submit-button overrides must use the page's exact supported origin; cross-origin actions and HTTPS-to-HTTP downgrades are rejected. Loopback HTTP retains the existing development policy. Username input/change events can alter the form, so visibility, field identity, deadline, and form actions are checked again after the asynchronous probe and before writing the password. These checks cannot stop JavaScript already running on the intended site from reading a filled password. The pipe server waits 250 ms after handled failures, including pipe-constructor `IOException`, and shutdown cancels that wait.

## Verification codes

Discovery never retrieves codes. **View TOTP** requests one desktop approval and then shows one grouped six- or eight-digit code with a countdown derived from desktop expiry. Codes outside their UTC time window disappear. Countdown ticks never retrieve a new code or open another approval prompt. Use **Choose account**, then **View TOTP**, for another approved read. **Choose account** immediately clears the panel, cancels a pending read, and refreshes metadata. Focus moves into the panel and returns to the selected account. The worker permits only one pending secret action, checks browser-derived origin/tab/window/document before and after asynchronous work, and accepts only an eligible ID returned by the current discovery.

**Copy** sends `copyCredentialTotp` for a separate desktop approval; the desktop regenerates the current code and writes its numeric value through the sensitive clipboard service. Clipboard cleanup after 30 seconds and on lock is best effort and clears only a still-owned clipboard value. Other applications, clipboard history, synchronization, and already-pasted values are outside that guarantee. The extension adds no clipboard permission and never receives TOTP configuration, otpauth URIs, or recovery codes. Content scripts and websites cannot request or receive codes through the extension.

Both code actions require browser integration to be enabled, an approved request, the actual desktop Unlocked state and current lifecycle, and recheck the credential's stored URL and capability on each request. After failure the panel clears; an explicit Refresh/View action is required. Unlock remains **Show YourSafe → unlock desktop → Refresh**. If approval takes desktop focus and closes the popup, reopen it to recover the pending action or approved result without a new secret request.

## Verification

Unit and static checks do not require browser registration. These commands are available for a later verification pass; building or passing mocks alone does not qualify live browser/desktop security. Run from the repository root:

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

`test/contract-fixtures.json` is consumed by C# and TS. C# unit tests cover hidden URLs, item filtering, exact origin, lifecycle changes while pending, in-memory framing, discovery byte budgeting/truncation, and imported null usernames. TS mock tests cover version-2 request/result validation, popup versus content authorization, one-use password selection, TOTP-only accounts, selected-account authorization, stale target/expiry during awaits, one-shot code grouping and expiry, clock changes, native connection serialization, disconnect/timeout/malformed frames, popup recovery without replay, explicit cancellation, background-tab isolation, and failure without retries.

The commands above build the DOM smoke bundle but do not run a browser, register Native Messaging or perform live IPC/install checks. The separate OS pipe peer check opens real named pipes; run it explicitly from the repository root when qualifying IPC:

```powershell
dotnet test .\tests\PasswordTool.Presentation.Tests --filter "FullyQualifiedName~Windows_pipe_peers"
```

Run the DOM smoke fixture through its loopback runner with an already-installed Playwright and browser; the unsupported `file://` origin intentionally fails the origin policy:

```powershell
$env:YOURSAFE_PLAYWRIGHT_PACKAGE = 'absolute-path-to-installed-playwright-package'
$env:YOURSAFE_BROWSER_EXECUTABLE = 'absolute-path-to-installed-chromium.exe'
node test/browser-smoke.mjs
```

No browser or automation dependency is downloaded by this smoke runner. Its ephemeral server serves only the synthetic HTML and test bundle on `127.0.0.1`. The isolated DOM test covers selected fields, fieldset-disabled and first-legend behavior, visibility/occlusion, form-action and submit overrides, and revalidation after username events. It is separate from live extension/desktop E2E. Building its bundle alone does not verify browser behavior.

Complete the following manual checklist separately on Chrome, Edge and Brave, using a disposable vault and synthetic credentials:

- Happy path for a normal, password-only and dynamically rendered form; input/change fire and submit does not.
- Discover password-only, password+TOTP and TOTP-only items; no code retrieval until View TOTP, and no Fill for a TOTP-only item.
- Approve separate Fill, View TOTP, and Copy requests on the desktop. Refusing, cancelling, or timing out must disclose no secret.
- View/copy six- and eight-digit codes with leading zeros, non-default periods and algorithms; expiry hides codes without starting another request, and approved copy at a boundary uses the current desktop code.
- Close the popup while desktop approval is pending, approve, and reopen it: recover the bounded result without replay. Cancel instead, or change the target: no secret may be delivered.
- Switch selected accounts, expire the pending lifetime, lock desktop, disconnect the host, disable integration, or change the stored URL: future access requires fresh explicit approval.
- Empty/hidden URL, non-loopback HTTP, a different subdomain/port and malformed/missing-scheme URLs do not match.
- Multiple login forms require focus; hidden/disabled/readonly/new-password/offscreen/covered fields and unsafe form actions never receive passwords.
- Before opening the toolbar popup, there must be no automatic content injection or website hint. Inspect activeTab-only access and the no-network CSP on each supported browser.
- Navigate, replace the document, switch browser tabs/windows, change SPA routes or hashes during discovery/retrieval: no fill. Focus loss to the desktop approval window alone must preserve the pending request.
- Reload, navigate or close a background tab during discovery/selection: the target account selection remains usable.
- Large matching-account lists: the popup lists complete entries within 64 KiB and clearly directs users to the desktop when truncated.
- Lock/logout/expire after discovery or while retrieval waits: no secret is delivered; after unlock select again.
- Disconnect NativeHost, close desktop, or send malformed/oversized frames: bounded failure without logging secrets or retrying retrieval.
- Start two Debug desktops with separate disposable test directories: an occupied pipe must not freeze the second desktop; closing it must cancel any retry wait.
- Clean install, upgrade, uninstall and portable ZIP: registrations target the installed manifests, unregister affects only owned values, portable launch never edits the registry, vault storage survives unchanged.

## Popup user stories

- Enabled, unlocked desktop: show the current origin and separate account title/username; desktop-approved selection fills once without submitting the form.
- Locked table: direct the user to Show YourSafe, Unlock Vault, then Refresh. Never request the Master Password in the extension.
- No matching account: explain how to add an account with this exact origin, then refresh.
- Desktop disconnected: explain how to start the matching desktop and check registration; keep recovery buttons available.
- Unsupported page or changed target: refuse filling and explain how to retry on the intended HTTPS page.
- Pending request: disable duplicate requests, permit cancellation, and recover after desktop focus closes the popup; password success consumes selection, and failure clears stale accounts.

`npm test` checks these popup states with a minimal DOM and synthetic responses, plus worker authorization races. This does not replace live Chrome/Edge/Brave Native Messaging qualification.

## Release distribution

Build the store extension with `pwsh scripts/Build-BrowserAutofill.ps1 -Production`; distribute `dist/production` through the browser stores. Release publishing requires the actual Chrome and Edge store extension IDs:

```powershell
pwsh .\scripts\Publish-WindowsRelease.ps1 -Version 1.0.0 -ChromeExtensionId $chromeStoreId -EdgeExtensionId $edgeStoreId -SigningMode Required
```

Missing, malformed, repeated-letter placeholders and the known development ID are rejected. The self-contained `win-x64` NativeHost is published beside `YourSafe.exe`. Per-browser manifests contain only the corresponding store origin and a relative NativeHost path. The per-user installer registers HKCU32 entries for Chrome/Edge/Brave; its uninstall cleanup removes a value only if it still points to that installation. The portable ZIP never auto-registers. Signing and qualification verify both executables plus the installer when present. Signing credentials and Inno Setup remain external controlled-release inputs.

This version defers iframe/shadow DOM support, multi-step login, desktop hotkeys, UI Automation, Auto-Type and clipboard autofill. No additional Contracts project, runtime Node package or frontend framework is needed.

Primary API references: [Chrome activeTab](https://developer.chrome.com/docs/extensions/develop/concepts/activeTab), [script injection](https://developer.chrome.com/docs/extensions/reference/api/scripting), [paint visibility tracking](https://web.dev/articles/intersectionobserver-v2), [Native Messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging), [document targeting](https://developer.chrome.com/docs/extensions/reference/api/tabs), [Windows pipe peer PID](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid).
