# QT Studio Storage Optimization — Phase 1

Ngày nghiệm thu: 2026-10-15

## Phạm vi

Phase 1 tối ưu vòng đời `History/Transactions` mà không thay đổi schema Project, nội dung chapter hoặc pipeline dịch. Tất cả kiểm thử và benchmark dùng Project tổng hợp trong thư mục tạm; không đọc hoặc sửa Project cá nhân.

## Nguyên nhân đã xác minh

Mỗi lần lưu chapter tạo một transaction có `journal.json`, các bản `before`/`after` của chapter và manifest. Sau khi transaction chuyển sang `Committed` hoặc `RolledBack`, thư mục vẫn tồn tại vô thời hạn. Khi mở Project, mã cũ còn đọc và băm lại các bản sao của cả transaction đã hoàn tất. Vì autosave/checkpoint diễn ra thường xuyên, dung lượng và số file tăng tuyến tính theo số lần lưu, đồng thời thời gian mở Project tăng theo toàn bộ lịch sử terminal.

## Vòng đời sau khi sửa

| Trạng thái | Tự dọn sau thao tác | Dọn thủ công | Lý do |
| --- | --- | --- | --- |
| `Committed` | Có, best effort | Có | Dữ liệu chính đã được ghi và xác minh |
| `RolledBack` | Có nếu không có retained | Có nếu không có retained | Trạng thái trước đã được phục hồi |
| `Pending` | Không | Không | Cần phục hồi hoặc quyết định |
| `RollingBack` | Không | Không | Rollback chưa hoàn tất |
| `RecoveryRequired` | Không | Không | Cần người dùng đối chiếu/phục hồi |
| Có `retained-*.bin` | Không | Không | Có dữ liệu cứu hộ cần giữ |
| Journal hỏng/không đọc được | Không | Không | Không đủ bằng chứng để xóa an toàn |
| Đường dẫn, reparse point hoặc file lạ | Không | Không | Từ chối xóa để tránh vượt phạm vi |

Việc dọn tự động là best effort. Nếu file đang bị khóa hoặc thiếu quyền, thao tác lưu vẫn được coi là thành công sau khi dữ liệu Project đã commit; transaction còn lại có thể được thử dọn sau từ giao diện.

## Dọn dữ liệu từ giao diện

Mở **Dự án → Dọn dữ liệu thừa…**. Cửa sổ luôn chạy Preview/Dry Run trước, hiển thị tổng số transaction, dung lượng, số mục có thể xóa và lý do các mục được giữ. Chỉ sau khi người dùng xác nhận, ứng dụng mới xóa từng transaction đủ điều kiện. Mục lỗi hoặc bị khóa được giữ nguyên để thử lại.

Ứng dụng chặn mở công cụ trong lúc đang dịch, có request AI, queue Project đang chạy hoặc chapter hiện tại còn thay đổi chưa lưu.

## An toàn đường dẫn và dữ liệu

- Chỉ xét thư mục GUID nằm trực tiếp dưới `History/Transactions`.
- Không đi theo junction, symbolic link hoặc reparse point.
- Không xóa thư mục có thư mục con hoặc file ngoài tập journal/before/after đã biết.
- Xóa từng file, xóa journal sau cùng và không dùng xóa đệ quy.
- Mỗi candidate được phân loại lại ngay trước khi xóa.
- Dùng cùng `OperationGate` của Project Repository để không chạy chồng với thao tác ghi trong tiến trình.
- Không xóa source rescue, draft, approval, glossary, character profile, checkpoint queue hoặc history biên tập nằm ngoài transaction terminal.

## Tối ưu khi mở Project

Khi kiểm tra recovery, transaction `Committed` và `RolledBack` chỉ được đọc/kiểm tra metadata đủ để xác định trạng thái terminal rồi bỏ qua. SHA của bản sao và file đang dùng chỉ được tính cho `Pending`, `RollingBack` và `RecoveryRequired`, là các trạng thái thực sự cần quyết định phục hồi.

## Benchmark tổng hợp

Kịch bản: một chapter tổng hợp, 1.000 lần `UpdateChapterAsync`, sau đó mở lại Project.

| Chỉ số | Trước | Sau |
| --- | ---: | ---: |
| Thư mục transaction còn lại | 1.001 | 0 |
| File transaction còn lại | 9.004 | 0 |
| Dung lượng transaction còn lại | 134.221.165 byte (~128 MB) | 0 byte |
| Thời gian mở lại | 719,3 ms | 5,11 ms (các lần đo: 4–5,3 ms) |
| Thời gian lưu trung bình | 19,619 ms | 30,555 ms (các lần đo: 24–30,6 ms) |

Chi phí lưu tăng khoảng 5–11 ms trong benchmark nhỏ vì transaction terminal được xóa đồng bộ ngay sau commit. Đổi lại, storage không tiếp tục tăng và lần mở sau không phải quét lịch sử terminal. Phase 1 ưu tiên trạng thái sạch, dễ kiểm chứng và không để công việc nền tồn tại khi ứng dụng đóng.

Benchmark có thể chạy bằng project `tools/QuickTranslator.AiEditor.Storage.Benchmark`; nó luôn tạo và tự xóa dữ liệu dưới `%TEMP%\QTStudio-Storage-Benchmark`.

## Kiểm thử bắt buộc

- Lưu/checkpoint thành công không tích lũy transaction terminal.
- Transaction bị ngắt giữ nguyên journal và bản before/after để recovery.
- Tập hỗn hợp chỉ xóa `Committed`/`RolledBack` hợp lệ; giữ Pending, retained và journal hỏng.
- File bị khóa làm cleanup thất bại an toàn; lần thử sau hoàn tất.
- Nội dung chapter, UserDraft, Approval, ConfirmedTerm, Glossary và Context không thay đổi sau cleanup/reopen.
- Mở Project không băm bản copy terminal đã lỗi thời.
- Menu WPF cho phép Preview/Dry Run mà không cần terminal.

## Giới hạn còn lại

- Transaction terminal cũ chỉ được xóa khi journal còn đọc được và cấu trúc thư mục nằm trong whitelist. Journal hỏng được giữ để đối chiếu thủ công.
- Cleanup không chạy đồng thời với thao tác trong cùng tiến trình nhờ `OperationGate`; khóa từ một tiến trình khác được xử lý bằng cách giữ mục lỗi và cho phép thử lại.
- Phase 1 chưa áp dụng quota theo tuổi/dung lượng cho dữ liệu retained vì đó là dữ liệu cứu hộ không được phép tự xóa.
