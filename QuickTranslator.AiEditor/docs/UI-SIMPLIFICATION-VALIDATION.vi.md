# QTStudio — Kết quả kiểm thử và sửa UI, 25/10/2026

Nhánh: `feat/qtstudio-simplified-ui`. PR: <https://github.com/hungdo9833-stack/QT-AIedited/pull/1>.
Đã rà soát toàn bộ diff 6 commit từ `2b51cf0` đến `e4caa96`, đọc tài liệu bàn giao, sửa và kiểm thử trên Windows với .NET SDK 10.0.401.
Checkout riêng `qtstudio-ui-review` giữ nguyên repo/nhánh và các thay đổi chưa commit tại workspace ban đầu.

## Kết quả Release

Các lệnh dưới đây đã chạy thành công (CLI đi qua `rtk proxy`):

```powershell
dotnet restore QuickTranslator.AiEditor.slnx
dotnet build QuickTranslator.AiEditor.slnx -c Release -warnaserror
dotnet test QuickTranslator.AiEditor.slnx -c Release --no-build
```

| Kiểm tra | Kết quả |
|---|---|
| Restore | Thành công, không tắt NuGet audit |
| Build Release, warnings-as-errors | 0 warning, 0 error |
| Core tests | 923 pass, 0 fail, 0 skip |
| WPF tests | 223 pass, 0 fail, 0 skip |
| Tổng | **1.146 pass, 0 fail, 0 skip** |
| Regression mới | 17 test case trong `SimplifiedUiRegressionTests` |

Restore đầu tiên trong sandbox gặp NU1900 do không truy cập được dịch vụ audit NuGet. Restore có quyền mạng đã giải quyết; các lệnh build/restore cuối chạy bình thường, không thêm cấu hình bỏ qua cảnh báo.
TRX cuối được lưu cục bộ trong `TestResults/release-acceptance/` (thư mục bị Git ignore).

Benchmark sẵn có `editor-refresh-benchmark --count 500` (20 lần refresh, cùng máy, Release): snapshot PR `e4caa96` trung bình 78,268 ms / 14.972.746 byte; bản sửa 77,610 ms / 14.992.773 byte. Kết quả gần nhau, chưa thấy suy giảm trong phép đo này; đây không phải benchmark throughput model/GPU hay cam kết hiệu năng trên mọi Project.

## Lỗi và cách khắc phục

1. **Trạng thái menu phụ phụ thuộc nút ẩn và thứ tự mảng.** Xóa bản sao nút ẩn/popup của các lệnh biên tập và chương; chính các `MenuItem` có tên được cập nhật trực tiếp bởi `UpdateButtons`/`EditorActionPolicy`/`UpdateProjectChapterCommands`. Không còn chép 13 phần tử theo chỉ số khi mở menu hoặc binding ElementName chéo popup cho các lệnh này. Tooltip và lý do khóa được áp dụng trực tiếp, kể cả khi menu đang mở.
2. **Mất đường vào Sửa nguyên văn.** Giữ lệnh này trong menu khi đã có chương; khóa đúng khi đang dịch. Lưu/hủy sửa nguồn tiếp tục dùng handler và preview/xác nhận hiện có.
3. **Reader không đồng bộ với Editor.** Bỏ radio Editor ẩn thuộc nhóm khác, dùng ba radio hiện hữu trong một nhóm; thoát Reader rõ ràng trước khi đổi bố cục; chặn Checked lồng nhau. Reader, Biên tập, Soát lỗi, Ctrl+1/Ctrl+2 và mở đoạn từ Reader đều giữ nội dung nháp.
4. **Hai mục điều hướng active khi đọc.** Reader thuộc mục Dịch & Biên tập; chỉ một mục active, thanh ba chế độ vẫn nhìn thấy. Mode strip dùng cùng palette ứng dụng, tránh màu Reader ghi đè trạng thái chọn của Editor.
5. **Overflow nhân vật bỏ qua trạng thái khóa.** Chuyển chính các nút Xóa/Lưu hồ sơ/Xem prompt vào popup, bỏ nút bản sao không binding. Trạng thái busy và lựa chọn nhân vật tác động đúng lên lệnh người dùng nhìn thấy.
6. **Tùy chọn panel bị giấu/mục sidebar xuất hiện lại.** Đưa điều khiển mục lục/Inspector và Auto/Ghim hiện/Ghim ẩn vào menu Hiển thị. Panel policy cập nhật đúng control đó. Escape đóng flyout trả focus về mục workspace hiển thị, thay vì control trong submenu đã đóng.
7. **Lệnh cũ không có đường truy cập.** Chương nháp mới và mở tiến độ `.qtaie.json` xuất hiện trong menu Tệp; Lưu và đổi theme dùng chính menu item có trạng thái, không dùng nút rail ẩn làm bản sao.
8. **Trạng thái nút mini queue sau thao tác async.** Bỏ việc bật nút vô điều kiện trong finally; đọc lại trạng thái coordinator. Chặn bấm lặp Pause/Resume trong lúc đang gửi lệnh; vẫn cho phép Pause khi Resume đã bắt đầu chạy. Chi tiết chỉ mở view, không gọi Resume. Không sửa công thức tiến độ hoặc luồng checkpoint.
9. **Layout tràn/chồng chữ ở 900 px.** Header dùng cột co giãn và cột hành động riêng. Header lưới được rút gọn và trim đúng phần trống; status bar chia cột, đường dẫn không ép mất thông báo. Nhãn điều hướng xuống dòng khi cần; thu rail chỉ giữ glyph và tên accessibility. Bảng nhân vật có chiều rộng cột tối thiểu và cuộn ngang để không ép tiêu đề thành vài ký tự.

