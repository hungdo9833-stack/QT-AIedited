# P0 — Chương mới nhập bị Queue bỏ qua

## Phạm vi và baseline

- Baseline: `codex/user-first-ui-phase4` tại `7d9067b673629f9c0eae4d703a705acf2f354fda`.
- Branch sửa lỗi: `codex/p0-imported-chapters-queue-skip`.
- Chỉ dùng Project tổng hợp trong thư mục tạm của test. Không đọc hoặc ghi Project cá nhân.
- Không đổi Project/Chapter schema, prompt, model, token budget, `think=false` hoặc quy tắc bảo vệ draft/approval.

## Lỗi dispatch đã xác minh

`WriteFastChapterCheckpointAsync(..., deferManifestCheckpoint: true)` lưu chapter trước và cập nhật summary trong `session.Manifest`, nhưng cố ý chưa ghi manifest xuống đĩa để giảm I/O. Worker của chapter kế tiếp sau đó chạy `PreflightWorkerChapterAsync`. Khi preflight cần đổi ranh giới segment, transaction yêu cầu manifest trong RAM phải giống manifest trên đĩa. Summary trì hoãn của chapter trước làm guard này thất bại với:

> Manifest trong bộ nhớ đã khác ổ đĩa; lưu/đối chiếu thay đổi trước khi thao tác chương.

Lỗi xảy ra trước khi gửi request của chapter kế tiếp. Ngoài ra, nhánh preflight không đổi ranh giới đã cập nhật `session.Manifest` sau khi lưu chapter nhưng chưa ghi manifest tương ứng xuống đĩa. Hai đường này tạo cùng một loại sai lệch RAM/đĩa.

Thông báo “Không có segment đủ điều kiện” là một lỗi phân loại độc lập: mọi chapter không có segment eligible đều bị ghi `Skipped`, kể cả trạng thái bất thường. Vì vậy UI làm mất dấu khác biệt giữa chapter đã dịch, chapter được bảo vệ và chapter chưa dịch nhưng mất eligibility.

## Bằng chứng tái hiện trước sửa

Regression dùng pipeline production đã tạo Project gồm hai chapter hoàn tất (398–399), nhập 52 chapter (400–451) qua `SafeBulkImportService`, đóng/mở Project, tạo Queue 54 chapter và chạy worker fake qua worker preflight với cấu hình chia đoạn khác.

Trước sửa, workflow tổng hợp phát hiện:

- Chapter mới đầu tiên có thể hoàn tất.
- Preflight của chapter kế tiếp thất bại vì manifest RAM/đĩa khác nhau.
- Chapter kế tiếp không nhận request vì worker preflight lỗi manifest; trạng thái item là `Failed` trong baseline này.
- Snapshot chẩn đoán cho thấy summary trong RAM đã có segment hoàn tất trong khi manifest đĩa vẫn giữ số hoàn tất cũ.

Sau sửa, toàn bộ segment eligible của 52 chapter mới nhận đúng một request, source giữ nguyên, hai chapter cũ không nhận request và Project mở lại không có orphan hoặc transaction cần quyết định.

### Giới hạn tái hiện trạng thái người dùng

Baseline `7d9067b` không tái hiện chính xác kết quả **52 item `Skipped` cùng thông báo generic** từ một import hoàn toàn sạch. Các snapshot sau import và sau persisted reload đều cho thấy mọi segment mới là `Untranslated`, không attempt/protection và eligible. Baseline tái hiện một dispatch blocker thật ở worker preflight, nhưng blocker đó được phân loại `Failed`, không phải `Skipped`.

Vì không truy cập Project thật, chưa thể chứng minh metadata nào trong bản người dùng đã làm 52 chapter mất eligibility. Những khả năng còn lại là binary chạy thử không cùng commit Phase 4, import được nhận là chapter đã tồn tại thay vì bản mới, metadata attempt/protection tồn tại trong file persisted, hoặc job cũ được recovery. Diagnostic mới giữ các count và reason thực tế để lần chạy sau xác định trường hợp nào xảy ra mà không thu thập nội dung truyện.

