# Recovery Key and session verification

Verified on 2026-10-02 using the existing test projects and an isolated Debug vault containing synthetic accounts. No project dependencies were added.

| Check | Result |
| --- | --- |
| Core tests | 139 passed |
| Presentation tests | 52 passed |
| Full solution Debug build | Passed, zero warnings/errors |
| WinUI x64 Release build | Passed, zero warnings/errors |
| Native action script | Nine checks passed at the 1200px minimum window width; results in `test-results/vault-ui-1200-dark.json` |
| 1440px native actions | First seven checks passed in the full run; permanent-delete and manual-lock checks passed in a focused follow-up |
| Fresh Dark visuals | White-table fix confirmed at 1200px, 1440px, and maximized width |
| Fresh Light visuals | Table contrast, tabs, ellipsis, and column alignment reviewed at 1200px, 1440px, and maximized width |
| 1440px native layout | Title header/row positions and widths match; Settings scroll viewport reaches the workspace edge |
| Maximized native layout | Columns expand and align; Editor, Backup, and Hash scroll viewports reach the workspace edge |
| Recovery Key rotation dialog | Continue disabled before saved confirmation; cancellation preserves config |
| Whitespace | `git diff --check` passed |

Core coverage includes matching DEK wrappers, wrong/tampered/unsupported credentials, recovery/rotation ciphertext preservation, replacement-credential reuse rejection, old credential/token rejection, setup and enrollment gates, staged write failures, legacy migration journal recovery after restart, all eight durations, expiration at protected read/write boundaries, and Trash retention purging only after authorized unlock. Presentation coverage includes ordered recovery steps, canceled and stale callbacks, Duplicate/Move/History, Trash/Restore, and permanent-delete confirmation.

The native script verifies Clear defaults, read-only details, History, duplicate cancellation and distinct-ID save, group moves, Trash/Restore, permanent-delete cancellation and confirmation, and manual lock with password-only re-unlock. It also compares the Title header and row geometry. Its JSON results and captures are written to ignored `test-results`.

The subsequent 1440px full run timed out after seven passing checks when the test window was minimized. That report remains in `test-results/vault-ui-fixed-1440-dark.json`. The remaining two checks were verified directly with the foreground native window; their passing evidence is in `test-results/vault-ui-remaining-1440-dark.json`. The current script requires the marked test window to be foreground before dialog actions and waits briefly after Trash selection.

```powershell
dotnet test tests/PasswordTool.Core.Tests --no-restore
dotnet test tests/PasswordTool.Presentation.Tests --no-restore
dotnet build PasswordTool.slnx --no-restore
dotnet build src/PasswordTool.WinUI -c Release --no-restore -p:Platform=x64
pwsh -File scripts/Test-VaultUi.ps1 -ProcessId <isolated-test-process-id> -TestDirectory <disposable-vault-directory> -CaptureLabel <size-and-theme>
```

The user explicitly waived 125–150% DPI and high-contrast runtime verification on 2026-10-02. Windows scale stayed at 100%, and high contrast stayed Off. Source resources use system colors in High Contrast; this does not establish runtime acceptance at the waived settings.

Fresh Dark screenshots exposed application-level Light brushes in the table and group tabs. These now use theme resources from the actual visual subtree, with separate Light/Dark/High Contrast table, card, divider, and tab resources. The opaque Dark table background preserves text contrast when a group has a bright accent color. Reviewed captures are `test-results/visual-fixed-1200-dark.png`, `test-results/visual-fixed-1440-dark.png`, and `test-results/visual-fixed-large-dark.png`.

Fresh Light captures after the same fix are `test-results/visual-fixed-1200-light.png`, `test-results/visual-fixed-1440-light.png`, and `test-results/visual-fixed-large-light.png`. At the minimum width, the table retains its internal horizontal scroll; at 1440px and maximized width, columns expand and remain aligned. Test windows were closed after verification.
