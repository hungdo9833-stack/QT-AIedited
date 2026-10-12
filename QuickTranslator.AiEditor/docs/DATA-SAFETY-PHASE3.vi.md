# Data Safety Phase 3 — 12/10/2026

Phạm vi F07 → F08 → F09, giữ F01–F06/F12. Baseline branch `codex/m0-data-safety-phase1`, HEAD local/GitHub `aebccdd5c0c907257ba7f22b9f6b428f7e60e111`, working tree sạch. Branch mới `codex/data-safety-phase3`. `phase3-baseline`: 242 Core + 30 WPF = **272 pass**, Release 0 lỗi/0 cảnh báo, locked restore đạt. Chỉ dữ liệu tổng hợp trong temp, không Project thật/Data từ điển.

## F07 — Project transaction và manifest

Nguyên nhân xác minh: main được move trước backup ngoài try; lỗi move backup/collision để main ở đường khác. Rollback chỉ trả một vài field, không trả UpdatedAt/counters. Add chưa xét orphan backup-only. `phase3-f07-red`: **8 fail / 0 pass** trước sửa.

Unit of Work mới ghi `History/Transactions/<id>/journal.json` schema 1 độc lập, có checksum, ProjectId, before/after manifest và bản sao/hash từng đường dẫn tác động. Không tăng schema manifest/chapter. Save journal Pending + bản sao được flush trước thay đổi live. Các bước file riêng không được coi là transaction nguyên tử. OperationGate thuộc session điều phối manifest/chapter writes; candidate manifest chỉ trở thành bản sống khi journal Committed ghi thành công. Rollback đối chiếu hash, trả cả bytes main/backup và toàn bộ manifest; dữ liệu rút khỏi live được chuyển vào retained trong transaction, không xóa unknown orphan. Rollback thất bại giữ RecoveryRequired, lỗi tổng hợp và bản sao; không âm thầm bỏ qua.

Trạng thái: Pending → Committed hoặc RollingBack → RolledBack; lỗi rollback → RecoveryRequired. Open chỉ inspect báo cáo, không tự sửa file. Journal chưa kết thúc chặn SaveManifest/RegisteredChapterStore. API InspectRecoveryAsync và RecoverTransactionAsync yêu cầu quyết định RollBack hoặc ConfirmCommitted. Confirm chỉ khi mọi file khớp after; rollback chỉ khi live còn khớp before/after. Journal/bản sao/checksum/ProjectId lỗi không được tự dùng. Orphan preview gồm primary và backup-only trong Chapters/History/Deleted; không tự xóa hoặc tự nhận ownership.

`phase3-f07-checkpoint`: **285 Core + 30 WPF = 315 pass**, Release 0 warning/error. 43 tests F07: 8 lỗi cũ; 27 điểm fault (add/delete/restore × đầu/giữa/cuối × IOException/quyền/hủy), 6 simulated process-loss rồi reopen/rollback, 1 xác nhận commit đã đủ file, 1 orphan preview giữ bytes. Không sửa test Phase 1/2. Lần green đầu có 3 fail do UnauthorizedAccess không nằm trong IOException trên Windows; wrapper giữ InnerException và báo nguyên nhân quyền/khóa rõ, không hạ test.

Giới hạn ở milestone F07: recovery preview/API Core đã có; tích hợp cứu hộ/thông báo nguồn và thao tác recovery WPF thuộc milestone F08 tiếp theo. Không mô phỏng mất điện thật. Journal snapshot giữ bytes lịch sử và cần dung lượng; chưa benchmark truyện hàng nghìn chương. Manifest/property pending UI ngoài thao tác chapter vẫn theo hành vi Phase 2; sẽ phân biệt authoritative/pending khi tích hợp recovery nếu cần, không làm mất editor inputs.

## F08 — Unified Recovery

Nguyên nhân: policy primary/backup không đồng đều; File.Exists khiến missing primary có backup bị hiểu thành defaults; ghi sau fallback đưa primary hỏng vào backup tốt. JSON null ở profile gây NullReference thay vì phân loại content lỗi; backup hỏng khi primary tốt bị mất bằng chứng lúc rotation. Reader initialization đánh dấu ready sau lỗi; mở project từ backup tự save trước xác nhận.

`phase3-f08-red`: **27 fail / 5 pass** trong 32 tests ban đầu. `phase3-f08-ui-red`: **3 fail / 0 pass** (Reader defaults và từ chối recovery không giữ context). `phase3-f08-content-red`: **2 fail / 1 pass** (profile null, mất evidence backup lỗi).

