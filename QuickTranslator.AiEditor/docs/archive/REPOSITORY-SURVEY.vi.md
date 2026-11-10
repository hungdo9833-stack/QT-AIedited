# Khảo sát repository và phạm vi Phase 1

Ngày: 11/10/2026. Repository `hungdo9833-stack/QuickTranslator`, ref được yêu cầu `ngoctay`, commit khảo sát `9f0ec5c81d771ec763b4fcb25ddcd8a08680a458`. Checkout Cloud dùng nhánh nội bộ `work` tại cùng commit. Các kết quả dưới đây phân biệt đọc mã và thực thi; không suy ra từ việc có một menu rằng chức năng đó chạy được.

## Kiến trúc hiện có

| Dự án | Vai trò | Công nghệ/phụ thuộc |
|---|---|---|
| QuickTranslator | Cửa sổ và quy trình dịch, soạn bản Việt, mở/lưu, cấu hình | C#, WinForms, .NET Framework 4.5; tham chiếu TranslatorEngine và WinFormsUI |
| TranslatorEngine | Hán Việt, VietPhrase, Names, bảng nghĩa, luật nhân và dữ liệu từ điển | C#, .NET Framework 4.5; không tham chiếu UI |
| WinFormsUI | DockPanel Suite: kéo/ghim/ẩn/tách các ô, lưu bố cục | WinForms, .NET Framework 2.0, System.Drawing và API Windows |

179 tệp được Git quản lý trước khi thêm module, 119 tệp C#, khoảng 38.125 dòng. Repository không có bộ test/CI/hướng dẫn build hay bộ từ điển vận hành trong commit khảo sát.

Luồng chính: **MainForm nhận văn bản Trung → gọi TranslatorEngine → nhận Hán Việt/VietPhrase và vị trí các cụm → DocumentPanel hiển thị → chọn cụm để tra nghĩa/chèn vào bản Việt**. WinFormsUI lo bố cục, không dịch văn bản.

| Tệp/nhóm quan trọng | Mối liên hệ |
|---|---|
| `Quick Source.sln`, ba `.csproj` | Tổ chức build cũ; ứng dụng/engine có cấu hình x86 |
| `QuickTranslator/QuickTranslator/MainForm.cs` | Điều phối sáu ô, gọi engine, chọn nghĩa, đồng bộ vùng chọn, mở/lưu và phím tắt |
| `DocumentPanel.cs`, `ScrollingRichTextBox.cs` | RichTextBox/RTF, vùng chọn, menu chuột phải, gõ tắt; cuộn dùng `user32.dll` |
| `Configuration.cs`, `ConfigurationPanel.cs`, `Shortcuts.cs` | Cấu hình màu/font/thuật toán/phím tắt; cấu hình cũ dùng BinaryFormatter |
| `UpdateVietPhraseForm.cs`, `UpdatePhienAmForm.cs` | Sửa VietPhrase/Names/phiên âm và lịch sử từ điển |
| `FindAndReplaceForm.cs`, `ExportToWordForm.cs`, `PostTTVForm.cs` | Tìm/thay thế, xuất Word và tạo nội dung diễn đàn |
| `TranslatorEngine/TranslatorEngine/TranslatorEngine.cs` | Thuật toán, trạng thái static, tra/nạp/lưu từ điển và lịch sử |
| `CharRange.cs` | Vị trí bắt đầu/độ dài phục vụ ánh xạ vùng chọn |
| `DictionaryConfigurationHelper.cs` | Tìm config và dữ liệu cạnh DLL; kiểm tra tệp |
| `CharsetDetector.cs`, `chardet/`, `HtmlParser.cs` | Nhận dạng bảng mã và xử lý HTML |
| `WinFormsUI/Docking/` | Thư viện bố trí ô và gọi API Windows |

Engine tách được khỏi UI nhưng dùng dữ liệu static, tự nạp/ghi từ điển, tìm tệp theo vị trí DLL. Muốn gọi từ giao diện mới cần lớp điều phối tuần tự, kiểm tra dữ liệu và kết quả đối chứng; không gọi đồng thời tùy ý.

## Kết quả kiểm tra thực tế đã có

Các phép thử dự án cũ/engine hiện đại được thực hiện trong đợt khảo sát trước, ở dự án/thư mục bên ngoài checkout. Không sửa nguồn gốc để có những kết quả này.

