# M4 — Multi-Chapter Translation Queue

Ngày nghiệm thu kỹ thuật: 13/10/2026

Branch: `codex/m4-multi-chapter-translation-queue`

Điểm nguồn M3: `860d28b81ec166383ddbfb2092448e6c209c66c6`

## Phạm vi triển khai

M4 cho phép chọn nhiều chapter trong Chapter Manager, xem Queue Plan rồi dịch tuần tự bằng `TranslationService` và `TranslationQueue` cấp chapter hiện có. M4 không viết lại Ollama/TranslateGemma, TraditionalTranslator hoặc VietPhrase, không đổi schema `.qtproj` và không triển khai phần giao diện M5.

## Kiến trúc và trạng thái bền vững

- `ProjectTranslationCoordinator` chỉ giữ một chapter trong bộ nhớ và chỉ gửi một request tại một thời điểm.
- Queue Plan chốt `ChapterId`, thứ tự, source/document/protected fingerprint, model options, prompt preset và Project resource revision trước khi chạy.
- `ProjectTranslationJobStore` lưu job ở `Queue/project-translation-job.json`, có SHA-256, file backup và recovery write của Data Safety Phase 3. Đây là dữ liệu bổ trợ; Project Manifest vẫn là nguồn có thẩm quyền cho chapter.
- Checkpoint chapter tiếp tục đi qua `TranslationService`, `RegisteredChapterStore` và transaction repository. Mỗi kết quả AI chỉ commit nếu storage revision vẫn đúng.
- Job ở trạng thái Running/PauseRequested/Cancelling khi ứng dụng dừng sẽ được mở lại thành Interrupted. Ứng dụng không tự gửi request; người dùng phải bấm Resume.
- Một process chỉ có một coordinator đang ghi cho mỗi Project. Job store dùng optimistic revision để từ chối trạng thái cũ hoặc job chưa kết thúc bị tạo trùng.

## Bảo vệ dữ liệu

- Segment có `UserDraft` hoặc Approval không bị queue ghi đè; chapter không còn segment hợp lệ được skip an toàn.
- Source, document, protected data, model, prompt hoặc resources đổi sau enqueue sẽ làm item thành NeedsReview trước khi gửi request.
- Nếu người dùng hoặc tiến trình khác lưu chapter trong lúc AI đang chạy, storage revision guard từ chối kết quả cũ.
- Pause và Cancel đợi request đang chạy kết thúc, giữ mọi checkpoint đã commit rồi dừng trước item tiếp theo.
- Lỗi provider/timeout được ghi trên chapter và job. Retry chỉ đưa chapter Failed trở lại Pending; chapter NeedsReview cần người dùng kiểm tra và tạo plan mới.
- Chuyển chapter/Project hoặc đóng ứng dụng sẽ Pause queue và chờ checkpoint trước khi đổi context.

## WPF

- Nút `Đưa vào hàng đợi dịch` hoạt động với multi-selection trong Chapter Manager.
- Queue Plan hiển thị danh sách và snapshot model/prompt/resources trước khi Start.
- Queue Progress hiển thị trạng thái thật theo chapter/segment, số thành công/lỗi/bỏ qua và lỗi cụ thể.
- Có Start, Pause, Resume, Cancel pending và Retry failed. Pause/Cancel vẫn dùng được trong lúc request đang chạy.
- Giao diện dùng style Dark Navy hiện có; MainWindow chưa được redesign.

## Kiểm thử tự động

- Dịch tuần tự 5/50/100 chapter; kiểm tra độ song song tối đa bằng 1 và reopen đọc đúng checkpoint.
- Pause/Resume, Cancel pending, interrupted restart và retry sau lỗi provider tổng hợp.
- Bảo vệ Approval/UserDraft, edit sau enqueue, stale AI result và duplicate coordinator.
- WPF Queue Plan không gửi request trước Start; progress thật; queue đang chạy khóa mở chapter; context switch Pause an toàn; Pause/Cancel khả dụng trong request.
- Toàn bộ regression Data Safety Phase 1–3 và M1–M3 được chạy lại trong Release.

## Giới hạn cần nghiệm thu thủ công

- Automated tests dùng fake provider, không yêu cầu Ollama. Cần thử Ollama/TranslateGemma thật với timeout, mất kết nối và thiếu VRAM trên máy Windows có model.
- M4 khóa coordinator trong cùng process và dùng optimistic file revision. Chạy hai bản ứng dụng trên cùng Project hoặc đặt Project trên network share chưa được hỗ trợ như workflow đồng thời.
- Cancel là cancel các item pending. Request Ollama hiện tại được để kết thúc để checkpoint nhất quán; đây không phải hard-kill model.
- Queue lưu một job hiện hành cho Project. Lịch sử dịch vẫn nằm trong chapter; M4 chưa thêm màn hình lịch sử nhiều job.
- Cảm giác vận hành với 100 chapter thật, sleep/hibernate, DPI/IME và đóng cưỡng bức tiến trình cần kiểm tra thủ công.

Chạy toàn bộ nghiệm thu và tạo package bằng `scripts/Validate-M4.ps1`. Quy trình thử thực tế nằm trong `docs/M4-MANUAL-TEST.vi.md` và được chép vào ZIP.
