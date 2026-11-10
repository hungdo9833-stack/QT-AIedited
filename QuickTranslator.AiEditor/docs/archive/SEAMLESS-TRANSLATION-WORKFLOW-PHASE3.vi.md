# QT Studio Phase 3 — Seamless Translation Workflow

## Phạm vi và baseline

- Nhánh nền: `codex/performance-first-phase2`, commit `ea3a9d242574e6c30e07ffbfac264aa860a2cec7`.
- Baseline: 609 Core + 85 WPF tests đạt.
- Không đổi schema Project/chapter, prompt mặc định, `think=false`, thuật toán pull-based dynamic scheduling, Character Consistency Lock, import hoặc export.
- Chỉ dùng Project tổng hợp trong `%TEMP%`.

## Nguyên nhân đã xác minh

1. `ProjectTranslationCoordinator` so sánh snapshot tài nguyên, prompt và model với Queue Plan rồi chuyển toàn job sang `Interrupted` khi có khác biệt hợp lệ.
2. `ValidateChapterRevision` coi thay đổi UserDraft/Approval giống thay đổi SourceText/ranh giới nên tạo `NeedsReview` giả.
3. Resume song song chỉ chấp nhận đúng worker snapshot cũ; đổi profile/mode/model buộc tạo Queue Plan khác dù phần chưa dịch vẫn an toàn.
4. Pause và Stop dùng chung hành vi chờ request. Luồng đóng ứng dụng gọi Pause nên có thể chờ toàn bộ thời gian sinh token.
5. Toàn bộ file job và manifest được ghi nhiều lần cho mỗi chapter. Benchmark 250 chapter ban đầu ghi job 1.258 lần và khoảng 1,095 GB dữ liệu trung gian.

## Hành vi mới

### Cấu hình và tài nguyên

- Glossary, Context, Character Profiles và Prompt mới được nạp tại ranh giới chapter.
- Chapter đang chạy giữ snapshot tài nguyên của request hiện tại; chapter tiếp theo dùng revision mới.
- Model options của coordinator đơn được làm mới ở ranh giới chapter.
- Resume tự làm mới tài nguyên, prompt và cấu hình hợp lệ, không cần Replan.
- Queue song song có thể đổi worker/mode khi Paused. Worker cùng profile nhưng SSH identity khác vẫn bị chặn; profile Remote mới chỉ được dùng sau khi GPU Manager đã tạo kết nối xác nhận HostKey.
- Completed items, checkpoint chapter và tổng metric worker được giữ khi đổi cấu hình.

### Revision và UserDraft

- Source fingerprint vẫn chặn mọi thay đổi SourceText, SegmentId, thứ tự hoặc ranh giới.
- Khi source fingerprint không đổi, coordinator làm mới protected/document fingerprint đã xác minh.
- UserDraft và Approval mới được giữ; `TranslationService.IsQueueEligible` tự bỏ qua segment được bảo vệ và dịch các segment còn lại.
- Revision-guarded store vẫn ngăn kết quả AI đến muộn ghi đè chỉnh sửa sau khi worker đã nạp chapter.

### Pause, Stop và recovery

- Pause không nhận request mới, chờ request hiện tại hoàn tất và checkpoint rồi chuyển Paused.
- Dừng nhanh hủy cancellation token của request hiện tại, giữ các checkpoint trước đó và để job ở trạng thái có thể Resume.
- Đóng ứng dụng dùng Dừng nhanh cho Project Queue rồi chỉ chờ atomic write bắt buộc.
- Mở lại không tự gửi request. Người dùng thấy hành động “Tiếp tục dịch”; segment đã checkpoint không được dịch lại.
- Kết quả Remote chưa xác định vẫn cần kiểm tra/xác nhận trước Retry.

### Ghi tiến độ

- Claim và trạng thái UI ngắn hạn chỉ cập nhật trong bộ nhớ.
- Job bền vững được checkpoint theo nhịp thích ứng, tối đa khoảng 20 checkpoint tiến độ cho một lượt chạy, cộng các trạng thái Start/Pause/Stop/Finish và lỗi.
- Chapter vẫn được atomic write sau mỗi kết quả hoàn tất.
- Manifest summary được cập nhật trong session và flush cùng checkpoint tiến độ/final state. Sau crash, chapter là nguồn dữ liệu có thẩm quyền và manifest được đối chiếu khi mở lại.
- Worker preflight không ghi lại toàn file job. Nếu ranh giới thật sự đổi, transaction chapter/manifest Phase 1 vẫn được dùng; nếu chỉ đổi options mà ranh giới giữ nguyên, chapter được atomic write.

