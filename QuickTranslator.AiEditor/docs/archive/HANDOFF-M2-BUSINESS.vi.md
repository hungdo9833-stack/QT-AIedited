# QT Studio — Bàn giao sang Codex Business để triển khai M2

Ngày chuẩn bị: 12/10/2026

Tài liệu này là điểm vào tự chứa cho một phiên Codex mới. Lịch sử chat cũ, thư mục `artifacts` và SDK cục bộ không phải là điều kiện để tiếp tục.

## 1. Điểm nguồn đã xác minh

- Repository: `https://github.com/hungdo9833-stack/QuickTranslator.git`
- Branch nguồn: `codex/m1-chapter-detection`
- Commit implementation M1: `cfaf33842617d124ed1ddb058ed8685959fb566c`
- Điểm nền Data Safety Phase 3: `b511687751ea6ca15cb6bd5aece51bd71bc8aa01`
- Branch `codex/m1-chapter-detection` đã được push lên GitHub và commit M1 local/remote đã được đối chiếu trước khi tạo tài liệu này.
- M1 kế thừa đầy đủ Data Safety Phase 1–3. Không merge vào `codex/project-workspace`, không force-push và không sửa Project thật/Data từ điển.

Commit M1 là mốc mã chức năng. Commit mới hơn chỉ chứa tài liệu bàn giao này vẫn là điểm xuất phát thích hợp cho M2.

## 2. Cách mở ở workspace Business

Nếu workspace Business chạy trên cùng máy và thấy thư mục hiện tại, mở trực tiếp:

`C:\Users\Hune21\Documents\Codex\QuickTranslator-Personal`

Sau đó kiểm tra, không reset/checkout khi working tree đang có thay đổi:

```powershell
git status --short --branch
git remote -v
git branch --show-current
git rev-parse HEAD
git log -1 --oneline
```

Kết quả mong đợi là branch `codex/m1-chapter-detection`, remote `origin` trỏ tới repository trên và working tree sạch.

Nếu workspace Business dùng máy/thư mục khác, clone mới:

```powershell
git clone https://github.com/hungdo9833-stack/QuickTranslator.git
Set-Location QuickTranslator
git switch codex/m1-chapter-detection
git status --short --branch
git rev-parse HEAD
```

Nếu clone/fetch cũ không thấy branch:

```powershell
git fetch origin codex/m1-chapter-detection:refs/remotes/origin/codex/m1-chapter-detection
git switch --create codex/m1-chapter-detection --track origin/codex/m1-chapter-detection
```

Không chép `.git`, `bin`, `obj`, `artifacts`, `.tools/nuget` hoặc Project người dùng giữa hai workspace. GitHub branch là nguồn bàn giao.

## 3. Môi trường và baseline bắt buộc

Solution dùng .NET 10. `global.json` cho biết SDK yêu cầu; máy phát triển trước đây dùng SDK 10.0.401 đặt riêng trong `.tools/dotnet`. `.tools` bị ignore và sẽ không đi theo Git.

Phiên mới phải chạy:

```powershell
dotnet --info
```

Nếu không có .NET 10 SDK, cài SDK vào thư mục công cụ riêng trong workspace sau khi người dùng cho phép thay đổi môi trường. Không sửa PATH hệ thống, Registry hoặc Runtime hiện có nếu người dùng chưa yêu cầu.

