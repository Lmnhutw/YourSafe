# YourSafe landing page

Static Vietnamese landing page: HTML, CSS, JavaScript, local fonts and captioned product screenshots. No build step, framework, package installation or backend.

## Preview

Open `website/index.html` from the repository root (or `index.html` from this folder), or serve this folder with an existing static server. For example, from the repository root, when Python is installed:

```powershell
python -m http.server 4173 --bind 127.0.0.1 --directory website
```

Open [the local preview](http://127.0.0.1:4173). Clipboard enhancement requires localhost or HTTPS; the visible command remains selectable everywhere. Navigation, FAQ, content and release links work without JavaScript. All three hero articles are visible without JavaScript; enhancement enables manual arrows, labeled pagination, arrow/Home/End keys and horizontal touch swipes. No autoplay. Reduced motion disables transitions; the enhanced stage keeps the same height across slides.

## Publish on GitHub Pages

`website/` cannot be selected as a branch publishing folder: GitHub's branch publishing supports `/` or `/docs`. Use the included GitHub Actions workflow, which uploads only the three page files and assets.

1. Commit and push the website and `.github/workflows/website-pages.yml` to the repository's default branch when ready.
2. In the GitHub repository, open **Settings → Pages → Build and deployment → Source → GitHub Actions**.
3. In **Actions**, select **Publish YourSafe website**, then **Run workflow** on the default branch.
4. Open the URL reported by the deployment. For this repository the expected project-site address is [the YourSafe project site](https://lmnhutw.github.io/PasswordTool.Core/); the workflow output is authoritative.

The workflow is manual, so editing or pushing app code does not publish a website. It uploads `index.html`, `styles.css`, `script.js`, and `assets/` only; it does not build or publish Windows packages. Deployment status and the configured Pages URL must be checked in GitHub Actions.

Reference: [GitHub publishing-source documentation](https://docs.github.com/en/pages/getting-started-with-github-pages/configuring-a-publishing-source-for-your-github-pages-site).

## Content and releases

The public website and desktop use **YourSafe**; the desktop executable is `YourSafe.exe`, while repository/project names and `%LocalAppData%\PasswordTool` retain `PasswordTool`. The root [README](../README.md) and [release operations](../docs/release-operations.md) describe current behavior. Download buttons lead to the release list, without inventing a current version, available installer or signing status. Asset origins and font license are in [PROVENANCE.md](assets/PROVENANCE.md) and [OFL.txt](assets/OFL.txt).

Features include credentials with recovery codes, local password generation/review, encrypted external backup and optional Chrome/Edge/Brave extension password fill and TOTP viewing. Extension availability is clearly labeled as manual development installation; app-only `-test` packages omit registration. Screenshots use demo data. Extension captures run actual popup source with synthetic transport responses and do not prove live integration. Images keep full aspect ratio, corners rounded on their own edges, and full-size links. The older `unlock-migration.png` is retained but no longer displayed.

Replacement screenshot requirements and filenames: [captures-inbox/README.md](assets/captures-inbox/README.md). Visible copy before and after: [CONTENT-CHANGES.md](CONTENT-CHANGES.md).

Native Desktop images are now fresh `winapp ui screenshot` captures of an isolated Debug vault with YouTube, Google, GitHub, Facebook, Netflix, Spotify, Microsoft and Discord. Service URLs are real; all credential values are fake. The opt-in seed test is `Create_opt_in_disposable_landing_vault` (`PASSWORDTOOL_LANDING_TEST_DIRECTORY`); Debug must launch with the same directory as `PASSWORDTOOL_UI_TEST_DIRECTORY`. `scripts/Capture-WebsiteDesktop.ps1` verifies the Debug marker before sign-in/navigation/capture. The inbox records which images are native captures, which extension images use synthetic transport, and the remaining real-browser context image.

## Verification

With an existing Playwright installation resolvable by Node (including through `NODE_PATH`):

```powershell
node --check website/script.js
node website/verify.cjs
git diff --check
```

`verify.cjs` uses installed Microsoft Edge on Windows, or Playwright Chromium elsewhere, to check 1440px desktop, 768px tablet, 390px mobile and 320px narrow screens, document overflow, assets, menu state and Escape handling, FAQ, clipboard success/failure, reduced motion, JavaScript-disabled behavior, and a `/PasswordTool.Core/` project base path. It writes desktop and mobile screenshots to `.impeccable/review/` and runs a temporary loopback-only server which is closed on completion. It makes no external requests and does not touch any vault or desktop application.
