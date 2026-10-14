# YourSafe — Chrome Web Store privacy declarations

Checked against the source, production build and local upload ZIP on 2026-10-14.
These texts are ready to paste into the item's **Privacy practices** tab.
They describe the extension, not the separate marketing website.

## Single purpose description

```text
YourSafe helps users sign in to websites using credentials and verification codes from their local YourSafe Windows vault. It fills selected login fields and lets users view or copy verification codes, with a separate desktop approval for each password fill, code view, or code copy. The extension requires the locally installed YourSafe Windows application.
```

## activeTab justification

```text
YourSafe needs temporary access to the active tab when the user opens its toolbar popup. It reads the current website address to find matching accounts in the local Windows vault. When the user clicks Fill, this temporary access also allows the packaged content script to inspect and fill login fields in that tab. YourSafe does not request persistent access to all websites or inject scripts automatically on page load.
```

## nativeMessaging justification

```text
YourSafe uses Native Messaging to communicate with its locally installed Windows native host and desktop application on the same computer. This connection supplies matching account metadata and performs the user's requested password-fill and verification-code actions. The desktop controls vault access and separately approves each secret action. Passwords and viewed codes are handled transiently; code copying is performed by the desktop. The extension does not upload vault data to a server or store the password vault in the browser.
```

## scripting justification

```text
After the user clicks Fill, YourSafe injects its packaged content.js into the selected tab's top-level document. The script identifies the intended username and password fields and checks field visibility, document identity, and form destinations. Credentials are filled only after a separate approval in the local YourSafe desktop application and another target check. The extension does not inject downloaded code, fill other frames, or submit login forms automatically.
```

## webNavigation justification

```text
YourSafe uses webNavigation.getFrame to obtain the active top-level document ID and bind a requested action to that exact document. Navigation, reload, history-state and fragment-change events cancel an interaction for the watched target tab, including a pending desktop approval. This prevents credentials or verification codes from being delivered to a changed page. These events are used for target validation and cancellation, not to retain browsing history or create browsing profiles.
```

## Remote code use

Select **No, I am not using remote code**. If the explanation field is required,
paste:

```text
YourSafe does not load or execute remotely hosted JavaScript or WebAssembly. All browser-executed extension code, including worker.js, popup.js and content.js, is bundled in the uploaded extension package. Its extension-page CSP restricts scripts to packaged files and blocks external network connections. Native Messaging exchanges validated JSON with the installed local YourSafe application; these messages are data, not downloaded executable code. The separate YourSafe website's Google Analytics tag is not included in the extension package.
```

## Data usage

Based on the current implementation, disclose these four categories. If the form
asks where data is processed, describe it as local processing for the requested
feature, with credential delivery to the user's selected website for autofill.

| Dashboard category | Why it applies | Actual use and retention |
| --- | --- | --- |
| Personally identifiable information | Account names and usernames, which may include email addresses, are returned from the local vault. | Display matching accounts and identify the user's chosen login. Metadata is held in memory for a bounded interaction. |
| Authentication information | Username/password pairs and generated verification codes are processed. | Password fill, code view and code copy each need a separate desktop approval. No master password or TOTP seed is returned to the extension. Passwords are not cached; a viewed code remains only until expiry or interaction invalidation. |
| Web history | The selected tab's URL and origin are used, even though no history list is stored. | Match accounts and keep actions bound to the selected document. Only the canonical origin is sent to the local desktop for matching; the current URL remains in the local interaction for target checks. No browsing-history database or profiling. |
| Website content | Packaged content code examines login-field/form attributes, visibility and form destinations. | Locate safe fields after an explicit Fill action. No general page scraping, screenshots or page-content upload. |

Do not select financial/payment information, health information, precise
location, personal communications, or user-activity monitoring: this extension
does not collect those categories for its feature. Operating the popup and
checking field focus/visibility do not create click, keystroke or mouse-activity
telemetry. Do not declare that the extension handles no user data merely because
the vault is local.

