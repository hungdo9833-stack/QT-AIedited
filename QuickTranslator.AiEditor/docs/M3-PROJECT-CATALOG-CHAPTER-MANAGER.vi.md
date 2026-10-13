# M3 — ProjectCatalog & Chapter Manager

Ngày nghiệm thu kỹ thuật: 13/10/2026

Branch: `codex/m3-project-catalog-chapter-manager`

Điểm nguồn M2: `5f851e38e8ee53bccc2844ae4356f1e1c24c64c1`

## Phạm vi đã triển khai

M3 thêm ProjectCatalog dẫn xuất hoàn toàn từ metadata trong Project Manifest và màn hình Chapter Manager WPF Dark Navy. Không đổi schema Project, không sửa detector/import M1–M2, không sửa dữ liệu dịch, Ollama, TranslateGemma, TraditionalTranslator hoặc VietPhrase và chưa triển khai Multi-Chapter Translation Queue M4.

## ProjectCatalog

- Manifest tiếp tục là nguồn dữ liệu có thẩm quyền. Catalog chỉ ở bộ nhớ, không có file index riêng và không ghi ngược manifest.
- Snapshot chứa metadata/summary chapter; quá trình build, query, search, filter và sort không gọi `LoadChapterAsync` và không đọc chapter body.
- Revision là SHA-256 của manifest đã serialize. Catalog bị invalidate sau transaction commit thành công và rebuild khi phát hiện `UpdatedAt`/số entry hoặc revision thay đổi.
- Repository chỉ phát sự kiện sau commit. Transaction rollback hoặc lỗi trước commit không phát trạng thái thành công.
- Add/import, rename, soft delete/restore, save translation, approval và trạng thái error được phản ánh qua summary manifest. `RegisteredChapterStore` ghi chapter body và summary manifest trong cùng transaction repository.
- Subscriber UI lỗi sau commit được cô lập và ghi trace, không biến một commit đã thành công thành lỗi giả cho người dùng.

## Chapter Manager WPF

- Hiển thị thứ tự đọc, số gốc hiện hành, tên Việt/Trung, quyển, trạng thái dịch/duyệt, tiến độ, thời gian cập nhật và History.
- Tìm kiếm theo số/tên; lọc trạng thái dịch, duyệt, active/History và quyển; sort theo thứ tự đọc, số, tên hoặc cập nhật.
- Extended multi-selection, mở trong Editor, rename, soft delete nhiều chapter và restore có preflight collision.
- DataGrid và danh sách sidebar dùng virtualization/recycling. Test dựng 10.000 row khi không hề có chapter body file.
- Cửa sổ đang mở tự cập nhật sau commit M2 import và các thao tác chapter; người dùng không cần bấm Refresh.
- Command surface cho selection M4 đã có nhưng nút queue bị vô hiệu hóa và không thực hiện dịch nhiều chương.

## Số chương trùng giữa các quyển

Không thể hỗ trợ bền vững mà không đổi schema: schema 1 chỉ có `Number`, bắt buộc duy nhất toàn Project và không lưu volume identity hoặc display order. M3 không tự đánh lại số và không dùng catalog làm nguồn ghi. Đề xuất schema/migration, backup, rollback và tests nằm trong `M3-DUPLICATE-NUMBERS-MIGRATION-PROPOSAL.vi.md`; chưa có migration nào được thực hiện.

## Hiệu năng đo trên dữ liệu tổng hợp

Môi trường đo: Windows 10.0.26100, .NET 10.0.12 Release; mọi Project benchmark có 0 file chapter body.

| Chapter | Rebuild sau reopen | Search | Filter | Sort | Bộ nhớ catalog xấp xỉ |
|---:|---:|---:|---:|---:|---:|
| 1.000 | 18,128 ms | 0,913 ms | 0,019 ms | 0,481 ms | 1.158.952 byte |
| 5.000 | 84,309 ms | 4,906 ms | 0,123 ms | 2,991 ms | 691.960 byte |
| 10.000 | 72,765 ms | 8,417 ms | 0,585 ms | 9,130 ms | 1.251.536 byte |

Các số là một lần chạy trên máy nghiệm thu, không phải SLA. Số bộ nhớ là chênh lệch managed heap xấp xỉ sau full GC nên có thể dao động giữa các lần chạy. Tool tái lập nằm ở `tools/QuickTranslator.AiEditor.ProjectCatalog.Benchmark`.

## Kiểm thử

- Full regression: 468 Core + 41 WPF = 509 tests đạt, 0 fail, 0 skipped.
- Release build: 0 lỗi, 0 cảnh báo.
- Catalog: 1.000/5.000/10.000 metadata entry, không body load; revision/rebuild; commit/rollback; rename/save/approve/delete/restore; reopen/crash/recovery; search/filter/sort.
- WPF: 10.000 rows + recycling; tìm kiếm/lọc/multi-select; M2 import tự xuất hiện; mở/rename/delete/restore giữ Editor và catalog nhất quán.
- Render smoke test WPF với 10.000 metadata row đã được kiểm tra trực quan ở 1320×820; header, bộ lọc, trạng thái và thanh cuộn hiển thị đúng trong Dark Navy.
- Toàn bộ 497 tests M2 tiếp tục đạt, gồm Data Safety Phase 1–3 và fault injection M2.

## Giới hạn cần nghiệm thu thủ công

- Native file dialog, IME Trung/Việt, DPI nhiều màn hình, keyboard navigation và cảm giác scroll dài cần kiểm tra trên máy sử dụng thực tế.
- Mỗi soft delete/restore trong một multi-selection là một transaction chapter độc lập. Nếu I/O lỗi giữa danh sách, các transaction đã commit vẫn giữ nguyên và UI phản ánh chính xác partial success.
- Approval filter của schema 1 phân biệt chapter `Approved` hoàn toàn với các trạng thái còn lại; manifest không lưu số segment đã duyệt riêng.
- Volume column/filter được chuẩn bị nhưng schema 1 chưa có metadata volume bền vững.

Chạy nghiệm thu và tạo package bằng `scripts/Validate-M3.ps1`. Quy trình thử import M2 rồi kiểm tra mục lục M3 nằm trong `docs/M3-MANUAL-TEST.vi.md` và được chép vào ZIP.
