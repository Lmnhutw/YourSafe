# Ảnh chụp cần bổ sung cho landing page YourSafe

Đặt ảnh PNG vào chính thư mục này. Chụp giao diện thật, dùng tài khoản demo; giữ nguyên toàn bộ cửa sổ/popup, không cần cắt hình vuông, bo góc hay thêm khung. Website sẽ giữ tỷ lệ gốc và bo góc trực tiếp trên ảnh.

## Trạng thái hiện tại — 2026-10-08

- **Đã chụp từ YourSafe Debug bằng `winapp ui screenshot`:** `01`, `02`, `04`, `05`, `06`. Cửa sổ 1384×960; PNG giữ toàn bộ vùng cửa sổ native 1370×953. Đã xem từng ảnh để kiểm tra clipping và dữ liệu demo.
- **Đã chụp giao diện extension thật từ source với transport giả:** `03`, `07`. Minh họa GitHub với tài khoản `example.test`; chưa phải bằng chứng Native Messaging hoặc autofill trực tiếp.
- **Đang để chờ:** `08-extension-login-context.png`, cần ảnh browser thực tế có trang đăng nhập và popup extension cạnh nhau.
- Kho demo gồm YouTube, Google, GitHub, Facebook, Netflix, Spotify, Microsoft và Discord, chia nhóm Personal/Work. URL là website thật; username, password, recovery codes và TOTP là dữ liệu giả. Netflix cố ý có mật khẩu demo yếu để minh họa Security Check.
- Landing page đã dùng ảnh kho và editor vừa chụp. Các ảnh tạo mật khẩu, Security Check và Backup được giữ ở đây để xem/bổ sung về sau.

## Ưu tiên cao

| Tên file | Cần thấy trong ảnh | Chuẩn bị trước khi chụp |
| --- | --- | --- |
| `01-desktop-vault.png` | Kho chính trên Windows, ô tìm kiếm, nhóm/nhãn và danh sách tài khoản | Kho đã mở; 5–8 tài khoản demo tên dễ hiểu như Email cá nhân, GitHub, Công việc; mật khẩu được che |
| `02-desktop-editor.png` | Toàn bộ màn hình thêm/sửa tài khoản: tên, username, URL, mật khẩu, ghi chú và mã khôi phục nếu có | Điền thông tin demo; dùng `alex@example.test` và `https://example.com`; che mật khẩu và secret TOTP |
| `03-extension-accounts.png` | Popup YourSafe nhận diện đúng website và hiển thị tài khoản phù hợp với nút Fill / View TOTP | Chạy Desktop, mở kho, vào website HTTPS có tài khoản demo; tránh trạng thái Website unavailable hoặc lỗi kết nối |

## Bổ sung để minh họa đủ tính năng

| Tên file | Cần thấy trong ảnh | Chuẩn bị trước khi chụp |
| --- | --- | --- |
| `04-password-generator.png` | Công cụ tạo mật khẩu/cụm từ, các lựa chọn và kết quả mẫu | Kết quả mới tạo chỉ phục vụ demo, chưa dùng cho tài khoản thật |
| `05-security-check.png` | Kết quả kiểm tra mật khẩu: yếu, trùng hoặc lâu chưa đổi | Dùng kho demo có kết quả; không hiển thị tài khoản, username hoặc mật khẩu thật |
| `06-encrypted-backup.png` | Màn hình tạo hoặc kiểm tra bản sao lưu có mật khẩu riêng | Ô mật khẩu để trống hoặc che; không lộ đường dẫn cá nhân, khóa khôi phục hay nội dung tệp sao lưu |
| `07-extension-totp.png` | Popup View TOTP với tên tài khoản, mã demo, countdown và Copy | Dùng TOTP demo; không chụp QR/secret hoặc mã xác thực tài khoản thật |
| `08-extension-login-context.png` | Website đăng nhập demo cạnh popup YourSafe đang có tài khoản phù hợp | Nếu dễ chụp, lấy toàn vùng browser có popup; tránh tab, bookmark, thông báo hoặc thông tin cá nhân |

## Kích thước và cách chụp

- Desktop: cửa sổ khoảng 1200–1440 px rộng, 750–900 px cao, hoặc kích thước đủ hiển thị nội dung không bị cắt. PNG, độ phân giải gốc.
- Extension: chụp riêng toàn bộ popup theo đúng chiều rộng/chiều cao tự nhiên; không kéo giãn, không cắt thành hình vuông. Ảnh `08` là ảnh riêng có bối cảnh website, không thay thế ảnh popup.
- Dùng theme sáng và cùng mức scale Windows nếu thuận tiện. Ảnh khác tỷ lệ vẫn được dùng đúng tỷ lệ.
- Không chụp mật khẩu thật, Recovery Key thật, QR/secret TOTP thật hay mã khôi phục thật. Nếu phải che thông tin, chụp lại với dữ liệu demo để giữ ảnh rõ.

Nếu bạn bổ sung ảnh browser thực tế hoặc muốn thay ảnh nào, đặt vào đây rồi nhắn tên file. Ảnh extension hiện dùng phản hồi demo; không xác nhận autofill trực tiếp từ các ảnh đó.