The publisher must review and certify the dashboard's data-use statements:

- User data is not sold or transferred for unrelated purposes.
- User data is used only for the extension's disclosed sign-in functionality.
- User data is not used to determine creditworthiness or for lending purposes.

The reviewed code matches these statements. Certification is still a publisher
declaration, not a guarantee of store approval. Native Messaging is local, but
autofill deliberately places credentials in the user's chosen website; that
website can read the values. The extension does not submit the form itself.

## Privacy policy URL

```text
https://lmnhutw.github.io/YourSafe/privacy-policy
```

The previous `/privacy` page was reachable during the review. Publish the updated
`website/privacy-policy.html` through **Publish YourSafe website** before submitting,
so the public policy includes login-form inspection and distinguishes website
GA4 from the extension's lack of analytics. GA4 must remain outside the
extension package and the credential demo.

## Publisher contact email and verification

Open **Developer Dashboard → Settings**. Set a publisher contact email you
control, save it, then use the verification action and complete Google's email
verification. The existing public YourSafe support address is
`lmnhut0211@gmail.com`; use it only if you control that mailbox. Providing a
privacy-policy URL or an email in the repository does not verify the publisher
account's contact email.

## Save Draft and review prerequisites

1. Fill the single-purpose field, four permission justifications and remote-code
   declaration above.
2. Disclose the four data categories, review the certification statements and
   complete them as the publisher. Set the privacy-policy URL.
3. Finish contact-email verification under Settings.
4. Click **Save Draft** in the item editor, then reopen the validation list.
   Confirm the nine reported blockers are cleared before **Submit for review**.
5. Provide reviewer instructions for the Windows companion and a synthetic test
   vault. The production installer must register `com.yoursafe.autofill` for the
   actual Chrome Web Store extension ID. The app-only `-test` prerelease omits
   browser registration and is insufficient to demonstrate the store build's
   Native Messaging flow.

## Implementation evidence

| Item | Source |
| --- | --- |
| Four permissions; no persistent host permissions or automatic content scripts; local-only CSP; package inputs | [`build.mjs`](build.mjs) |
| Current tab URL, document ID, navigation cancellation, explicit script injection and approved secret requests | [`src/worker.ts`](src/worker.ts) |
| Local Native Messaging and validated request/response envelopes | [`src/native.ts`](src/native.ts), [`src/protocol.ts`](src/protocol.ts) |
| Form/field inspection and guarded native input setters | [`src/fields.ts`](src/fields.ts), [`src/content.ts`](src/content.ts) |
| In-memory popup state and one-shot code display | [`src/popup.ts`](src/popup.ts) |
| Desktop opt-in disclosure before sharing account names/usernames | [`MainPage.xaml`](../src/PasswordTool.WinUI/MainPage.xaml) |
| Data handling, retention and permission disclosure | [`website/privacy-policy.html`](../website/privacy-policy.html) |

TypeScript checks, mock contract/worker/popup/native tests and the production
build passed during this review. The JS/HTML in
`dist/YourSafe-chrome-1.0.0-65e2c3c.zip` matched the local production files; its
manifest has the same four permissions and CSP. The inspected package contains
packaged JS/HTML/CSS/icons, with no GA4 tag, remote script loader, eval, or WASM.
This does not verify which ZIP is currently uploaded in the dashboard or qualify
a production Windows installer/native registration.

## Official references

- [Privacy practices fields, permission justifications and remote-code declaration](https://developer.chrome.com/docs/webstore/cws-dashboard-privacy)
- [User-data disclosures, including local processing and visited URLs](https://developer.chrome.com/docs/webstore/program-policies/user-data-faq)
- [Manifest V3 remotely hosted code](https://developer.chrome.com/docs/extensions/develop/migrate/remote-hosted-code)
- [Publisher contact email and verification](https://developer.chrome.com/docs/webstore/set-up-account)
