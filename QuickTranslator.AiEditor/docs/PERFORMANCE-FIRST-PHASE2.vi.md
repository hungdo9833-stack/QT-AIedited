# QT Studio Performance-First Refactor — Phase 2

Ngày nghiệm thu: 2026-10-15

## Baseline

- Nhánh nền: `codex/storage-optimization-phase1`.
- Commit nền: `d0baedb6922c78bb6f2dbb729666f4cad78ef0dc`.
- Nền này đã chứa hai commit Index Translate 9B `think=false` từ `feature/index-translate-nothink-test`.
- Phase 1 baseline: 685/685 tests đạt; Release 0 lỗi/0 cảnh báo.
- Toàn bộ benchmark và fault injection dùng Project tổng hợp trong `%TEMP%`; không đọc hoặc sửa Project cá nhân.

## Đường lưu trước Phase 2

Một segment chạy trong chapter queue trước đây có thể lưu chapter ba lần:

1. Sau khi đánh dấu `Translating`, trước khi gọi AI.
2. Ngay sau khi nhận và xử lý kết quả AI.
3. Sau khi tăng `Queue.NextIndex` và xóa `CurrentSegmentId`.

Mỗi lần lưu đi qua `RevisionGuardedChapterStore` → `UpdateChapterIfStorageRevisionAsync` → `ProjectTransactionJournal.ExecuteAsync`. Checkpoint tạo transaction gồm chapter backup, chapter, manifest backup, manifest, các bản before/after và journal được ghi lại sau từng file. Phase 1 xóa transaction terminal ngay sau commit, nhưng vẫn phải tạo, băm, flush và xóa toàn bộ dữ liệu này.

Truy vết tĩnh một checkpoint Phase 1 ở trạng thái ổn định cho thấy tối thiểu khoảng 14 lần đọc file, 18 lần ghi, 10 file tạm/atomic write và 9 lần xóa file transaction; chưa tính thao tác metadata thư mục. Đây là nguồn I/O lớn nhất. Queue Job Store không ghi ở mỗi sự kiện progress: `OnChapterProgress` chỉ cập nhật bộ nhớ và thông báo UI; job được ghi tại các chuyển trạng thái cần Resume, nên không phải hotspot chính.

## Fast Checkpoint

`UpdateChapterAsync` và `ProjectTranslationCheckpoint` không còn đi qua transaction nhiều file. Đường mới:

1. Xác minh Project/chapter identity và revision ngay trước khi ghi.
2. Serialize chapter một lần.
3. Ghi chapter bằng file tạm + flush + atomic replace; primary hợp lệ trước đó trở thành `.bak` gần nhất.
4. Ghi manifest summary bằng atomic replace độc lập.
5. Chỉ phát sự kiện catalog sau khi cả hai bước hoàn tất.

Mỗi checkpoint thường xuyên có đúng hai atomic write: chapter và manifest. Không tạo journal, bản before/after hoặc lịch sử checkpoint.

`ProjectTransactionJournal` vẫn được giữ cho Add/Import, Soft Delete/Restore, resegmentation cấu trúc, batch title và Worker Segmentation Preflight vì các thao tác này thay đổi nhiều file có quan hệ cấu trúc.

## Crash giữa chapter và manifest

Chapter là nguồn dữ liệu có thẩm quyền cho nội dung dịch. Nếu process dừng sau khi chapter đã atomic replace nhưng trước khi manifest được ghi, chapter mới vẫn hợp lệ. Khi mở Project:

- Chỉ chapter có timestamp mới hơn manifest mới được kiểm tra.
- Nếu summary thực sự khác, manifest được tái tạo từ chapter và ghi nguyên tử một lần.
- Nếu summary giống nhau, manifest không bị ghi lại.
- ChapterId/ProjectId không khớp làm mở Project thất bại rõ ràng; không tạo orphan giả hoặc tự nhận file lạ.

Crash trước atomic replace giữ nguyên primary cũ và xóa file tạm. Crash sau manifest write được xem là checkpoint đã commit. Không cần journal hoặc xác nhận của người dùng cho các trường hợp này.

## Revision guard và nhiều GPU

- Chapter store dùng khóa dùng chung theo thư mục để serialize các lần ghi vào cùng Project trong một tiến trình.
- Worker giữ SHA của chapter đã nạp. SHA được kiểm tra bên trong khóa ngay trước atomic replace.
- Revision cũ gây `ProjectTranslationConflictException`; không ghi đè UserDraft, Approval hoặc kết quả worker mới hơn.
- `ProjectSession.OperationGate` tiếp tục serialize chapter/manifest update của cùng session.
- Scheduler nhiều GPU, quy tắc mỗi worker xử lý một chapter và cơ chế worker nhanh nhận chapter Pending tiếp theo không thay đổi.

