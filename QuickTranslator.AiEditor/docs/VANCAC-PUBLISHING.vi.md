# Đăng lên Sắc Viện

QT Studio đăng bản dịch tiếng Việt trực tiếp lên website Sắc Viện qua Publisher API v1. Hợp đồng API nằm ở `docs/PUBLISHER_API.md` trong repo website.

## Cách dùng

1. Trên website, đăng nhập tài khoản **Tác giả** hoặc **Quản trị** → **Tài khoản** → **Kết nối QT Studio**. Đặt tên mã, chọn thời hạn, bấm **Tạo mã** rồi sao chép mã `vcp_…`. Mã chỉ hiện một lần.
2. Trong QT Studio: **Workspace → Xuất bản**.
   - Chọn định dạng **Sắc Viện · Đăng lên web** và ngôn ngữ **Bản dịch tiếng Việt**.
   - Chọn phạm vi chương (toàn bộ / dải / một chương).
   - Nếu có chương chưa hoàn tất, tích xác nhận đăng bản nháp giống khi xuất file.
   - Bấm **Đăng lên Sắc Viện…**.
3. Trong cửa sổ đăng:
   - **Kết nối:** nhập địa chỉ website (HTTPS, chỉ gốc, ví dụ `https://ten-mien.com`), dán mã, bấm **Lưu và kiểm tra kết nối**.
   - **Truyện:** chọn truyện đích, hoặc **Tạo truyện mới trên website…** (tên, đường dẫn tự sinh không dấu, giới thiệu và thể loại lấy từ Project; tạo ở trạng thái nháp). Cửa sổ cho biết bao nhiêu chương sẽ tạo mới và bao nhiêu đã có.
   - **Đăng:** chọn **Bản nháp** (chương mới là nháp, chương đã có giữ trạng thái) hoặc **Xuất bản ngay**. Bấm **Bắt đầu đăng**.
4. Người đọc chỉ thấy chương khi **cả truyện và chương** đều đã xuất bản. Trên website có nút **Xuất bản mọi chương nháp của truyện** trong trang quản lý.

## Hành vi và an toàn dữ liệu

- **Nội dung:** lấy từ `ProjectPublicationService.ReadChaptersAsync`, cùng bộ ghép `ChapterTranslationComposer` với Reader và xuất TXT.
  - Chương xóa mềm không được gửi; chương chưa có bản dịch bị bỏ qua.
  - Chương chưa hoàn tất/cần kiểm tra chỉ được gửi khi đã xác nhận.
  - Tiêu đề bắt đầu bằng đúng số chương (`Chương 12: …`, `第12章 …`) được bỏ phần số, vì website tự hiển thị "Chương N:".
- **Cách gửi:**
  - Từng chương một, theo thứ tự đọc, khóa theo **số chương**. Gửi lại là an toàn: chương giống hệt báo "không đổi"; chương khác nội dung được cập nhật và giữ giá trên website.
  - **Dừng sau chương hiện tại** có hiệu lực ngay sau request đang chạy. Chạy lại sẽ tiếp tục, chương đã gửi báo "không đổi".
- **Lỗi:**
  - Lỗi mạng / 429 / 5xx: thử lại tối đa 4 lần (2 s, 4 s, 8 s; tôn trọng `Retry-After` tới 30 s).
  - Lỗi theo chương (ví dụ quá 200.000 ký tự) được ghi lại, phần còn lại vẫn chạy.
  - **401/403** (mã hết hạn, bị thu hồi, mất quyền) dừng toàn bộ.
- **Project:** không bị ghi. Nếu Project thay đổi trong lúc đăng (hàng đợi dịch, Editor), lượt đăng dừng và báo; các chương đã gửi vẫn giữ trên website.
- **Bí mật:**
  - Mã kết nối chỉ nằm trong **Windows Credential Manager** (`QTStudio.VanCac.PublisherToken.v1`).
  - Địa chỉ website, truyện đích theo `ProjectId` và lựa chọn nháp/xuất bản nằm trong `%LOCALAPPDATA%\QuickTranslator.AiEditor\vancac-publisher.json`.
  - Không có gì ghi vào `.qtproj` hay thư mục Project. Mã chỉ đi trong header `Authorization`, không vào URL hay log.
  - Website chấp nhận HTTPS; HTTP chỉ được phép với `localhost`/`127.0.0.1` để thử local.

## Mã nguồn

| Tệp | Vai trò |
| --- | --- |
| `Core/Publishing/VanCacPublisherClient.cs` | HTTP client Publisher API, retry, kiểm tra URL/mã/giới hạn chương, `VanCacSlug` |
| `Core/Publishing/VanCacPublishRunner.cs` | Gửi tuần tự, bỏ qua/dừng/hủy, báo cáo; `WebTitle` |
| `Wpf/VanCacPublishWindow.xaml(.cs)` | Cửa sổ kết nối, chọn/tạo truyện, đăng, nhật ký |
| `Wpf/VanCacPublisherSettings.cs` | `VanCacTokenVault` (Credential Manager) và `VanCacPublisherPreferences` (LocalAppData) |
| `Wpf/WindowsCredentialStore.cs` | Tách từ `GeminiApiKeyVault` để dùng chung; thông báo lỗi Gemini giữ nguyên |
| `Wpf/ProjectPublicationView.xaml(.cs)` | Thêm định dạng "Sắc Viện · Đăng lên web" (ẩn tùy chọn file, chặn bản gốc tiếng Trung) |

## Kiểm thử

- `VanCacPublisherTests` (Core, 33): URL/mã, Bearer + JSON, retry/backoff, lỗi 401/422, phân trang, giới hạn, slug, tiêu đề, runner bỏ qua/lỗi/dừng/hủy/Project đổi.
- `VanCacPublishUiTests` (WPF, 4): vault, preferences, định dạng mới trong cửa sổ Xuất bản, cửa sổ đăng mở mà không đọc vault/thiết lập thật.
- Toàn bộ: Core **962/962**, WPF **232/232**, build Release `-warnaserror` 0 cảnh báo.
- Smoke thật với website staging (27/10/2026):
  - Client + runner thật: tạo truyện; lượt đầu 2 tạo mới + 1 bỏ qua chưa hoàn tất; lượt 2 "không đổi"; sửa 1 chương → 1 cập nhật.
  - Xuất bản hàng loạt 3; tiêu đề "Chương 2: Trở về" → "Trở về"; tiếng Việt giữ nguyên.
  - Dữ liệu thử đã xóa, mã đã thu hồi.

## Giới hạn

- Không có xóa chương/truyện, ảnh bìa hay đặt giá qua QT Studio.
- Không đăng bản gốc tiếng Trung.
- Mỗi lần gửi một chương: truyện hàng nghìn chương mất thời gian tương ứng (khoảng 0,3–1 s mỗi chương tùy mạng).
