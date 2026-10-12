# M2 — Import Preview & Safe Bulk Import

Ngày nghiệm thu kỹ thuật: 12/10/2026

Branch: `codex/m2-import-preview-safe-bulk`

Điểm nguồn M1: `41a86863e7ab02b502780e9dd6eb3daff6085c2d`

## Phạm vi đã triển khai

M2 nối trực tiếp Chapter Detection Engine M1 vào ứng dụng WPF. Người dùng mở Project, chọn TXT, xem/sửa plan và nhập các chapter đã chọn bằng transaction journal Phase 3. Không có detector thứ hai, không đổi schema Project, không sửa Ollama, TranslateGemma, TraditionalTranslator, VietPhrase hoặc Reader và chưa có Translation Queue M3.

## Import Preview

- Đọc TXT theo UTF-8 nghiêm ngặt hoặc lựa chọn rõ GBK/GB18030, giữ raw bytes và hash của M1.
- Hiển thị chapter, số, tên, quyển, confidence và cảnh báo; hỗ trợ tìm kiếm và lọc chapter có vấn đề hoặc vùng unclassified.
- Cho sửa số/tên, include/exclude, split, merge, dời boundary và hoàn tác.
- Mỗi thao tác tạo một plan bất biến mới. `ValidateExactCoverage` tiếp tục bảo đảm mọi vùng ghép lại đúng nguồn ký tự ban đầu.
- Hiển thị ngữ cảnh quanh hai boundary và giữ vùng unclassified trong source rescue.
- Virtualization của `DataGrid` tránh tạo toàn bộ visual cho danh sách hàng nghìn chapter.

## Safe Bulk Import

- Preflight toàn bộ selection trước khi ghi: ID ổn định, số active, deleted ID, orphan path và nội dung của chapter đã nhập từ lần trước.
- Tạo và xác minh ZIP source rescue trước lần ghi Project đầu tiên. ZIP chứa raw source cùng import plan, raw/text hash, ProjectId và plan fingerprint.
- Chia theo giới hạn số chapter và số byte. Mỗi chunk atomic; partial success chỉ xảy ra giữa các chunk đã commit.
- Dùng transaction journal Phase 3 cho chapter files và manifest, đọc lại từng chapter trước commit và giữ journal phục vụ reopen/recovery.
- Resume có tính idempotent: cùng stable ID và cùng nội dung được báo `AlreadyImported`; bất kỳ khác biệt nội dung/metadata nào bị chặn như collision.
- Báo cáo riêng `Imported`, `AlreadyImported`, `RolledBack`, `NotStarted` và `NeedsRecovery`.
- Character Profiles, Glossary, Context, UserDraft, Approval và Edit History hiện có không bị viết lại. Chapter mới nhận snapshot từ canonical Project resources.

## Tương thích dữ liệu

M2 không thay schema manifest hoặc chapter. Project cũ tiếp tục được đọc bằng repository hiện có. Transaction bổ sung dùng metadata journal tương thích ngược của Phase 3; source rescue là file bổ sung trong `Backups`, không phải dữ liệu bắt buộc để mở Project cũ.

Số chapter active trong schema hiện hành vẫn là duy nhất toàn Project. Nếu một bộ truyện bắt đầu lại từ chương 1 theo từng quyển, Preview giữ đúng số nguồn và báo collision; người dùng phải quyết định số Project duy nhất trước khi import. M2 không tự đánh lại số.

## Kiểm thử

- Full regression cuối: 460 Core + 37 WPF = 497 tests đạt, 0 fail, 0 skipped.
- Release build: 0 lỗi, 0 cảnh báo; publish self-contained Windows x64 thành công.
- Import thực 1/50/500/5.000 chapter tổng hợp rồi reopen, kiểm tra manifest, file count, orphan và recovery.
- Preview sửa số/tên, include/exclude, split/merge/move boundary, undo và exact source coverage.
- Collision active/deleted/path, source rescue/hash và resume không trùng.
- Fault injection sau journal, giữa file, trước commit, verify, disk-full giả lập, cancel trước/trong/giữa chunk và process loss rồi reopen/recover/resume.
- Bảo toàn byte của chapter đã duyệt có draft/history cùng Project Glossary.
- WPF integration mở Project, tạo preview, import 50 chapter rồi đóng/mở Project.

Kiểm tra tương tác WPF trên Windows dùng Project/TXT tổng hợp 50 chương. Sau khi sửa một chapter bị trùng từ 13 thành 12, số cảnh báo giảm từ 2 xuống 0 và nút xác nhận được bật.

## Giới hạn nghiệm thu thủ công

- Cần người dùng kiểm tra native file dialog, IME tiếng Trung/Việt, DPI nhiều màn hình và thao tác chuột trên máy sử dụng thực tế.
- Disk-full và process loss được fault-inject; chưa chủ động làm đầy ổ đĩa hoặc kết thúc cưỡng bức ứng dụng trên Project thật.
- Preview edits chỉ tồn tại trong cửa sổ cho tới khi người dùng xác nhận; source rescue được tạo khi bắt đầu import.
- Không cam kết transaction nhiều process hoặc ổ mạng không có semantics replace/flush tương đương NTFS cục bộ.

Chạy nghiệm thu và tạo package bằng `scripts/Validate-M2.ps1`. Hướng dẫn 50 chương nằm tại `docs/M2-50-CHAPTER-MANUAL-TEST.vi.md` và được chép vào ZIP phát hành.
