Purpose: Sửa các lỗi review tĩnh của YourSafe browser autofill bằng thay đổi tối thiểu.

Assumptions: Extension chưa được đăng ký. Chỉ chạy unit test, kiểm tra tĩnh và build; hoãn flow browser/extension, IPC thực tế và install.

Final Prompt:

Sửa các case dưới đây trong repo PasswordTool.Core. Đọc toàn bộ flow popup → worker → content script và NativeHost → WinUI pipe server → Presentation → Core trước khi sửa. Giữ kiến trúc, chính sách bảo mật và các thay đổi đang có trong workspace; không thêm dependency hoặc refactor ngoài phạm vi.

1. **P1 — Race consent (`browser-extension/src/worker.ts`):** kiểm tra interaction identity, expiry và target sau khi chờ `currentTarget()`. Discovery phải kiểm tra generation sau các lần await trước khi gọi desktop hoặc tạo interaction. Invalidation trong lúc API trả target cũ phải ngăn secret retrieval/delivery.
2. **P2 — Pipe retry (`src/PasswordTool.WinUI/AutofillPipeServer.cs`):** thêm delay có cancellation vào đường lỗi để constructor pipe ném `IOException` liên tục không giữ luồng UI hoặc tạo vòng lặp nóng. Stop phải kết thúc được khi đang đợi retry.
3. **P2 — Field selection (`browser-extension/src/fields.ts`):** giữ cả username và password đã chọn khi kiểm tra lại trước mỗi lần ghi. Form có nhiều ô text vẫn fill được khi username đã được focus; lựa chọn password bằng focus cũng phải được giữ. Nếu DOM thay thế, disable hoặc đổi loại field trong username events thì không ghi password.
4. **P2 — Tab scope (`browser-extension/src/worker.ts`):** navigation, refresh, hash/SPA change và đóng tab chỉ hủy interaction/discovery của tab liên quan. Đổi active tab trong cửa sổ đích hoặc đổi cửa sổ đang focus vẫn phải hủy consent.
5. **P2 — Inherited disabled (`browser-extension/src/fields.ts`):** dùng `matches(':disabled')` để nhận diện trạng thái thực của input, gồm fieldset disabled và ngoại lệ first legend.
6. **P2 — Discovery size (`AutofillPipeServer.cs`, `WireProtocol.cs` và TS contract/popup):** trả các account hoàn chỉnh vừa giới hạn 64 KiB của toàn envelope, tính theo JSON UTF-8 thực tế. Báo `truncated` khi chưa liệt kê hết; popup hướng người dùng đến desktop. Đồng bộ validation ở C# và TypeScript, không tăng giới hạn framing.
7. **P2 — Null username (`src/PasswordTool.Core/Services/VaultService.cs`):** chuẩn hóa username null từ backup import thành chuỗi rỗng ở cả metadata và secret projection trước khi tạo DTO.
8. **Ponytail cleanup (`src/PasswordTool.NativeHost/Framing.cs`):** bỏ lần parse JSON trùng trong framing; giữ kiểm tra chiều dài, UTF-8 và validation qua `WireProtocol.ParseRequest/ParseResponse` tại caller.

Verification criteria:

- Thêm regression check cho stale target sau await, expiry trong await, discovery bị hủy, tab nền, lựa chọn field, inherited disabled, discovery vượt budget và backup username null.
- Chạy Core/Presentation unit test, bỏ test OS pipe peer; chạy TS mock/unit check, typecheck và build WinUI/NativeHost/extension.
- Thêm/build DOM check để chạy sau; không đăng ký extension, chạy browser E2E, IPC thực tế hoặc install.
- Báo rõ kết quả đã kiểm chứng, phần hoãn và giới hạn discovery. Desktop, NativeHost và extension phải build cùng contract.
