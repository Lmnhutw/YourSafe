# Windows 11 tab appearance verification

Verification completed on 2026-10-04 (Asia/Saigon). The existing evidence directory retains its 2026-10-03 name.

The implemented behavior follows `win11-ui-appearance-fix-prompt.md` and the user's final corrections: normal app accent buttons use **#204BDB**, and **each tab has its own right-click Rename tab / Change tab color menu**. Appearance settings expose Background System/Light/Dark and Horizontal/Vertical vault layout. There is no button-color preference.

## Storage and rendering

- Custom group names and colors use the existing encrypted vault group storage. Built-in All/Ungrouped names and colors, background, and layout use local `%LocalAppData%\PasswordTool\appearance.json`; Debug UI tests redirect this file into their disposable directory.
- A selected colored tab has its chosen fill and luminance-selected black/white text. Inactive colored tabs retain an 8 px color marker. Credentials remain on a neutral table surface.
- The color dialog previews the exact RGB value and provides Save, Cancel, and Reset color. Invalid input stays in the dialog with an error. Rename/color handlers discard results from an obsolete unlock session.
- Owned Light/Dark brushes keep app accent blue without modifying shared native brushes. High Contrast uses system brush definitions and suppresses custom tab colors in source; runtime verification remains outstanding.
- Workspace errors are visible while unlocked. Native button templates retain input states. Navigation selection follows the displayed route. Editor saves reject overlap, Ctrl+N preserves an open editor, dirty drafts require discard confirmation, and security locking clears editor fields.
- Window initialization and minimum constraints use monitor work area and window DPI. The minimum width is no longer forced to 1200 DIP.

## Verified results

| Check | Result and evidence |
| --- | --- |
| Core xUnit suite | **140/140 passed**, including strict six-digit RGB validation, no name/data mutation on invalid color, reset/normalization, and malformed legacy cosmetic colors during master/trusted unlock. |
| Presentation xUnit suite | **58/58 passed**, including appearance round-trip/fallback/contrast and overlapping-save success/error recovery. |
| Focused appearance tests | **4/4 passed**; these also run inside the Presentation suite. |
| Final Debug x64 build | **Passed** after removal of temporary diagnostics, **0 warnings / 0 errors**, 23.29 seconds. |
| Windows 11 native batch | **11/11 passed**, result file `test-results/appearance-20261003-17694/ui-results/appearance-ui-win11-tabs-r2.json`. |
| Screenshot evidence | **15 captures** from that batch. Actual rendering was reviewed separately from preference/automation assertions. |
| Small-window native controls | Fresh marked Debug process at **640 × 720**: both right-click actions visible/enabled and color picker Save/Reset color/Cancel reachable. Captures `win11-tabs-final-small-menu.png` and `win11-tabs-final-small-picker.png` in the same `ui-results` directory. |
| Windows 11 Snap | **Verified on this monitor** using Win+Left against the focused marked Debug app. The resulting **1280 × 1440** capture shows reachable/readable vault navigation, header, search, tab strip, and table with no clipped main controls: `win11-tabs-final-snap.png`. |

The native batch ran only against the marked disposable Debug vault. It verified:

1. Light/Dark background changes and persisted layout, while app buttons stayed blue.
2. All tab exact RGB validation/persistence and Cancel preserving the prior color.
3. Horizontal/vertical layout surviving page navigation.
4. Ungrouped tab color remaining independent of All, including reset.
5. Custom Work tab color through its right-click menu and encrypted refresh, including Cancel and reset.
6. Right-click Rename tab for custom and built-in labels.
7. Trash Back to Vault synchronizing navigation selection.
8. Visible editor validation and Ctrl+N preserving the draft.
9. Discard confirmation protecting edits; cancel returning to selected Vault.
10. Rapid double-click Save creating exactly one item.
11. System background remaining selectable and preferences surviving navigation.

Earlier native runs exposed two real issues: desktop High Contrast event subscription required a UWP CoreWindow, and palette lookup could reuse a shared native brush. The final source uses desktop preference notifications and constructs owned palettes once. Temporary startup/theme diagnostics were removed before the final build.

## Reproduction commands

Run these in PowerShell from `D:\PROJECTS\YourSafe\PasswordTool.Core`. Clear the opt-in fixture environment before running the full suites; the seeder intentionally refuses an already initialized test vault.

```powershell
$env:PASSWORDTOOL_UI_TEST_DIRECTORY = $null
dotnet test tests\PasswordTool.Core.Tests\PasswordTool.Core.Tests.csproj --no-restore --nologo
dotnet test tests\PasswordTool.Presentation.Tests\PasswordTool.Presentation.Tests.csproj --no-restore --nologo
dotnet build src\PasswordTool.WinUI\PasswordTool.WinUI.csproj --no-restore --configuration Debug -p:Platform=x64 --nologo
```

For a fresh native test session, choose a new directory, then seed and launch the Debug project. The WinApp path below is the bundled version used in this workspace; use the installed CLI if its package location differs.

```powershell
$testDirectory = Join-Path (Get-Location) ('test-results\appearance-' + [Guid]::NewGuid().ToString('N'))
$env:PASSWORDTOOL_UI_TEST_DIRECTORY = $testDirectory
$env:PASSWORDTOOL_UI_TEST_WIDTH = '1200'
dotnet test tests\PasswordTool.Core.Tests\PasswordTool.Core.Tests.csproj --no-restore --filter FullyQualifiedName~Create_opt_in_disposable_ui_vault --nologo
$winAppPath = 'C:\Users\lmnhu\.nuget\packages\microsoft.windows.sdk.buildtools.winapp\0.6.1\tools\win-x64\winapp.exe'
& $winAppPath run src\PasswordTool.WinUI\PasswordTool.WinUI.csproj --no-build --no-restore --arch x64 -p Platform=x64 -c Debug --detach --json
```

Unlock that synthetic vault with the fixture's synthetic credentials. Pass the launched app's actual process ID to the batch script:

```powershell
.\scripts\Test-AppearanceUi.ps1 -ProcessId <debug-app-process-id> -TestDirectory $testDirectory -WinAppPath $winAppPath -CaptureLabel win11-tabs
```

The script fails closed unless the process contains `YourSafeDisposableUiTest` with a HelpText matching the supplied directory. It scopes UI automation to that process, including native popup windows, and saves JSON plus screenshots under `ui-results`. It must never be pointed at the user's real vault. The double-save check creates one synthetic item per run.

## Remaining runtime coverage

Windows 10, Windows Contrast themes, and formal 125%/150% DPI checks have **not** been run in this session. Source branches and mathematical contrast tests support those paths but do not constitute runtime passes. No machine-wide Windows theme/accent preference was changed. The verified small-window and Snap cases apply to the tested Windows 11 monitor setup.

GitNexus impact analysis could not run because this repository had no registered index. Callers were traced directly: tab menu handlers → `ShellViewModel.UpdateGroupAsync` → existing flow/Core group persistence; built-in tab preferences → `AppearanceService.Save` → atomic local settings file; editor UI → shared `SaveItemAsync` overlap guard. No API endpoint, encryption format, or dependency was changed. The user's pre-existing `win-x64.pubxml` modification was preserved. No commit or installer publish was performed.

