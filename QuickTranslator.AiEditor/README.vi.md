# QuickTranslator AI Editor — AI offline, Reader Mode và Editor bốn ô

Kết quả ổn định dữ liệu M0/F01–F04 trên Windows, cách chạy regression tests và lưu ý tương thích tài nguyên Project: [DATA-SAFETY-PHASE1.vi.md](docs/DATA-SAFETY-PHASE1.vi.md).

Ứng dụng độc lập để dịch tiểu thuyết Trung–Việt bằng **Ollama trên máy Windows**. Người dùng nhập một chương, bấm **Dịch toàn chương** một lần; ứng dụng chia đoạn, dịch tuần tự từng đoạn, lưu sau mỗi đoạn và ghép bản Việt theo đúng thứ tự. Người dùng vẫn có thể sửa, duyệt hoặc dịch lại riêng từng đoạn.

Mã mới nằm hoàn toàn trong thư mục này, dùng **C# / .NET 10 LTS / WPF**. Không sửa QuickTranslator, TranslatorEngine hay WinFormsUI gốc. Dịch AI vẫn chạy khi không có Data QuickTranslator; hai ô Hán Việt và VP đơn ý cần người dùng chọn bộ Data riêng trong Settings.

## Quản lý dự án và nhiều chương

AI Editor lưu một bộ truyện trong một thư mục riêng có manifest `*.qtproj`. Các đường dẫn chương, preset, Reader, hồ sơ nhân vật, glossary và tài nguyên trong manifest đều là đường dẫn tương đối, nên có thể đóng ứng dụng rồi di chuyển **cả thư mục dự án** sang ổ khác. Không di chuyển riêng từng tệp khi ứng dụng đang mở.

1. Chọn **Tệp → Dự án mới**, nhập tên truyện và chọn thư mục cha. Ứng dụng tạo thư mục dự án gồm `Chapters`, `Characters`, `Glossary`, `Settings`, `Resources`, `Backups`, `History` và `Cache`.
2. Dùng nút **+ TXT** ở danh sách bên trái để thêm một hoặc nhiều chương UTF-8. Tìm kiếm, lọc trạng thái và sắp xếp chương ngay phía trên danh sách. Nút Trước/Sau chuyển chương theo số chương.
3. Khi đổi dự án/chương, ứng dụng chờ request Ollama hiện tại kết thúc, tạm dừng queue, lưu checkpoint, bản sửa, preset và manifest rồi mới chuyển. Queue chỉ thuộc chương và dự án đang mở.
4. **Dự án → Sao lưu ZIP** tạo tệp mới, đọc kiểm chứng toàn bộ ZIP và không đưa `Cache` hay các backup cũ vào. **Khôi phục bản sao** luôn dùng một thư mục mới; không ghi đè thư mục có dữ liệu.
5. **Tệp → Nhập tiến độ cũ** quét thư mục Chapters cũ, cho chọn chương, tạo ZIP nguồn trước khi nhập và xác minh nguyên văn/lịch sử ở đích. Nguồn cũ không bị sửa hoặc xóa.
6. **Bỏ khỏi danh sách** trong Thư viện chỉ xóa lối tắt recent. Lệnh xóa dự án riêng chuyển toàn bộ thư mục vào Thùng rác Windows sau khi người dùng xác nhận; không áp dụng cho dự án đang mở.
7. Chọn **Workspace → Xuất bản** để ghép toàn bộ chương đang hoạt động theo thứ tự mục lục thành một file TXT UTF-8 hoặc PDF A4. Chương đã xóa mềm không được xuất; chương chưa hoàn tất/cần kiểm tra chỉ được đưa vào file sau khi người dùng xác nhận xuất bản nháp.

Có thể mở dự án bằng `QuickTranslator.AiEditor.exe "D:\Truyen\TenTruyen.qtproj"`. Ứng dụng không ghi Windows Registry. Trình **Xuất bản** hiện chỉ ghép toàn bộ chương vào một file; chưa có dàn trang bìa, mục lục PDF hay xuất nhiều tập.

## 1. Chuẩn bị trên Windows

