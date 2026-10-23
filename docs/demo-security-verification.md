# Báo cáo sửa lỗi và kiểm chứng security cho demo

Ngày ghi nhận: **2026-10-12**. Phạm vi: extension TypeScript → Native Messaging → NativeHost → Windows named pipe → WinUI approval → vault/storage.

## 2026-10-22 approved-flow and protocol v3 verification

This is a separate record for the group/editor/settings/generator and browser-integration changes. The October 12 campaigns below remain historical evidence; they do not qualify this later build.

On October 23, the user accepted the current UI, explicitly stopped further UI testing, and then chose to omit installer/portable testing and finalize implementation. Implementation is closed under that accepted scope. Remaining UI and packaging checks are waived, not passing tests; the observations and limitations below are retained as evidence. This closure does not designate or qualify a signed/store release.

| Boundary | Verified result | Evidence limits |
| --- | --- | --- |
| Core | **330/330 PASS** | Includes group migration, persistence rollback and legacy-data compatibility. Controlled tests do not establish installed-app behavior. |
| Presentation | **156/156 PASS** | Includes editor validation, settings authorization, actual deadline-based status, independent control/secret quotas, pending approval and stale clipboard guards. Clipboard and approval doubles remain test doubles. |
| Desktop and NativeHost | **WinUI Debug and Release x64 builds PASS; NativeHost build and development publish PASS** | Compilation and publishing do not qualify an installer or portable distribution. |
| Extension | **Typecheck, tests and build PASS** | Shared C#/TypeScript fixtures cover protocol v3, explicit incompatible-version rejection and both status flags. |
| Browser DOM | **42 checks PASS** | Browser field/form behavior is separate from an installed-extension/native-host campaign. |
| Popup in a real browser with a synthetic worker | **OFF/pending states, keyboard behavior, Light/Dark and forced-colors checks PASS** | This verifies the actual popup markup/styles with controlled messages; it does not prove live native routing or Windows notifications. |
| Real NativeHost process and marked disposable WinUI | **20/20 verified** | Malformed/fragmented framing; explicit v1/v2 mismatch; OFF controls and metadata denial; EOF/extra-frame/terminated-host cancellation; competing-client denial; independent Open YourSafe while approval queues behind an existing dialog; fresh consent for the next request; global 30/minute admission. |
| Selected Chrome follow-ups on October 23 | **E11: 1/1 verified; E12/E14: 10/10 verified** | Separate fresh one-minute and thirty-minute synthetic vaults, actual development extension/NativeHost/WinUI, and restored registration/manifest/environment baselines. These selected campaigns do not qualify Release or turn the older failed campaign into a full pass. |
| Runner checks | **PASS** | PowerShell parsing, 13 JSON/error cases, six synthetic OTP vectors, eight retry checks, Node syntax and window-interop compilation. These checks do not drive a desktop. |

The real process report is `test-results/requested-flows-20261022-main/ui-results/native-host-process.json`, using the adjacent Debug NativeHost and synthetic desktop PID 45228. Its queued-dialog case ran before the later minimize/restore assertions were added. No minimized-window or notification result is inferred from that report.

Not established by these runs: taskbar attention; notification delivery, warm/cold activation and stale-notification cleanup; complete native UI appearance/keyboard/minimum-size acceptance; and installed/portable qualification. Further UI and packaging tests were subsequently waived as recorded above. The notification probe reports `observed`, `notObserved` or `unavailable`; absence is never counted as a passing notification test. No production vault, browser profile or production registration is covered by this record.

