# Kết quả kiểm chứng AI Editor, Project Workspace và các module biên tập

Thực hiện lần cuối ngày **12/10/2026** trong Codex Cloud **Linux x64**, .NET SDK **10.0.401**. Kết quả áp dụng cho module độc lập `QuickTranslator.AiEditor`; QuickTranslator, TranslatorEngine và WinFormsUI gốc không bị sửa.

## Đã thực thi

| Kiểm tra | Kết quả |
|---|---|
| Restore solution và restore lại bằng `--locked-mode` | Thành công; ba project có lockfile dependency |
| Build solution Release, gồm Core/WPF/Tests | **0 lỗi, 0 cảnh báo** |
| Unit test Release với xUnit, không dùng Ollama/GPU | **193/193 đạt**, 0 lỗi, 0 bỏ qua |
| Publish WPF `win-x64`, phụ thuộc runtime | Thành công; có EXE Windows và các DLL/cấu hình |
| Kiểm tra Git đối với mã gốc | Không có diff ở ngoài thư mục module AI Editor |
| Git và ignore dữ liệu/đầu ra | Chỉ thêm thư mục module; `.qtaie.json`, `.characters.json`, backup, data, bin/obj/artifacts bị ignore |

Các trường hợp được runner báo gồm cả các tham số riêng của test theo bảng:

| Nhóm | Số test | Nội dung đã kiểm tra |
|---|---:|---|
| TextSegmenter | 14 | 1.000/5.000 chữ Hán, cỡ đoạn, CRLF/LF/khoảng trắng, lời thoại, câu dài, chữ ngoài BMP/emoji/dấu tổ hợp, ID ổn định, chuỗi ngẫu nhiên và đầu vào Unicode sai |
| OllamaClient | 20 | JSON request/options/metrics, localhost, kiểm tra kết nối chỉ đọc `/api/tags`, model thiếu/từ xa, không gửi nguyên văn trước xác minh model, HTTP/redirect, JSON lỗi, timeout/hủy, một request tại một thời điểm |
| TranslationService | 11 | Lượt dịch riêng chỉ xử lý đoạn yêu cầu, không auto-approve, dịch lại giữ nháp/lịch sử, sửa khi AI chạy, duyệt/sửa lại, hủy, lưu lỗi trước/sau API, lưu đồng thời và phục hồi gián đoạn |
| ChapterFileStore | 6 | Đọc/ghi toàn bộ dữ liệu, nguyên văn/prompt/cấu hình, backup/checksum, hai tệp hỏng, hủy lưu và từ chối đoạn sai |
| Kiểm tra kết quả/prompt | 4 | Kết quả rỗng/cắt/ngắn/lặp/còn chữ Hán; prompt và tham khảo tùy chọn |
| Luồng tích hợp HTTP giả lập | 1 | OllamaClient → TranslationService → tệp thật trong thư mục tạm → chỉnh/duyệt → mở lại |
| TranslationQueue | 12 | Tuần tự đúng thứ tự, tự chuyển đoạn, pause/resume/cancel, crash recovery, retry lỗi, không ghi đè bản sửa/duyệt, snapshot cấu hình, chương 5.000 chữ Hán |
| Chỉnh ranh giới | 9 | Tách/gộp/đổi ranh giới, khôi phục nguyên văn, bảo vệ bản sửa, chỉ dịch lại phần ảnh hưởng và phục hồi cấu trúc cũ |
| Prompt Preset | 9 | Tạo/sửa/nhân bản/chọn/xóa tùy chỉnh, bảo vệ preset mặc định, validation placeholder, snapshot lịch sử, thuật ngữ/ngữ cảnh và backup preset |
| Nâng cấp dữ liệu | 9 | Đọc fixture schema 1 có checksum thật, migration schema 2 → 3, checkpoint gián đoạn, trạng thái/cảnh báo và tương thích lưu/đọc |
| Nhân vật và xưng hô | 24 | Mẹ/vợ dùng `nàng`, cha dùng `hắn`, lời thoại/ngôi thứ nhất/quan hệ gia đình, hai nhân vật mơ hồ, nhiều đại từ khác chủ thể, đoạn trích dẫn, bảo vệ bản sửa, chương cũ, hoàn tác, áp dụng nguyên tử, glossary, JSON/checksum, schema và queue/preset |
| Reader Mode | 8 | Nối segment không sinh khoảng trắng, ánh xạ offset, cảnh báo, round-trip theme/font/vị trí/bookmark, ba mật độ, lề, thu gọn dòng trống chỉ ở tầng hiển thị và palette Dark không trắng |
| Context menu nguyên văn | 9 | Snapshot vùng chọn, policy giống nhau cho ô chỉ đọc/chỉnh sửa, thêm nhanh nhân vật, nhận diện tên đã có, đầu vào sai, tìm vị trí, excerpt dài, lưu glossary và bảo vệ bản người dùng |
| Engine truyền thống và alignment | 20 | Loader UTF-8 chỉ đọc, VietPhrase2/Names2, bốn mẫu QT 2020, cache/cấu hình, chương dài, 林悦, 王秀兰, ba cụm lặp, nhiều câu/CRLF, vùng cuối segment và chiếu selection ba chiều |
| Batch Replace và Chinese Text Cleaner | 20 | CJK mở rộng/offset/count, tên lặp/chồng lấn/chuỗi con, xung đột/unknown/giữ nguyên, bảo vệ draft/approval, revision/undo, queue lock, làm sạch sau dịch không gọi Ollama lần hai, backup/phạm vi/partial failure nhiều chương, round-trip dữ liệu và lỗi ghi |
| Project Core/Workspace/Tools | 17 | Tạo/mở/di chuyển dự án, đường dẫn tương đối, checksum/backup manifest, liên kết ProjectId, trùng số chương, bảo vệ bản sửa, nhân bản, xóa mềm/phục hồi, settings, recent, ZIP/Zip Slip/không ghi đè và nhập legacy không sửa nguồn |

