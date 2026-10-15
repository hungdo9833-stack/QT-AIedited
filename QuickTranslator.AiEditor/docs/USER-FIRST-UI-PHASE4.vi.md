# QT Studio Phase 4/4 — User-First UI/UX Redesign

## Phạm vi và baseline

- Nhánh nền: `codex/queue-workflow-phase3` tại `c4d59902f92cd51990b24df93622d00b0ee0d45e`.
- Nhánh triển khai: `codex/user-first-ui-phase4`.
- Checkpoint A: `b90e42b` — Import, Nhập & Dịch và resume workflow.
- Checkpoint B: `6223b44` — Queue UI, cập nhật tiến độ và GPU Settings.
- Checkpoint C: xem commit cuối của nhánh — Editor, Recovery, responsive layout và nghiệm thu.
- Dữ liệu kiểm thử đều là Project tổng hợp trong thư mục tạm. Không mở hoặc sửa Project cá nhân.

Phase 4 chỉ thay đổi presentation/workflow và phần đọc bảng mã cần thiết cho Import. Không thay đổi schema Project, pipeline dịch, prompt mặc định, Dynamic Multi-GPU Scheduling, checkpoint Phase 2, resume Phase 3, Character Consistency Lock, UserDraft/Approval hoặc `think=false`.

## Kết quả kiểm toán và thay đổi

### Import TXT

- Tự thử UTF-8 nghiêm ngặt trước, sau đó GB18030/GBK; không chấp nhận ký tự thay thế làm nội dung hợp lệ.
- Khi nhiều bảng mã có vẻ hợp lệ hoặc kết quả đáng nghi, Preview báo cần kiểm tra và vẫn cho chọn thủ công trong phần nâng cao.
- Màn hình mặc định chỉ hiển thị số chương mới, đã có, cần kiểm tra và có thể nhập.
- Có lệnh hàng loạt **Bỏ qua chương đã có**. Nếu trùng số nhưng nguồn khác, mục đó vẫn là xung đột cần xem lại.
- Có **Nhập** và **Nhập & Dịch**. Nhập & Dịch tạo queue từ đúng các ChapterId vừa commit rồi bắt đầu ngay.
- Import thành công dùng trạng thái gọn, không mở báo cáo kỹ thuật chặn thao tác.

### Translation Queue

- Chọn chương rồi bấm **Dịch các chương đã chọn** sẽ kiểm tra, lưu kế hoạch nội bộ và bắt đầu ngay; không còn lần bấm Start thứ hai.
- Khi mở lại Project có queue tiếp tục được, MainWindow hiển thị `Đã dịch X/Y chương. Còn Z chương.` và một nút **Tiếp tục dịch**.
- Màn hình chính chỉ giữ tổng số, hoàn thành, đang dịch, còn lại, tiến độ, worker/GPU, tốc độ, ETA và lỗi cần chú ý.
- Các cấu hình, retry/recovery và chi tiết kỹ thuật nằm trong vùng mở rộng.
- `RefreshView` giữ một `ObservableCollection`, cập nhật hàng hiện có theo ChapterId, throttle 100 ms, không gán lại ItemsSource và không tái tạo toàn bộ QueueRow cho mỗi ProgressChanged.
- Event handler và timer được hủy khi cửa sổ đóng.

### GPU Manager và Settings

- Danh sách mặc định chỉ hiện GPU, Local/Remote, model và trạng thái kết nối.
- Context, output tokens, temperature, timeout, kích thước đoạn và SSH nằm trong phần nâng cao.
- GPU Manager là nơi chỉnh cấu hình runtime chính. MainWindow chỉ dẫn người dùng đến đó, tránh hai màn hình lưu giá trị khác nhau.
- Đổi backend/model sử dụng cơ chế pause/refresh/resume Phase 3; không còn popup yêu cầu tạo lại Queue Plan.

### Editor, Chapter Manager và Recovery

- **Tách đoạn** lấy vị trí con trỏ ở ô nguyên văn. Nếu con trỏ đúng cuối câu, tách trực tiếp; nếu gần cuối câu, đưa một gợi ý dễ đọc; chọn từ chối sẽ mở lựa chọn nâng cao không dùng thuật ngữ UTF-16.
- Sửa nguyên văn an toàn lưu trực tiếp. Khi ảnh hưởng bản dịch/nháp/approval hoặc thay phần lớn nguồn, chỉ có một hộp xác nhận tổng hợp.
- Chapter Manager giữ các thao tác thường dùng ở mặt trước; thao tác chuyên sâu nằm trong **Tùy chọn chương**.
- Recovery dùng ngôn ngữ `Phục hồi bản trước`, `Giữ dữ liệu hiện tại`, `Lưu gói cứu hộ`; GUID/journal và nguyên nhân kỹ thuật nằm trong **Chi tiết kỹ thuật**.
- Thanh lệnh Editor có cuộn ngang ở màn hình hẹp để không làm mất nút.

## Workflow trước và sau

