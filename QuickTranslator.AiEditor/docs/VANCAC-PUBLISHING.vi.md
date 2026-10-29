# Đăng lên Sắc Viện

QT Studio đăng bản dịch tiếng Việt trực tiếp lên website Sắc Viện qua Publisher API v1. Hợp đồng API nằm ở `docs/PUBLISHER_API.md` trong repo website.

## Cách dùng

1. Trong QT Studio, mở **Workspace → Tài khoản Sắc Viện…** hoặc bấm trạng thái **Sắc Viện: chưa đăng nhập** ở thanh dưới cùng.
   - Nhập **Địa chỉ website** (mặc định `https://sacvien.com`), **Email**, **Mật khẩu** rồi bấm **Đăng nhập** (hoặc Enter).
   - Tài khoản phải đã xác minh, đang hoạt động và có vai trò **Tác giả** hoặc **Quản trị**. Thông báo từ website được hiển thị ngay trong cửa sổ.
   - Sau khi đăng nhập, cửa sổ hiển thị tên, vai trò, website và thời hạn phiên. **Đăng xuất** cố gắng thu hồi riêng phiên này trên website rồi xóa mã khỏi máy, kể cả khi không liên lạc được website.
   - Thanh trạng thái dùng tên đã lưu, không gọi mạng khi khởi động ứng dụng. Escape đóng cửa sổ.
2. **Workspace → Truyện trên Sắc Viện…** mở danh sách truyện của tài khoản: tên, trạng thái và số chương. Chọn truyện để xem số chương, tiêu đề và trạng thái **Nháp / Đã xuất bản / Ẩn**.
   - **Tải lại** cập nhật danh sách; **Mở trên web** mở trang `/truyen/{slug}` trong trình duyệt mặc định.
   - **Liên kết với Project đang mở** ghi nhớ truyện đích theo `ProjectId`, không sửa Project. Cửa sổ này chỉ để xem, không sửa nội dung.
3. Trong QT Studio: **Workspace → Xuất bản**.
   - Chọn định dạng **Sắc Viện · Đăng lên web** và ngôn ngữ **Bản dịch tiếng Việt**.
   - Chọn phạm vi chương (toàn bộ / dải / một chương).
   - Nếu có chương chưa hoàn tất, tích xác nhận đăng bản nháp giống khi xuất file.
   - Bấm **Đăng lên Sắc Viện…**.
4. Trong cửa sổ đăng:
   - **Tài khoản:** kiểm tra tên và website; bấm **Đăng nhập…** hoặc **Đổi tài khoản…** nếu cần. Khi chưa đăng nhập, chọn truyện và đăng chương được khóa.
   - **Truyện:** dùng liên kết Project đã lưu; nếu chưa liên kết, tự chọn truyện có tên trùng tên Project (bỏ khoảng trắng đầu/cuối, không phân biệt hoa thường, giữ dấu tiếng Việt). Nếu không trùng, chọn thủ công, hoặc **Tạo truyện mới trên website…** (tên, đường dẫn tự sinh không dấu, giới thiệu và thể loại lấy từ Project; tạo ở trạng thái nháp). Cửa sổ cho biết bao nhiêu chương sẽ tạo mới và bao nhiêu đã có.
   - **Đăng:** chọn **Bản nháp** (chương mới là nháp, chương đã có giữ trạng thái) hoặc **Xuất bản ngay**. Bấm **Bắt đầu đăng**.
   - **Dự phòng:** mở mục thu gọn **Nâng cao: dùng mã kết nối**, nhập địa chỉ website (HTTPS, chỉ gốc), dán mã `vcp_…` tạo tại **Tài khoản → Kết nối QT Studio** trên website, rồi **Lưu và kiểm tra kết nối**. Cách dùng mã thủ công vẫn được hỗ trợ.
5. Người đọc chỉ thấy chương khi **cả truyện và chương** đều đã xuất bản. Trên website có nút **Xuất bản mọi chương nháp của truyện** trong trang quản lý.

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
  - Khi gặp **401** trong lúc xem truyện hoặc đăng chương, QT Studio báo **Phiên đăng nhập Sắc Viện đã hết hạn hoặc bị thu hồi. Hãy đăng nhập lại.** và có nút đăng nhập. Những chương đã gửi thành công vẫn giữ nguyên trên website.
  - Yêu cầu đăng nhập bằng mật khẩu chỉ gửi **một lần**, không tự thử lại, kể cả lỗi mạng hay 429. Người dùng chủ động đăng nhập lại.
