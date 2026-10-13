# YourSafe Chrome Web Store images

Prepared on 2026-10-13. Upload these PNGs in the Chrome Web Store Developer Dashboard.

| Store field | File | Dimensions |
| --- | --- | --- |
| Store icon | [icon-128.png](icon-128.png) | 128 × 128 |
| Small promotional tile | [promo-440x280.png](promo-440x280.png) | 440 × 280 |
| Screenshot 1: matching accounts | [01-choose-account-1280x800.png](01-choose-account-1280x800.png) | 1280 × 800 |
| Screenshot 2: desktop approval | [02-desktop-approval-1280x800.png](02-desktop-approval-1280x800.png) | 1280 × 800 |
| Screenshot 3: verification code | [03-verification-code-1280x800.png](03-verification-code-1280x800.png) | 1280 × 800 |
| Screenshot 4: approved password fill | [04-password-autofill-1280x800.png](04-password-autofill-1280x800.png) | 1280 × 800 |

The screenshots contain actual UI captures arranged on captioned canvases. They were captured from the unpacked YourSafe development extension in an isolated Google Chrome profile, using the adjacent Debug NativeHost and real WinUI desktop approval. The UI was not recreated for these images.

All accounts are fake `example.test` accounts in a marked disposable vault. The login page is a local demo that never sends a sign-in request. The verification code comes from a public synthetic TOTP secret. No real account data is pictured.

The capture session passed 12 focused assertions covering account selection and unrelated-site exclusion, real desktop approval, verification-code countdown, correct username/password fill, a masked password field, and no automatic form submission. All six image dimensions were checked and the images were visually reviewed. Temporary development registrations were restored and compared with their baseline.

The icon comes from the existing app logo; the promotional tile uses the same logo. Local raw captures, temporary scripts, browser profiles, disposable vault files, and diagnostic reports are not part of this upload pack.

Builds and extension tests passed during image preparation. These images do not establish store approval or production installer/signing qualification. The extension requires YourSafe for Windows with matching Native Messaging registration; see the [extension README](../../README.md) for the integration and release requirements.