Ba test WPF thất bại ở bản bàn giao đã được xử lý: lỗi active navigation thực tế và các kỳ vọng theo bố cục cũ. Các test cũ được cập nhật theo control menu và điều hướng mới; không xóa test.

## Kiểm tra hành vi đã thực hiện

- WPF thật trên STA dispatcher: tạo Window/control, mở cửa sổ, đổi workspace/radio, phát click và xử lý shortcut/focus. Các test dùng Project tổng hợp trong thư mục tạm; không mở dữ liệu thật, không gọi model ngoài.
- Ba chế độ chuyển lặp lại, giữ draft, thanh chế độ vẫn hiển thị khi đọc; màu Light/Dark được kiểm tra và render.
- Menu Thêm đang mở cập nhật khóa ngay khi busy; Sửa nguyên văn vẫn truy cập được; lựa chọn chương đơn/nhiều và lệnh nhập web tiếp tục có test hiện hữu.
- Tài nguyên mặc định mở Nhân vật; tên hồ sơ và ngữ cảnh chưa lưu còn nguyên qua Từ điển, Cài đặt, Reader rồi quay lại. Các test hiện hữu về persistence, nhất quán nhân vật, ledger/proposals, preset và layout JSON cũ đều pass.
- Snapshot mini queue: Ready, Running, PauseRequested, Paused, Interrupted, Cancelling, CompletedWithErrors, Completed; tiến độ mẫu có Completed/Failed/Skipped khớp công thức chi tiết.
- Job thật với provider giả lập: Pause chờ checkpoint, Chi tiết không tăng request, Tiếp tục xử lý chương còn lại đến Completed; mini bar ẩn khi hoàn tất.
- Một và hai worker giả lập: mở Chi tiết vẫn dùng cùng coordinator/job, chuyển workspace không dừng job, Pause giữ checkpoint của tất cả worker; tiến độ cộng dồn đúng. Các test Core về phân công worker/restart/retry cũng pass.

## Kiểm tra bố cục và giới hạn DPI

`Wpf_layout_and_render_matrix_preserves_primary_controls` kiểm tra 900 / 1180 / 1600 **WPF DIP**, cả Light/Dark, Biên tập / Soát lỗi / Đọc / Nhân vật / Cài đặt.
Mỗi view render bằng `RenderTargetBitmap` ở 96 / 120 / 144 / 192 DPI: **120 trường hợp render**, đồng thời kiểm tra bounds nút chính, menu, thanh chế độ và rail thu gọn.
Ảnh 96 DPI được lưu cục bộ trong `TestResults/ui-captures/` khi đặt `QTSTUDIO_UI_CAPTURE_DIR`; các ảnh tiêu biểu đã được xem để phát hiện và sửa lỗi chồng chữ/cắt nhãn.

**DPI raster không mô phỏng Windows thay DPI monitor.** Chưa xác nhận tự động việc chuyển monitor ở 100/125/150/200%, IME, vị trí popup sát cạnh màn hình hoặc toàn bộ trình tự chuột/bàn phím của người dùng thật.

Tái tạo ảnh:

```powershell
$env:QTSTUDIO_UI_CAPTURE_DIR = Join-Path (Get-Location) 'TestResults/ui-captures'
dotnet test tests/QuickTranslator.AiEditor.Wpf.Tests -c Release --filter SimplifiedUiRegressionTests
```

## Còn cần kiểm thử thủ công trước khi merge

- DPI Windows thật 100/125/150/200%, kéo giữa monitor; popup/overflow sát cạnh màn hình, Reader tìm kiếm/appearance/focus/toàn màn hình và cửa sổ thấp.
- GPU Local/Remote thật, SSH/mất kết nối, hai GPU đang chạy lâu, pause/resume sau khởi động lại; kiểm tra throughput với model đang dùng.
- Gemini thật: lưu/xóa key Credential Manager, khảo sát, duyệt và áp dụng proposal, hoàn tác. Không dùng key hoặc gọi API trả phí trong đợt này.
- Nghiệm thu thao tác đầu-cuối trên **bản sao** một Project cũ: thêm/sửa/xóa nhân vật, khóa đại từ/xưng hô, hộp thoại nhập/xuất hồ sơ và từ điển, sửa ngữ cảnh/preset, xuất bản; đóng/mở lại và kiểm tra nội dung. Automated tests kiểm tra dữ liệu tổng hợp và nhiều đường UI, chưa thay thế nghiệm thu này.

Không đổi file Core, engine dịch, Project schema, cấu trúc nhân vật, checkpoint, công thức tiến độ hoặc cơ chế phân công hàng đợi. Không merge vào base.
**PR tiếp tục Draft**: kiểm tra tự động đã đạt, nhưng chưa đủ bằng chứng nghiệm thu phần cứng/API và DPI monitor thực để xác nhận sẵn sàng merge.