Automated tests dùng Data nhân tạo và `HttpMessageHandler` giả lập. Test lưu/backup dùng file thật trong thư mục tạm Linux và tự xóa; không đưa file/chương/từ điển của người dùng vào repository hay artifact.

Ngoài test tự động, bộ `Data.zip` do người dùng cung cấp được giải nén trong `/tmp` để kiểm chứng chỉ đọc. Kết quả: 12.570 âm Hán Việt, 731.968 VietPhrase sau khi phủ file bổ sung, 2.688 tên, 12.058 đại từ và 297 luật. Loader báo 163 cảnh báo cấu trúc, chủ yếu là khóa lặp/dòng không tương thích và `PanguDict/Dict.dct` được khai báo nhưng không có trong ZIP. Bốn kết quả đã xác nhận đều khớp:

| Nguyên văn | VP đơn ý |
|---|---|
| `片尾曲` | `Ending song` |
| `不知道是不是` | `Không biết có phải là` |
| `一个人形` | `Một bóng người` |
| `房卒` | `Phòng Tốt` |

Bài đo tĩnh với cùng Data trên chuỗi 8.400 UTF-16 code units tạo 4.000 span alignment, tổng source span đúng 8.400 và mất khoảng 28 ms sau khi Data đã nạp; thời gian nạp thay đổi theo cache ổ đĩa trong container. Đây không phải benchmark Windows và không đánh giá độ đúng ngữ nghĩa ngoài bốn mẫu trên.

Kiểm chứng alignment bằng Data thật cho `林悦` tạo Hán Việt `Lâm duyệt`, VP `Rừng duyệt` và chiếu VP `0+10` về source `0+2`, Hán Việt `0+9`. `王秀兰` tạo Hán Việt `Vương tú lan`; chọn toàn bộ Hán Việt chiếu về source `0+3` và toàn bộ VP `0+14`. Với `林悦` lặp ba lần, chọn lần thứ ba chiếu về source `4+2`, không nhảy về lần đầu. VP của `王秀兰` trong chính Data ZIP hiện là `Vương show lan` vì `VietPhrase2` có `秀=show`; không sửa từ điển hoặc thuật toán trong bug fix này.

Nguyên nhân lỗi hiển thị đã được xác định trong `MainWindow.Traditional.cs`: handler cũ chỉ gọi `TextBox.Select()` trên hai control không focus. Mã không có lớp visual highlight riêng, nên kết quả phụ thuộc inactive-selection rendering của theme WPF và hai vùng đích có thể hoàn toàn không được vẽ. Bản sửa dùng `TextBoxRangeHighlightAdorner` cho hai ô đích, không đổi focus/nội dung/Undo; selection chuẩn chỉ còn ở ô người dùng đang thao tác.

Lệnh chính đã chạy từ thư mục module:

```text
dotnet restore QuickTranslator.AiEditor.slnx --locked-mode
dotnet build QuickTranslator.AiEditor.slnx -c Release --no-restore
dotnet test tests/QuickTranslator.AiEditor.Tests/QuickTranslator.AiEditor.Tests.csproj -c Release --no-build --no-restore
dotnet publish src/QuickTranslator.AiEditor.Wpf/QuickTranslator.AiEditor.Wpf.csproj -c Release -r win-x64 --self-contained false --no-restore
```

Đường dẫn artifact Project Workspace và checksum được ghi ở mục “Lần chạy cuối”. Đây là bản phụ thuộc runtime: máy chạy cần .NET 10 Desktop Runtime x64 và Ollama/model local.

## Chưa thực thi — không tính là đạt

