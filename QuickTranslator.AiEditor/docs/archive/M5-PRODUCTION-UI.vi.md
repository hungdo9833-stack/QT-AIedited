# M5 — Production UI Redesign

## Phạm vi

M5 thay shell WPF và workspace biên dịch bằng giao diện QT Studio Dark Navy đã duyệt. Màn Biên dịch là màn hình chính khi mở Project. M5 tái sử dụng toàn bộ repository, transaction, autosave, recovery, Chapter Detection M1, Safe Bulk Import M2, ProjectCatalog M3 và Project Translation Queue M4. Không đổi schema Project, không viết lại engine dịch và chưa thay Reader hiện có.

## Shell và điều hướng

- Thanh điều hướng bên trái mở trực tiếp Biên dịch, Chương, Nhập TXT, Hàng đợi, Thư viện và Reader.
- Thanh đầu cửa sổ có Project switcher, trạng thái hàng đợi, nút ẩn/hiện mục lục, Inspector, theme và Save.
- Menu Tệp/Dự án và các lệnh an toàn cũ vẫn còn để không làm mất đường thao tác hiện hữu.
- Chapter Manager M3, Import Preview M2 và Queue M4 dùng chính service/coordinator đã có; shell không tạo cơ chế song song thứ hai.
- Trạng thái queue ở shell lấy từ tiến độ thật của `ProjectTranslationCoordinator`.

## Workspace biên dịch

- Chế độ mặc định là hai ô: nguyên văn Trung và bản Việt đang biên tập.
- Chế độ bốn ô hiển thị Trung, Hán Việt, VietPhrase và AI dịch/bản Việt biên tập bằng các control hiện có.
- Khi đổi hai ô/bốn ô, selection segment, nội dung draft và focus biên tập không bị tạo lại hoặc ghi đè.
- Splitter cho phép thay đổi tỷ lệ. Bố cục, chế độ, theme và trạng thái panel được lưu trong LocalAppData, nằm ngoài `.qtproj`.
- Inspector bên phải hiển thị Dictionary/Hán Việt/VietPhrase theo selection, Glossary, Context và Character Profile hiện hành của Project.
- Command bar giữ semantics của dịch, pause/resume/cancel, retry, approve, history, export, replace, save và recovery.

## Library

- Library đọc danh sách Project đã biết và dựng card với tiêu đề, tác giả, trạng thái, tiến độ, chương hiện hành cùng hành động tiếp tục biên dịch.
- `ReadLibrarySummaryAsync` chỉ đọc manifest/catalog metadata; không load toàn bộ `ChapterDocument` để dựng thư viện.
- Tìm kiếm và lọc chạy trên summary. Tạo, tìm Project và xóa mục khỏi danh sách dùng luồng đã có. Xóa dữ liệu Project vẫn là thao tác riêng có xác nhận.

## Theme, DPI và bàn phím

- Dark Navy là mặc định; Light theme vẫn hoạt động bằng resource động dùng chung.
- Font ưu tiên Segoe UI Variable và có fallback Segoe UI.
- Shell tự thu gọn mục lục ở dưới 1320 DIP và Inspector ở dưới 1100 DIP. Ở cửa sổ hẹp, nhãn phụ được ẩn để giữ các lệnh chính.
- Kích thước tối thiểu 900 x 520 DIP hỗ trợ Full HD ở 200%. Bố cục cũng được đo ở Full HD, 2K và các mức 100%/150%/200%.
- Phím tắt: `Ctrl+1` hai ô, `Ctrl+2` bốn ô, `Ctrl+S` lưu và `Ctrl+Shift+I` ẩn/hiện Inspector.

## Khác biệt có chủ đích so với mockup

- Menu cũ được giữ vì chứa các lệnh Project, phục hồi và cấu hình chưa có vị trí tương đương an toàn trong mockup.
- Reader chỉ được nối vào shell; việc thiết kế lại Reader thuộc M6.
- Inspector hiển thị dữ liệu chữ từ resource hiện có, chưa thêm hệ thống từ điển hay component mới.
- Ở Full HD 200%, mục lục và Inspector tự thu gọn để diện tích soạn thảo không bị bó hẹp; người dùng vẫn mở Chương/Inspector qua điều hướng và nút shell.

## Xác minh

- UI integration tests kiểm tra hai/bốn ô, bảo toàn draft/selection, Inspector, theme, persistence, responsive layout và luồng M2 → M3 → M4 → M5.
- Smoke runner dựng cửa sổ WPF thật trên Project tổng hợp và xuất ảnh bằng `RenderTargetBitmap`.
- Không có dữ liệu Project cá nhân, Data từ điển hoặc output dịch thật trong test/package.
- Nghiệm thu Release ngày 13/10/2026: Core 480/480, WPF 52/52, tổng 532/532; 0 failed, 0 skipped; build 0 lỗi và 0 cảnh báo.
