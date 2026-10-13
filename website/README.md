# YourSafe landing page

Static Vietnamese landing page: HTML, CSS, JavaScript, local fonts and captioned product screenshots. No build step, framework, package installation or backend.

## Preview

The homepage source is `website/home.html`. To preview through an existing basic static server, from the repository root when Python is installed:

```powershell
python -m http.server 4173 --bind 127.0.0.1 --directory website
```

Open [the local preview](http://127.0.0.1:4173/home.html). Basic servers such as Python's `http.server` require `.html` when opening a page directly; navigation uses the clean `/home` and `/demo-login` routes supported by GitHub Pages and the verification server. Navigation, FAQ, content and release links work without JavaScript. All three hero articles are visible without JavaScript; enhancement enables unboxed SVG arrows, labeled pagination, arrow/Home/End keys and horizontal touch swipes. Slides advance every five seconds, and pause while hovered, focused or the page is hidden. There is no stop button. Both the outgoing and incoming slides crossfade for 400ms without horizontal movement; rapid navigation cancels the previous fade. Only the selected slide is accessible during a transition. Reduced motion disables autoplay and cancels fades; the enhanced stage keeps the same height across slides. The first slide's footer links to the login demo.

## Publish on GitHub Pages

`website/` cannot be selected as a branch publishing folder: GitHub's branch publishing supports `/` or `/docs`. Use the included GitHub Actions workflow, which uploads only the page files and assets.

1. Commit and push the website and `.github/workflows/website-pages.yml` to the repository's default branch when ready.
2. In the GitHub repository, open **Settings → Pages → Build and deployment → Source → GitHub Actions**.
3. In **Actions**, select **Publish YourSafe website**, then **Run workflow** on the default branch.
4. Open the URL reported by the deployment. The requested public routes are [YourSafe home](https://lmnhutw.github.io/YourSafe/home) and [the login demo](https://lmnhutw.github.io/YourSafe/demo-login); the workflow output is authoritative for the deployment base URL.

The workflow is manual, so editing or pushing app code does not publish a website. It uploads `home.html`, a generated `index.html` copy for the root URL, `styles.css`, `navigation.js`, `script.js`, `demo-login.html`, `demo-login.js`, and `assets/` only; it does not build or publish Windows packages. Page links omit `.html`, while source files retain the extension for correct HTML serving. Deployment status and the configured Pages URL must be checked in GitHub Actions.

Reference: [GitHub publishing-source documentation](https://docs.github.com/en/pages/getting-started-with-github-pages/configuring-a-publishing-source-for-your-github-pages-site).

## Content and releases

The public website and desktop use **YourSafe**; the desktop executable is `YourSafe.exe`, while repository/project names and `%LocalAppData%\PasswordTool` retain `PasswordTool`. The root [README](../README.md) and [release operations](../docs/release-operations.md) describe current behavior. Download buttons lead to the release list, without inventing a current version, available installer or signing status. Asset origins and font license are in [PROVENANCE.md](assets/PROVENANCE.md) and [OFL.txt](assets/OFL.txt).

Features include credentials with recovery codes, local password generation/review, encrypted external backup and optional Chrome/Edge/Brave extension password fill and TOTP viewing. Extension availability is clearly labeled as manual development installation; app-only `-test` packages omit registration. The header's Tiện ích link and closing CTA lead to the extension section. Extension download links beside the Windows buttons and the extension's primary button intentionally use `#` until the owner supplies a store URL; the adjacent trial guide opens Demo. Installation copy covers the installer, ZIP extraction and recovery key, with technical release details linked separately. Screenshots use demo data. Extension captures run actual popup source with synthetic transport responses and do not prove live integration. Product images share a viewport-contained preview on hover or keyboard focus, except the large vault overview and extension/web context images marked `data-no-preview`. Escape, blur or leaving an eligible image closes its preview. Images retain their aspect ratio and rounded corners, and do not link to image files. Captions use centered 0.8rem text at 85% opacity. Touch devices show the original images without a hover overlay. The older `unlock-migration.png` is retained but no longer displayed.

The storage illustration shows the original encrypted vault inside a Windows computer and a separate backup device. A copy moves along the arrow three times when the figure first becomes visible, then stops after 4.8 seconds. The underlined “Bản sao mã hóa” text replays the illustration; it does not export data. Resizing switches the path from horizontal to vertical on mobile. Reduced motion and JavaScript-disabled browsing retain all labels without animation; the replay action is disabled. This interactive illustration stays in place rather than opening an image-hover overlay.

The sticky header keeps navigation and a primary download button available while scrolling. Its homepage height is 68px on desktop and 60px with the mobile menu closed; download actions retain a 44px target. Navigation links underline on hover/focus, and include the login demo. The footer shows the brand description, copyright, LinkedIn/GitHub links and a short invitation to share feedback or ask questions. Inline links open a new GitHub issue page or the visitor's mail app addressed to `lmnhut0211@gmail.com`; both work without JavaScript. There is no feedback form or automatic issue/email sending.

Replacement screenshot requirements and filenames: [captures-inbox/README.md](assets/captures-inbox/README.md). Visible copy before and after: [CONTENT-CHANGES.md](CONTENT-CHANGES.md).

Native Desktop images are now fresh `winapp ui screenshot` captures of an isolated Debug vault with YouTube, Google, GitHub, Facebook, Netflix, Spotify, Microsoft and Discord. Service URLs are real; all credential values are fake. The opt-in seed test is `Create_opt_in_disposable_landing_vault` (`PASSWORDTOOL_LANDING_TEST_DIRECTORY`); Debug must launch with the same directory as `PASSWORDTOOL_UI_TEST_DIRECTORY`. `scripts/Capture-WebsiteDesktop.ps1` verifies the Debug marker before sign-in/navigation/capture. The inbox records which images are native captures, which extension images use synthetic transport, and the remaining real-browser context image.

## Login demo

The header and CTA link to `demo-login`, and links back to the homepage use `home`. The demo's visible address and copy action omit `.html`, even when opened through the legacy `.html` URL. Any username/password (including blank values) succeeds locally after explicit submission. Extension Fill only populates the fields. Submitting or retrying clears the values; leaving the page clears them too. There are no credential requests, logs, cookies, or storage writes. A page CSP blocks form navigation and network connections; inputs stay disabled until the submit handler is attached. The demo does not query extension installation state.

To try the real extension, serve the site over HTTPS or loopback HTTP, unlock YourSafe, enable browser integration, and save a disposable credential with the demo page's exact origin (scheme, host and port). Use the page's address-copy button; GitHub credentials do not match the GitHub Pages site. Select Fill and approve on the desktop, then click Đăng nhập. Opening the HTML as a `file:` URL supports manual demonstration but not extension fill.

`assets/captures-inbox/08-extension-login-context.png` is a labeled illustration rendered from the real demo page and extension popup source, with synthetic transport and fake credentials. It is not live Native Messaging qualification. Reproduce only this capture with `node scripts/Capture-WebsiteExtension.cjs --login-context` using the existing Playwright/esbuild installations; requests for the illustrated public address are intercepted locally.

## Verification

With an existing Playwright installation resolvable by Node (including through `NODE_PATH`):

```powershell
node --check website/script.js
node --check website/navigation.js
node --check website/demo-login.js
node website/verify.cjs
git diff --check
```

`verify.cjs` uses installed Microsoft Edge on Windows, or Playwright Chromium elsewhere, to check 1440px desktop, 768px tablet, 390px mobile and 320px narrow screens, document overflow, assets, menu state and Escape handling, FAQ, clipboard success/failure, reduced motion, JavaScript-disabled behavior, clean routes, and a `/YourSafe/` project base path. Its temporary loopback server mirrors GitHub Pages' extensionless HTML lookup and serves the root from `home.html`. It writes desktop and mobile screenshots to `.impeccable/review/` and closes the server on completion. It makes no external requests and does not touch any vault or desktop application.

The demo checks additionally bundle the installed extension's `fields.ts` with its existing esbuild dependency and exercise field discovery, visibility, origin validation and Fill on the real page. They verify Enter submission, arbitrary/empty input, success/reset/focus, absence of requests/storage, address-copy failures, root/project paths, and disabled inputs without JavaScript or with a failed script. These are browser compatibility checks, not live Desktop/Native Messaging E2E.
