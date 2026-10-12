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

`phase2-f12-final`: **235 Core + 30 WPF = 265 pass**, không skip/fail, Release 0 warning/error. 11 WPF tests mới: Reader save propagation, SaveAssets, chuyển chapter/Retry, batch Giữ nguyên lỗi + duplicate dispatch, Previous/Next save failure, Character/Glossary resource locked file + Retry, Project Properties locked manifest + Retry, Quick Character/Glossary nhập dialog + locked file giữ inputs. WPF chạy STA Windows, mở MainWindow ngoài vùng màn hình, mở ShowDialog thật và kích hoạt RoutedEvent, toàn bộ stores trỏ temp. Đây là kiểm thử tích hợp tự động, không phải nghiệm thu bằng chuột/mắt của người dùng. Không gọi Ollama thật.

## Rà soát bổ sung F05 — checkpoint và hủy giữa nhiều chương

Bốn tests `phase2-f05-checkpoint-red` fail trước guard: các đường SetQueueCurrent, AdvanceQueue, CheckpointElapsed, SetQueueStatus có thể sửa document trong lúc ứng viên batch đang ghi. Guard mutation được áp dụng trước sửa ở cả bốn đường. Không thêm Queue nhiều chương hoặc thay thuật toán Queue.

Hai tests `phase2-f05-project-cancel-red-confirmed` fail trước sửa: hủy tại chương thứ hai của Replace/Undo ném TaskCanceledException, làm caller mất báo cáo chương đầu đã ghi. Sau sửa, nếu backup đã tạo và vòng ghi bắt đầu, coordinator trả kết quả từng chương đã ghi/chưa ghi, kèm lý do hủy; chương đang ghi bị hủy và các chương còn lại không bị thay đổi. Hủy trước/đang tạo backup vẫn ném lỗi và chưa áp dụng chương nào. Thêm test khóa file chapter thật (FileShare.Read): primary bytes/live JSON/dirty flag giữ nguyên, file tạm được dọn và Retry thành công.

Kết quả cuối `phase2-final-complete`: **242 Core + 30 WPF = 272 pass**, 0 fail, 0 skip. Gồm toàn bộ 220 baseline và 52 regression tests mới. Locked restore đạt. Build Release toàn solution bằng `--no-restore --no-incremental`: **0 lỗi, 0 cảnh báo**; log `artifacts/data-safety/phase2-final-complete/build-full.txt`. Không sửa tests F01–F04; tất cả tests cũ vẫn được chạy.

## File thay đổi trong Phase 2

Các đường dẫn dưới đây tính từ `QuickTranslator.AiEditor/`:

- Core: `src/QuickTranslator.AiEditor.Core/Models/TextCleaning.cs` (approval metadata), `Persistence/DocumentJson.cs` (validate metadata), `TextCleaning/BatchReplacementEngine.cs` (prepare/validate/commit và Undo guard), `TextCleaning/BatchProjectReplacementService.cs` (báo cáo hủy/partial success).
- Service: `src/QuickTranslator.AiEditor.Core/Translation/TranslationService.TextCleaning.cs` (save candidate/commit transaction), `TranslationService.cs` (mutation guard/source translation), `TranslationService.Editing.cs` (rename/structure/processing guard), `TranslationService.Queue.cs` (checkpoint guard).
- WPF: `src/QuickTranslator.AiEditor.Wpf/MainWindow.TextCleaning.cs` (callback/gate/error tại nguồn), `MainWindow.Reader.cs` (chỉ save preference/timer), `MainWindow.Projects.cs` (Loaded unwrap và shortcut callback), `MainWindow.WorkspaceLifetime.cs` (báo request outcome), `MainWindow.xaml.cs` (source save/close/error), `MainWindow.ContextMenus.cs` (giữ inputs để Retry).
- Tests mới: `tests/QuickTranslator.AiEditor.Tests/AtomicBatchTests.cs`, `SafeBatchUndoTests.cs`, `tests/QuickTranslator.AiEditor.Wpf.Tests/AsyncSafetyTests.cs`.
- Tài liệu: `docs/DATA-SAFETY-PHASE2.vi.md`.

