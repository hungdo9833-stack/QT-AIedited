# Kiến trúc QT Studio hiện hành

## Luồng tổng thể

```text
WPF / Editor / Reader
    -> ProjectRepository / feature services / Translation Coordinator
    -> SimpleFileStorage
    -> JSON UTF-8 trong thư mục Project hoặc LocalAppData
```

`SimpleFileStorage` là primitive lưu file duy nhất. Dữ liệu đề xuất được serialize và validate trước, ghi vào file tạm cùng thư mục, flush xuống đĩa rồi thay file đích. Việc ghi cùng một đường dẫn được tuần tự hóa. Hệ thống không tạo `.bak`, checksum file, journal, shadow copy, rescue archive hoặc state machine phục hồi.

QT Studio đang trong giai đoạn phát triển và chỉ hỗ trợ schema hiện hành. Project/schema cũ, JSON envelope và adapter chuyển đổi cũ không nằm trong runtime. Ứng dụng không tự xóa dữ liệu thử nghiệm cũ; file không đúng schema hiện tại được báo lỗi.

## Project và layout

Một Project mới có layout:

```text
Project/
  <name>.qtproj
  Chapters/
  Deleted/
  Resources/project-resources.json
  Settings/
    translation.json
    reader.json
    export.json
    prompts/presets.qtaip.json
    ai-book-intelligence.json
  Queue/project-translation-job.json
  Cache/
```

`ProjectManifest` là nguồn metadata có thẩm quyền. `ProjectCatalog` là read model dẫn xuất, chỉ giữ metadata và tự invalidated sau khi repository commit. Nội dung chương chỉ được nạp khi cần.

`ProjectResourceStore` sở hữu một JSON thống nhất cho Character Profiles, Glossary và Context. Snapshot trong chapter chỉ phục vụ request/biên tập đang làm việc; nó không ghi ngược tài nguyên Project. Cấu hình dịch, Reader, Export, preset, queue và AI Book Intelligence có store trực tiếp riêng.

## Thao tác nhiều file

Không có giao dịch xuyên nhiều file. Các thao tác nhiều chương validate toàn bộ điều kiện nghiệp vụ trước, sau đó commit từng chương theo thứ tự. Nếu I/O lỗi giữa danh sách, các chương đã commit vẫn hợp lệ và báo cáo trả rõ phần đã hoàn thành, lỗi và chưa xử lý. Import TXT giữ Verified Chapter Reconstruction và Source Coverage Audit; đây là kiểm tra tính chính xác nội dung, không phải cơ chế phục hồi storage.

Xóa mềm chuyển file chương sang `Deleted/`; phục hồi chuyển nó về `Chapters/`. Đây là trạng thái nghiệp vụ do người dùng yêu cầu, không phải lịch sử autosave.

## Translation và Multi-GPU Queue

Mỗi chương lưu trạng thái segment và queue hiện tại. `ProjectTranslationJobStore` lưu kế hoạch/tiến độ Project hiện tại để pause, resume và mở lại. Request đang chạy khi ứng dụng dừng được đưa về trạng thái interrupted; ứng dụng không tự gửi lại trước khi người dùng xác nhận Resume.

Coordinator ngăn hai coordinator ghi cùng Project. Mỗi worker chỉ claim một ChapterId tại một thời điểm. Trước khi ghi kết quả AI, `TranslationChapterStore` kiểm tra source/ranh giới và dữ liệu được người dùng bảo vệ trong cùng critical section với lần ghi; kết quả đến muộn không thể ghi đè UserDraft, Approval hoặc source edit.

Các fingerprint của source, protected content và Chapter Reconstruction là kiểm tra nghiệp vụ/độ chính xác nội dung. Chúng không khóa file, không chọn backup và không tạo storage revision history.

## Editor, Reader, Export và tài nguyên

`ChapterTranslationComposer` là quy tắc ghép segment dùng chung cho Reader và TXT Export. Reader chỉ nạp chương cần thiết. Editor, Character Consistency, Batch Replace và AI Book Intelligence đều lưu qua repository/store trực tiếp.

Batch Replace vẫn giữ revision biên tập trong ChapterDocument để hỗ trợ Undo an toàn. Approval, UserDraft, TranslationAttempt và các lịch sử biên tập là dữ liệu sản phẩm, khác với autosave/recovery history đã bị loại bỏ.

## Ranh giới lỗi

- JSON sai cú pháp, thiếu trường bắt buộc hoặc sai schema: báo lỗi và giữ nguyên file.
- Lỗi ghi trước replace: file đích cũ giữ nguyên; file tạm được dọn theo best effort và lỗi dọn được ghi Trace.
- Không có fallback sang file khác và không tự tạo defaults nếu file bắt buộc đang hỏng.
- File cấu hình tùy chọn chỉ nhận default khi file thực sự chưa tồn tại.
- Save/Autosave/background worker dùng các gate theo Project, resource hoặc path để tránh ghi chồng.

## Kiểm thử

Tests dùng Project tổng hợp trong thư mục tạm. Các suite bao phủ create/open/save/switch, import 1–5.000 chương, queue tuần tự và hai worker, pause/resume/restart, Character/Glossary/Context, AI Book Intelligence, Reader/Editor/Export, fault injection và audit không phát sinh `.bak`/journal/rescue.
