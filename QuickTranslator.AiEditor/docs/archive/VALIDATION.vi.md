# Validation — Simple Storage Refactor

Lần kiểm chứng cuối: **16/10/2026**, Windows x64, .NET SDK **10.0.401**.

## Kết quả tự động

| Kiểm tra | Kết quả |
|---|---:|
| Core regression (`QuickTranslator.AiEditor.Tests`) | **491/491 đạt**, 0 lỗi, 0 bỏ qua |
| WPF integration (`QuickTranslator.AiEditor.Wpf.Tests`) | **89/89 đạt**, 0 lỗi, 0 bỏ qua |
| Tổng | **580/580 đạt**, 0 lỗi, 0 bỏ qua |
| Release solution build với `-warnaserror` | **0 lỗi, 0 cảnh báo** |
| Strict production dependency audit (`src`, `tools`) | Không còn tên recovery/journal/backup bị cấm |
| WPF smoke off-screen 1600×900 | Khởi tạo Project tổng hợp, Editor và tạo ảnh 157.408 byte thành công |

Các test dùng Project tổng hợp trong thư mục tạm. Không mở, sửa hoặc xóa Project truyện của người dùng.

## Ma trận acceptance hiện hành

| Workflow | Bằng chứng |
|---|---|
| Tạo, mở, lưu, chuyển Project | `ProjectSystemTests`, `SafeSwitchingTests` |
| JSON nhân vật trực tiếp, locked relationships | `CharacterConsistencyLockTests`, `ProjectResourceTests` |
| Glossary, Context và Resources thống nhất | `ProjectResourceTests`, `ProjectOwnershipTests` |
| Import 1/50/500/5.000 chương, coverage chính xác | `SafeBulkImportTests`, detector/import tests |
| Collision và nhập lại chương | `SafeBulkImportTests` |
| Rename, duplicate, soft delete/restore | `ProjectSystemTests`, chapter manager tests |
| Lưu dịch từng đoạn và chống kết quả AI đến muộn | `ProjectTranslationCoordinatorTests`, `ImportedChapterQueueRegressionTests` |
| Pause/resume/cancel/restart queue | `ProjectTranslationCoordinatorTests` |
| Hai worker dịch song song | coordinator tests và benchmark 500 chương |
| Ghi liên tiếp được tuần tự hóa | `SimpleFileStorageTests`, coordinator tests |
| Fault injection giữ nguyên file đích | `SimpleFileStorageTests`, `AsyncSafetyTests` |
| Không sinh `.bak`, journal, rescue/history | storage/project/import tests và audit `src/tools` |
| AI Book Intelligence lưu/mở/resume | `BookIntelligenceSession2Tests` |
| Reader, Editor và Export | `ReaderModeTests`, publication tests, WPF integration |
| Schema cũ/envelope cũ | Bị từ chối rõ ràng theo quyết định kiến trúc mới; không migration |

## Benchmark tổng hợp

### 1.000 lần lưu chapter

- Tổng: **3.778,224 ms**.
- Trung bình: **3,778 ms/lần**; median **3,688 ms**; P95 **4,745 ms**.
- Mở lại Project: **1,865 ms**.
- Hash storage: **0/lần**.
- Mỗi checkpoint ghi chapter và manifest qua hai file tạm/thay thế; không tạo backup/history.
- Dung lượng Project cuối: **31.386 byte**; checkpoint cuối mở lại chính xác.

### Queue 500 chương, hai worker giả lập

- Hoàn thành **500/500**, 500 ChapterId duy nhất.
- Local/Remote: **250/250 request**.
- Thời gian: **34.812,888 ms**, khoảng **14,362 chương/giây**.
- Resume đến request đầu: **237,878 ms**; hủy request đang chạy: **10,863 ms**.
- Dung lượng Project cuối: **6.007.022 byte**.
- Tổng byte ghi cộng dồn: **497.937.433 byte**. Đây là workload AI tức thời cực đoan; manifest/job hiện vẫn được ghi nhiều lần và là điểm tối ưu I/O còn lại, không phải dung lượng lưu cuối.

## Lệnh đã chạy

```powershell
dotnet test tests\QuickTranslator.AiEditor.Tests\QuickTranslator.AiEditor.Tests.csproj -c Release --no-restore
dotnet test tests\QuickTranslator.AiEditor.Wpf.Tests\QuickTranslator.AiEditor.Wpf.Tests.csproj -c Release --no-restore
dotnet build QuickTranslator.AiEditor.slnx -c Release --no-restore -warnaserror
dotnet run --project tools\QuickTranslator.AiEditor.Storage.Benchmark -c Release --no-restore -- 1000
dotnet run --project tools\QuickTranslator.AiEditor.QueueWorkflow.Benchmark -c Release --no-restore -- 500 2
dotnet run --project tools\QuickTranslator.AiEditor.M5.UiSmoke -c Release --no-build -- two --capture <temp.png> --width 1600 --height 900
```

## Giới hạn đã biết

- Project/schema/envelope cũ không còn được hỗ trợ; dữ liệu cũ trên đĩa không bị tự động xóa.
- Thao tác nhiều file validate trước rồi commit tuần tự. Nếu I/O lỗi giữa danh sách, kết quả trả rõ phần đã commit/thất bại/chưa xử lý; không rollback tự động.
- Atomic replace bảo vệ file đích khỏi nội dung ghi dở. Nó không thể cam kết đồng bộ nguyên tử giữa chapter và manifest khi máy mất điện đúng giữa hai lần replace.
- Benchmark dùng fake provider, không đo tốc độ Ollama/GPU hoặc chất lượng dịch.