RecoveryFiles dùng chung cho manifest/chapter/character/resources/settings/glossary/context/reader/presets/library/traditional preferences. RecoveryInfo ghi nguồn Primary/Backup/Missing, path, điều kiện file, lỗi primary và hash snapshot. Missing cả hai chỉ cho defaults ở store optional; file bắt buộc thiếu báo FileNotFound. Content không hợp lệ chỉ dùng backup đã decode/validate được; cả hai hỏng chặn đọc và save defaults. Access/sharing/IOException không coi là missing/corruption và không tự fallback để ghi đè. Schema/checksum/ProjectId được xác minh theo format hiện có; v1 chapter checksum và schema 1–3 vẫn đọc được. Raw legacy settings/UTF-8 context vẫn giữ format cũ, không giả định có checksum/ProjectId chưa được format đó ghi; canonical resource bundle có checksum và ProjectId. UTF-8 context đọc strict, kiểm tra giới hạn/nul. Schema/required fields của raw JSON được kiểm tra.

Write xác minh dữ liệu đề xuất và trạng thái primary/backup trước commit. Khi recovery thay primary hoặc rotation backup lỗi, xuất ZIP chứa raw bytes và manifest SHA256, xác minh lại archive; không ghi đè rescue hiện có. Backup tốt được giữ khi primary hỏng/thiếu. RestoreBackupAsync kiểm tra snapshot hash preview, từ chối backup đổi sau preview. Core Save là yêu cầu ghi rõ của caller; WPF thêm bước xuất cứu hộ rồi xác nhận trước thay primary. Reader lỗi load không ready/writable, timer không lưu defaults. Library chưa đọc/xác nhận không ghi shortcut mặc định. Project/chapter context chỉ commit sau khi target/resource hợp lệ và quyết định phục hồi đã xong. Có dialog transaction/orphan preview và xuất báo cáo/ZIP; orphan không tự nhận/xóa.

`phase3-f08-checkpoint`: **359 Core + 33 WPF = 392 pass**, Release 0 warning/error. 74 Core tests F08: matrix 12 stores × 5 tình huống; 9 lỗi ở đầu/giữa/cuối recovery write (IO/quyền/hủy), rescue export lỗi, preview bị đổi, 2 content null/schema, 1 giữ backup hỏng trong rescue. 3 WPF tests mới. File permissions được giả lập UnauthorizedAccess và sharing lock Windows; không sửa ACL/Project thật. ZIP rescue và fixtures nằm temp.

Một setup test F04 trước đây dùng API SaveTranslationAsync ghi options không hợp lệ. API mới validate trước write nên setup đổi sang inject raw JSON trực tiếp vào thư mục test; assertion giữ service/context cũ giữ nguyên. Không hạ expectation hay bỏ test. Lần tích hợp UI đầu có 2 lỗi compile setter internal; đã sửa quyền sở hữu report tại Repository thay vì mở setter cho UI. SDK/runtime/PATH không đổi.

Chỉ lớp preferences persistence/load của Traditional được điều chỉnh để báo/xác nhận recovery, không dịch/chuyển/mở Data từ điển thật hoặc sửa engine. Reader composer/render không đổi. File tạm cleanup lỗi được ghi Trace rõ; không dùng catch rỗng mới. Legacy importer cleanup được xử lý ở F09 tiếp theo.

Giới hạn: raw legacy JSON/text có validation nhưng không có checksum trước đó; chỉ canonical envelope chứng minh integrity/ownership bằng checksum/ID. Các primary được phục hồi theo từng file, rescue giữ toàn bộ trạng thái trước; lỗi giữa nhiều file phải báo chưa hoàn tất và thử lại, không tuyên bố transaction toàn settings. Chưa nghiệm thu trực quan/DPI bằng người dùng; tests WPF chạy dispatcher với data tổng hợp. Disk-full/mất điện thật chưa thử.

## F09 — Import legacy an toàn

Nguyên nhân đã xác minh: importer cũ chỉ ghi nhận ID đã tạo sau khi đọc lại/xác minh; lỗi ở lần đọc đó bỏ sót chapter đã được AddChapter commit. Rollback từng chương bằng soft-delete còn để entry/file đích, không trả trạng thái ban đầu. VerifyEquivalent chỉ so vài field và số lượng attempts/approvals nên chấp nhận nội dung lịch sử bị đổi. `phase3-f09-red-confirmed`: **7 fail / 0 pass** trước sửa (lỗi đọc sau tạo và 6 thay đổi source/draft/attempt/approval/history/status vẫn giữ số lượng).