| Đã thực thi | Kết quả | Giới hạn |
|---|---|---|
| Rebuild solution gốc Debug và Release trên Mono 6.12 / xbuild 14 / Linux | Mỗi cấu hình 0 lỗi, 95 cảnh báo | Chưa build bằng Windows/Visual Studio |
| Engine gốc với từ điển nhân tạo | 7 kiểm tra mẫu đạt | Không phải bộ dữ liệu thật hoặc kiểm thử đầy đủ thuật toán |
| MainForm trên Mono và màn hình ảo Xvfb | Hiển thị kết quả dịch mẫu | Có GTK fallback/X11 BadMatch; không phải kiểm tra UI Windows |
| Năm cửa sổ phụ | 5/5 lỗi `MissingManifestResourceException` | FindAndReplace, ExportToWord, UpdateVietPhrase, UpdatePhienAm, PostTTV |
| Biên dịch nguyên 35 tệp nguồn engine trong dự án ngoài repo, đích .NET 10 | 0 lỗi, 4 cảnh báo; cùng 7 kiểm tra mẫu đạt | Không chứng minh dùng trực tiếp DLL Framework cũ hoặc tương đương trên từ điển thật |
| Bảng mã GB2312 trên .NET 10 | Cần đăng ký `CodePagesEncodingProvider`; vòng đọc/ghi mẫu đạt sau khi đăng ký | Chưa kiểm tra mọi encoding cũ |

Phụ thuộc thiếu/cũ: `MonoDevelop.Deployment.Linux` không tìm thấy nhưng mã compile không dùng; tài nguyên `.resources` ứng dụng không được nhúng; thiếu `Dictionaries.config`, 12 nguồn từ điển, `Shortcuts.txt` và các config phụ. Nguồn DockPanel ghi assembly 2.9 nhưng ghi chú tham chiếu ghi bản khác. QuickConverter/QuickWebBrowser không có trong solution và lệnh gọi đã bị comment. BinaryFormatter không phù hợp .NET hiện đại; WinForms docking/RTF/SendMessage cần lớp UI mới.

**Đọc mã, chưa thực thi đầy đủ:** lệnh nhập/xuất Word và gọi web đã vô hiệu hóa; đường mở `.qt` qua menu và đường kéo thả không giống nhau; `ToSimplified` trả nguyên chuỗi; kiểm tra tồn tại Baike là stub. Build thành công không loại bỏ các thiếu hụt này.

## Chức năng cũ cần bảo tồn trong dự án Modern về sau

Đây là danh mục đối chiếu, không phải các chức năng đã triển khai trong AI Editor Phase 1:

| Nhóm | Chi tiết cần giữ |
|---|---|
| Sáu ô làm việc | Trung, Hán Việt, VietPhrase nhiều nghĩa, VietPhrase một nghĩa, Nghĩa, Việt |
| Hán Việt | Phiên âm, dấu câu/đoạn và vị trí tương ứng với nguyên văn |
| VietPhrase | Nhiều nghĩa và một nghĩa; tùy chọn thuật toán; ngoặc/ký hiệu nghĩa; ưu tiên Names và cụm dài; luật nhân/Pronouns |
| Bảng nghĩa | Tra cụm đang chọn; các nguồn từ điển, tên, phiên âm; chèn/chọn nghĩa đúng vị trí |
| Biên tập bản Việt | Sao chép/chèn từ Hán Việt/VietPhrase, chọn nghĩa khác, đảo cụm, gõ tắt, undo và định dạng RTF |
| Đồng bộ văn bản | Click/bôi đen/tô đỏ/định vị cuộn giữa các ô; giữ ánh xạ `CharRange` khi xuống dòng và Unicode |
| Quản lý từ điển | Thêm/sửa/xóa VietPhrase, Names chính/phụ, phiên âm; lịch sử, sắp xếp/nén và nạp lại |
| Mở/lưu | Văn bản/HTML/`.qt`, lưu bản Việt và định dạng, kéo thả, cảnh báo chưa lưu; đối chiếu riêng menu/kéo thả |
| Điều hướng và tìm | Chương/tệp trước-sau, tìm/thay thế, đánh dấu, focus ô và vị trí làm việc |
| Phím tắt/gõ tắt | Lệnh chọn nghĩa/chèn/chuyển focus/đánh dấu/dịch; bảng gõ tắt, xung đột phím và tùy biến |
| Cấu hình | Font/màu, thuật toán, hành vi chọn/chèn, phím tắt, đường dẫn và bố cục docking qua lần khởi động |
| Chức năng bổ sung | Word, nội dung BBCode Tàng Thư Viện, tra web, phồn thể/giản thể; phần thiếu phải đối chiếu bản EXE trước khi cam kết khôi phục |

## Đề xuất module AI Editor