Real toast delivery and warm/stale/cold activation were not verified. Any future notification or exact Release runtime qualification would require an isolated Windows profile or VM. The [SDK's unpackaged COM registration](https://github.com/microsoft/WindowsAppSDK/blob/main/dev/AppNotifications/AppNotificationUtility.cpp#L103-L123) records the executable and activation argument, not the prior Debug process's fixture environment. Debug instances also share one pipe namespace per Windows user/session despite separate fixture directories. Source review, a manual relaunch with fixture variables, and foreground restoration do not establish notification activation. No further UI or packaging campaign is scheduled under the accepted closure scope.

The later native process campaign (`test-results/requested-flows-20261022-browser/ui-results/native-host-process.json`, PID 42760) verified the twenty process scenarios again and recorded a separate window observation: both approval attention and independent Open YourSafe restored the minimized window, made it visible and reached foreground. Existing-dialog preservation, no early secret response and pending cleanup also passed. Notification delivery is not inferred from successful foreground activation.

The subsequent Vault-notice follow-up passed on Debug PID 12360 at 900 DIP width: the integration-off notice appears only on Vault, Open Settings hides it, the native accessible close button is six pixels from the right edge, and explicit dismissal survives navigation and integration toggles for the current process. Light-theme screen captures were reviewed. Evidence: `test-results/requested-flows-20261022-browser/ui-results/integration-notice.json`, `integration-notice-light-min900.png` and `integration-settings-without-notice.png`. The blue brush uses alpha without reducing text/button opacity; High Contrast has system-brush fallbacks. Actual High Contrast rendering remains unverified.

The later real Chrome 155.0.8059.85 campaign verified **36 cases** through E10, including pending popup reopen/Open YourSafe, real clipboard timer/lock/current-code checks, target changes, OFF/lock cancellation, edits/deletion and worker restart. Its overall report remains **failed**, because the original empty selection also attempted E11 against the thirty-minute fixture. The expiry helper rejected that prerequisite; no expiry pass is claimed. Evidence: `test-results/requested-flows-20261022-browser/extension-e2e-results-fe690fa7-6b43-495d-8fbb-43d0d00b5107/results.json`. Registration, manifest and environment cleanup comparisons all match their baselines. The runner default now excludes E11; select it explicitly with a fresh one-minute fixture.

On October 23, Chrome 155.0.8059.85 verified E11 against a new one-minute fixture (`test-results/requested-flows-20261023-browser-expiry/extension-e2e-results-0ad5815b-d518-4f03-a2ec-b5537fec9ec4/results.json`). Natural vault expiry removed the pending approval, delivered no password or TOTP, and left the popup locked. That synthetic app closed normally before a separately seeded thirty-minute app was launched. The latter verified exactly the nine E12 clipboard cases and E14 desktop-close cancellation (`test-results/requested-flows-20261023-browser-clipboard/extension-e2e-results-72f23c90-7725-44ce-a92d-e41eef42d617/results.json`). Timer/lock cleanup, same-text and changed-text replacement preservation for OTP/password, no delivery after close, and unavailable popup state all passed. No desktop/NativeHost process remained after cleanup.

Both selected campaigns restored all development Native Messaging keys, the manifest and process environment to their baselines; each fixture retains its `ui-results/native-registration-cleanup.json`. Their runner/control/worker hashes match the current files. The clipboard fixture also records five payload hashes observed during the live campaign and unchanged afterward (`runtime-payload-observed.json`, `campaign-verification.json`); that is not a pre-launch baseline or Release qualification. The old report's worker still matches, but its runner/control hashes differ. The three reports contain 47 unique verified Chrome cases in aggregate; they remain separate evidence and do not constitute one full-green campaign. Distinct-browser competition, actual Windows notification activation and exact installed/portable qualification remain unproven.

Release payload preparation and static payload/source/offline/installer-template checks passed under `artifacts/requested-flows-20261022-release`. The two executable hashes are recorded there. This folder has no release designation: the payload was not launched, installed, signed or shipped, and no installer was built because Inno Setup was unavailable. Exact Release runtime qualification needs a disposable Windows profile or VM; Debug-only vault/appearance/mutex overrides do not isolate Release storage or HKCU notification/installer state. Synthetic release-pipeline and qualification checks also passed independently.

That prepared payload predates the Vault-notice follow-up and must be regenerated before Release runtime qualification. Native UI batches also remain partial: hash/verify/inspect, backup layout, unlocked editor, required-field live errors and several minimum-width captures ran, but tooltip/foreground/selection preconditions interrupted the complete batches. A separate focused Enter-key probe displayed the recovery tooltip. Preserve the failed batch reports; do not treat these selected observations as full native UI acceptance. One older disposable process displayed a generic fatal dialog during close; later initial, signed-in and post-native-campaign closes exited normally, so its cause remains unconfirmed. Temporary exception-type/stack diagnostics were removed.

The current Core suite passed **330/330** after fixing a real CSV-import compatibility bug: a passwordless TOTP-only entry no longer receives a password-change timestamp. The new regression covers preview, import, duplicate rejection, metadata/TOTP preservation, persistence and reopening. The focused CSV/lifecycle/rollback checks passed 32 cases before the full suite; Presentation then rebuilt against the updated Core and passed **155/155**.

A later Presentation run passed **156/156** after extending the existing duplicate/edit-data regression to cover both All (`GroupId = null`) and a named source group. It verifies the group returned for editing and the saved duplicate, then moves the original to a different group and checks Trash/restore data preservation. Evidence: `test-results/requested-flows-20261022-group-retention/presentation-group-retention-20261022.trx`. This closes the Presentation assertion gap; actual WinUI non-All picker selection remains a separate runtime check. No production source changed in that extension of the regression.

The latest row/URL follow-up passed four focused checks at 900 DIP on a fresh synthetic vault (`test-results/row-layout-20261022-v1/row-layout-results.json`): Recovery backup health and its action share a row; save hint and Cancel/Save share a row; and URL guidance opens with Enter even with No URL selected. All three screen captures were reviewed. The new URL help sits outside the disabled TextBox, with its own accessible name and guidance.

A separate real one-minute expiry campaign on PID 16216 verified timeout clearing a draft without TOTP. Its next fresh-editor assertion failed with a title different from the injected fixture, so that campaign remains inconclusive; no retained-draft defect is concluded. Its report and captures are preserved under `test-results/requested-flows-20261022-expiry/ui-results-attempt1-20261022`. No claim is made for its unreached TOTP/settings-prompt timeout cases. That runner now skips screen captures when its marked app is not foreground rather than recording another app's visible screen.

On October 23, after the user reserved the test windows, a fresh current Debug build and sign-in on PID 13320 verified all four editor timeout/re-unlock cases, with and without TOTP. The five-case runner stopped at Settings because the name `2 minutes` matched both a ComboBoxItem and its child TextBlock. Its four PASS results and the failed prerequisite remain in `test-results/requested-flows-20261022-expiry/ui-results/editor-lock-security-ui.json`. Both affected runners now wait for the duration ComboBox's expanded state before selecting the visible ListItem. A settings-only derivation of the same case then passed in `editor-lock-security-settings-rerun.json`, with extraction provenance saved beside the fixture. Natural expiry dismissed the password prompt; the selected two-minute duration was not persisted, the configuration hashes were identical, and the stored duration remained one minute. These are separate reports, not a single full-green campaign. All three available captures were reviewed; the other three captures were skipped because the app was not foreground. Normal close of this synthetic process also passed (`normal-close-20261023.json`). No production code changed for this runner correction; the old close error remains unconfirmed.

A fresh current candidate was prepared after the CSV and row/URL changes under `artifacts/requested-flows-20261022-release-current`: desktop/NativeHost Release publishes and production-extension build succeeded, all thirteen candidate file checksums verified, and 163 source fingerprints stayed unchanged during compilation. The previous preparation folder remains historical. The current candidate is also unqualified for release: no installer/runtime/notification/signing/shipping pass is claimed; see its `payload-preparation.json` and `candidate-checksums.sha256`.

## Lượt tiếp tục từ commit `af92805`

Người dùng đã yêu cầu tiếp tục các phần chưa hoàn thành trong commit gần nhất. Bằng chứng mới dưới đây được tách khỏi checkpoint cũ; không cộng các partial campaign để tuyên bố một lượt full green.

| Phần đã kiểm tra lại | Kết quả mới | Phạm vi |
| --- | --- | --- |
| Core | **318/318 PASS** | Thêm opt-in snapshot UI fixture; seed thật chạy riêng **1/1 PASS**. Fixture chỉ chứa credentials giả. |
| Presentation | **137/137 PASS** | Bao gồm OS pipe peer và lifecycle/clipboard mocks; không coi mocks là Windows lifecycle thật. |
| HTTP API | **PASS** | Smoke thật cho roundtrip/no-store, policy, body/password/hash/KDF limits, malformed input, Host, concurrency và rate. API build còn một warning CA1416 hiện hữu, không có lỗi. |
| DOM browser | **42 HTTP + 44 HTTPS PASS trên từng Chrome, Edge, Brave** | Chạy riêng từng browser; HTTPS dùng certificate giả trong isolated context, không sửa Windows trust. |
| Historical snapshot WinUI | **7/7 PASS** | Hai password inputs; cancel/failure clearing được chứng minh bằng rejected reuse và unchanged pair; success kết thúc session; current replacement password bị từ chối, historical password + Authenticator được chấp nhận. Đã xem tám screenshots. |
| Group deletion WinUI | **3/3 PASS** | Sửa phrase UI có chữ `the` khác phrase Core; xác nhận/cancel/delete thật trên group rỗng giả do batch tạo. Cancel giữ hashes; delete giữ mọi group và credentials fixture trước đó. Đã xem hai screenshots. |
| NativeHost process → WinUI | **17/17 PASS** | Chạy lại executable adjacent Debug sau build, gồm global 30/minute admission across new hosts. |
| Hai desktop Debug | **15/15 guarded control actions PASS** | Hai disposable directories khác nhau, integration cùng bật; lock/unlock/disable/close vẫn responsive. `.config`/`.storage` hashes cả hai giữ nguyên và cả hai process kết thúc. Không phải browser secret routing giữa hai vault hay production installation qualification. |
| Runner checks | **PASS** | 13 JSON/error checks, 6 independently pinned SHA1/SHA256 OTP vectors, 8 read-only retry/no-mutation-retry checks; 14 snapshot parser checks; staging write failure/hash/path checks; clipboard wrong-owner negative checks không đọc/đổi clipboard có sẵn. |
| Release scripts | **PASS** | Synthetic pipeline/qualification và Chrome/Edge/Brave registration contracts; không phải installer/signature acceptance thực tế. |
| WinUI build | **0 warnings, 0 errors** | Debug x64 sau group-dialog và background clipboard cleanup fixes. NativeHost self-contained development publish thành công. |

Runner đã sửa để giữ lỗi WinApp gốc thay vì lỗi thiếu `matchCount`, chỉ re-resolve một lần cho read-only `stale_element`, và không tự replay mutation. Clipboard helper kiểm tra owner/sequence ngay trong lock trước khi clear hoặc same-text replacement. Ca Copy gần hết hạn chỉ chấp nhận OTP của kỳ hiện tại. Development manifest được stage trước khi publish; wrapper khôi phục manifest, environment và cả value/type của Chrome/Edge/Brave registration, rồi đọc lại baseline để ghi report.

Chrome, Edge và Brave đã hoàn tất **47/47 verified trên từng campaign**, overall `verified`, trên cùng build có background clipboard cleanup fix. Đã đối chiếu cả ba danh sách case IDs và source hashes giống nhau; không cộng partial runs. Scope chọn `E01,E02,E03,E04,E05,E06,E07,E08,E09,E10,E12,E14`; natural one-minute expiry E11 chạy riêng.

| Campaign mới | Browser/version | Kết quả | Evidence |
| --- | --- | --- | --- |
| Edge main | Edge **155.0.4283.45**, Chrome phụ **154.0.8037.98** | **47/47 verified** | `test-results/security-resume-edge/extension-e2e-results-f46dd7b1-ac45-437b-bb88-783cb731adc1/results.json` |
| Chrome main | Chrome **154.0.8037.98**, Edge phụ **155.0.4283.45** | **47/47 verified** | `test-results/security-resume-chrome/extension-e2e-results-c12dba71-bf4c-4c25-8a6e-1735a72477a7/results.json` |
| Brave main | Brave/Chromium **155.0.8059.40**, Chrome phụ **154.0.8037.98** | **47/47 verified** | `test-results/security-resume-brave/extension-e2e-results-a3bb01a2-5419-4570-8f1d-adff077cd53b/results.json` |
| Edge natural expiry | Edge **155.0.4283.45** | **1/1 verified** | `test-results/security-resume-expiry/extension-e2e-results-28250c4e-f0e0-4217-a713-89739ce753a3/results.json` |
| Chrome natural expiry | Chrome **154.0.8037.98** | **1/1 verified** | `test-results/security-resume-expiry-chrome/extension-e2e-results-53a80fb4-208c-453b-b9f8-6236190e9a0e/results.json` |
| Brave natural expiry | Brave/Chromium **155.0.8059.40** | **1/1 verified** | `test-results/security-resume-expiry-brave/extension-e2e-results-b87ec42e-0a59-46b4-80a9-669f6155df42/results.json` |

Cả sáu lượt đã kiểm tra độc lập registration/manifest/environment restoration: các baseline comparisons đều true và không có cleanup failure. E11 dùng ba fixture được seed một phút trước process launch, đặt lại vault deadline rồi chờ tự hết hạn; approval bị hủy, field không có password, code panel rỗng và popup báo locked. Các lượt E11 mất khoảng 79 giây, không đổi Windows clock. Ba desktop E11 đã đóng bình thường sau từng lượt.

Đã launch một desktop Debug thứ hai dùng **cùng marked disposable directory**: dialog `YourSafe is already running.` xuất hiện, dismiss dialog rồi tiến trình thứ hai kết thúc; tiến trình gốc và marker được giữ. Evidence: `test-results/security-resume-brave/ui-results/single-instance-ui.json`. Đây là runtime denial của duplicate Debug instance cùng fixture; không coi là qualification nhiều production installation hoặc các Debug fixture khác nhau.

Sau browser campaigns, đã chạy thêm hai process Debug dùng **hai marked directories khác nhau**, `security-resume-edge` và `security-resume-chrome`. Cả hai sign-in và bật integration đồng thời; process thứ hai vẫn lock/unlock/disable/enable và đóng bình thường; process thứ nhất vẫn lock/unlock/disable và đóng bình thường. **15/15 guarded control actions PASS**, unchanged pair hashes, không có cleanup error. Command: `pwsh -NoProfile -File test-results/Test-CompetingDisposableDesktops.ps1`; source và report `test-results/security-resume-competing-desktops.json` là local ignored evidence. Scope là UI responsiveness khi hai development listeners cạnh tranh; không kiểm chứng browser chọn vault nào hoặc nhiều production installation.

Windows clipboard mở được trong read-only probe ở session 2 / `WinSta0` / `Default` và các real copy/read checks của lượt Edge mới; error 5 của checkpoint cũ chưa tái hiện ở đây và chưa có bằng chứng để quy kết nguyên nhân của lỗi cũ.

Lượt Edge `extension-e2e-results-d67ddd51-8775-4413-ac26-5bf7d96ea007` ghi **36 ca verified và một ca failed** tại password clipboard cleanup sau 30 giây; không gọi lượt này full green. Các ca đã đạt gồm remove-password/reset, queued deletion, ba image submitters, worker restart, hai profile và Chrome/Edge đồng thời. Ca password riêng đã tái chạy **1/1 PASS** trong `extension-e2e-results-8bb340f1-0a8c-4966-a644-8be4f9e449e5` sau sửa service chung; không cộng hai lượt để chốt acceptance.

Cleanup trong `SensitiveClipboardService` đã bỏ WinRT `Clipboard.GetContent` để đọc bounded UTF-16 bytes và so sánh hash ngay trong native clipboard lock. Owner, sequence và generation vẫn được kiểm tra; buffers/hash được zero và handle được unlock trước khi clear. Thay đổi ảnh hưởng cùng boundary của password, OTP, recovery/setup và reveal/history copy, không đổi interface. [Microsoft ghi rõ WinRT Clipboard cần app có focus](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.datatransfer.clipboard). Đây là dependency không phù hợp với background cleanup; nguyên nhân đầy đủ của mọi lỗi clipboard ở checkpoint cũ vẫn chưa được chứng minh. Lượt Edge green gồm approved SHA256/eight-digit/sixty-second account copy, password/OTP cleanup, và toàn bộ tám same-text/changed-text × timer/lock replacement checks.

Bằng chứng mới dùng local ignored artifacts:

- Snapshot: `test-results/security-resume-snapshot/ui-results/snapshot-restore-20261012-141647-d66eb567/snapshot-restore-security-ui.json` và tám PNGs cạnh đó.
- Group: `test-results/security-resume-edge/ui-results/group-deletion-confirmation-ui.json`.
- NativeHost: `test-results/security-resume-edge/ui-results/native-host-process.json`.
- Restoration: `ui-results/native-registration-cleanup.json` trong từng fixture của sáu campaign; cả ba keys, manifest và bảy environment values khớp baseline, không có cleanup failure.
- Campaign index: `test-results/security-resume-summary.json`; kiểm tra lại sáu complete reports và hash của runner/control/development worker, clipboard source, WinUI DLL, adjacent NativeHost đều khớp files hiện tại.
- Lượt đầu bị lỗi runner vẫn giữ riêng: Edge `extension-e2e-results-ce64fc26-0252-4896-a928-61fcacfc24a1/results.json` dừng sau E01 tại stale toggle query; snapshot `ui-results/snapshot-restore-security-ui-first-failed.json` và group `ui-results/group-deletion-confirmation-ui-first-failed.json` ghi selector failure trước khi sửa.

Final cleanup: các desktop/PIDs đã ghi nhận và các isolated E2E browser processes đều kết thúc; không còn YourSafe/NativeHost process trong inventory cuối. `git diff --check` và parser checks cho các PowerShell runners PASS. Thay đổi vẫn uncommitted; không publish, sign hoặc đổi production registration.

### Các gate còn lại của lượt hiện tại

| Gate | Trạng thái và giới hạn |
| --- | --- |
| Windows lock/suspend thật | **Not run**. Cần người dùng cho phép trước vì làm gián đoạn desktop. Không coi table lock, natural vault expiry hoặc mocked lifecycle là OS lock/suspend. |
| Queued write khi Windows lifecycle event đến trước UI callback | **Partially verified**. Presentation tests chặn write sau invalidation; chưa chứng minh timing của SystemEvents → WinUI dispatcher trên Windows thật. Không dùng clipboard timer/replacement PASS để thay thế gate này. |
| Login expiry | **Partially verified**. Deadline 300 phút đã có clock-controlled tests; chưa chờ hết 300 phút trên installed-extension campaign. App không có logout button riêng; normal desktop close đi qua logout được kiểm tra trong E14. |
| Nhiều desktop | **Partially verified**. Duplicate Debug instance cùng directory bị chặn thật; hai directories khác nhau có integration đồng thời vẫn lock/unlock/disable/close được, unchanged pair hashes. Chưa qualification browser secret routing giữa hai test vault hoặc nhiều production installation. |
| Distribution | **Outside local demo scope**. Chưa chạy clean-machine installer/upgrade/uninstall, portable distribution, production signing hoặc browser-store registration. |

Connected review đã trace callers của clipboard service, dialog confirmation, snapshot flow và các runner/error/cleanup paths. Không tìm thấy ASP.NET Core hay TypeScript production boundary cần thay đổi thêm trong lượt này; regression và HTTP/runtime checks được ghi riêng bên trên. GitNexus chưa index repository này nên caller/source tracing dùng `rg` và đọc source/tests trực tiếp.

### Lệnh tái hiện các campaign mới

Fixture phải chứa dữ liệu giả và app Debug có HelpText `YourSafeDisposableUiTest` với đúng directory/PID. Seed mỗi fixture mới bằng `Create_opt_in_disposable_ui_vault`, `PASSWORDTOOL_AUTOFILL_TEST_ORIGIN=http://127.0.0.1:55275`; main campaign dùng mặc định 30 phút, E11 dùng `PASSWORDTOOL_UI_TEST_VAULT_MINUTES=1` từ trước khi launch process mới. Không seed vào directory đã có vault.

```powershell
# Cùng lệnh và CasePrefix cho từng Chrome, Edge, Brave; mỗi browser có fixture/profile riêng.
$env:YOURSAFE_SECOND_BROWSER_EXECUTABLE = '<different installed browser executable>'
pwsh -NoProfile -File scripts/Test-AutofillNativeIntegration.ps1 `
  -ProcessId <marked-app-pid> -TestDirectory '<fresh synthetic main fixture>' `
  -BrowserExecutable '<installed browser executable>' -PlaywrightPackage '<installed playwright package>' `
  -FixturePort 55275 -CasePrefix 'E01,E02,E03,E04,E05,E06,E07,E08,E09,E10,E12,E14' `
  -AllowExistingRegistrationOverride

# E11 chạy riêng trên mỗi browser với app mới đã cache setting một phút.
pwsh -NoProfile -File scripts/Test-AutofillNativeIntegration.ps1 `
  -ProcessId <marked-one-minute-app-pid> -TestDirectory '<fresh synthetic one-minute fixture>' `
  -BrowserExecutable '<installed browser executable>' -PlaywrightPackage '<installed playwright package>' `
  -FixturePort 55275 -CasePrefix 'E11' -AllowExistingRegistrationOverride
```

Executable đã dùng: Chrome `C:\Program Files\Google\Chrome\Application\chrome.exe`, Edge `C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe`, Brave `C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe`. Playwright package: `C:\Users\lmnhu\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\node_modules\playwright`. Mỗi report lưu browser version và SHA-256 của runner/control/worker; `ui-results/runtime-build-hashes.json` lưu source clipboard service, WinUI DLL và adjacent NativeHost executable đã dùng.

## Checkpoint trước khi tiếp tục

Tại checkpoint cũ, goal đã **paused theo yêu cầu người dùng**, chưa complete. R1–R4 đã triển khai và qua regression; R5 mới xác minh một phần. **Checkpoint cũ chưa chốt đủ điều kiện demo toàn bộ flow.** Trạng thái sau khi tiếp tục nằm ở phần đầu báo cáo.

Các app/browser test đã dừng. Đăng ký NativeHost development HKCU32 của Chrome và Edge đã được kiểm tra khôi phục đúng giá trị và kiểu dữ liệu ban đầu. Không thay đăng ký production, không sửa vault/profile thật, không thực hiện signing hay deployment.

Báo cáo này ghi lại kết quả của các lượt đã chạy, không phải cam kết rằng mọi artifact sau này đều đã được qualification. Unit/mock, DOM browser, OS pipe peer, NativeHost process và installed-extension E2E là các lớp bằng chứng riêng.

## Các phần đã sửa

| ID | Hành vi sau sửa | Vị trí chính |
| --- | --- | --- |
| R1 | Timeout xóa password, recovery codes, notes, draft TOTP, baseline và item reference; chuyển về Vault trước khi chờ input. Không tự mở unlock dialog để giữ bản nháp. Bỏ cờ giữ khóa xuyên thời gian chờ người dùng; callback cũ không được phục hồi editor sau lifecycle change. | `src/PasswordTool.WinUI/MainPage.xaml.cs`, `NavigationDialogService.cs`; lifecycle tests trong Presentation |
| R2 | Kiểm tra các image submitter thuộc form, gồm `input[type=image]` bên trong và bên ngoài qua `form=`. Kiểm tra lại đích submit sau username events; chặn cross-origin và HTTPS → HTTP. | `browser-extension/src/fields.ts`, `test/browser-smoke.ts` |
| R3 | Ghi thất bại hoàn tác thay đổi RAM cho delete, restore, permanent delete, group, CSV import, purge và normalization. Lần save thành công sau đó không commit thao tác đã báo lỗi. Giữ mutation counter và không phục hồi dữ liệu sau lock. | `src/PasswordTool.Core/Services/VaultService.cs` |
| R4 | Master Password hiện tại xác nhận quyền thay vault; password lịch sử xác thực snapshot khi khác. Xác thực wrapped key, Authenticator, ciphertext/tag và dữ liệu giải mã trước commit. Snapshot dùng credentials cũ vẫn restorable; restore thành công kết thúc session. | `VaultService.cs`, `VaultStorageService.cs`, `AppFlowCoordinator.cs`, `BackupViewModel.cs`, `MainPage.xaml` và code-behind |

Các sửa bổ sung phát hiện khi kiểm chứng:

- Bắt `InvalidDataException` tại NativeHost và WinUI pipe listener. Malformed frame thất bại an toàn, không để exception/stack trace thoát ra stdout/stderr.
- Từ chối snapshot có item/group `null` thay vì để `NullReferenceException` thoát ra.
- Đưa trusted-token revocation vào bước trước khi thay live pair. Nếu revocation thất bại, restore không thay vault hiện tại. Nếu token đã bị thu hồi rồi bước ghi sau đó thất bại, có thể cần sign-in mới; đây là tác động availability, không phải mất vault.
- Thêm ô password lịch sử cho snapshot và xóa cả hai ô password sau mỗi restore attempt.

Không thêm dependency mới, không hạ approval/origin/rate policy và không thay đổi autocomplete-case behavior đã được xác nhận là browser normalization.

Thay đổi có sẵn về notification `IsSignedIn` trong `ShellViewModel` và test binding tương ứng được giữ nguyên; không tính là finding mới của lượt remediation này.

## Tests và công cụ bổ sung

- `tests/PasswordTool.Core.Tests/VaultPersistenceRollbackTests.cs`: 29 ca fault/rollback/lifecycle.
- `tests/PasswordTool.Core.Tests/VaultSnapshotVerificationTests.cs`: 16 ca tamper, historical credentials, lifecycle và write failure.
- `BackupViewModelTests` và `RecoveryAndActionFlowTests`: historical-password caller, cancelled/stale confirmation và stale re-unlock.
- Fixture opt-in hỗ trợ origin loopback riêng và password-only account bằng dữ liệu giả.
- `scripts/Test-EditorLockSecurityUi.ps1`: timeout thật một phút trên WinUI, guarded bằng disposable marker.
- `scripts/AutofillTestControl.ps1`: điều khiển app test và clipboard probe với marker/PID guard.
- `scripts/Test-AutofillNativeIntegration.ps1`: temporary development registration, khôi phục trong `finally`, hỗ trợ NativeHost-only và case-prefix selection.
- `browser-extension/test/extension-e2e.mjs`: profile riêng, extension cài thật, popup thật, approval thật, kết quả từng ca.
- `browser-extension/test/native-host-process.mjs`: framing/cancellation/concurrency/admission qua executable NativeHost thật.
- DOM runner hỗ trợ HTTPS fixture tự ký; chỉ isolated test context bỏ qua certificate error, không sửa certificate trust của Windows.

Các runner còn có lỗi/chưa qualification đầy đủ như liệt kê bên dưới. Việc script parse/build được không đồng nghĩa mọi nhánh đã chạy thành công.

## Kết quả đã xác minh

| Lớp | Kết quả | Giới hạn bằng chứng |
| --- | --- | --- |
| Core | **317/317 PASS** | Gồm 29 rollback và 16 snapshot verification cases; fault injection là controlled test, không phải filesystem failure trên UI thật. |
| Presentation | **137/137 PASS** | Có lifecycle/approval/snapshot caller và Windows OS pipe-peer test. Approval/clipboard mocks vẫn là mocks. |
| Extension | Typecheck, mock tests, development/production build và sáu fixture self-check PASS | Không thay thế installed-extension E2E. |
| DOM Chrome | **42 HTTP + 44 HTTPS PASS** | Actual browser DOM, gồm image submitters, event mutation, field eligibility/visibility và HTTPS downgrade; không đi qua NativeHost approval. |
| DOM Edge | **42 HTTP + 44 HTTPS PASS** | Cùng phân biệt lớp bằng chứng như Chrome. |
| WinUI editor timeout | **4/4 PASS** | Natural one-minute timeout với/không có TOTP draft; cancel/wrong password và fresh empty editor. Bốn screenshots đã được xem. Protected PasswordBox/managed references không thể đọc đầy đủ qua UIA; clearing references được rà từ source. |
| NativeHost process → WinUI | **17/17 PASS** | Actual adjacent development executable, verified Windows pipe peer và disposable vault. Chi tiết bên dưới. |
| API HTTP security smoke | PASS | Roundtrip/no-store, educational policy, body/password/hash/KDF limits, malformed input, hostile Host, concurrent work rejection và global rate limit. |
| Release scripts | Pipeline, qualification, browser registration checks PASS | Synthetic release artifacts; không qualification installer/signature/store thực tế. |
| Build | WinUI và NativeHost build thành công, 0 warning/error trong các lượt build này | Không phải production signing/package acceptance. |
| Whitespace | `git diff --check` PASS ở checkpoint đã ghi nhận | Chỉ kiểm tra whitespace. |

17 ca NativeHost đã đạt: zero length, oversized frame, truncated header/body, malformed UTF-8/JSON, duplicate property, unknown property, wrong version, unknown action, fragmented valid discovery, pending approval cancelled by EOF/extra frame, competing client denied, host termination cancels prompt, second request needs fresh consent, và global admission across new clients. Lượt 31 discovery qua các NativeHost riêng nhận đúng 30 request và từ chối request tiếp theo, không mở thêm approval.

## Installed-extension E2E

Browsers đã dùng: **Chrome 154.0.8037.98**, **Edge 155.0.4283.45**. Các lượt dùng profile riêng và vault giả; fixture HTTP chỉ bind `127.0.0.1`, port **55275**. Default port 49173 bị Windows từ chối nên fixture/seed cùng chuyển sang port khả dụng; không nới origin policy.

| Browser | Bằng chứng đã lưu | Kết luận |
| --- | --- | --- |
| Chrome | Campaign lưu gần nhất có **8 ca verified**: integration off, desktop locked, normal/password-only/dynamic fill, password-only account, cancel và approval timeout. | **Partially verified**; dừng ở clipboard, chưa có full qualification trên source runner cuối. Các campaign trước có thêm quan sát nhưng không dùng để tuyên bố một lượt full green. |
| Edge | Hai campaign có **24 ca verified khác nhau**: các flow nền tảng như Chrome; thêm TOTP-only view, password-account TOTP view không fill, code expiry không tự xin approval, popup close/reopen chỉ fill một lần, hash/reload/navigation/history/tab/window/document change, background-tab isolation, pending lock/disable/password edit/URL edit. | **Partially verified**; có blocked clipboard và failed cases dưới đây. Không phải 24/24 của toàn ma trận. |

Các ca chưa đạt:

1. **Clipboard probe bị blocked:** Windows trả **error 5, open holder PID 0**. Tình trạng vẫn xảy ra khi probe được launch qua Windows Shell trong hidden window. Desktop availability check đã qua nhưng clipboard access chưa được xác minh. Không đọc clipboard cũ hoặc đổi Windows security settings để vượt giới hạn.
2. **Approved Copy OTP trên Edge failed:** popup sau approval trả `Choose Fill or View TOTP for an account.` thay vì `Copied the current code`. Chưa xác định do application behavior, expiry, môi trường clipboard hay runner timing. Chưa có bằng chứng secret leak; cũng không coi copy flow là đạt.
3. **Pending remove-password failed ở cleanup:** runner dừng tại `resetPassword` vì JSON UIA thiếu `matchCount`. Remove-password/cancellation được thực hiện một phần, nhưng toàn ca chưa đạt và fixture không được coi là đã khôi phục. Cần fixture mới cho lượt sau.

Runner đã có cách ghi clipboard access-denied là `blocked` và giữ nonzero exit status; các ca độc lập có thể tiếp tục. Điều này không biến blocked case thành PASS hay xóa các failed records trước đó.

## Evidence local

Các đường dẫn dưới đây tương đối với repository. `test-results`/browser profiles là local ignored artifacts, **không được đưa lên Git** trong commit này. Báo cáo và source tests được version control; muốn tái xác minh cần rerun hoặc giữ riêng các artifact local này.

| Artifact | Đường dẫn |
| --- | --- |
| WinUI four-case report/screenshots | `test-results/security-editor/ui-results/editor-lock-security-ui.json` và `editor-security-*.png` |
| NativeHost 17-case report | `test-results/security-extension-edge/ui-results/native-host-process.json` |
| Chrome latest partial report | `test-results/security-extension-chrome2/extension-e2e-results/results.json` |
| Edge first campaign | `test-results/security-extension-edge/extension-e2e-results-13c5b275-b32f-483f-adb2-0747f91e69a6/results.json` |
| Edge additional campaign | `test-results/security-extension-edge/extension-e2e-results-40357db6-7a69-46ec-bc8c-008fa918fe7a/results.json` |
| Registration baseline | `test-results/security-extension-edge/ui-results/native-registration-before.json` |

Các lệnh chính đã chạy từ repository root (npm commands chạy trong `browser-extension`):

```powershell
dotnet test tests/PasswordTool.Core.Tests/PasswordTool.Core.Tests.csproj --no-restore --verbosity minimal
dotnet test tests/PasswordTool.Presentation.Tests/PasswordTool.Presentation.Tests.csproj --no-restore --verbosity minimal
dotnet build src/PasswordTool.WinUI/PasswordTool.WinUI.csproj -c Debug --no-restore -p:Platform=x64
dotnet build src/PasswordTool.NativeHost/PasswordTool.NativeHost.csproj -c Debug --no-restore
pwsh -NoProfile -File scripts/Test-PasswordApiSecurity.ps1
pwsh -NoProfile -File scripts/Test-ReleasePipeline.ps1
pwsh -NoProfile -File scripts/Test-ReleaseQualification.ps1
pwsh -NoProfile -File scripts/Test-BrowserIntegration.ps1

# Trong browser-extension:
npm.cmd run typecheck
npm.cmd test
npm.cmd run build
npm.cmd run build:production
npm.cmd run test:browser:build
npm.cmd run test:e2e:self-check
node test/browser-smoke.mjs
```

DOM runner dùng `YOURSAFE_PLAYWRIGHT_PACKAGE` trỏ tới Playwright đã cài và `YOURSAFE_BROWSER_EXECUTABLE` trỏ tới Chrome/Edge. HTTPS lượt test thêm `YOURSAFE_TEST_TLS_KEY`/`YOURSAFE_TEST_TLS_CERT` cho synthetic certificate. Không tải dependency/browser mới.

Native/extension campaigns chạy qua `Test-AutofillNativeIntegration.ps1` với marked app PID, fixture directory, browser executable, installed Playwright package, `-FixturePort 55275` và temporary registration override đã được cho phép. Native 17-case lượt dùng `-NativeHostOnly`; Edge lượt bổ sung dùng `-CasePrefix 'E05-password,E05-code,E06,E07,E08,E09,E12-desktop'`. PIDs lúc chạy (21008, 42204, 55440) đã kết thúc, không tái sử dụng cho lượt mới.

## Công việc còn lại tại checkpoint cũ

| Ưu tiên | Việc còn phải sửa/điều tra | Kiểm chứng cần có |
| --- | --- | --- |
| 1 | Tìm nguyên nhân clipboard access denied trong test environment; tái hiện trạng thái Copy OTP thất bại trên Edge. Không mặc định đây chỉ là lỗi runner hoặc chỉ là lỗi product. | Separate approve/cancel Copy; numeric OTP/password đúng; cleanup sau 30 giây/on-lock; replacement/same-text replacement được giữ; queued write sau lock bị chặn. Code near-expiry cần case riêng và timing được kiểm soát. |
| 2 | Sửa UIA JSON/error handling để báo lỗi WinApp gốc thay vì truy cập `matchCount` không tồn tại. Điều tra/reset lại remove-password case bằng fixture mới. | Credential capability change không phát secret, approval bị hủy; reset fixture thành công. |
| 3 | Hoàn tất installed-extension campaign trên source cuối cho Chrome và Edge; giữ kết quả từng lượt, không cộng partial runs thành full-green verdict. | Delete khi approval queued, ba image-submit cases trên Edge, các clipboard cases và các ca chưa đến được sau runner failure. Kiểm lại các ca tương ứng trên Chrome. |
| 4 | Bổ sung runtime/lifecycle evidence còn thiếu. | Actual Windows lock/suspend có kiểm soát; logout/vault expiry trong installed-extension flow; worker restart; desktop close; nhiều browser/profile/desktop đồng thời. Native multi-client concurrency đã đạt nhưng không thay thế các ca này. Không tự khóa/suspend máy khi chưa được cho phép. |
| 5 | Kiểm chứng historical snapshot restore qua WinUI thật. | Current/historical password inputs, cancel/failure clearing, successful return to sign-in và restored credentials. Core/Presentation đã đạt nhưng UI thật chưa chạy. |
| 6 | Rà connected change và hoàn thiện tài liệu acceptance khi các gate đã qua. | Review các runner/error paths; cập nhật requirements/README và report bằng kết quả mới. Những đoạn tài liệu cũ nói verification deferred không phải trạng thái acceptance hiện tại. |
| Ngoài local demo scope | Qualification distribution thực tế nếu chuẩn bị phát hành. | Installer clean install/upgrade/uninstall, portable behavior, signing độc lập và browser-store identities. Synthetic release PASS không thay thế các kiểm tra này. |

## Giới hạn ghi nhận tại checkpoint cũ

- Chưa có installed-extension E2E full-green cho Chrome/Edge; goal chưa complete và demo toàn bộ flow chưa sign-off.
- Timeout/lock xóa state/control/reference không đồng nghĩa guaranteed zeroization mọi managed/JavaScript string. UIA không chứng minh được việc xóa vật lý bộ nhớ.
- Clipboard cleanup là best effort: không thu hồi dữ liệu đã paste, clipboard history/sync hoặc bản sao ở process khác.
- Desktop approval hiển thị origin claim từ client; NativeHost/pipe checks không cryptographically attest actual website/extension identity. Same-user malware và code trên website đã nhận password vẫn nằm ngoài bảo đảm này.
- Các kiểm chứng đã thực hiện dùng dữ liệu giả. Actual Windows lifecycle, toàn bộ clipboard và distribution qualification vẫn cần bằng chứng riêng.
