# Data Safety Phase 3 — 12/10/2026

Phạm vi F07 → F08 → F09, giữ F01–F06/F12. Baseline branch `codex/m0-data-safety-phase1`, HEAD local/GitHub `aebccdd5c0c907257ba7f22b9f6b428f7e60e111`, working tree sạch. Branch mới `codex/data-safety-phase3`. `phase3-baseline`: 242 Core + 30 WPF = **272 pass**, Release 0 lỗi/0 cảnh báo, locked restore đạt. Chỉ dữ liệu tổng hợp trong temp, không Project thật/Data từ điển.

## F07 — Project transaction và manifest

Nguyên nhân xác minh: main được move trước backup ngoài try; lỗi move backup/collision để main ở đường khác. Rollback chỉ trả một vài field, không trả UpdatedAt/counters. Add chưa xét orphan backup-only. `phase3-f07-red`: **8 fail / 0 pass** trước sửa.

Unit of Work mới ghi `History/Transactions/<id>/journal.json` schema 1 độc lập, có checksum, ProjectId, before/after manifest và bản sao/hash từng đường dẫn tác động. Không tăng schema manifest/chapter. Save journal Pending + bản sao được flush trước thay đổi live. Các bước file riêng không được coi là transaction nguyên tử. OperationGate thuộc session điều phối manifest/chapter writes; candidate manifest chỉ trở thành bản sống khi journal Committed ghi thành công. Rollback đối chiếu hash, trả cả bytes main/backup và toàn bộ manifest; dữ liệu rút khỏi live được chuyển vào retained trong transaction, không xóa unknown orphan. Rollback thất bại giữ RecoveryRequired, lỗi tổng hợp và bản sao; không âm thầm bỏ qua.

Trạng thái: Pending → Committed hoặc RollingBack → RolledBack; lỗi rollback → RecoveryRequired. Open chỉ inspect báo cáo, không tự sửa file. Journal chưa kết thúc chặn SaveManifest/RegisteredChapterStore. API InspectRecoveryAsync và RecoverTransactionAsync yêu cầu quyết định RollBack hoặc ConfirmCommitted. Confirm chỉ khi mọi file khớp after; rollback chỉ khi live còn khớp before/after. Journal/bản sao/checksum/ProjectId lỗi không được tự dùng. Orphan preview gồm primary và backup-only trong Chapters/History/Deleted; không tự xóa hoặc tự nhận ownership.

`phase3-f07-checkpoint`: **285 Core + 30 WPF = 315 pass**, Release 0 warning/error. 43 tests F07: 8 lỗi cũ; 27 điểm fault (add/delete/restore × đầu/giữa/cuối × IOException/quyền/hủy), 6 simulated process-loss rồi reopen/rollback, 1 xác nhận commit đã đủ file, 1 orphan preview giữ bytes. Không sửa test Phase 1/2. Lần green đầu có 3 fail do UnauthorizedAccess không nằm trong IOException trên Windows; wrapper giữ InnerException và báo nguyên nhân quyền/khóa rõ, không hạ test.

Giới hạn ở milestone F07: recovery preview/API Core đã có; tích hợp cứu hộ/thông báo nguồn và thao tác recovery WPF thuộc milestone F08 tiếp theo. Không mô phỏng mất điện thật. Journal snapshot giữ bytes lịch sử và cần dung lượng; chưa benchmark truyện hàng nghìn chương. Manifest/property pending UI ngoài thao tác chapter vẫn theo hành vi Phase 2; sẽ phân biệt authoritative/pending khi tích hợp recovery nếu cần, không làm mất editor inputs.