## Giảm số lần checkpoint AI

- Không ghi trạng thái `Translating` trước request. Khi mất điện, request đang sinh token có thể mất như phạm vi Phase 2 cho phép.
- Dịch riêng một segment lưu đúng một lần sau khi kết quả hoặc lỗi đã được ghi vào bộ nhớ.
- Queue gộp kết quả AI, `NextIndex`, elapsed và việc xóa current segment vào một checkpoint sau request.
- Cancel/fault được lưu ở terminal checkpoint của Queue.
- Queue chapter có N segment thành công thực hiện N + 2 lần lưu: một lần khởi động Queue, N checkpoint kết quả và một lần trạng thái kết thúc. Trước đây cùng luồng cần khoảng 3N + 2 lần lưu.

## Benchmark 1.000 checkpoint

Kịch bản dùng cùng một chapter tổng hợp và 1.000 lần `UpdateChapterAsync`.

| Chỉ số | Trước Phase 1 | Sau Phase 1 | Sau Phase 2 |
| --- | ---: | ---: | ---: |
| Trung bình | 19,619 ms | 30,555 ms | 8,129–9,980 ms |
| Median | Chưa instrument | Chưa instrument | 7,591–9,281 ms |
| p95 | Chưa instrument | Chưa instrument | 11,755–14,198 ms |
| Transaction còn lại | 1.001 thư mục | 0 | 0 |
| Dung lượng transaction | 134.221.165 byte | 0 | 0 |
| Dung lượng Project sau test | ~134,3 MB gồm transaction | ~64 KB | 64.506–64.536 byte |
| Mở lại Project | 719,3 ms | 5,11 ms | 11,4–17,1 ms |

Phase 2 giảm khoảng 67–73% thời gian lưu so với Phase 1 và khoảng 49–59% so với baseline trước Phase 1. Chênh lệch thời gian mở lại vẫn ở mức mili giây và không tăng theo số checkpoint. Mở lại sau crash giả lập giữa chapter/manifest mất 7,77 ms và khôi phục đúng summary.

Instrumentation Phase 2 trên mỗi checkpoint:

| Hoạt động | Số lần/checkpoint |
| --- | ---: |
| Serialize | 2 |
| Hash | 9 |
| Đọc file | 2 |
| Byte đọc | ~32.253 byte |
| Ghi file | 2 |
| Byte ghi | ~32.253 byte |
| File tạm | 2 |
| Atomic backup replacement | 2 |
| Xóa file sau commit | 0 |

## Fault injection và tương thích

Đã kiểm tra:

- Crash trước chapter write, sau chapter write/trước manifest và sau manifest write.
- Exception sau khi file tạm đã flush: primary cũ giữ nguyên, temp được dọn.
- Primary hỏng với `.bak` hợp lệ: tạo rescue và ghi checkpoint mới có kiểm soát.
- Worker dùng revision cũ không thể ghi đè bản sửa tay.
- Project cũ mở không cần migration hoặc thay schema.
- Hai worker xử lý chapter khác nhau, worker nhanh nhận việc tiếp theo, pause/resume/cancel và reopen job.
- UserDraft, Approval, history biên tập, glossary, character profile, source và Queue được giữ nguyên.
- Transaction Phase 1 cho thao tác cấu trúc vẫn vượt qua fault injection và cleanup tests.

## Giới hạn

- Manifest summary có thể chậm hơn chapter trong khoảng rất ngắn giữa hai atomic write. Open Project tự đối chiếu trường hợp này.
- Cơ chế phát hiện summary cần đối chiếu dùng timestamp file; trên Windows/NTFS có độ phân giải phù hợp với ứng dụng hiện tại. Project được sao chép từ filesystem có timestamp bất thường có thể khiến một số chapter mới hơn manifest được đọc để xác minh, nhưng chỉ ghi manifest khi summary thực sự khác.
- `.bak` chỉ là phiên bản primary hợp lệ gần nhất, không còn là lịch sử rollback của từng checkpoint.
- Kết quả request AI chưa hoàn tất có thể mất khi process bị dừng; segment đã checkpoint hoàn tất không bị dịch lại khi Resume.