Tạo ứng dụng độc lập **WPF trên .NET 10 LTS**, solution riêng trong `QuickTranslator.AiEditor`. Core xử lý chuỗi/dữ liệu/HTTP/lưu/duyệt; UI chỉ trình bày và nhận thao tác; Tests dùng HTTP giả lập. Phase 1 không tham chiếu, sao chép hoặc sửa TranslatorEngine/WinFormsUI. Cách này thử được quy trình AI mà không phụ thuộc bộ từ điển đang thiếu hoặc thay thuật toán cũ.

Nếu bổ sung engine sau này, đặt nó sau một adapter riêng và kiểm tra cùng từ điển/kết quả/vị trí đối chứng. Giữ Hán Việt/VietPhrase là công cụ tra cứu hỗ trợ, AI là một nguồn bản nháp; hai nguồn không tự ghi đè phần người dùng sửa.

| Thứ tự | Ưu tiên | Nhiệm vụ nhỏ | Điều kiện hoàn thành |
|---|---|---|---|
| 1 | P0 | Prototype Core/WPF/test/lưu offline | Quy trình 6 bước chạy; không auto-next; bản sửa không bị AI ghi đè — đã có mã, còn kiểm tra Windows thật |
| 2 | P0 | Thử Windows/Ollama và mất kết nối/lưu lỗi | Hoàn thành bảng thử, ghi rõ lỗi và thời gian/tokens/VRAM thực tế |
| 3 | P0 | Đối chứng bản QuickTranslator đang dùng | Hash EXE/DLL/config trên bản sao, đối chiếu chức năng và từ điển; không đưa dữ liệu riêng lên repo |
| 4 | P0 | Xác minh giấy phép mã và dữ liệu | Có cơ sở tái sử dụng/phân phối từng thành phần |
| 5 | P1 | Kiểm tra prompt/model với mẫu được phép dùng | Người dịch đối chiếu đủ câu/tên/xưng hô; chọn cỡ đoạn/context theo phép đo |
| 6 | P1 | Từ điển/ngữ cảnh thực và quản lý phiên bản | Cắm vào interface đã có; theo dõi ngân sách token; không gửi toàn chương ngầm |
| 7 | P1 | Xuất toàn chương và xem lịch sử bản duyệt | Xuất đúng thứ tự, xác định đoạn chưa duyệt, giữ nguyên lịch sử |
| 8 | P1 | Adapter engine đối chứng nếu cần | Không đổi thuật toán; so sánh chữ và vùng chọn trên dữ liệu có quyền sử dụng |
| 9 | P2 | Modern hóa UI QuickTranslator theo từng nhóm | Sáu ô, nhiều nghĩa, từ điển, phím tắt/RTF được kiểm tra hồi quy riêng |
| 10 | P2 | Đóng gói/cập nhật và kiểm tra sử dụng dài ngày | Không mất tiến độ, có sao lưu/phục hồi và nghĩa vụ giấy phép rõ ràng |

## Rủi ro nguồn GitHub khác bản EXE trên máy

Rủi ro cao về tính đầy đủ: thiếu tài nguyên/dữ liệu, các lệnh bị comment, version thư viện không nhất quán, và không có bản phần mềm đang dùng để đối chiếu. Dấu vết nhãn IL/goto có thể đến từ việc tái tạo/chỉnh nguồn; không đủ để kết luận nguồn gốc hoặc quyền tác giả. Cùng thuật toán nhưng khác từ điển/config cũng có thể khác kết quả. Chưa kiểm tra EXE, DLL hoặc từ điển của bạn, nên chưa thể tuyên bố Modern tương đương bản đang dùng.

## Giấy phép

Không tìm thấy LICENSE/COPYING/NOTICE hoặc giấy phép chung trong commit gốc. Repository công khai không tự cấp quyền phân phối phần mềm hoặc dữ liệu.

- 27 tệp `chardet` có header lựa chọn MPL 1.1 / GPL 2.0 / LGPL 2.1; giữ thông báo và xác định nghĩa vụ theo nhánh giấy phép áp dụng. Không suy ra giấy phép của engine chính từ những header này.
- WinFormsUI có thông báo bản quyền Weifen Luo/người đóng góp nhưng bản copy không kèm toàn văn giấy phép; cần đối chiếu đúng phiên bản upstream.
- Mã ứng dụng/engine, tài nguyên và các bộ từ điển cần xác minh riêng. Model TranslateGemma/Ollama/.NET và thư viện mới cũng có điều khoản riêng nếu đóng gói phân phối.
- Prototype hiện không kèm nguồn gốc đã sửa, dữ liệu từ điển, model hoặc chương truyện. Chưa gán giấy phép mới cho toàn repository.

Những nhận định này là kiểm tra thông tin có trong repo, không phải xác nhận đã có quyền phát hành phần QuickTranslator gốc.
