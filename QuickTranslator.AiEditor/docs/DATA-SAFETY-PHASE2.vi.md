# Data Safety Phase 2 — 12/10/2026

Phạm vi được duyệt: F05, F06, F12; kế thừa F01–F04. Branch `codex/m0-data-safety-phase1`, baseline `99685b1e36529fcf9073903ea0d196699e61d871`. Working tree sạch trước khi bắt đầu. Không mở/sửa Project thật.

Baseline chạy lại trên Windows, SDK riêng 10.0.401: restore locked thành công; Release **0 lỗi/0 cảnh báo**; **201 Core + 19 WPF = 220 tests đạt**, không bỏ qua. Logs/TRX: `artifacts/data-safety/phase2-baseline` (không commit).

Checkpoint chuẩn bị được tạo trước khi sửa. Kết quả red/green và nghiệm thu từng nhóm được bổ sung tại tài liệu này.

## F05 — Atomic Batch Replacement

Nguyên nhân: ApplyToChapter vừa kiểm tra vừa sửa segment; ApplyBatchReplacementAsync sửa bản sống trước khi SaveAsync. Lỗi muộn hoặc lỗi ghi tạo nội dung/lịch sử dở dang.

Cách sửa: chuẩn bị toàn bộ ứng viên riêng, validate revision, text, approval version/ID, status, protected draft, xung đột/offset và DocumentJson trước commit. Service giữ save gate, khóa mutation trong lúc ghi, chỉ thay bản sống/saved revision khi store thành công. Lỗi/hủy giữ nguyên document, dirty flag và history; Retry dùng cùng preview nếu chưa thay đổi. Không thay schema file ở F05. Giữa nhiều chương vẫn partial success có báo cáo và backup riêng; không tuyên bố transaction toàn project.

Tests trước sửa: `phase2-f05-red`, 20 tests mới: **16 fail / 4 pass**. Tests bổ sung lỗi lưu/hủy với nháp chưa lưu, retry và partial success project. Sau sửa `phase2-f05-final`: **224 Core + 19 WPF = 243 pass**, không skip/fail. Locked restore và Release build 0 warning/error. Dữ liệu đều tổng hợp trong temp; artifacts được ignore.

## F06 — Safe Batch Undo

Tests trước sửa `phase2-f06-red`: **5 fail / 4 pass** trên 9 tests. Thất bại gồm approve ở đầu/giữa/cuối, approve rồi save/reopen và lỗi ghi Undo. Các guard sửa tay đã có hoạt động; test sửa rồi trở lại cùng text xác nhận revision vẫn ngăn ghi đè.

Batch thủ công mới lưu hai metadata optional `AppliedApprovalVersion` (số approval append-only) và `AppliedLastApprovalId`. Đây là dữ liệu cần thiết để xác minh quyết định duyệt sau reopen; không tăng schema version hoặc migrate/xóa lịch sử. Preview F05 kiểm tra version/ID hiện hành. Undo validate toàn bộ đoạn trước, từ chối approval mới hoặc draft revision/text đã đổi; bản duyệt trước batch được giữ và có thể phục hồi nếu không có quyết định mới. Batch cũ thiếu stamp vẫn load/validate được, nhưng từ chối tự Undo với hướng dẫn đối chiếu history/backup thủ công. Checksum v2/v3 xác minh JSON gốc nên metadata thiếu không phá việc đọc file cũ.

Undo dùng cùng transaction ghi ứng viên của F05; lỗi ghi giữ nguyên live/disk/history và cho Retry. Sau sửa `phase2-f06-green`: **235 Core + 19 WPF = 254 pass**, 0 skip/fail; Release 0 warnings/errors. Bổ sung test legacy và approval có trước batch. Không xóa approvals/drafts/edit history.

## F12 — Async Error Handling

Nguyên nhân xác minh: SaveReaderPreferencesAsync bắt lỗi rồi trả về thành công, nên SaveProjectAssets/context switch tiếp tục; callback Giữ nguyên/Glossary/Character trong batch không có catch/operation gate; callback xóa shortcut thư viện thiếu catch; khởi tạo Projects dùng Dispatcher async lồng mà chưa unwrap inner Task. Quick Character/Glossary bắt lỗi nhưng rollback riêng editor làm mất dữ liệu vừa xác nhận, và có thể lệch với các bước lưu trước đã thành công.

Sửa tại nguồn: helper lưu trả lỗi lên caller; timer Reader bắt lỗi và khóa thao tác, không chạy chồng với chuyển context. Ba callback batch dùng guard chung, catch rõ lỗi + hướng dẫn thử lại, unlock finally; chỉ quét lại sau thành công. Double click điều hướng bị chặn khi ghi; navigation nhận lock mới sau khi dialog đã giải phóng lock. Callback thư viện và Projects Loaded xử lý lỗi/await đúng. SaveCurrent chụp service/session nguồn. Close chỉ bỏ qua request hủy/lỗi Ollama đã được ghi nhận và báo rõ; lỗi đĩa bất ngờ chặn đóng. ShowError hướng dẫn giữ ứng dụng mở, sửa quyền/dung lượng rồi Retry. Quick Character/Glossary giữ editor inputs khi lỗi, không rollback riêng UI. Project Properties, Previous/Next và các Character/Glossary handlers sẵn có catch được kiểm chứng bằng fault injection, không báo lưu thành công khi thất bại.

Không thêm Dispatcher catch-all để che lỗi tại nguồn. Test batch cài trap Dispatcher **chỉ trong test** để đếm exception thoát handler; sau sửa đếm bằng 0. Không sửa thuật toán/render của Reader; chỉ đường lưu preference và timer liên quan context.

Tests trước sửa: `phase2-f12-red-confirmed`: **4 fail / 0 pass**, gồm lỗi helper Reader, SaveAssets, switch và hai exception thoát Dispatcher do nhấp Giữ nguyên lặp. `phase2-f12-quick-red`: **2 fail / 0 pass** do mất inputs Quick Character/Glossary. Lần chạy đầu test mới có lỗi compile thiếu using Xunit; đã sửa test trước khi dùng kết quả red. Lần đầu thao tác dialog chưa đợi tab render; đã sửa harness để chờ rồi tái hiện lỗi thật. Test resource fault ban đầu chưa tạo file canonical; đã sửa setup trước khi đánh giá production.

`phase2-f12-final`: **235 Core + 30 WPF = 265 pass**, không skip/fail, Release 0 warning/error. 11 WPF tests mới: Reader save propagation, SaveAssets, chuyển chapter/Retry, batch Giữ nguyên lỗi + duplicate dispatch, Previous/Next save failure, Character/Glossary resource locked file + Retry, Project Properties locked manifest + Retry, Quick Character/Glossary nhập dialog + locked file giữ inputs. WPF chạy STA Windows, mở Window/ShowDialog thật ngoài vùng màn hình và kích hoạt RoutedEvent, toàn bộ stores trỏ temp. Đây là kiểm thử tích hợp tự động, không phải nghiệm thu bằng chuột/mắt của người dùng. Không gọi Ollama thật.
