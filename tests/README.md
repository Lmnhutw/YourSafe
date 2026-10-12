# Choosing tests

Run the checks for the boundary you changed. The Core and Presentation projects are separate because service correctness does not prove cancellation, stale callbacks or UI authorization. Shared protocol fixtures run in both C# and TypeScript to catch disagreement between implementations.

| Area | Check | Purpose |
| --- | --- | --- |
| Core | `dotnet test tests/PasswordTool.Core.Tests` | Hashing, encryption, storage, backup/import, recovery, TOTP and session deadlines |
| Presentation | `dotnet test tests/PasswordTool.Presentation.Tests` | Flows, input validation, cancellation, queued operations and native protocol/peer authorization |
| HTTP API | Build `src/PasswordTool.Api`, then `pwsh -File scripts/Test-PasswordApiSecurity.ps1` | Real HTTP limits, Host validation, no-store, algorithm policy and KDF admission |
| Extension | In `browser-extension`: `npm.cmd run typecheck` and `npm.cmd test` | Protocol, target authorization, worker, popup and native-connection mocks |
| Browser DOM | See [extension instructions](../browser-extension/README.md) | Actual browser fields, forms and injected filling; mocks cannot verify these |
| Release construction | `pwsh -File scripts/Test-ReleasePipeline.ps1` | Staging containment, payload exclusion, checksums and signing failure |
| Release qualification | `pwsh -File scripts/Test-ReleaseQualification.ps1` | Final artifact integrity and qualification without modifying release files |
| Browser registration | `pwsh -File scripts/Test-BrowserIntegration.ps1` | Production/development manifest boundaries and Chrome/Edge/Brave registration contracts |
| Website | `node website/verify.cjs` | Site content, navigation, assets and release links |

The Windows pipe-peer test requires Windows. On another platform use the existing `FullyQualifiedName!~Windows_pipe_peers` filter. The two opt-in Core fixture tests do nothing unless their fixture environment variable is set; preserve them because native tests and website captures use them. Unset those variables for a normal unit run.

## Native UI batches

| Script | Coverage |
| --- | --- |
| `Test-AppearanceUi.ps1` | Theme/tab persistence, ComboBox first-click selection and downward placement, two/three-button modal order, editor drafts and navigation |
| `Test-WorkspaceUi.ps1` | Password generation/hash/verify/inspect, sensitive output reset, backup/settings geometry and locked/unlocked Add/Ctrl+N |
| `Test-VaultUi.ps1` | Search/filter defaults, read-only details, duplication, group moves, Trash/Restore/delete and password-only table re-unlock |
| `Test-TotpFlyoutUi.ps1` | Code grouping, timer renewal, setup validation and accessible help |
| `Test-EditorLockSecurityUi.ps1` | Real one-minute timeout with/without draft TOTP, draft dismissal, cancelled/incorrect re-unlock and a fresh empty editor |
| `Test-AutofillNativeIntegration.ps1` | Installed extension and real NativeHost/WinUI approval using isolated browser profiles; `-NativeHostOnly` checks real process transport/cancellation/competing clients |

`Test-ComboBoxUi.ps1` and `Test-DialogButtonsUi.ps1` were merged into Appearance. `Test-PasswordToolUi.ps1` and `Test-RequestedLayoutUi.ps1` were merged into Workspace; generation and successful verification now run once per batch. Each script accepts `-WinAppPath` when WinApp CLI is not on PATH.

Seed a **new, empty** directory, then launch the Debug app with the same environment variable. The fixture contains only synthetic credentials and has a ten-minute table-lock timer so UI batches can finish.

```powershell
$env:PASSWORDTOOL_UI_TEST_DIRECTORY = Join-Path $PWD 'test-results/ui-disposable'
dotnet test tests/PasswordTool.Core.Tests --filter FullyQualifiedName~Create_opt_in_disposable_ui_vault
# Launch the Debug app from this shell, then sign in and unlock its synthetic vault.
pwsh -File scripts/Test-WorkspaceUi.ps1 -ProcessId <test-app-pid> -TestDirectory $env:PASSWORDTOOL_UI_TEST_DIRECTORY
Remove-Item Env:PASSWORDTOOL_UI_TEST_DIRECTORY
```

Use a fresh fixture for independent batches, and keep the app in the foreground for pointer/keyboard checks. Vault tests permanently delete only the copy they create. Scripts verify the exact `YourSafeDisposableUiTest` HelpText directory before driving the app and treat a WinApp wait timeout as failure even when its exit code is zero. Captures/results are ignored; Vault, Appearance and Workspace write under `<TestDirectory>/ui-results`, TOTP writes under `<TestDirectory>`.

Editor Lock Security requires a separate disposable fixture with a one-minute vault duration **before app launch**; the regular ten-minute fixture deliberately exceeds its 75-second timeout bound. The batch writes under `<TestDirectory>/ui-results`. It verifies readable editor controls and navigation, while protected PasswordBox values and managed references are outside UIA's visibility. It never locks or suspends Windows; record simulated lifecycle tests and actual OS events separately.

Review native screenshots for appearance assertions. Passing UI Automation properties alone does not verify colors, clipping, contrast or DPI behavior. Native scripts and the DOM smoke test also do not replace the installed-extension → NativeHost → desktop end-to-end check.