- Chưa mở EXE/giao diện WPF trên Windows; chưa thử DPI/font/IME/clipboard và chặn cửa sổ thứ hai thực tế.
- Chưa thao tác Project Library/sidebar/menu bằng WPF thật trên Windows, chưa kiểm tra Windows Recycle Bin, mở thư mục bằng Explorer hoặc double-click `.qtproj`. Core Project, ZIP và migration đã chạy bằng file thật trong thư mục tạm Linux; XAML/C# đã build.
- Chưa thao tác context menu thật bằng chuột trên WPF, chưa đo mật độ ở Full HD/2K hoặc nhiều kích thước cửa sổ. Core policy, dữ liệu và XAML/C# đã build/test; các thao tác trực quan nằm trong checklist Windows.
- Chưa thao tác cửa sổ Batch Replace thật trên Windows: Ctrl+H, chọn dòng DataGrid, lọc/sắp xếp, double-click điều hướng, confirmation bản bảo vệ, progress nhiều chương và nút Reader/Quality cần chạy theo checklist. Core scanner/preview/apply/undo/backup và XAML/C# đã build/test trong Linux.
- Chưa mở Editor bốn ô trên Windows để xác nhận Adorner bằng mắt với kéo chuột, double-click, Shift+mũi tên, theme/DPI và cuộn thật. Alignment Core đã có automated tests; mã vòng đời highlight và XAML/C# đã build trong Linux. Vì thiếu Windows/Wine nên chưa tuyên bố bug giao diện đã được xác nhận hoàn toàn.
- Chưa kết nối Ollama thật, chưa xác minh template/prompt với bản TranslateGemma đã tải trên máy người dùng.
- Chưa đo GPU/VRAM/tốc độ, chưa đánh giá chất lượng hay đầy đủ ngữ nghĩa bản dịch.
- Chưa đánh giá Character Pronoun Manager trên chương truyện thật. 24 test dùng câu nhân tạo, gồm cả trường hợp cố ý gây nhầm; chúng chứng minh quy tắc bảo vệ dữ liệu chứ không chứng minh phân giải đúng mọi đồng tham chiếu văn học.
- Chưa thử mất điện/đóng cưỡng bức/ổ đĩa đầy hoặc thao tác replace/backup trên Windows; Core đã thử checkpoint, backup nhiều chương, partial failure và lỗi ghi bằng file Linux/mock.
- Chưa chạy quy trình thực khi ngắt Internet. Mã transport/model guard đã được kiểm tra bằng mock; cần kiểm chứng cả tiến trình Ollama riêng.
- Chưa publish hoặc chạy bản kèm runtime (`--self-contained true`); README có hướng dẫn, không coi là kết quả đã thử. Bản framework-dependent đã publish nhưng chưa thể mở WPF trong Linux.
- Chỉ bốn mẫu VP/Names nêu trên đã được đối chiếu với kết quả QT 2020 do người dùng xác nhận. Chưa có corpus vàng cho Hán Việt, luật nhân, thuật toán ưu tiên phức tạp hay toàn bộ dấu câu; không tuyên bố tương thích hoàn toàn QT 2020. Chưa xác nhận quyền phân phối bộ Data, mã nguồn gốc hoặc model nên artifact không chứa các thành phần này.

Build WPF trên Linux xác minh C#/XAML và đầu ra, không thay cho kiểm thử thao tác Windows. Cảnh báo chất lượng chỉ là quy tắc gợi ý. Dùng [WINDOWS-CHECKLIST.vi.md](WINDOWS-CHECKLIST.vi.md) để hoàn tất các phần còn thiếu.

## Lần chạy cuối

Ngày 12/10/2026, SDK 10.0.401:

- `dotnet test`: **193 đạt, 0 lỗi, 0 bỏ qua**; TRX tại `/workspace/quicktranslator-audit/ai-editor-test-results/project-workspace-final.trx`.
- `dotnet build QuickTranslator.AiEditor.slnx -c Release --no-restore`: **0 lỗi, 0 cảnh báo**.
- `dotnet publish` framework-dependent cho `win-x64`: thành công tại `/workspace/quicktranslator-audit/ai-editor-batch-cleaner-win-x64`.
- `QuickTranslator.AiEditor.exe` được nhận diện là **PE32+ GUI x86-64**. Gói ZIP đã qua `unzip -t`, không có lỗi.
- Gói chạy thử: `/workspace/quicktranslator-audit/QuickTranslator.AiEditor-ProjectWorkspace-win-x64.zip`.
- SHA-256 của ZIP được ghi trong câu trả lời bàn giao bên ngoài gói (không tự nhúng checksum của chính ZIP); dùng `sha256sum` hoặc `Get-FileHash` để kiểm tra.
- Gói gồm README tiếng Việt và bảng kiểm Windows bên cạnh EXE/DLL.
- Git không có diff ở QuickTranslator, TranslatorEngine hoặc WinFormsUI gốc; toàn bộ mã mới nằm trong `QuickTranslator.AiEditor/`. Không tìm thấy tệp chương `.qtaie.json` hay hồ sơ người dùng `.characters.json` trong module.
