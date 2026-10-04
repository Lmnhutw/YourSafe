Purpose: Implement the P1/P2 findings from the Windows 11 UI review.

Assumptions: Buttons use fixed blue #204BDB on Windows 10 and 11. The user wants each tab's name and color editable through its right-click menu, with no option to recolor buttons. System background is the initial background. Custom group names/colors belong to the encrypted vault; built-in tab labels/colors and background/layout preferences belong to the local Windows installation. All reviewed editor/navigation/window fixes remain in scope.

Final Prompt:

```text
Work in D:\PROJECTS\YourSafe\PasswordTool.Core. Implement the existing review findings end to end with the smallest correct changes.

Context and boundaries
- Read the current XAML, code-behind, Presentation callers, Core group validation/storage, and existing test scripts before editing.
- Preserve the user's modified win-x64.pubxml. Do not commit or publish.
- Preserve encryption, recovery, TOTP, clipboard, session/locking behavior and vault format. ASP.NET API endpoints are unrelated to desktop appearance.
- Reuse WinUI native controls, existing dialogs, group storage, error properties, test projects and stdlib. Do not add dependencies or a theming framework.
- Run GitNexus impact analysis when an index is available; otherwise record the missing index and trace callers directly.

Required implementation
1. P1: Display ShellViewModel status/error messages inside the unlocked workspace. Invalid color/editor input must remain visible and recoverable.
2. P2: Keep buttons and normal app selection blue #204BDB. Do not expose global button recoloring. Appearance settings only choose Background System/Light/Dark and Horizontal/Vertical tab layout; apply immediately to controls and dialogs.
3. P2: Persist background, tab layout, and optional built-in All/Ungrouped tab labels/colors in one local appearance.json, isolated by the existing Debug test directory. Missing/corrupt preferences fall back safely. Save failures must be visible. Appearance requires no Master Password.
4. P2: Right-click every tab, including All and Ungrouped, to Rename tab or Change tab color. Use native ColorPicker with a sample tab preview, explicit Save, Cancel and Reset color. Reuse encrypted group name/color storage for user groups; reject stale dialog results after locking.
5. P2: Give each tab an independent color; show a color marker on inactive tabs and a separate selected/focused indicator. Support both Windows 10 and 11. Keep credential table text on a neutral surface.
6. P2: Choose black/white text using actual relative-luminance contrast, at least 4.5:1 for ordinary text. High Contrast must always use system brushes, preserving the stored preference for later.
7. P2: Restore native normal/hover/pressed/disabled/focus states. Prefer the default Button template with lightweight styling over a ContentPresenter-only template.
8. P2: Synchronize NavigationView selection whenever programmatic navigation changes the route; avoid recursive SelectionChanged navigation. Back from Trash, editor save/cancel and backup reminder must highlight the visible page.
9. P2: Reject overlapping editor saves and disable Save during I/O. Double-clicking Save for a new item must create exactly one item; recover correctly after errors.
10. P2: Ctrl+N while editing must preserve current input. Confirm deliberate abandonment of dirty input; security locking still clears secrets immediately.
11. P2: Size and minimum constraints must respect monitor work area and DPI and allow normal Windows 11 Snap. Small windows retain reachable controls and scrolling.
12. Enforce exactly six hexadecimal digits at the shared Core color boundary; outer trim/uppercase/empty-to-null remain supported. Invalid new colors cannot mutate names/data. Malformed legacy cosmetic colors must not prevent unlocking.

Verification and deliverables
- Add focused checks using existing xUnit projects for preference persistence/fallback, color contrast and strict Core validation. No parallel test framework.
- Run Core and Presentation tests and the WinUI x64 build.
- Create/run a winapp ui batch against a marked disposable Debug vault only; verify background/layout persistence, per-tab right-click rename/color save/cancel/reset/validation, built-in tab preferences, fixed blue buttons, workspace errors, navigation, Ctrl+N, discard confirmation and duplicate-save prevention. Capture screenshots and inspect actual rendering.
- Never operate on the user's real vault or change machine-wide theme/accent merely to make app tests pass.
- Report any unavailable Win10, High Contrast or DPI checks as unverified, not passed from source inspection.
- Deliver a short change summary, test counts/native results, and remaining runtime limitations.
```