Chỉ source/tests/docs được đưa vào checkpoint. SDK/cache/build/log/TRX bị ignore. Không có Project thật, bản dịch, từ điển hoặc dữ liệu riêng trong thay đổi.

## Nghiệm thu thủ công còn cần

Dùng một project tổng hợp mới hoặc bản sao riêng, không thử lỗi trên project thật:

1. Batch một chương có bản nháp, approval và edit history; làm preview stale ở đoạn đầu/giữa/cuối, xác minh nội dung và history nguyên vẹn. Thử Replace → Approve → Undo và Replace → Edit → Undo: phải từ chối rõ lý do. Replace → Save → đóng/mở lại → Undo không có sửa/duyệt mới phải thành công.
2. Kiểm tra bằng chuột/phím các dialog Batch, Glossary và Character: lúc đang ghi không điều hướng/đóng/chạy lặp; khi lỗi, nội dung nhập vẫn có để bấm lại hoặc Lưu hồ sơ/Lưu thiết lập. Xác nhận thông báo đủ dễ đọc. Tests đã thực hiện RoutedEvent và ShowDialog thật, nhưng chưa nghiệm thu trực quan.
3. Ollama thật: request dài hoặc lỗi mạng/model, sửa draft khi đang dịch, thử Next/Previous/đóng project. Xác nhận checkpoint/nguồn request đúng và không có kết quả ghi sang chương khác. Tests F04 dùng FakeClient, không xác minh model thực.
4. Quyền thư mục Windows bị từ chối, đầy ổ đĩa, mất điện/kill process ngay lúc ghi, antivirus/đồng bộ đám mây. Đã inject IOException/cancellation và khóa file thật, chưa tạo các điều kiện hệ thống này.

## Giới hạn và bảo vệ dữ liệu

Atomicity là một chương qua ChapterFileStore atomic rename/replace; backup/checksum hiện có được giữ. Nhiều chương không là transaction toàn project: có backup và báo từng chương, kể cả hủy giữa vòng ghi. Lỗi lúc tạo backup dừng trước ghi. Project resources + chapter + manifest là các bước lưu riêng, chưa có transaction đa file; lỗi báo chưa hoàn tất và giữ inputs để Retry, có thể có bước trước đã ghi. Project Properties giữ manifest đề xuất trong bộ nhớ khi save thất bại để thử lại, không báo đã lưu.

Batch cũ thiếu approval stamp bị từ chối tự Undo, giữ toàn bộ history để đối chiếu hoặc khôi phục thủ công từ backup. Không hạ về build cũ để bỏ guard này. Chuẩn bị ứng viên clone cả chương có chi phí RAM/CPU theo độ lớn lịch sử; chưa benchmark truyện hàng nghìn chương trong scope này. Không bảo đảm phục hồi từ mọi lỗi phần cứng hoặc lỗi runtime ngoài các nguồn đã xử lý. Không có thử nghiệm Project thật.

## Checkpoint và bàn giao

- Baseline Phase 1 giữ nguyên trong ancestry: `99685b1e36529fcf9073903ea0d196699e61d871`.
- Checkpoint M0 Phase 2 trước sửa: `1c5a691a1835f871fd0c6a23c931619abc416f22`.
- F05: `0349e220a6d22ada16d5516571206b3ba776259a`.
- F06: `a75a5de8a39bdaf07cccd9b5ed7d5e8f8e64c017`.
- F12: `2c6bdce5aa4a66e4e382d040ccf4dfea1fdc901a`.
- Rà soát bổ sung F05 được checkpoint riêng sau 272 tests pass; SHA HEAD bàn giao được ghi trong phản hồi cuối.

Branch bàn giao: [codex/m0-data-safety-phase1](https://github.com/hungdo9833-stack/QuickTranslator/tree/codex/m0-data-safety-phase1). Push thường, không force, không merge vào `codex/project-workspace` hoặc `ngoctay`. Handoff local ref vẫn `965cf6832f6c9831eb4a8da5527fd3f2aa5199c5`.

Lệnh chạy lại: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File QuickTranslator.AiEditor/scripts/Validate-DataSafety.ps1 -Stage phase2-recheck`. SDK riêng/cache/artifacts ở workspace; ExecutionPolicy Bypass chỉ cho process này, không đổi cấu hình hệ thống.