| Tác vụ đã xác minh | Trước Phase 4 | Sau Phase 4 |
|---|---:|---:|
| Chọn chương và bắt đầu dịch | 2 hành động: tạo queue + Start | 1 hành động Dịch |
| Resume sau khi mở Project | Mở Queue rồi Resume | 1 nút Tiếp tục dịch trên MainWindow |
| Project 1–100, TXT 1–300 | Xử lý từng collision trước khi nhập | Bỏ qua chương đã có + Nhập |
| Import và bắt đầu dịch | Import, sang Chapter Manager, chọn, tạo queue, Start | Chọn TXT, Nhập & Dịch |
| Sửa nguyên văn an toàn | 1 popup preview | 0 popup |
| Sửa nguyên văn ảnh hưởng dữ liệu | Preview + xác nhận bảo vệ | 1 xác nhận tổng hợp |
| Import thành công | Popup báo cáo chặn thao tác | Trạng thái gọn trong cửa sổ |
| Cấu hình model/runtime | MainWindow và GPU Manager | GPU Manager là bề mặt chỉnh duy nhất |

Các hộp thoại vẫn được giữ cho quyết định có hậu quả thật: thay đổi nguồn đã có dữ liệu bảo vệ, recovery chưa chắc chắn, thay lịch sử, kết nối Remote/SSH và xuất bản chưa hoàn chỉnh.

## Kiểm thử chức năng

- Import Project có chương 1–100 với TXT 1–300: bỏ 100 chương cũ, import 200 chương mới, giữ tiêu đề chương cũ, mở lại có đủ 300 chương, không orphan/recovery.
- Auto decoding legacy và bảo toàn nguyên văn.
- Queue bắt đầu ngay, queue resume banner, không duplicate request.
- Queue lớn giữ nguyên ItemsSource, selection và đối tượng row sau refresh.
- Split theo caret, gợi ý cuối câu gần nhất và source edit confirmation classification.
- Recovery presentation và quyết định Core cũ.
- Các suite Phase 1–3: transaction cleanup, checkpoint, crash/reopen, multi-GPU, UserDraft/Approval, import/export.

## Benchmark

Máy nghiệm thu hiện tại, Release, Project tổng hợp:

| Queue rows | Số refresh | Trung bình | Allocation/refresh | CPU | Wall |
|---:|---:|---:|---:|---:|---:|
| 300 | 200 | 0.085 ms | 10,656 B | 15.625 ms | 17.111 ms |
| 1,000 | 200 | 0.307 ms | 33,064 B | 46.875 ms | 61.412 ms |
| 5,000 | 50 | 1.540 ms | 161,064 B | 78.125 ms | 77.078 ms |

Sau lần tạo ban đầu, số QueueRow giữ đúng 300/1.000/5.000 và không bị tạo lại khi chỉ cập nhật tiến độ. Đây là benchmark phương thức cập nhật UI bằng dữ liệu tổng hợp; không phải phép đo CPU toàn ứng dụng khi hai GPU thật đang suy luận.

Kiểm tra không hồi quy Phase 3 với 250 chương/2 worker:

- 3.913 giây, 63.897 chương/giây.
- Resume đến request đầu: 91.228 ms.
- Resource refresh: 17.621 ms.
- 24 lần ghi job; Local/Remote 125/125; hoàn thành 250 ChapterId khác nhau.
- Dừng request: 12.211 ms.

Kiểm tra storage 250 checkpoint:

- Trung bình 7.680 ms; median 7.510 ms; p95 9.747 ms.
- Không tạo transaction directory/file dư.
- Reopen 7.411 ms; resume stale manifest 4.426 ms.

## Ảnh nghiệm thu WPF

Ảnh được render từ cửa sổ WPF thật bằng Project tổng hợp:

- `artifacts/user-first-ui-phase4/screenshots/two.png`
- `artifacts/user-first-ui-phase4/screenshots/four.png`
- `artifacts/user-first-ui-phase4/screenshots/chapter.png`
- `artifacts/user-first-ui-phase4/screenshots/queue.png`
- `artifacts/user-first-ui-phase4/screenshots/gpu.png`
- `artifacts/user-first-ui-phase4/screenshots/reader.png`
- Các biến thể `*-1366x768-150pct.png` dùng để kiểm tra bố cục hẹp.

Ảnh baseline trước Phase 4 nằm tại `artifacts/ux-redesign-v1/smoke`.

## Giới hạn nghiệm thu

- WPF thật đã được build/render và các luồng focus/caret/draft được kiểm thử tự động; chưa thực hiện phiên gõ IME tiếng Việt thủ công kéo dài.
- Không kết nối Ollama/GPU thật trong Phase 4; automated tests dùng fake provider để kiểm tra phân phối, pause/resume và bảo vệ dữ liệu.
- Benchmark queue đo lớp cập nhật UI và kiểm tra identity/selection; cảm giác cuộn trên từng thiết bị/DPI vẫn nên được nghiệm thu thủ công.
- Bố cục 1366×768 ở 150% ưu tiên Editor và dùng cuộn thanh lệnh; sidebar/inspector có thể thu gọn theo thiết kế responsive hiện có.

## Build và chạy

```powershell
dotnet build QuickTranslator.AiEditor.slnx -c Release
dotnet run --project src/QuickTranslator.AiEditor.Wpf/QuickTranslator.AiEditor.Wpf.csproj -c Release
```