Importer mới preflight toàn bộ selection: ID/số chương/đường dẫn nguồn trùng, ID đã tồn tại kể cả xóa mềm, số active trùng, primary hoặc backup đích đã có. Đọc raw primary/backup nguồn một lần thành snapshot immutable, decode đúng bytes này, xác minh checksum/format trước khi dùng; không ghi lại hoặc di chuyển nguồn. Backup-only cũng được liệt kê và import. ZIP nguồn tạo bằng CreateNew, entry có chỉ số để hai basename giống nhau không va chạm; manifest ZIP ghi đường nguồn/hash/length và archive được đọc kiểm tra lại trước ghi target.

Toàn bộ đợt import dùng một transaction journal chung, ghi các đường tạo và bản sao trước khi publish file. Published marker được cập nhật ngay sau file có hiệu lực, trước khi xác minh nội dung; lỗi không thể bỏ sót output vừa tạo. Manifest candidate và expected chapter được chuẩn bị trên clone. Sau ghi, đọc trực tiếp từng primary đích (không fallback để che primary lỗi), so toàn bộ JSON typed: nguyên văn, segment/ID/order, drafts/revisions, AI attempts/prompt/metrics, approvals, draft/normalization/batch/segmentation history, queue, status và snapshots. Sau đó kiểm tra hash tất cả file kế hoạch rồi mới ghi Committed.

Metadata chuyển có chủ đích là ProjectId, ChapterNumber và CharacterProject.Id snapshot sang target. Journal lưu original và target identity cùng đường ZIP để truy vết; các tài nguyên canonical hiện hành của Project đích không lấy từ snapshot legacy. ChineseTitle, nội dung và history không bị trim/chuẩn hóa trong import. Không dùng số lượng lịch sử làm bằng chứng tương đương.

Lỗi/cancellation rollback toàn đợt, trả bytes manifest/main/backup và bộ nhớ. File mới bị hỏng ở bước verify được chuyển vào retained-changed trong journal để giữ evidence và giải phóng đường retry; file tồn tại trước transaction có xung đột không bị ghi đè. Nếu quyền/khóa khiến rollback thất bại, report NeedsRecovery và giữ journal/ZIP, chặn ghi tiếp; sau unlock có thể đối chiếu rollback và retry. Báo cáo tách Imported, RolledBack, NotStarted, NeedsRecovery, kèm ID/số/path nguồn thực tế/path đích/transaction/error. Lỗi nạp editor sau khi import đã commit được thông báo riêng, không tuyên bố import đã rollback.

Kiểm tra fixture v1 phát hiện metadata mặc định mới được tạo theo thời điểm đọc, làm cùng bytes legacy sinh hai snapshot khác nhau. Decoder nay chỉ khởi tạo các preset/queue/character field **vắng mặt** bằng metadata ổn định từ chapter; migration chỉ bổ sung CharacterProject null của schema cũ, không xóa snapshot đã lưu. Checksum v1 vẫn xác minh trước migration. Đã thêm 2 tests schema 1/2 bảo toàn toàn bộ character snapshot. Hai lần suite trung gian thất bại do timestamp v1 và schema 2 null; đã sửa nguyên nhân và chạy lại xanh, không bỏ fixture hoặc giảm so sánh nội dung.

Rà soát recovery cuối phát hiện lỗi mới: rollback trả manifest chính nhưng backup bị khóa, phương thức ném lỗi trước refresh session. `phase3-recovery-refresh-red`: **1 fail / 0 pass**. Đã refresh manifest/report bằng token không hủy trước trả lỗi; nếu chính bước reread lỗi, giữ snapshot và report RecoveryRequired chặn write, tổng hợp nguyên nhân thay vì báo rollback hoàn tất. Test khóa manifest backup, reopen, so bộ nhớ/ổ đĩa, unlock/retry đạt.

`phase3-f09-checkpoint`: **392 Core + 33 WPF = 425 pass**, Release 0 warning/error. 30 tests LegacyImportSafety mới gồm 7 lỗi gốc, 9 fault đầu/giữa/cuối × IO/quyền/hủy, 3 lỗi xác minh tại chương đầu/giữa/cuối, rich histories/resources, 3 duplicate preflight, 2 simulated crash/reopen, backup-only, output hỏng, rollback bị khóa/recovery/retry, basename trùng và fixture v1. Cộng thêm 2 migration tests và 1 refresh recovery test.