Chạy nghiệm thu M1 trước khi tạo branch M2:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File QuickTranslator.AiEditor/scripts/Validate-M1.ps1 -Stage business-handoff-baseline
```

Kết quả chuẩn:

- Locked restore thành công.
- Build Release: 0 lỗi, 0 cảnh báo.
- Core: 443 tests đạt.
- WPF: 34 tests đạt.
- Tổng: 477 đạt, 0 fail, 0 skipped.
- Demo tổng hợp: 5.000 chapter, 5 volume, exact round-trip là `true`.

Script tắt NuGet audit cho invocation vì audit endpoint có thể bị chặn trong sandbox; package graph vẫn được khóa bằng `packages.lock.json`. Nếu restore thiếu package và mạng bị chặn, yêu cầu quyền mạng thay vì thay version package hoặc lockfile.

Chỉ sau khi baseline đạt và working tree sạch mới tạo branch:

```powershell
git switch --create codex/m2-import-preview-safe-bulk
```

Không commit artifacts, SDK/cache, Project thật, bản dịch, từ điển, credentials hoặc dữ liệu riêng.

## 4. Tài liệu phải đọc theo thứ tự

1. `docs/HANDOFF-M2-BUSINESS.vi.md` — tài liệu này.
2. `docs/M1-CHAPTER-DETECTION.vi.md` — hợp đồng đầu ra detector, encoding, test và giới hạn.
3. `docs/DATA-SAFETY-PHASE3.vi.md` — transaction journal, recovery và legacy import.
4. `docs/DATA-SAFETY-PHASE2.vi.md` — atomic batch, safe undo và async error handling.
5. `docs/DATA-SAFETY-PHASE1.vi.md` — ownership, rename, registration và safe switching.
6. `docs/ARCHITECTURE.vi.md`, `docs/VALIDATION.vi.md`, `docs/WINDOWS-CHECKLIST.vi.md`.

Các tài liệu có đường dẫn máy cũ chỉ để truy vết; dùng đường dẫn tương đối trong clone mới.

## 5. M1 đã cung cấp gì cho M2

Pipeline:

`RawSourceSnapshot → UnicodeLineScanner → recognizers → CandidateClassifier → ChapterSequenceAnalyzer → BoundaryPlanner → ImmutableImportPlan`

Mã chính:

- `src/QuickTranslator.AiEditor.Core/Importing/RawSourceReader.cs`
- `src/QuickTranslator.AiEditor.Core/Importing/UnicodeLineScanner.cs`
- `src/QuickTranslator.AiEditor.Core/Importing/ChapterTitleRecognizers.cs`
- `src/QuickTranslator.AiEditor.Core/Importing/CandidateClassifier.cs`
- `src/QuickTranslator.AiEditor.Core/Importing/ChapterSequenceAnalyzer.cs`
- `src/QuickTranslator.AiEditor.Core/Importing/BoundaryPlanner.cs`
- `src/QuickTranslator.AiEditor.Core/Importing/ImportModels.cs`
- `src/QuickTranslator.AiEditor.Core/Importing/ChapterDetector.cs`

Tests/công cụ:

- `tests/QuickTranslator.AiEditor.Tests/ChapterDetectionGoldenTests.cs`
- `tests/QuickTranslator.AiEditor.Tests/ChapterDetectionSafetyTests.cs`
- `tests/QuickTranslator.AiEditor.Tests/Fixtures/ChapterDetection/golden-headings.tsv`
- `tools/QuickTranslator.AiEditor.ChapterDetector.Demo/`
- `scripts/Validate-M1.ps1`

Hợp đồng phải giữ:

- Không thay đổi chữ Hán, dấu câu, khoảng trắng hoặc newline nguồn.
- Offset công khai là UTF-16; mọi vùng phải liên tục, không hở/chồng và ghép lại đúng từng ký tự.
- Raw bytes, text, encoding và SHA-256 được giữ trong snapshot.
- Auto encoding chỉ chấp nhận UTF-8 nghiêm ngặt; GBK/GB18030 phải do caller chọn rõ và decode lỗi phải dừng.
- Detector không tự sửa/đánh lại số, không bỏ vùng chưa phân loại.
- Candidate có recognizer, evidence, confidence và decision; trường hợp mơ hồ đưa sang Preview.
- M1 chưa ghi Project, manifest hay chapter file.

Giới hạn chủ đích cần được xử lý bằng Preview hoặc corpus sau này, không lén nới detector:

- Bare numeric heading như `001 - Tên` chưa được tự nhận.
- Roman numerals, OCR hỏng và heading nhiều dòng có thể còn unclassified.
- Dòng giống tiêu đề có thể là `Rejected` hoặc `NeedsReview` và vẫn được giữ nguyên trong snapshot.

## 6. Phạm vi M2 đã được định hướng

M2 là **Import Preview + Safe Bulk Import**. Không triển khai Translation Queue nhiều chương, redesign production toàn bộ, Reader mới hoặc thay thuật toán Ollama/TranslateGemma/TraditionalTranslator/VietPhrase.

### Import Preview

- Nhận `ImmutableImportPlan` của M1; không viết lại detector trong UI.
- Danh sách tiêu đề/chapter là khu vực chính, có tìm kiếm/lọc theo accepted, uncertain, rejected và issue.
- Mặc định ẩn thông tin kỹ thuật; evidence, offsets, hash và recognizer nằm trong panel chi tiết khi cần.
- Cảnh báo rõ chapter thiếu, trùng, đảo thứ tự, nhảy số, volume reset, candidate mơ hồ và xung đột với Project.
- Cho sửa title/number/kind, đổi quyết định include/exclude, split, merge, chỉnh boundary và đánh dấu volume.
- Mọi sửa đổi tạo plan/revision bất biến mới, có Undo/Redo hoặc lịch sử thao tác; không mutation trực tiếp snapshot nguồn.
- Luôn có đối chiếu coverage và exact reconstruction. Vùng unclassified phải hiện diện, không bị bỏ khi import.
- Preview không tự đánh lại số, không tự ghi đè chapter cũ và không tuyên bố thành công trước khi transaction commit.

### Safe Bulk Import

- Preflight toàn bộ selection trước khi ghi: Project/session, manifest revision, ChapterId, số active, path, primary/backup hiện có, deleted entry, source hash, plan revision và encoding.
- Dùng ID ổn định được tạo trong plan thực thi; Retry không sinh một bộ ID khác làm trùng dữ liệu.
- Mọi chapter đã publish phải được đăng ký trong manifest; không có đường tạo file chapter bên ngoài cơ chế đăng ký.
- Tận dụng transaction/recovery hiện có thay vì ghi thẳng file: `ProjectRepository.Transactions.cs`, `ProjectTransactionJournal.cs`, `RegisteredChapterStore.cs`, `RecoveryFiles.cs`.
- Ghi theo transaction hoặc chunk có journal. Atomicity bắt buộc trong một chunk; giữa nhiều chunk chỉ cho partial success có kiểm soát với báo cáo `Imported/RolledBack/NotStarted/NeedsRecovery`.
- Giữ source rescue/hash và bằng chứng before/after đủ để reopen, rollback hoặc resume an toàn sau lỗi/cancel/process loss.
- Không xóa/sửa source đầu vào, chapter cũ, deleted chapter, approvals, drafts, edit history, checkpoints hoặc Project resources.
- Khi rollback không thể xác minh, chặn write tiếp, giữ journal/evidence và đưa người dùng vào recovery flow. Không tự quyết định phục hồi.
- Sau commit phải đọc lại primary trực tiếp, xác minh identity/nội dung/hash và manifest; không dùng fallback backup để che primary lỗi.

### Tests phải viết trước phần sửa phức tạp

- Preview operations: split/merge/boundary/title/number/kind/include/exclude; Undo/Redo; coverage và exact round-trip sau mỗi bước.
- Missing/duplicate/reversed/volume reset/ambiguous/unclassified; Unicode, CRLF/LF/mixed, UTF-8/GBK/GB18030 và decode failure.
- Collision preflight với active/deleted chapter, duplicate ID/number/path, manifest đổi sau preview, source/plan hash đổi.
- Fault injection ở đầu/giữa/cuối: chapter write, backup rotation, manifest write, journal update, post-write verify và report.
- Cancellation trước commit, trong chunk, giữa các chunk; simulated process loss rồi reopen/recover/resume.
- Retry idempotent; không orphan, không duplicate, không mất source hoặc history.
- Hàng nghìn chapter: preview không cần load toàn bộ chapter Project vào RAM và UI phải dùng virtualization/cancellation.
- Full 477-test regression phải tiếp tục đạt; Release 0 lỗi/0 cảnh báo.
- WPF integration chỉ dùng Project tổng hợp trong temp. Nghiệm thu chuột/IME/DPI/native dialog ghi riêng là thủ công.

## 7. Các invariant Data Safety không được phá

- **F01:** Project sở hữu Character Profiles, Glossary, Context và revision hiện hành; snapshot chapter chỉ truy vết.
- **F02:** Rename đồng bộ document sống, manifest và autosave dưới save gate.
- **F03:** Mọi chapter persist qua đăng ký thống nhất; editor không attach/write chapter ngoài manifest.
- **F04:** Switch lưu và validate trước khi commit context; request cũ không ghi sang chapter/project mới.
- **F05:** Batch trong một chapter chuẩn bị/validate toàn bộ rồi mới commit; lỗi giữ nguyên live/disk/history.
- **F06:** Undo không vượt approval/manual edit mới; approval history append-only được giữ.
- **F07:** Add/Delete/Restore dùng journal, rollback và recovery preview; không tự xử lý orphan.
- **F08:** Store recovery xác minh checksum/identity, giữ rescue evidence và yêu cầu quyết định người dùng khi cần.
- **F09:** Import legacy preflight/transaction/verify toàn lịch sử; rollback cả đợt hoặc báo NeedsRecovery.
- **F12:** Async handler báo lỗi tại nguồn, không catch rỗng, không báo thành công khi save thất bại và ngăn thao tác chồng nhau.

M2 không thay schema hiện có nếu chưa chứng minh cần thiết. Metadata/journal mới phải backward-compatible và có tests đọc fixture cũ. Không tự prune history, backups, checkpoints hoặc transaction evidence.

## 8. Điểm tích hợp cần đọc trước khi thiết kế API M2

- `src/QuickTranslator.AiEditor.Core/Projects/ProjectRepository.cs`
- `src/QuickTranslator.AiEditor.Core/Projects/ProjectRepository.Transactions.cs`
- `src/QuickTranslator.AiEditor.Core/Projects/ProjectTransactionJournal.cs`
- `src/QuickTranslator.AiEditor.Core/Projects/RegisteredChapterStore.cs`
- `src/QuickTranslator.AiEditor.Core/Persistence/RecoveryFiles.cs`
- `src/QuickTranslator.AiEditor.Core/Projects/LegacyProjectImporter.cs`
- `src/QuickTranslator.AiEditor.Wpf/MainWindow.Projects.cs`
- `src/QuickTranslator.AiEditor.Wpf/MainWindow.WorkspaceLifetime.cs`
- `src/QuickTranslator.AiEditor.Wpf/MainWindow.Recovery.cs`
- `tests/QuickTranslator.AiEditor.Tests/ProjectTransactionTests.cs`
- `tests/QuickTranslator.AiEditor.Tests/LegacyImportSafetyTests.cs`
- `tests/QuickTranslator.AiEditor.Tests/UnifiedRecoveryTests.cs`
- `tests/QuickTranslator.AiEditor.Wpf.Tests/RecoveryUiTests.cs`

Trước khi sửa, tìm tất cả đường import TXT/chapter hiện tại và xác định đường nào phải được thay bằng coordinator M2. Không thêm workaround trực tiếp vào click handler.

## 9. Quy trình Git cho M2

1. Xác minh remote/branch/HEAD/working tree.
2. Chạy baseline 477 tests trên branch M1.
3. Tạo `codex/m2-import-preview-safe-bulk` từ HEAD M1 mới nhất.
4. Tạo checkpoint chuẩn bị trước thay đổi chức năng.
5. Viết red tests trước từng nhóm quy tắc phức tạp.
6. Commit riêng tối thiểu cho Preview Core, transaction bulk import, WPF Preview và hardening/final report.
7. Sau mỗi checkpoint: targeted tests, full regression và Release build khi phù hợp.
8. Push thường lên GitHub; không force-push, không merge vào M1/Phase 3/project-workspace.
9. Dừng sau M2 để người dùng nghiệm thu; chưa tự triển khai M3 Translation Queue.

Git identity trước đây không được đặt global. Nếu máy chưa có identity, dùng theo từng lệnh commit:

```powershell
git -c user.name=Codex -c user.email=codex@localhost commit ...
```

## 10. Những thứ không nằm trên GitHub

- `.tools/dotnet`, `.tools/nuget`, `.tools/cli`.
- `QuickTranslator.AiEditor/artifacts/**`, logs và TRX.
- ZIP demo M1 Windows x64 đã từng tạo cục bộ.
- Project thử nghiệm và dữ liệu temp.
- Mockup cục bộ ngoài repository và lịch sử chat Personal.

Các mục này không cần để viết M2. Demo M1 và artifacts có thể tạo lại bằng `Validate-M1.ps1`. Không chuyển Project thật vào phiên mới để làm test.

## 11. Prompt dán vào phiên Codex Business

```text
Tiếp tục QT Studio tại repository hungdo9833-stack/QuickTranslator.

Đọc QuickTranslator.AiEditor/docs/HANDOFF-M2-BUSINESS.vi.md trước và làm đúng toàn bộ quy trình trong đó. Branch nguồn là codex/m1-chapter-detection; commit implementation M1 là cfaf33842617d124ed1ddb058ed8685959fb566c, còn HEAD có thể mới hơn do commit tài liệu bàn giao. Trước khi sửa, xác minh remote/branch/HEAD/working tree và chạy QuickTranslator.AiEditor/scripts/Validate-M1.ps1; baseline phải có 477 tests đạt và Release 0 lỗi/0 cảnh báo.

Sau baseline, tạo branch codex/m2-import-preview-safe-bulk và triển khai đúng M2: Import Preview cùng Safe Bulk Import. Dùng ImmutableImportPlan của M1, bảo toàn exact source coverage và tất cả invariant Data Safety Phase 1–3. Viết regression tests trước quy tắc phức tạp, dùng dữ liệu tổng hợp/temp, fault injection đầu/giữa/cuối, rồi chạy toàn bộ regression và Release build. Không sửa Project thật/Data từ điển, không thay Ollama/TranslateGemma/TraditionalTranslator/VietPhrase/Reader, không triển khai Translation Queue M3, không merge hoặc force-push. Commit theo checkpoint, push branch M2 và dừng để tôi nghiệm thu.
```

## 12. Khi nào có thể đóng workspace Personal

Chỉ cần giữ workspace Personal cho tới khi phiên Business đã:

1. Checkout đúng branch/HEAD từ GitHub.
2. Đọc được tài liệu này và các báo cáo Phase 1–3/M1.
3. Nhận diện .NET 10 SDK.
4. Chạy đủ 477 tests và Release build thành công.
5. Tạo/push được branch M2.

Sau năm bước này, toàn bộ nguồn cần thiết đã nằm trong Git/GitHub; artifacts Personal chỉ còn giá trị tham khảo cục bộ.
