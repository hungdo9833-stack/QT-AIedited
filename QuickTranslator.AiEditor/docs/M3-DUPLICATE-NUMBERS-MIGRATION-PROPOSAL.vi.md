# Đề xuất schema cho số chương trùng giữa các quyển

Trạng thái: đề xuất riêng, **chưa triển khai trong M3**.

## Vì sao M3 không thể xử lý bền vững bằng schema hiện tại

Manifest schema 1 chỉ lưu một trường `Number` trên chapter và bắt buộc `Number` của mọi chapter active phải duy nhất trong toàn Project. Manifest không có định danh quyển, số chương gốc tách biệt hoặc thứ tự đọc bền vững. Dùng tên chương, vị trí trong mảng hoặc catalog cache để suy ra các giá trị này sẽ tạo nguồn dữ liệu thứ hai và có thể thay đổi sau rename/recovery.

M3 vì vậy giữ nguyên ràng buộc hiện hành. `ProjectCatalog` dùng `ChapterId` làm định danh, hiển thị `Number` hiện tại như số gốc và suy ra `DisplayOrder` chỉ để đọc/sort. Cột quyển để trống cho Project schema 1. Không có thao tác tự đánh lại số.

## Schema 2 được đề xuất

- `Volumes`: danh sách gồm `VolumeId` ổn định, tên gốc, số quyển gốc nếu có và `DisplayOrder`.
- Chapter tiếp tục giữ `ChapterId` ổn định và bổ sung `VolumeId`, `OriginalChapterNumber` có thể null, chuỗi số gốc nếu cần giữ cách viết, và `DisplayOrder` duy nhất toàn Project.
- `DisplayOrder` quyết định thứ tự đọc. Cặp `(VolumeId, OriginalChapterNumber)` phục vụ phát hiện trùng trong cùng quyển; cùng số ở hai quyển khác nhau là hợp lệ.
- Tiêu đề và số gốc được giữ đúng nguồn. Không dùng `DisplayOrder` để thay thế hoặc ghi đè số gốc.
- Manifest vẫn là nguồn có thẩm quyền. Catalog tiếp tục là read model có thể rebuild hoàn toàn.

## Tương thích và migration

1. Trước migration, tạo ZIP rescue đã xác minh hash và bản sao nguyên vẹn manifest schema 1.
2. Chạy migration bằng transaction journal Phase 3. Mỗi chapter cũ nhận một `VolumeId` mặc định, `OriginalChapterNumber = Number`, và `DisplayOrder` theo thứ tự đọc hiện hành.
3. Đọc lại, validate toàn bộ quan hệ volume/chapter, số lượng, đường dẫn và hash chapter trước commit.
4. Ứng dụng mới phải mở Project schema 1 trực tiếp và chỉ migrate khi người dùng xác nhận. Không migration ngầm khi chỉ mở Project.
5. Phiên bản ứng dụng hiện tại từ M3 trở về trước sẽ không thể hiểu quy tắc duplicate của schema 2. Muốn quay lại schema 1 phải phục hồi backup trước migration; dữ liệu tạo thêm sau migration phải được export/rescue trước rollback.

## Rollback và kiểm thử bắt buộc

- Fault injection trước journal, giữa manifest write, trước commit và sau crash/reopen.
- Migration 0/1/10.000 chapter, nhiều quyển, số trùng khác quyển, số trùng cùng quyển, chapter không số và ngoại truyện.
- So sánh byte chapter body, UserDraft, Approval, Edit History, Glossary, Context và Character Profiles trước/sau.
- Rebuild catalog từ manifest schema 2 và xác minh không cần chapter body.
- Rollback khôi phục chính xác schema 1 khi migration chưa commit; recovery idempotent khi journal đang dở.
- Mở Project schema 1 bằng bản mới mà không tự ghi schema 2.

Migration này cần một lần phê duyệt riêng trước khi triển khai vì nó thay đổi định dạng bền vững và giới hạn khả năng mở Project bằng phiên bản QT Studio cũ.