## Sửa lỗi

1. Theo dõi rõ khi manifest có summary trì hoãn. Trước worker structural preflight, coordinator chỉ flush khi có checkpoint trì hoãn.
2. Nhánh preflight không đổi ranh giới ghi chapter và manifest thành một trạng thái nhất quán trước khi công bố commit.
3. Thêm diagnostic eligibility chỉ đọc ở các mốc Import, persisted reload, Plan, worker preflight và ngay trước quyết định. Diagnostic chỉ lưu ID, số lượng, QueueMode, kết quả phân loại và loại attempt; không lưu nguyên văn, bản dịch, prompt hoặc credential.
4. Thêm disposition riêng cho `AlreadyTranslated`, `TranslatedThisJob`, `ProtectedContent`, `MissingEligibility`, `Failed`, `NeedsReview` và `Cancelled`.
5. Chapter còn nội dung chưa dịch nhưng không có segment eligible được đánh dấu `NeedsReview/MissingEligibility`; job kết thúc `CompletedWithErrors`, thay vì thành công sạch.
6. Queue UI hiển thị disposition và bản tóm tắt diagnostic trong cột Chi tiết.

## Bảo toàn dữ liệu

- Không thay đổi `TranslationService.IsQueueEligible`; semantics Failed/InvalidOutput vẫn yêu cầu lệnh “Dịch lại lỗi”.
- Không xóa attempt, draft, approval, history hoặc checkpoint.
- Không tự dịch segment có `UserDraft`, `Approval` hoặc `ProtectedByPreviousEdit`.
- Source fingerprint, protected fingerprint, document fingerprint, revision guard, transaction journal và recovery vẫn được dùng.
- Job cũ vẫn đọc được: các trường metadata mới có giá trị mặc định khi không có trong JSON cũ.

## Khôi phục 52 chương đã bị bỏ qua

Không nhập lại TXT và không xóa history:

1. Mở Project bằng bản đã có sửa lỗi và chờ kiểm tra recovery hoàn tất.
2. Vào **Chương**, lọc hoặc tìm các chương 400–451 (hoặc đúng 52 chương có `0/N`).
3. Chọn các chương đó và bấm **Dịch các chương đã chọn** để tạo Queue Plan mới.
4. Kiểm tra cột **Trạng thái/Chi tiết**. Chapter mới hợp lệ phải có eligibility lớn hơn 0; chapter được bảo vệ sẽ ghi rõ lý do.
5. Bắt đầu queue. Không dùng “Dịch lại lỗi” trừ khi diagnostic thật sự ghi Failed/InvalidOutput.
6. Sau vài chapter, Pause rồi đóng/mở Project một lần, Resume và xác nhận số request/segment không tăng trùng.

Nếu màn hình recovery báo transaction cần quyết định, xử lý recovery trước khi tạo queue mới; không chọn rollback/commit nếu chưa kiểm tra preview và rescue data.

## Smoke test Ollama trên Windows

1. Sao chép Project thử hoặc tạo Project tổng hợp; không dùng bản duy nhất của dữ liệu thật.
2. Chọn Ollama Local và model mong muốn, kiểm tra kết nối.
3. Nhập một TXT có ít nhất 3 chương bằng **Nhập & Dịch**.
4. Xác nhận Queue Plan hiển thị eligibility dương cho các chapter mới.
5. Bắt đầu; xác nhận request thực sự tới Ollama và trạng thái chuyển `Đang dịch` → `Đã dịch`.
6. Pause giữa chapter, đóng/mở ứng dụng, Resume và kiểm tra không request lại segment đã checkpoint.
7. Tạo một chapter có draft/approval; xác nhận phần được bảo vệ không bị ghi đè và UI ghi `Được bảo vệ`.
8. Đóng/mở Project; xác nhận bản dịch, source và Queue trạng thái vẫn còn, không xuất hiện orphan/recovery bất thường.

Inference GPU thật không thuộc automated test; checklist này phải được thực hiện trên Windows có Ollama đang chạy.