Đã chạy thêm **cửa sổ WPF thật trên Windows với dữ liệu tổng hợp**: mở modal recovery, click rollback pending, xác nhận rescue ZIP đã xuất, đóng dialog, reopen không còn pending. Test `phase3-wpf-recovery` đạt; không dùng Project/Reader/library thật. Đây là kiểm thử tự động STA/dispatcher và controls thật, chưa thay nghiệm thu trực quan bằng người dùng.

## Kết quả cuối và checkpoint

| Mốc | Core pass | WPF pass | Tổng | Release |
|---|---:|---:|---:|---|
| Baseline Phase 2 | 242 | 30 | 272 | 0 lỗi / 0 cảnh báo |
| F07 | 285 | 30 | 315 | 0 lỗi / 0 cảnh báo |
| F08 | 359 | 33 | 392 | 0 lỗi / 0 cảnh báo |
| F09 checkpoint | 392 | 33 | 425 | 0 lỗi / 0 cảnh báo |
| Phase 3 cuối | 392 | 34 | **426** | **0 lỗi / 0 cảnh báo** |

`phase3-final`: locked restore, toàn bộ regression suite **426 pass, 0 fail, 0 skipped**. Build Release bổ sung `--no-incremental` cũng 0 warning/error. Tăng 154 tests so baseline. Tests F01–F06/F12 vẫn chạy đầy đủ; chỉ sửa setup inject JSON không hợp lệ ở một test F04 như đã giải thích, không bỏ hoặc nới assertion.