1. Dùng Windows x64 được .NET 10 hỗ trợ; khuyến nghị Windows 11.
2. Cài **.NET 10 SDK x64** từ [Microsoft](https://dotnet.microsoft.com/download/dotnet/10.0). SDK dùng để biên dịch; nếu chỉ chạy bản đã đóng gói, xem mục 7.
3. Ollama và `translategemma:12b` của bạn đã có sẵn. Mở Ollama, mở PowerShell rồi chạy:

   ```powershell
   dotnet --version
   ollama list
   ```

   Dòng đầu phải bắt đầu bằng `10.`; danh sách model phải có đúng `translategemma:12b` đã tải đầy đủ.
4. Tắt tính năng cloud của Ollama. Trong PowerShell:

   ```powershell
   [Environment]::SetEnvironmentVariable('OLLAMA_NO_CLOUD', '1', 'User')
   $env:OLLAMA_NO_CLOUD = '1'
   ```

   Thoát hẳn Ollama ở khay hệ thống, mở lại để nhận biến môi trường mới. Nếu dùng `ollama serve`, mở cửa sổ PowerShell mới và chạy lệnh đó. Không chạy thêm server nếu Ollama đang hoạt động. Các bản Ollama mới hỗ trợ biến này; kiểm tra/cập nhật phiên bản nếu cần. Nguồn: [cấu hình Ollama](https://github.com/ollama/ollama/blob/main/envconfig/config.go).
5. Kiểm tra API bằng lệnh chỉ đọc danh sách model, không gửi chương:

   ```powershell
   Invoke-RestMethod http://127.0.0.1:11434/api/tags
   ```

AI Editor dùng `http://127.0.0.1:11434/api/chat`, kiểm tra `/api/tags` trước mỗi lần dịch. Model không có dữ liệu cục bộ, có `remote_host`/`remote_model`, hoặc tên chứa `cloud` bị từ chối trước khi gửi nguyên văn. HTTP không dùng proxy, không theo chuyển hướng và không có dịch vụ cloud thay thế. Ollama là chương trình riêng: chỉ dùng server/model bạn tin cậy. Sau khi cài đủ phần mềm, hãy ngắt Internet để kiểm chứng quy trình offline bằng bảng thử ở mục 9.

Máy RTX 3060 12GB, i5-12400F, RAM 32GB có thể dùng làm máy thử mục tiêu. Tốc độ và mức dùng VRAM của model 12B còn phụ thuộc bản lượng tử hóa, context và phiên bản Ollama; chưa đo trên phần cứng của bạn. GPU không phải điều kiện để chạy unit test.

## 2. Biên dịch và mở ứng dụng

Mở thư mục `QuickTranslator.AiEditor` trong File Explorer. Bấm thanh địa chỉ, gõ `powershell`, nhấn Enter. Đây là thư mục chứa `QuickTranslator.AiEditor.slnx`, không phải solution cũ `Quick Source.sln`.

Chạy từng lệnh; nếu lệnh báo lỗi thì dừng và đọc thông báo:

```powershell
dotnet restore QuickTranslator.AiEditor.slnx --locked-mode
dotnet build QuickTranslator.AiEditor.slnx -c Release --no-restore
dotnet test tests/QuickTranslator.AiEditor.Tests/QuickTranslator.AiEditor.Tests.csproj -c Release --no-build
dotnet run --project src/QuickTranslator.AiEditor.Wpf/QuickTranslator.AiEditor.Wpf.csproj -c Release --no-build
```

Lần restore đầu cần Internet để tải bộ tham chiếu WPF và thư viện kiểm thử, hoặc cần một bộ cache NuGet đã chuẩn bị trước. **Ứng dụng đã biên dịch không cần Internet để dịch.** Không gửi chương hay từ điển trong quá trình restore/build/test; các test chỉ dùng dữ liệu nhân tạo.

## 3. Dịch một chương

1. Ở tab **Thiết lập dịch**, chọn nhóm **AI & hiệu năng**, kiểm tra model, temperature `0.1`, context `4096`, đầu ra tối đa `1536` token và timeout `300` giây. Nút **Kiểm tra kết nối** chỉ đọc danh sách model local, không gửi nội dung chương. Các nhóm Prompt, từ điển, nhân vật và chất lượng nằm ở sidebar bên trái.
2. Về tab **Bản dịch chương**, đặt tiêu đề rồi bấm **Dán chương** hoặc **Nhập TXT**. Tệp nhập phải là UTF-8; GB2312/Big5 cần đổi sang UTF-8 trước. Cỡ đoạn mặc định là **300 / 400 / 500** chữ Hán.
3. Bấm **Dịch toàn chương**. Ứng dụng tự chia nếu cần rồi gửi từng đoạn theo thứ tự, tối đa một request Ollama tại một thời điểm. Kết quả mới có trạng thái chờ duyệt nhưng hàng đợi vẫn tiếp tục.
4. **Tạm dừng** chờ request hiện tại hoàn tất và lưu an toàn rồi dừng. **Tiếp tục** bỏ qua những đoạn đã hoàn tất hoặc được người dùng bảo vệ. **Hủy hàng đợi** hủy request hiện tại; có thể tiếp tục phần còn lại sau đó. Đoạn lỗi không chặn đoạn kế tiếp; dùng **Dịch lại các đoạn lỗi** khi đã sửa nguyên nhân.
5. Bản Việt ở cột phải cập nhật theo từng đoạn và mỗi khối vẫn gắn với ID đoạn. Có thể sửa ngay; bản sửa được tự lưu theo nhịp 500 ms và hàng đợi không ghi đè lên đoạn đã sửa hoặc đã duyệt.
6. Trong tab **Phân đoạn & chỉnh sửa**, chọn một đoạn để dùng Editor 2×2: **Trung | Hán Việt / VP đơn ý | AI dịch**. Chọn chữ/cụm trong một trong ba ô đầu sẽ tô vùng tương ứng ở hai ô còn lại bằng lớp highlight xanh độc lập với focus, kể cả từ lặp. Ô đang thao tác giữ selection chuẩn của Windows; ứng dụng không chuyển focus sang ô khác. Ô AI độc lập và vẫn có dịch lại, nháp, duyệt, prompt cùng lịch sử. Có thể kéo splitter, ẩn hoặc phóng lớn từng ô. Việc tách/gộp/đổi ranh giới vẫn yêu cầu tạm dừng và bảo vệ bản sửa như trước.
7. Bấm **Xuất TXT UTF-8** để tạo bản dịch theo đúng thứ tự. Nếu còn đoạn lỗi hoặc chưa có bản dịch, ứng dụng cảnh báo và không gọi chương là hoàn tất; người dùng vẫn có thể chủ động xuất bản hiện có.
8. Bấm **Lưu tiến độ** trước khi nghỉ. Với dự án mới, lần sau mở tệp `.qtproj` hoặc chọn từ **Dự án gần đây**; checkpoint hàng đợi, đoạn, bản sửa, lịch sử, preset và cấu hình được khôi phục. **Mở tiến độ cũ** dành cho tệp `.qtaie.json` legacy. Ứng dụng không tự gửi dữ liệu sau khi mở lại; bấm **Tiếp tục** khi đã kiểm tra.

Khi chưa có bản sửa, ô nháp hiển thị bản AI gần nhất để bạn bắt đầu chỉnh. Nút **Xem prompt / cấu hình lần dịch** cho xem snapshot chính xác của từng lần gọi. Chuyển preset về sau không thay lịch sử cũ.

### Chế độ đọc

Tab **Bản dịch chương** mở ở **Chế độ đọc**. Reader nối trực tiếp nội dung bản dịch của các segment theo thứ tự, không tự thêm dấu cách hoặc xuống dòng ở ranh giới AI. Nếu còn đoạn trống, lỗi hoặc cần kiểm tra, dòng cảnh báo bên dưới cho biết bản đọc chưa hoàn chỉnh.

- **Aa Kiểu đọc** có ba preset mật độ **Gọn / Tiêu chuẩn / Thoáng**. Có thể chỉnh riêng font đã cài trên Windows, cỡ 14–30, giãn dòng, khoảng cách đoạn, lề trang, chiều rộng, căn đều và thụt đầu dòng. Thay đổi hiển thị ngay và được lưu cục bộ.
- **Thu gọn dòng trống dư thừa khi hiển thị** bỏ các paragraph rỗng khỏi `FlowDocument` nhưng không xóa ký tự xuống dòng trong bản dịch đã lưu. Tắt tùy chọn để xem đúng mọi dòng trống gốc.
- Dark Mode áp dụng cùng bảng màu cho trang đọc, thanh Reader, tìm kiếm, nút chế độ và popup Aa để tránh các mảng trắng quanh nội dung.
- `F11` bật/tắt toàn màn hình; **Tập trung** ẩn phần không cần thiết, `Esc` để thoát. `Page Up`, `Page Down`, `Space` cuộn trang; `Ctrl` + `+`/`-` đổi cỡ chữ.
- `Ctrl+F` tìm trong bản Việt. Nút **Đánh dấu** nhớ vị trí. Ứng dụng nhớ vị trí cuộn theo ID chương và không kéo về đầu khi một segment dịch xong.
- Chọn hoặc nhấp đúp nội dung rồi bấm **Mở đoạn trong biên tập** để chuyển đúng segment. Reader chỉ cho chọn/sao chép; chỉnh sửa vẫn thực hiện trên dữ liệu segment.
- Bấm **Biên tập** để xem nguyên văn, hàng đợi và các khối dịch. Thông tin kỹ thuật không nằm trong vùng đọc truyện.

### Thiết lập theo nhóm

- **AI & hiệu năng:** model local, kiểm tra kết nối, temperature, context, token, timeout và cỡ đoạn.
- **Data QuickTranslator:** chọn thư mục có `Dictionaries.ini`, nạp lại Data, xem số mục/cảnh báo và chọn quy tắc ưu tiên tên. Loader đọc UTF-8, không sửa từ điển và không hardcode đường dẫn máy. `VietPhrase2` ghi đè `VietPhrase`; `Names2` ghi đè `Names`; tên riêng được xét trước VietPhrase.
- **Prompt Presets:** chọn nhanh hoặc mở cửa sổ quản lý để tạo, nhân bản, đổi tên/mô tả, xóa preset tùy chỉnh, khôi phục prompt mặc định và kiểm tra biến.
- **Từ điển & ngữ cảnh:** nhập/xuất TXT, nhập JSON, đếm thuật ngữ/ký tự và lưu snapshot vào chương.
- **Nhân vật & xưng hô:** tìm hồ sơ, nhập/xuất và chỉnh quan hệ bằng bảng chọn nhân vật đích. Dữ liệu sai được giữ trong cửa sổ để sửa.
- **Chất lượng bản dịch:** quét tên/xưng hô, quét chữ Hán còn sót, bật làm sạch ánh xạ đã xác nhận và hoàn tác các đợt thay thế cục bộ.

Chuyển nhóm không xóa nội dung chưa lưu; thanh trạng thái nhắc khi thiết lập đang thay đổi. Khi hàng đợi chạy, cấu hình bị khóa để giữ snapshot nhất quán.

Nếu cần kiểm tra đồng bộ ba ô, bật **Ghi log chẩn đoán alignment** trong nhóm **Data QuickTranslator**. Log chỉ ghi ID segment, hash phiên bản nguyên văn và các khoảng `start+length`; không ghi nội dung truyện. Đường dẫn log được hiển thị ngay dưới tùy chọn, mặc định là `%LOCALAPPDATA%\QuickTranslatorAiEditor\Logs\alignment.log`.

### Menu chuột phải trên nguyên văn

Bôi đen chữ Trung trong **Nguyên văn chương** hoặc **Nguyên văn đoạn**, rồi nhấn chuột phải. Vùng chọn được giữ nguyên cả trên ô chỉ đọc. Menu có các thao tác:

- **Thêm vào hồ sơ nhân vật:** mở cửa sổ nhập nhanh với tên Trung đã điền sẵn. Người dùng phải nhập/xác nhận tên Hán Việt, chọn giới tính và đại từ; nút **Hồ sơ nâng cao** mở bí danh, quan hệ và ghi chú. Tên đã tồn tại sẽ mở hồ sơ cũ thay vì tạo trùng.
- **Thêm vào Glossary Trung–Việt:** nhập nghĩa trực tiếp; nếu mục đã có, ứng dụng hiển thị nghĩa cũ và hỏi trước khi thay. Chương chưa chia sẽ hỏi trước khi tạo tệp tiến độ để lưu dữ liệu bằng hệ thống chương hiện có.
- **Tìm các vị trí xuất hiện:** liệt kê theo đoạn và mở đúng vị trí trong nguyên văn.
- **Dịch riêng phần đã chọn:** gọi Ollama với preset/thiết lập đang dùng và chỉ hiển thị kết quả; không ghi vào segment, không ghi đè nháp hoặc bản đã duyệt.
- **Sao chép:** sao chép đúng vùng đã chọn.

Tên nhân vật bị giới hạn 16 ký tự hiển thị, phải có chữ Hán và không được chứa dấu kết câu/xuống dòng. Ứng dụng không tự đoán tên Hán Việt. Tùy chọn quét lại sau khi thêm nhân vật chỉ tạo báo cáo xem trước; mọi áp dụng lên chương cũ vẫn qua quy trình chuẩn hóa có lịch sử và hoàn tác.

### Quét chữ Hán và thay thế hàng loạt

1. Tạm dừng hàng đợi và chờ request hiện tại lưu xong. Trong **Biên tập**, bấm **Tìm & thay thế hàng loạt** hoặc nhấn `Ctrl+H`. Trong **Thiết lập dịch → Chất lượng bản dịch**, nút **Quét chữ Hán còn sót** mở cùng cửa sổ ở danh sách quét. Reader cũng hiện số cụm còn sót và nút nhảy sang công cụ này.
2. Chọn phạm vi **segment đang chọn**, **chương hiện tại** hoặc **tất cả chương trong bộ truyện**. Phạm vi bộ truyện chỉ nạp file có cùng Character Project ID; chương thuộc dự án khác không được ghi.
3. Nhập một cặp Tìm/Thay hoặc nhiều dòng dạng `王秀兰 => Vương Tú Lan`. Có thể bật ánh xạ từ Glossary/CharacterProfile. Gợi ý Names/Names2, VietPhrase hoặc Hán Việt truyền thống được ghi rõ là **chỉ gợi ý** và mặc định không được chọn tự động.
4. Bấm **Tìm tất cả / Xem trước**. Bảng cho biết chương, segment, câu trước/sau, phần đổi trong dấu `【…】`, nguồn quy tắc, xung đột và trạng thái bảo vệ. Khớp cụm dài hơn được xét trước; không dùng ranh giới từ tiếng Anh cho chữ Hán. Nếu hai ánh xạ xung đột, nhập cặp Tìm/Thay trực tiếp để chọn cách viết rồi tạo lại preview.
5. Các vị trí đã sửa tay/đã duyệt và gợi ý chưa xác nhận mặc định bỏ chọn. Nếu chủ động chọn bản được bảo vệ, ứng dụng hỏi lại, tạo revision mới và đưa đoạn về chờ duyệt. Bấm **Thay vị trí đang chọn** hoặc **Thay tất cả vị trí được chọn**; không có dữ liệu nào được ghi trước bước xác nhận này.
6. **Hoàn tác lần thay thế** trả lại toàn bộ đợt gần nhất khi nội dung chưa bị sửa tiếp. Nếu có sửa mới, ứng dụng từ chối ghi đè. Thao tác nhiều chương tạo bản sao trong `Chapters\BatchBackups` trước khi ghi và báo riêng chương đã ghi/chưa ghi.

Tab **Chữ Hán còn sót** nhận diện các vùng CJK Unicode mở rộng, gom ký tự Hán liền nhau, đếm số lần và phân loại tên/thuật ngữ đã xác nhận, xung đột, chưa biết hoặc đã đánh dấu giữ nguyên. Tìm kiếm và cột bảng dùng để lọc/sắp xếp; nhấp đúp mở đúng chương, segment và offset. Có thể xác nhận cụm đang chọn vào Glossary hoặc CharacterProfile. Ứng dụng không mặc định mọi cụm là tên và không tự đoán âm Hán Việt.

Trong **Chất lượng bản dịch**, bật **Tự áp dụng ánh xạ đã xác nhận sau dịch** nếu muốn. Sau mỗi response, ứng dụng lưu nguyên văn AI vào lịch sử, chỉ áp dụng ánh xạ đã xác nhận và không xung đột lên bản làm việc, đánh dấu phần còn lại cần kiểm tra rồi tiếp tục queue. Không có lượt gọi Ollama thứ hai. Bản nháp đã sửa/duyệt không bị bước này ghi đè.

### Quản lý nhân vật và xưng hô

1. Trong tab **Thiết lập dịch**, đặt tên bộ truyện/dự án rồi bấm **Thêm nhân vật**. Nhập tên Việt, tên Trung chỉ khi đã xác nhận, bí danh, giới tính, quan hệ, đại từ chính/phụ và các cách gọi cần tránh. Với Nam/Nữ, ô trống được điền gợi ý `hắn/hắn ta` hoặc `nàng/nàng ấy`; người dùng vẫn có thể chọn hoặc nhập cách khác.
2. Bấm **Lưu hồ sơ**. Mỗi bộ hồ sơ có Project ID riêng, được lưu cục bộ và chụp vào dữ liệu chương. **Xuất JSON UTF-8** để dùng lại cho chương khác của cùng truyện; chỉ **Nhập JSON** khi đúng bộ truyện.
3. Tên Trung–Việt đã xác nhận được thêm vào glossary của chương. Ứng dụng không suy đoán chữ Hán khi hồ sơ chỉ có tên phiên âm.
4. Nếu bật **Tự áp dụng quy tắc an toàn sau dịch**, bản AI gốc vẫn được lưu nguyên trong lịch sử. Bản chuẩn hóa là phiên bản riêng, không được tự đánh dấu Đã duyệt và không ghi đè nháp/bản đã duyệt.
5. Với chương cũ, bấm **Kiểm tra & chuẩn hóa xưng hô**. Báo cáo phân loại vị trí có thể áp dụng, mơ hồ, đã được bảo vệ hoặc hợp lệ. Chọn một dòng để mở đúng đoạn; bấm **Áp dụng đề xuất an toàn** hoặc **Hoàn tác chuẩn hóa**.

Bộ máy chỉ tự thay khi đại từ nằm trong lời kể, khớp danh sách cần tránh và tên/bí danh của đúng một hồ sơ xuất hiện rõ trong câu hiện tại hoặc tối đa hai câu trước. Các trường hợp sau được giữ nguyên hoặc yêu cầu xác nhận:

- Đại từ nằm trong lời thoại trực tiếp hoặc là danh xưng gia đình như `bà nội`, `anh trai`.
- Không có tên nhân vật làm tiền ngữ, hoặc có nhiều nhân vật cùng phù hợp.
- Đoạn đã được người dùng sửa hoặc duyệt.
- Cách xưng hô ngôi thứ nhất `tôi`, `ta` hoặc từ không nằm trong quy tắc của hồ sơ.

Bộ máy chạy bằng quy tắc cục bộ, không Find & Replace toàn chương và không gọi thêm model. Nó không hiểu đầy đủ mọi cấu trúc tiếng Việt; trường hợp nhập nhằng luôn cần người đọc quyết định. Chưa có tùy chọn gọi Ollama lần hai để phân giải câu khó.

## 4. Đoạn, trạng thái và cảnh báo

Chia đoạn ưu tiên xuống dòng/đoạn văn, kết thúc lời thoại và câu. Đoạn cuối có thể ngắn hơn mức tối thiểu. Câu/lời thoại dài vượt giới hạn phải chia nhỏ: không thể vừa giữ nguyên một câu quá dài vừa bảo đảm tối đa 500 chữ Hán. Quy tắc dự phòng cắt ở ranh giới ký tự hoàn chỉnh, không mất dấu câu, khoảng trắng hoặc xuống dòng.

Chuỗi Unicode dùng đơn vị ký tự hiển thị: không cắt đôi chữ ngoài BMP, dấu tổ hợp, emoji có liên kết. Văn bản có ít chữ Hán còn có giới hạn 2.000 đơn vị ký tự mặc định để tránh một đoạn Latin quá dài. Ghép `SourceText` theo thứ tự phải khôi phục chính xác chuỗi đầu vào, không phải khôi phục byte/BOM/bảng mã của tệp gốc. ID đoạn ổn định khi giữ cùng ID chương, nguyên văn và ranh giới; nhập lại thành chương mới sẽ có ID khác.

| Trạng thái | Ý nghĩa |
|---|---|
| Chưa dịch | Chưa có lần gọi AI và chưa có bản nháp |
| Đang dịch | Có đúng một yêu cầu dịch đang chờ |
| Chờ duyệt | Có kết quả AI hoặc nháp, cần người đọc quyết định |
| Cần kiểm tra | Đã có kết quả nhưng bộ kiểm tra phát hiện dấu hiệu thiếu, lặp hoặc bị cắt; không tự duyệt |
| Đã duyệt | Người dùng đã bấm Duyệt cho bản hiện tại |
| Có lỗi | Kết quả rỗng, API/lưu tệp lỗi, hủy hoặc lần dịch trước bị gián đoạn |

Kiểm tra tự động gợi ý kết quả rỗng, hết giới hạn token/chưa kết thúc, lặp từ, quá ngắn, mất nhiều đoạn văn hoặc còn nhiều chữ Hán. Ngoại trừ kết quả rỗng, đây là **cảnh báo theo dấu hiệu**, có thể báo nhầm hoặc bỏ sót. Không xác minh tính đúng của nghĩa, tên riêng hay việc dịch đủ từng câu. API không luôn báo việc cắt đầu vào khi context nhỏ. Prompt dài/từ điển mở rộng về sau cần kiểm tra ngân sách token riêng. Luôn đối chiếu nguyên văn trước khi duyệt.

Prompt mặc định yêu cầu dịch Chinese (zh) → Vietnamese (vi), giữ tên/lời thoại/đoạn, không tóm tắt hoặc giải thích; Ollama áp dụng chat template của model. Prompt Preset Manager cho phép tạo, sửa, nhân bản, chọn và xem trước prompt thực tế. Thuật ngữ, ghi chú và một phần ngữ cảnh đã duyệt ngắn có thể được thêm; ứng dụng không nhét toàn bộ các đoạn trước vào prompt. Chất lượng vẫn **chưa được kiểm chứng bằng TranslateGemma thật** trong Codex Cloud.

## 5. Dữ liệu và phục hồi

Mặc định lưu ở:

```text
%LOCALAPPDATA%\QuickTranslatorAiEditor\Chapters
```

Dán đường dẫn trên vào thanh địa chỉ File Explorer để mở thư mục. Mỗi chương có file `{ID}.qtaie.json` và bản dự phòng `.qtaie.json.bak`; thư viện preset và `CharacterProjects` nằm trong thư mục dữ liệu ứng dụng. Các file chứa nguyên văn, ranh giới/ID đoạn, bản AI, bản chuẩn hóa, bản sửa, hồ sơ nhân vật, lịch sử, checkpoint hàng đợi, preset, prompt/cấu hình và số token/thời gian API trả về.

Mỗi đợt Batch Replace lưu các vị trí, nội dung trước/sau, nguồn quy tắc và revision của segment trong file chương. Bản AI gốc trong `TranslationAttempt` không bị sửa. Với phạm vi nhiều chương, bản sao trước thao tác nằm tại `%LOCALAPPDATA%\QuickTranslatorAiEditor\Chapters\BatchBackups\{thời-gian}-{batch-id}` cùng manifest cục bộ; thư mục này có thể lớn dần và người dùng tự quản lý sau khi đã sao lưu an toàn.

Tùy chỉnh Reader, bookmark và vị trí đọc nằm riêng tại `%LOCALAPPDATA%\QuickTranslatorAiEditor\Reader\reader-settings.json`, có bản dự phòng. Tệp chương không đổi schema vì đây là sở thích hiển thị của máy.

Đường dẫn Data và lựa chọn engine truyền thống nằm tại `%LOCALAPPDATA%\QuickTranslatorAiEditor\Traditional\traditional-settings.json`. File này chỉ chứa đường dẫn/cấu hình, không chứa nội dung từ điển. Kết quả Hán Việt, VP và alignment được cache trong bộ nhớ theo hash nguyên văn cùng fingerprint Data; đổi nguyên văn hoặc Data sẽ tự tạo lại. Ứng dụng không ghi vào thư mục Data.

Khi lưu, ứng dụng ghi tệp tạm cùng thư mục, flush xuống đĩa rồi thay tệp chính; lưu trước đó thành `.bak`. Có checksum và kiểm tra nguyên văn/ID/thứ tự để phát hiện dữ liệu hỏng. Nếu file chính lỗi, ứng dụng thử bản dự phòng và báo rõ đang phục hồi. Nếu cả hai lỗi, ứng dụng báo lỗi và giữ các tệp. Không tự tạo dữ liệu rỗng đè lên tệp hỏng.

Ứng dụng lưu trước request, sau mỗi đoạn, lúc sửa/duyệt/đổi ranh giới, lúc chuyển chương và khi đóng bình thường. Nếu chưa lưu được lúc đóng, cửa sổ vẫn mở để giữ nháp. Một cửa sổ được cho phép trong mỗi phiên Windows để giảm nguy cơ hai ứng dụng ghi cùng dữ liệu. Không mở cùng chương bằng công cụ hoặc phiên Windows khác để sửa đồng thời.

Mất điện/tắt cưỡng bức vẫn có thể mất phần sửa kể từ lần lưu hoàn tất gần nhất; nhịp 500 ms và bản dự phòng giảm rủi ro, không bảo đảm chống mọi lỗi ổ đĩa. Trước thử nghiệm quan trọng, sao lưu cả thư mục dữ liệu sang một thư mục cục bộ khác. Không sửa tay JSON vì checksum sẽ không khớp. Dữ liệu là văn bản thuần, không mã hóa; ai có quyền đọc thư mục Windows có thể đọc được.

Không có chức năng tải dữ liệu lên GitHub hoặc telemetry. `.gitignore` của module loại trừ dữ liệu tiến độ, thư mục `data`, `artifacts`, `bin`, `obj`, kết quả test. Giữ chương truyện, từ điển và bản sao lưu bên ngoài repository; ignore không bảo vệ một tệp riêng tư được bạn cố ý đặt dưới tên khác và commit. Không commit bản dịch/dữ liệu riêng tư.

## 6. Khi gặp lỗi

| Thông báo/vấn đề | Cách xử lý |
|---|---|
| Không kết nối được localhost | Mở Ollama; thử `/api/tags`; kiểm tra `ollama serve` nếu chưa có server |
| Model chưa cài / model từ xa bị từ chối | Kiểm tra `ollama list`; chọn tên đầy đủ của model local đã tải, mặc định `translategemma:12b` |
| Quá thời gian chờ | Tăng timeout lên 600–900 giây; thử chương mới với đoạn nhỏ hơn; kiểm tra model có tràn VRAM sang RAM hay không |
| Bản dịch bị cắt | Tăng giới hạn đầu ra; nếu tổng đầu vào/đầu ra quá lớn, tăng context trong khả năng bộ nhớ hoặc giảm cỡ đoạn. Dịch lại thủ công |
| Không lưu được | Giữ ứng dụng mở, kiểm tra dung lượng/quyền ghi rồi bấm Lưu tiến độ; sao lưu dữ liệu hiện có |
| Không mở được cả chính và dự phòng | Giữ nguyên hai file và dùng bản sao lưu bên ngoài; không ghi đè chúng |
| Máy báo thiếu .NET khi mở EXE | Cài .NET 10 **Desktop Runtime** x64 cho bản phụ thuộc runtime, hoặc dùng bản đóng gói kèm runtime |

## 7. Tạo thư mục chạy riêng

Trong thư mục module, chọn một trong hai lệnh dưới. Các lệnh publish có thể cần Internet khi tải runtime pack lần đầu:

```powershell
# Nhẹ hơn; máy chạy cần .NET 10 Desktop Runtime x64.
dotnet publish src/QuickTranslator.AiEditor.Wpf/QuickTranslator.AiEditor.Wpf.csproj -c Release -r win-x64 --self-contained false -o artifacts/win-x64

# Kèm .NET runtime; kích thước lớn hơn, vẫn cần Ollama/model local đã cài.
dotnet publish src/QuickTranslator.AiEditor.Wpf/QuickTranslator.AiEditor.Wpf.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64-self-contained
```

Mở `QuickTranslator.AiEditor.exe` trong thư mục xuất. Giữ cả thư mục, không chỉ sao chép riêng EXE. Có thể chuyển thư mục này sang máy Windows offline sau khi chuẩn bị xong Ollama/model/runtime. Đường dẫn lưu chương vẫn là LocalAppData, không nằm trong thư mục ứng dụng.

## 8. Cấu trúc và kết quả đã kiểm chứng

```text
QuickTranslator.AiEditor/
  src/QuickTranslator.AiEditor.Core/   chia đoạn, hàng đợi, preset, nhân vật/xưng hô, dữ liệu, Ollama, duyệt/lưu
  src/QuickTranslator.AiEditor.Wpf/    cửa sổ Windows và thao tác người dùng
  tests/QuickTranslator.AiEditor.Tests/ unit test và HTTP giả lập
  docs/                              khảo sát repository, kiến trúc và kiểm tra Windows
```

Core không phụ thuộc WPF, GPU hoặc thuật toán TranslatorEngine. WPF chỉ kết nối nút bấm/ô văn bản với Core. Thư viện test: xUnit và Microsoft.NET.Test.Sdk, không được đưa vào ứng dụng chạy.

Kết quả chạy ngày 11/10/2026 trong Linux Codex Cloud dùng SDK 10.0.401 được ghi tại [VALIDATION.vi.md](docs/VALIDATION.vi.md). Build WPF từ Linux kiểm tra được C#/XAML và liên kết dự án; **không chứng minh cửa sổ đã chạy trên Windows**. Các API test là giả lập; không chứng minh tốc độ/chất lượng model thật.

## 9. Giới hạn còn lại

Thực hiện [bảng thử Windows](docs/WINDOWS-CHECKLIST.vi.md) trước khi dùng cho dữ liệu quan trọng. Chưa tích hợp Hán Việt/VietPhrase/bảng nghĩa hoặc UI quản lý từ điển của QuickTranslator gốc; chưa nhập `.qt`/RTF/Word, chưa có trình cài đặt và cơ chế cập nhật. Bộ nhớ ngữ cảnh hiện chỉ dùng thuật ngữ/ghi chú và một đoạn đã duyệt ngắn. Character Pronoun Manager xử lý các mẫu có tiền ngữ rõ ràng, chưa phải bộ phân tích đồng tham chiếu ngữ nghĩa hoàn chỉnh.

Khảo sát/phụ thuộc/giấy phép của phần mềm gốc và thứ tự phát triển đề xuất nằm tại [REPOSITORY-SURVEY.vi.md](docs/REPOSITORY-SURVEY.vi.md). Thiếu giấy phép chung của repository gốc vẫn là điểm cần xác minh trước khi tái sử dụng/phân phối phần đó. Module mới không sao chép mã hoặc tài nguyên của ba dự án gốc.
