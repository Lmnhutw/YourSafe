# Frontend Structure Contract

> Source of truth for presentation structure and naming in the WinUI desktop client.

## Scope

- Frontend root: `src/PasswordTool.WinUI`
- Presentation root: `src/PasswordTool.Presentation`
- Framework: WinUI 3 with Windows App SDK
- Styling system: XAML resources and native WinUI controls
- Applies to: all windows, pages, forms, dialogs, navigation, status surfaces, and lists in the Windows clients

## Naming Convention

- Name controls and helpers by responsibility, such as `sensitiveSessionPanel` or `folderFilterComboBox`.
- Do not name controls after coordinates, colors, or incidental visual appearance.
- Use `*Page` for navigable surfaces and `*Window` for top-level WinUI windows.
- Name ViewModels `*ViewModel`, platform contracts `I*Service` or by a focused capability, and native adapters after the contract they implement.
- Neither client has CSS or a DOM, so CSS/BEM naming does not apply.

## Ownership

| Area | Responsibility | Location |
| --- | --- | --- |
| Shared visual language | Spacing, typography, cards, and theme-aware resources | `src/PasswordTool.WinUI/Themes/Styles.xaml` |
| Vault workspace | Header, commands, filters, security status, and virtualized credential list | `src/PasswordTool.WinUI/MainPage.xaml` |
| Authentication | Explicit first-launch, recovery, setup, and unlock states | `src/PasswordTool.WinUI/MainPage.xaml` |
| Feature dialogs | Short-lived, native modal interactions only | focused WinUI dialog service |
| Platform-neutral UI state | MVVM state, commands, routes, and secret-free list projections | `src/PasswordTool.Presentation` |
| WinUI shell | Navigation, authentication surface, feature pages, native resources | `src/PasswordTool.WinUI` |
| WinUI platform adapters | Dialogs, file pickers, guarded clipboard, session/power events | `src/PasswordTool.WinUI` |

## Layout Rules

- Use effective-pixel layout, adaptive states, and the system DPI scale; do not apply manual pixel scaling to XAML controls.
- A disabled feature must disable every related input and remain understandable through text, not color alone.
- Keep the virtualized credential list as the main Vault surface; secondary commands belong in the command bar or navigation.
- Pages own layout. Shared XAML resources own reusable visual styling and must not contain business or security logic.
- Use the Windows-native title bar, focus behavior, modal behavior, and system dialogs.
- In WinUI, use native `NavigationView`, `ListView`/`ItemsRepeater`, `InfoBar`, and system pickers where their behavior fits the task.
- Keep list rows virtualized and secret-free. Do not place password, recovery-code, TOTP-secret, or key material in bound list-item objects.
- Use explicit loading, first-launch, recovery, unlock, unlocked, empty, and error states; never infer security state from control visibility alone.
- Preserve keyboard navigation, visible focus, accessible names, 200% scaling, high contrast, and light/dark theme behavior.

## Component Boundaries

- Extract a helper only for repeated styling or a meaningful layout responsibility.
- Do not build a custom control framework or duplicate service behavior in presentation code.
- Security authorization, vault storage, encryption, clipboard lifecycle, and timeout policy remain outside the theme and layout layer.
- Presentation may coordinate Core services but must not reference WinUI. WinUI may reference Presentation and Core only through composition and platform adapters.
- Synchronous vault operations must cross the serialized background-operation boundary before returning results to the UI thread.

## Cross-Skill Rule

All future UI work must follow this contract and must not introduce a competing styling or naming system.

## Static Website

- `website/` is a separate HTML/CSS/JavaScript surface; the WinUI rules above do not apply to its DOM.
- Preserve its established semantic kebab-case classes (`site-header`, `button-primary`, `extension-visuals`). New classes belong to the responsible surface, such as `demo-panel`, `demo-fields`, and `closing-demo`.
- Shared tokens and controls live in `website/styles.css`; page behavior stays in its own deferred script. Use static stylesheets, explicit labels, and stable class/ID selectors; do not add a framework or rename existing classes.
- Shared header behavior lives in `website/navigation.js`. It runs in the head to set the pre-paint JavaScript marker and attaches menu and header-size handlers on `DOMContentLoaded`; other page scripts remain deferred.
