# Ảnh chụp landing page YourSafe

Đặt ảnh PNG vào chính thư mục này. Chụp giao diện thật, dùng tài khoản demo; giữ nguyên toàn bộ cửa sổ/popup, không cần cắt hình vuông, bo góc hay thêm khung. Website sẽ giữ tỷ lệ gốc và bo góc trực tiếp trên ảnh.

## Trạng thái các ảnh hiện có

Các trạng thái dưới đây mô tả những tệp đã có theo [nguồn ảnh](../PROVENANCE.md), không phải một đợt chụp hoặc kiểm thử mới.

- **Đã chụp từ YourSafe Debug bằng `winapp ui screenshot`:** `01`, `02`, `04`, `05`, `06`. Cửa sổ 1384×960; PNG giữ toàn bộ vùng cửa sổ native 1370×953. Đã xem từng ảnh để kiểm tra clipping và dữ liệu demo.
- **Đã chụp giao diện extension thật từ source với transport giả:** `03`, `07`. Minh họa GitHub với tài khoản `example.test`; chưa phải bằng chứng Native Messaging hoặc autofill trực tiếp.
- **Đã bổ sung — 2026-10-13:** `08-extension-login-context.png`, minh họa trang đăng nhập thử cạnh giao diện popup thật từ source. Tài khoản và phản hồi kết nối là dữ liệu mô phỏng; chưa phải bằng chứng kết nối Desktop thật. Chụp lại bằng `node scripts/Capture-WebsiteExtension.cjs --login-context`.
- Kho demo gồm YouTube, Google, GitHub, Facebook, Netflix, Spotify, Microsoft và Discord, chia nhóm Personal/Work. URL là website thật; username, password, recovery codes và TOTP là dữ liệu giả. Netflix cố ý có mật khẩu demo yếu để minh họa Security Check.
- Landing page dùng các bản sao ảnh kho và editor trong `../homepage/`. Các ảnh tạo mật khẩu, Security Check và Backup được giữ ở đây để xem/bổ sung về sau, chưa được hiển thị trên landing page.

## Ảnh giao diện chính

| Tên file | Cần thấy trong ảnh | Chuẩn bị trước khi chụp |
| --- | --- | --- |
| `01-desktop-vault.png` | Kho chính trên Windows, ô tìm kiếm, nhóm/nhãn và danh sách tài khoản | Kho đã mở; 5–8 tài khoản demo tên dễ hiểu như Email cá nhân, GitHub, Công việc; mật khẩu được che |
| `02-desktop-editor.png` | Toàn bộ màn hình thêm/sửa tài khoản: tên, username, URL, mật khẩu, ghi chú và mã khôi phục nếu có | Điền thông tin demo; dùng `alex@example.test` và `https://example.com`; che mật khẩu và secret TOTP |
| `03-extension-accounts.png` | Popup YourSafe hiển thị tài khoản phù hợp với nút Fill / View code | Ảnh hiện có dùng phản hồi mô phỏng. Nếu thay bằng ảnh kết nối thật, chạy Desktop, mở kho, bật kết nối trình duyệt và vào website có tài khoản demo đúng origin; tránh trạng thái Website unavailable hoặc lỗi kết nối |

## Ảnh tính năng và bối cảnh

| Tên file | Cần thấy trong ảnh | Chuẩn bị trước khi chụp |
| --- | --- | --- |
| `04-password-generator.png` | Công cụ tạo mật khẩu/cụm từ, các lựa chọn và kết quả mẫu | Kết quả mới tạo chỉ phục vụ demo, chưa dùng cho tài khoản thật |
| `05-security-check.png` | Kết quả kiểm tra mật khẩu: yếu, trùng hoặc lâu chưa đổi | Dùng kho demo có kết quả; không hiển thị tài khoản, username hoặc mật khẩu thật |
| `06-encrypted-backup.png` | Màn hình tạo hoặc kiểm tra bản sao lưu có mật khẩu riêng | Ô mật khẩu để trống hoặc che; không lộ đường dẫn cá nhân, khóa khôi phục hay nội dung tệp sao lưu |
| `07-extension-totp.png` | Popup View code với tên tài khoản, mã demo, countdown và Copy | Dùng TOTP demo; không chụp QR/secret hoặc mã xác thực tài khoản thật |
| `08-extension-login-context.png` | Website đăng nhập demo cạnh popup YourSafe đang có tài khoản phù hợp | Nếu dễ chụp, lấy toàn vùng browser có popup; tránh tab, bookmark, thông báo hoặc thông tin cá nhân |

## Kích thước và cách chụp

- Chụp Desktop bằng [Capture-WebsiteDesktop.ps1](../../../scripts/Capture-WebsiteDesktop.ps1) với cả `-ProcessId` và `-TestDirectory` trỏ tới app Debug và kho dùng một lần đã seed bằng `Create_opt_in_disposable_landing_vault`. Script kiểm tra marker trước khi thao tác, ghi ảnh `01`, `02`, `04`, `05`, `06` vào thư mục này và ghi `capture-results.json` trong kho dùng một lần. Kiểm tra trạng thái `CAPTURED`/`PENDING`; script không tự cập nhật các bản sao ảnh trong `../homepage/`.
- Desktop: script đặt cửa sổ 1384×960; ảnh native hiện có là 1370×953. Giữ PNG ở độ phân giải gốc và đủ nội dung, không cắt thành hình vuông.
- Extension: chụp riêng toàn bộ popup theo đúng chiều rộng/chiều cao tự nhiên; không kéo giãn, không cắt thành hình vuông. Ảnh `08` là ảnh riêng có bối cảnh website, không thay thế ảnh popup.
- Dùng theme sáng và cùng mức scale Windows nếu thuận tiện. Ảnh khác tỷ lệ vẫn được dùng đúng tỷ lệ.
- Không chụp mật khẩu thật, Recovery Key thật, QR/secret TOTP thật hay mã khôi phục thật. Nếu phải che thông tin, chụp lại với dữ liệu demo để giữ ảnh rõ.

Nếu bạn bổ sung ảnh browser thực tế hoặc muốn thay ảnh nào, đặt vào đây rồi nhắn tên file. Ảnh extension hiện dùng phản hồi demo; không xác nhận autofill trực tiếp từ các ảnh đó.