## Định lượng workflow

| Tình huống | Trước Phase 3 | Sau Phase 3 |
|---|---:|---:|
| Sửa Glossary khi Queue chạy rồi tiếp tục | Pause/Replan/Confirm/Start, 4 thao tác | 0 thao tác; áp dụng từ chapter tiếp theo |
| Queue Interrupted do resource/model hợp lệ trong test | 1 | 0 |
| UserDraft trước khi worker nhận gây NeedsReview trong test | 1 | 0 |
| Xung đột SourceText/ranh giới | Bị chặn | Vẫn bị chặn |

## Benchmark tổng hợp

Máy đo: Windows, .NET 10 Release, fake provider trả kết quả ngay; số liệu đo storage/coordinator, không đại diện tốc độ inference thật.

### 250 chapter

| Worker | Thời gian | Throughput | Resume → request đầu | Nhận resource revision mới | Job writes | Bytes written |
|---|---:|---:|---:|---:|---:|---:|
| 1 | 5.787 s | 43.202 chapter/s | 94.191 ms | 16.280 ms | 23 | 15.05 MB |
| 2 | 4.649 s | 53.778 chapter/s | 99.738 ms | 23.782 ms | 24 | 15.41 MB |

Trong workload giả bị giới hạn bởi I/O, hai worker tăng throughput khoảng 24,5%. Không suy rộng con số này thành tốc độ Ollama thực tế.

### 1.000 chapter, hai worker

- Hoàn thành: 1.000/1.000; ChapterId duy nhất: 1.000.
- Phân phối: Local 500, Remote 500; không double claim.
- Thời gian Queue: 47.793 s; throughput 20.924 chapter/s.
- Resume đến request đầu: 236.913 ms.
- Resource revision mới được quan sát: 80.072 ms.
- Job revision writes: 25.
- I/O: 5.095 reads, 4.045 writes, 62.63 MB written.
- Project cuối: 19.53 MB; transaction terminal: 0 file / 0 byte.
- Dừng fake request đang treo: 14.335 ms.

So với vòng benchmark 250 chapter ban đầu, job writes giảm từ 1.258 xuống 24 và dữ liệu ghi giảm từ khoảng 1,095 GB xuống 15,41 MB. Setup tạo Project không nằm trong thời gian Queue.

## Kiểm thử trọng tâm

- Đổi Glossary sau 3/20 chapter; phần còn lại dùng resource mới.
- Đổi model khi Running và khi Paused/Resume.
- UserDraft sau enqueue, gồm chapter nhiều segment.
- Source edit sau enqueue vẫn NeedsReview và không gửi request.
- Hai worker đổi Glossary khi cả hai đang Running.
- Remote offline để Local hoàn tất phần Pending.
- Đổi từ Parallel sang một worker trên cùng JobId.
- SSH identity thay đổi vẫn bị chặn.
- Stop request treo, Pause, Cancel cũ và crash/reopen.
- Crash khi manifest/job progress chưa flush; không dịch lại segment hoàn tất.
- Phase 1 transaction cleanup, Phase 2 fast checkpoint và fault injection.

## Giới hạn còn lại

- Đổi worker/GPU nóng khi Running không thay client của request đang chạy. Workflow production áp dụng thay đổi worker sau Pause/Resume.
- Pause vẫn chờ request hiện tại hoàn tất theo chủ đích; Dừng nhanh dùng cancellation phía client.
- Với Remote, cancellation HTTP không chứng minh inference trên server hoặc việc tính phí đã dừng.
- SourceText/ranh giới thay đổi, SSH identity không khớp và kết quả Remote không xác định vẫn cần người dùng xử lý.
- Nút xử lý lại chỉ còn dành cho chapter có xung đột nguồn thật; workflow cấu hình thông thường không dùng nút này.