SDK riêng `.tools/dotnet` 10.0.401, runtime Windows .NET/Desktop 10.0.12; không sửa PATH/Registry/runtime hệ thống. TRX/restore/build logs nằm trong thư mục ignored `QuickTranslator.AiEditor/artifacts/data-safety/<stage>`, không commit. Có thể chạy lại từ workspace:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File QuickTranslator.AiEditor/scripts/Validate-DataSafety.ps1 -Stage phase3-acceptance
```

Checkpoint F07: `ec2531dbe3b53a3ecbd5784f16825a5043065b57`.
Checkpoint F08: `bad54e13112757dfb11fea2d196a85fe787be2e0`.
Checkpoint F09: commit có subject `fix: transact legacy imports and verify complete history`; SHA được ghi trong báo cáo bàn giao/chat và tra bằng `git log` (không ghi SHA của chính commit vào nội dung commit đó).
Branch bàn giao: [codex/data-safety-phase3](https://github.com/hungdo9833-stack/QuickTranslator/tree/codex/data-safety-phase3). Không merge/force push. Branch Phase 2 và commit bàn giao `965cf6832f6c9831eb4a8da5527fd3f2aa5199c5` được bảo toàn.

## Nghiệm thu thủ công và rủi ro còn lại

- Dùng bản sao Project tổng hợp, kiểm tra recovery dialog ở DPI/font thực tế, nguồn backup hiển thị, chọn Không giữ nguyên context và files; kiểm tra ZIP trước xác nhận. Import nhiều chương, hủy/lỗi/quyền/khóa rồi retry; đối chiếu report từng chapter, approvals/drafts/history.
- Mở Project thật chỉ sau khi có backup riêng và nghiệm thu. Không có thử nghiệm nào trong lượt này đọc/sửa/di chuyển Project thật hoặc Data từ điển. Không viết lại Reader, Ollama, TranslateGemma, TraditionalTranslator; chỉ persistence/preferences và recovery có liên quan.
- Chưa thử disk-full, mất điện thật, network share/đồng bộ cloud hoặc nhiều tiến trình đồng thời. Fault injection IO/quyền/hủy và FileShare locks Windows đã chạy; simulated process-loss là bỏ qua rollback in-process rồi tạo Repository/session mới, không phải kill/mất điện thật. Ollama/AI live không được gọi trong suite; F04 dùng fake request để kiểm tra ownership.
- Không có cam kết atomic toàn Project settings hoặc nhiều process. OperationGate bảo vệ writers trong session; không thay filesystem thành database. Nếu mất quyền đọc sau rollback, trạng thái chưa xác minh được báo rõ và chặn writes; cần xử lý quyền/khóa rồi reopen.
- Journal/copies/rescue tăng dung lượng, chưa benchmark truyện hàng nghìn chương; open inspect lịch sử và hashes có chi phí. Không tự prune transaction/history/checkpoints. Không tự xóa orphan hoặc tự áp dụng quyết định recovery. Journal/copies lỗi hoặc external edit không có bằng chứng before/after yêu cầu xuất cứu hộ/đối chiếu thủ công.
- Raw legacy JSON/context không có checksum/ProjectId gốc; validation không chứng minh dữ liệu chưa từng bị sửa. Envelope mới không được thêm vào các format cũ trong lượt này. Chỉ journal là metadata mới độc lập; field bổ sung của journal có default để đọc journal F07 trước đó.
- Manifest properties chưa lưu vẫn giữ pending inputs theo Phase 2; transaction chương từ chối chạy nếu manifest pending khác ổ đĩa. F07 bảo đảm memory/disk cho Add/Delete/Restore và recovery sau lỗi; không tự bỏ các inputs đang chờ retry của Project Properties.
- Không thiết kế UI mới, Book Builder hoặc Queue nhiều chương. Dừng sau push Phase 3 để người dùng nghiệm thu.

## File thay đổi

Danh sách dưới đây so với HEAD Phase 2; chỉ source, tests và tài liệu. Không Data/Project/artifacts/credentials.

- [QuickTranslator.AiEditor/docs/DATA-SAFETY-PHASE3.vi.md](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/docs/DATA-SAFETY-PHASE3.vi.md)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/AssemblyInfo.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/AssemblyInfo.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Models/CharacterPronouns.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Models/CharacterPronouns.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Persistence/AtomicLocalFile.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Persistence/AtomicLocalFile.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Persistence/ChapterFileStore.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Persistence/ChapterFileStore.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Persistence/CharacterProjectStore.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Persistence/CharacterProjectStore.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Persistence/DocumentJson.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Persistence/DocumentJson.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Persistence/PromptPresetStore.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Persistence/PromptPresetStore.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Persistence/ReaderPreferencesStore.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Persistence/ReaderPreferencesStore.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Persistence/RecoveryFiles.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Persistence/RecoveryFiles.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Persistence/TraditionalEditorPreferencesStore.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Persistence/TraditionalEditorPreferencesStore.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Projects/LegacyProjectImporter.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Projects/LegacyProjectImporter.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Projects/ProjectBackupService.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Projects/ProjectBackupService.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Projects/ProjectLibraryStore.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Projects/ProjectLibraryStore.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Projects/ProjectModels.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Projects/ProjectModels.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Projects/ProjectRepository.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Projects/ProjectRepository.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Projects/ProjectRepository.Transactions.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Projects/ProjectRepository.Transactions.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Projects/ProjectResourceStore.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Projects/ProjectResourceStore.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Projects/ProjectSettingsStore.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Projects/ProjectSettingsStore.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Projects/ProjectTransactionJournal.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Projects/ProjectTransactionJournal.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Projects/RegisteredChapterStore.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Core/Projects/RegisteredChapterStore.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Wpf/MainWindow.Projects.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Wpf/MainWindow.Projects.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Wpf/MainWindow.Reader.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Wpf/MainWindow.Reader.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Wpf/MainWindow.Recovery.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Wpf/MainWindow.Recovery.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Wpf/MainWindow.Traditional.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Wpf/MainWindow.Traditional.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Wpf/MainWindow.WorkspaceLifetime.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Wpf/MainWindow.WorkspaceLifetime.cs)
- [QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Wpf/MainWindow.xaml.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/src/QuickTranslator.AiEditor.Wpf/MainWindow.xaml.cs)
- [QuickTranslator.AiEditor/tests/QuickTranslator.AiEditor.Tests/ChapterUpgradeTests.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/tests/QuickTranslator.AiEditor.Tests/ChapterUpgradeTests.cs)
- [QuickTranslator.AiEditor/tests/QuickTranslator.AiEditor.Tests/LegacyImportSafetyTests.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/tests/QuickTranslator.AiEditor.Tests/LegacyImportSafetyTests.cs)
- [QuickTranslator.AiEditor/tests/QuickTranslator.AiEditor.Tests/ProjectTransactionTests.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/tests/QuickTranslator.AiEditor.Tests/ProjectTransactionTests.cs)
- [QuickTranslator.AiEditor/tests/QuickTranslator.AiEditor.Tests/UnifiedRecoveryTests.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/tests/QuickTranslator.AiEditor.Tests/UnifiedRecoveryTests.cs)
- [QuickTranslator.AiEditor/tests/QuickTranslator.AiEditor.Wpf.Tests/RecoveryUiTests.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/tests/QuickTranslator.AiEditor.Wpf.Tests/RecoveryUiTests.cs)
- [QuickTranslator.AiEditor/tests/QuickTranslator.AiEditor.Wpf.Tests/SafeSwitchingTests.cs](C:/Users/Hune21/Documents/Codex/QuickTranslator-Personal/QuickTranslator.AiEditor/tests/QuickTranslator.AiEditor.Wpf.Tests/SafeSwitchingTests.cs)
