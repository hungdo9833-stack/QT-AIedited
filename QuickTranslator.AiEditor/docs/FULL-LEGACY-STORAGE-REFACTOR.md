# Full Legacy Storage Removal & Dependency Refactor

Trạng thái: **COMPLETED theo kiến trúc Project mới**, ngày 16/10/2026.

## Quyết định kiến trúc

QT Studio đang phát triển nên chỉ hỗ trợ schema hiện hành. Project thử nghiệm cũ, legacy envelope, migration và compatibility adapter đã bị loại khỏi runtime. Ứng dụng không tự xóa dữ liệu cũ trên máy; khi người dùng chọn file sai schema, thao tác dừng với lỗi rõ ràng.

Luồng storage sau refactor:

```text
WPF / Editor / Reader
  -> ProjectRepository / feature services / Translation Coordinator
  -> SimpleFileStorage
  -> JSON UTF-8 hiện hành
```

`SimpleFileStorage` validate bytes trước khi ghi, tạo file tạm cùng thư mục, flush xuống đĩa và replace file đích. Ghi cùng đường dẫn được tuần tự hóa. Không có checksum bắt buộc, `.bak`, transaction journal, rollback/replay, rescue ZIP, recovery wizard, autosave history hay recovery checkpoint.

## Thành phần đã xóa

Production:

- `Persistence/AtomicLocalFile.cs`
- `Persistence/RecoveryFiles.cs`
- `Projects/ProjectBackupService.cs`
- `Projects/ProjectTransactionJournal.cs`
- `Projects/ProjectRepository.Transactions.cs`
- `Projects/ProjectRepository.Queue.cs`
- `Projects/LegacyProjectImporter.cs`
- `BookIntelligence/BookIntelligenceMigration.cs`
- `Wpf/MainWindow.Recovery.cs`
- `Wpf/MainWindow.StorageCleanup.cs`
- `Wpf/ProjectStorageCleanupWindow.cs`

Các test/fixture chỉ dành cho recovery, transaction và schema v1 cũng đã bị xóa. Chúng được thay bởi test kiến trúc hiện hành, fault injection và kiểm tra từ chối schema cũ; không xóa test chức năng đang còn hiệu lực.

## Module đã refactor

- Project lifecycle, manifest và chapter CRUD.
- Safe Bulk Import, title translation và resegmentation.
- Character Profiles, quan hệ/xưng hô, Glossary và Context qua một `ProjectResources` hiện hành.
- Prompt, Reader, Traditional, Ollama profile, Library và workspace preferences.
- Multi-Chapter/Multi-GPU queue và current progress persistence.
- Batch Replace, source editing, Reader/Editor/Export.
- AI Book Intelligence và review queue.

Import vẫn giữ Verified Chapter Reconstruction và Source Coverage Audit. Queue vẫn lưu trạng thái hiện tại, pause/resume/restart. `TranslationChapterStore` kiểm tra source và dữ liệu được bảo vệ trong cùng critical section với lần ghi, để kết quả AI cũ không ghi đè UserDraft/Approval hoặc source mới.

## Dependency trước và sau

Trước:

```text
Feature modules
  -> RecoveryFiles / checksummed envelopes / backup selection
  -> ProjectTransactionJournal / rollback / recovery state
  -> AtomicLocalFile + .bak + journal copies
```

Sau:

```text
Feature modules
  -> validation và conflict guard nghiệp vụ
  -> SimpleFileStorage
  -> current JSON file
```

Hash còn lại chỉ phục vụ nhận diện source, candidate import, cache và chống kết quả AI đến muộn. Chúng không chọn backup, khóa file hoặc tạo storage revision history.

## Audit dependency

Tìm kiếm `src` và `tools` cho các tên sau trả về 0 kết quả: `RecoveryFiles`, `RecoveryInfo`, `RecoverySource`, `RecoveryFileCondition`, `ProjectTransactionJournal`, `ProjectRecoveryReport`, `ProjectRecoveryDecision`, `TransactionRollback`, `TransactionRecovery`, `RescueArchive`, `RestoreBackupAsync`, `CheckpointHistory`, `.bak`, `Backups`, `History/Transactions`.

Không có no-op wrapper hoặc implementation giả để giữ API cũ. Các phase report cũ trong `docs` là tài liệu lịch sử, không phải dependency runtime.

## Milestone commits

- M1: `028c46827add3bca296fff40c8ad7e8f54569a20` — direct Simple Storage primitive.
- M2: `7825ee15d3cd10afba6c539191c3495caa4234c8` — migrate feature modules và xóa runtime legacy.
- M3: `479f27ed4c6d5443e1f7411549ca242891e58aa3` — tài liệu kiến trúc hiện hành và dọn workflow recovery.
- M4: commit validation/acceptance cuối trên cùng branch; SHA được ghi trong báo cáo bàn giao sau commit.

Kết quả test, benchmark và giới hạn chi tiết nằm trong [VALIDATION.vi.md](VALIDATION.vi.md).