- **Project:** không bị ghi. Nếu Project thay đổi trong lúc đăng (hàng đợi dịch, Editor), lượt đăng dừng và báo; các chương đã gửi vẫn giữ trên website.
- **Bí mật:**
  - Mã kết nối chỉ nằm trong **Windows Credential Manager** (`QTStudio.VanCac.PublisherToken.v1`).
  - Địa chỉ website, `DeviceId` (UUID chữ thường, cố định cho thiết bị), tên hiển thị, vai trò, thời hạn phiên, truyện đích theo `ProjectId` và lựa chọn nháp/xuất bản nằm trong `%LOCALAPPDATA%\QuickTranslator.AiEditor\vancac-publisher.json`.
  - **Mật khẩu không lưu ở đâu**: chỉ gửi trong JSON của yêu cầu HTTPS đăng nhập; ô mật khẩu được xóa sau khi gửi. Không đưa mật khẩu vào URL, log hay thông báo lỗi. Tên thiết bị gửi khi đăng nhập lấy từ tên máy, tối đa 60 ký tự.
  - Không có gì ghi vào `.qtproj` hay thư mục Project. Mã chỉ đi trong header `Authorization`, không vào URL hay log.
  - Website chấp nhận HTTPS; HTTP chỉ được phép với `localhost`/`127.0.0.1` để thử local.

## Mã nguồn

| Tệp | Vai trò |
| --- | --- |
| `Core/Publishing/VanCacPublisherClient.cs` | HTTP client Publisher API, retry, kiểm tra URL/mã/giới hạn chương, `VanCacSlug` |
| `Core/Publishing/VanCacPublishRunner.cs` | Gửi tuần tự, bỏ qua/dừng/hủy, báo cáo; `WebTitle` |
| `Wpf/VanCacPublishWindow.xaml(.cs)` | Cửa sổ kết nối, chọn/tạo truyện, đăng, nhật ký |
| `Wpf/VanCacAccountManager.cs`, `VanCacAccountWindow.xaml(.cs)` | Trạng thái tài khoản dùng chung, đăng nhập/đăng xuất |
| `Wpf/VanCacNovelsWindow.xaml(.cs)`, `MainWindow.VanCac.cs` | Duyệt truyện/chương, liên kết Project, menu và thanh trạng thái |
| `Wpf/VanCacPublisherSettings.cs` | `VanCacTokenVault` (Credential Manager) và `VanCacPublisherPreferences` (LocalAppData) |
| `Wpf/WindowsCredentialStore.cs` | Tách từ `GeminiApiKeyVault` để dùng chung; thông báo lỗi Gemini giữ nguyên |
| `Wpf/ProjectPublicationView.xaml(.cs)` | Thêm định dạng "Sắc Viện · Đăng lên web" (ẩn tùy chọn file, chặn bản gốc tiếng Trung) |

## Kiểm thử

- `VanCacPublisherTests`: URL/mã, đăng nhập không Bearer với đúng 4 khóa JSON, thời hạn phiên, lỗi đăng nhập không thử lại/không lộ mật khẩu, DELETE phiên với Bearer; API xuất bản, retry/backoff, phân trang, giới hạn, slug, tiêu đề, runner bỏ qua/lỗi/dừng/hủy/Project đổi.
- `VanCacPublishUiTests`: vault và preferences giả; đăng nhập/đăng xuất; trạng thái tài khoản/thanh trạng thái không gọi mạng lúc khởi động; cửa sổ đăng, mã thủ công, ghép tên truyện; duyệt truyện/chương, liên kết Project, trạng thái rỗng/hết phiên. Không dùng vault, thiết lập hay mạng thật.
- Smoke thật với website staging (27/10/2026):
  - Client + runner thật: tạo truyện; lượt đầu 2 tạo mới + 1 bỏ qua chưa hoàn tất; lượt 2 "không đổi"; sửa 1 chương → 1 cập nhật.
  - Xuất bản hàng loạt 3; tiêu đề "Chương 2: Trở về" → "Trở về"; tiếng Việt giữ nguyên.
  - Dữ liệu thử đã xóa, mã đã thu hồi.

## Giới hạn

- Không có xóa chương/truyện, ảnh bìa hay đặt giá qua QT Studio.
- Không đăng bản gốc tiếng Trung.
- Mỗi lần gửi một chương: truyện hàng nghìn chương mất thời gian tương ứng (khoảng 0,3–1 s mỗi chương tùy mạng).
