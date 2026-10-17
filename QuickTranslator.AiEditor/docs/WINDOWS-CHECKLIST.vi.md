# Bảng chạy thử trên Windows

Chưa thực hiện những thao tác này trong Codex Cloud. Dùng chương giả lập hoặc nội dung bạn được phép dùng; giữ file/ảnh/log chứa truyện riêng tư trên máy của bạn. Ghi kết quả đạt/lỗi, phiên bản Windows/Ollama, model/quantization, cấu hình và thời gian thực.

## Project Manager và Workspace

- [ ] Tạo dự án mới, kiểm tra `.qtproj` và đủ thư mục; đóng/mở bằng menu, recent, double-click/đối số EXE. Không có mục Registry nào do ứng dụng tự tạo.
- [ ] Thêm nhiều TXT UTF-8, tìm/lọc/sắp xếp, đổi tên, nhân bản, Trước/Sau và xóa mềm. Kiểm tra tệp xóa nằm trong `History/Deleted`, không mất bản gốc ngoài ý muốn.
- [ ] Sửa và duyệt bản dịch, đổi chương/dự án khi queue đang chạy. Ứng dụng phải chờ request, pause, lưu checkpoint; không ghi bản của dự án A vào B và không tự dịch tiếp.
- [ ] Đổi preset, glossary, context, Reader và Character ở hai dự án; đóng/mở lại và xác nhận chúng tách biệt. Đường dẫn Data QuickTranslator vẫn dùng chung ở cấp ứng dụng.
- [ ] Di chuyển nguyên thư mục dự án khi ứng dụng đã đóng rồi định vị lại trong Thư viện. Chương, ảnh bìa, preset và hồ sơ vẫn mở vì manifest dùng đường dẫn tương đối.
- [ ] Tạo ZIP, mở thử bằng Explorer/7-Zip rồi khôi phục vào thư mục mới. Thử chọn thư mục đích có tệp: ứng dụng phải từ chối và giữ nguyên tệp đó.
- [ ] Nhập vài `.qtaie.json` legacy; kiểm tra ZIP trước nhập, nguyên văn, bản sửa, lịch sử, queue và preset. Gây một tệp hỏng trong bản sao thử: thao tác phải dừng/hoàn tác, nguồn không đổi.
- [ ] Thử mất điện/kill process trên dữ liệu thử trong lúc sửa và sau khi một segment vừa dịch xong. Mở lại `.qtproj`, kiểm tra checkpoint gần nhất, `.bak`, trạng thái Interrupted và không tự gửi Ollama.
- [ ] Dùng lệnh bỏ recent và xác nhận thư mục vẫn còn. Chỉ thử lệnh chuyển vào Thùng rác với dự án giả, sau khi đóng dự án; phục hồi từ Recycle Bin để kiểm tra.
- [ ] Kiểm tra Full HD/125% DPI: sidebar chương, menu, tab Reader và Editor bốn ô vẫn dùng được khi thu/phóng cửa sổ.

## Reader Mode và Settings V2

- [ ] Chuyển Reader/Biên tập nhiều lần; vùng đọc không hiện nhãn segment, trạng thái queue hoặc nguyên văn Trung.
- [ ] Dùng một câu bị chia giữa hai segment; Reader không sinh dấu cách/xuống dòng. Nhấp hai phía ranh giới phải mở đúng segment.
- [ ] Thử bốn theme, các font gợi ý và một font Windows khác; kiểm tra dấu tiếng Việt, cỡ 14/20/30, giãn dòng 1.3/1.7/2.2, căn đều và thụt đầu dòng.
- [ ] Cuộn chương dài, để queue cập nhật rồi xác nhận Reader không nhảy về đầu. Đóng/mở lại và kiểm tra vị trí cùng bookmark.
- [ ] Thử `F11`, `Esc`, Focus Mode, `Page Up`, `Page Down`, `Space`, `Ctrl+F`, `Ctrl++`, `Ctrl+-` trên Full HD, 2K và DPI 125%/150%.
- [ ] Chuyển qua sáu nhóm Settings khi có nội dung chưa lưu; ô nhập không bị xóa và trạng thái nhắc chưa lưu còn đúng.
- [ ] Thử toàn bộ thao tác trong dialog Prompt Preset, prompt sai biến và cảnh báo đóng khi chưa lưu.
- [ ] Nhập glossary TXT/JSON, xuất TXT, lưu/mở lại context; thử dòng sai và thuật ngữ Trung trùng nhau.
- [ ] Trong hồ sơ nhân vật, thêm quan hệ thiếu đích hoặc thiếu tên quan hệ; cửa sổ phải giữ dữ liệu để sửa và không crash.
- [ ] Thu/phóng, đổi kích thước cửa sổ/dialog; không mất nút lưu hoặc tràn ngoài vùng làm việc.
- [ ] So sánh Gọn/Tiêu chuẩn/Thoáng trên chương có xuống dòng sau mỗi câu và chương có nhiều dòng trống. Nội dung lưu/xuất không được thay đổi khi đổi mật độ hoặc bật/tắt thu gọn dòng trống.
- [ ] Trong Dark Mode, kiểm tra trang đọc, khoảng trống quanh trang, thanh Reader, ô tìm kiếm, nút Reader/Editor và popup Aa không còn mảng trắng chói; lặp lại ở cửa sổ thường, phóng to và Focus Mode.
- [ ] Trên cả ô Nguyên văn chương đang sửa và ô Nguyên văn đoạn chỉ đọc: bôi đen `张三`, nhấn chuột phải, xác nhận vùng chọn không mất và đủ năm lệnh.
- [ ] Thêm nhanh `张三 = Trương Tam`, chọn giới tính/đại từ rồi lưu. Thử lại cùng tên: phải mở hồ sơ cũ. Thử chọn cả câu dài: lệnh thêm nhân vật phải bị khóa hoặc báo hợp lệ, không crash.
- [ ] Thêm/cập nhật glossary từ vùng chọn, đóng/mở lại chương và kiểm tra dữ liệu. Thử chọn Không khi ứng dụng hỏi tạo tiến độ cho chương chưa chia.
- [ ] Tìm mọi vị trí và mở một kết quả ở đoạn khác. Dịch riêng vùng chọn bằng Ollama; kết quả chỉ ở cửa sổ xem, bản nháp/duyệt trước đó không đổi.

## Quy trình cơ bản

### Editor bốn ô và Data QuickTranslator

- [ ] Vào **Thiết lập dịch → Data QuickTranslator**, chọn đúng thư mục chứa `Dictionaries.ini`; xác nhận số mục và cảnh báo thiếu file tùy chọn. Đóng/mở lại ứng dụng và kiểm tra đường dẫn được khôi phục.
- [ ] Đặt Data ở thư mục chỉ đọc, nạp lại và dịch Hán Việt/VP; kiểm tra ứng dụng không tạo, sửa, đổi thời gian hay backup file nào trong Data.
- [ ] Đối chiếu với QT 2020: `片尾曲 → Ending song`, `不知道是不是 → Không biết có phải là`, `一个人形 → Một bóng người`, `房卒 → Phòng Tốt`. Các ví dụ khác phải được ghi riêng; bốn mẫu này không chứng minh tương thích toàn bộ.
- [ ] Chọn một chữ, nhiều chữ, cụm VietPhrase nhiều chữ, dấu câu và vùng qua xuống dòng trong từng ô Trung/Hán Việt/VP; hai ô còn lại phải hiện highlight xanh rõ dù không focus. Lặp cùng một cụm ba lần và chọn lần thứ hai/thứ ba để kiểm tra không nhảy về lần đầu.
- [ ] Thử kéo chuột, double-click và `Shift` + phím mũi tên trong cả ba ô. Focus phải ở ô đang thao tác; hai highlight đích không được làm mất selection hoặc đưa con trỏ sang ô khác.
- [ ] Bật log alignment, chọn `林悦`/`rừng duyệt`, kiểm tra log có ba range và `outcome=mapped` nhưng không chứa nội dung truyện. Tắt tùy chọn rồi xác nhận file không có dòng mới.
- [ ] Kéo hai splitter, ẩn/phóng lớn từng ô rồi bấm **Hiện đủ 4 ô**. Thử cửa sổ Full HD ở 100%/125%/150% DPI; nút AI, lịch sử, nháp và cảnh báo vẫn truy cập được.
- [ ] Đổi segment liên tục khi Data đang nạp và khi Hán Việt/VP đang tạo; UI không treo, kết quả cũ không hiện nhầm trên segment mới.
- [ ] Đổi nguyên văn bằng tách/gộp rồi kiểm tra alignment được tạo lại. Bản AI đã sửa/duyệt, lịch sử, Reader Mode và checkpoint hàng đợi phải giữ nguyên.
- [ ] Đổi sang thư mục thiếu/sai UTF-8: báo lỗi rõ, AI Editor vẫn dịch bằng Ollama và không crash.

1. Restore, build, test theo README; ghi số test và lỗi nếu có.
2. Mở ứng dụng; thử mở lần hai để kiểm tra thông báo một cửa sổ.
3. Nhập chương có 1.000–5.000 chữ Hán, xuống dòng CRLF/LF, lời thoại `“…”` / `「…」`, chữ ngoài BMP và emoji. Chia mặc định, kiểm tra các đoạn và dấu câu. Dùng nguồn không có câu kết thúc để thử chia dự phòng.
4. Bấm **Dịch toàn chương**. Kiểm tra Ollama chỉ nhận tuần tự một đoạn, thanh tiến độ tăng đúng thứ tự và bản Việt được cập nhật theo từng khối. Ghi thời gian, token, chất lượng và mức dùng GPU bằng Task Manager/`ollama ps`; không suy từ tên GPU rằng model luôn vừa VRAM.
5. Bấm **Tạm dừng** giữa chương: request hiện tại phải kết thúc và lưu rồi hàng đợi mới dừng. Đóng/mở lại tệp, bấm **Tiếp tục** và kiểm tra các đoạn hoàn tất không bị dịch lại.
6. Thử **Hủy hàng đợi**, mở lại tiến độ rồi tiếp tục. Tạo một lỗi Ollama ở một đoạn; các đoạn sau vẫn chạy, chương không báo thành công và **Dịch lại các đoạn lỗi** chỉ xử lý lỗi.
7. Trong lúc hàng đợi chạy, sửa một đoạn chưa tới lượt. Kiểm tra hàng đợi bỏ qua đoạn đó và không ghi đè bản sửa. Sửa bản AI, duyệt, rồi sửa bản đã duyệt; trạng thái trở lại chờ duyệt và lịch sử cũ còn nguyên.
8. Trong tab phân đoạn, sau khi tạm dừng hãy tách, gộp và đổi ranh giới câu. Kiểm tra nguyên văn ghép lại giống tuyệt đối, phần không bị ảnh hưởng giữ ID/bản dịch, phần đã sửa được bảo vệ và có thể khôi phục từ lịch sử.
9. Tạo/nhân bản/sửa/chọn preset, xem trước prompt và dịch lại một đoạn. Kiểm tra từng lần dịch giữ snapshot preset riêng. Xuất TXT UTF-8 và so thứ tự với các khối bản dịch.
10. Lưu, đóng, mở lại tiến độ. Kiểm tra nguyên văn, checkpoint hàng đợi, nháp, trạng thái, lịch sử, preset và cấu hình mỗi lần gọi. Mở thử một tệp chương schema cũ trên bản sao.

## Nhân vật và xưng hô

1. Tạo một Project nhân vật thử có một nữ (`nàng/nàng ấy`, tránh `bà/cô/cô ấy`) và một nam (`hắn/hắn ta`, tránh `ông/anh ấy`). Lưu, đóng/mở chương, xuất JSON rồi nhập vào chương thử khác. Kiểm tra Project ID, bí danh, quan hệ và tên Trung–Việt không đổi.
2. Dịch hoặc nhập bản thử có `Vương Tú Lan quay đầu. Bà nhìn con trai.`. Kiểm tra bản AI gốc còn trong lịch sử, bản chuẩn hóa dùng `Nàng`, trạng thái vẫn chờ duyệt và Hoàn tác trả đúng văn bản cũ.
3. Thử `Bà nội hắn`, `anh trai`, `“Mẹ ơi!”`, `“Bà ơi!”`, ngôi kể `tôi/ta` và đoạn trích dẫn. Các phần này không được thay như đại từ trần thuật.
4. Cho hai nhân vật nữ xuất hiện trước `bà/cô ấy`. Báo cáo phải yêu cầu xác nhận, không tự chọn một người. Bấm cảnh báo và kiểm tra UI mở đúng đoạn, câu nguồn và vị trí bản dịch.
5. Sửa tay hoặc duyệt một đoạn trước khi quét. Báo cáo phải đánh dấu được bảo vệ; Áp dụng an toàn và Hoàn tác không được ghi đè nội dung người dùng.
6. Đổi tên Trung–Việt đã xác nhận trong hồ sơ. Kiểm tra glossary bỏ ánh xạ hồ sơ cũ và chỉ giữ ánh xạ mới; hồ sơ không có tên Trung không được tạo chữ Hán giả.
7. Chạy Dịch toàn chương với tự chuẩn hóa bật, rồi pause/resume/cancel. Đại từ mơ hồ không được chặn đoạn tiếp theo; checkpoint, preset và lịch sử chuẩn hóa phải còn sau khi mở lại.

## Batch Replace và chữ Hán còn sót

1. Mở chương thử có `王秀兰` lặp nhiều lần, `林哲`, một cụm chưa biết và một chữ CJK ngoài BMP. Bấm **Quét chữ Hán còn sót**; kiểm tra cụm, số lần, chương/segment, phân loại và câu chứa. Tìm/lọc/sắp xếp, rồi nhấp đúp để mở đúng vị trí.
2. Xác nhận `王秀兰 = Vương Tú Lan` trong CharacterProfile và `林哲 = Lâm Triết` trong Glossary. Tạo preview toàn chương; các ánh xạ xác nhận phải được chọn, cụm chưa biết không bị tự thay. Đề xuất Names/Hán Việt phải ghi rõ nguồn và mặc định bỏ chọn.
3. Thử `林哲` và `林哲宇` trong cùng câu. Quy tắc dài phải thắng tại tên dài; tên ngắn độc lập vẫn được thay. Thử hai nghĩa khác nhau cho cùng khóa: preview phải báo xung đột, không cho áp dụng cho tới khi nhập lựa chọn trực tiếp.
4. Sửa tay một segment và duyệt một segment khác. Preview phải bỏ chọn cả hai. Chủ động chọn một bản bảo vệ, xác nhận cảnh báo, kiểm tra revision mới, trạng thái chờ duyệt, approval/AI gốc vẫn còn.
5. Thay một vị trí, thay nhiều vị trí, rồi **Hoàn tác lần thay thế**. Sau đó sửa thêm bằng tay và thử hoàn tác lại: ứng dụng phải từ chối ghi đè bản sửa mới.
6. Tạo hai chương cùng Character Project ID và một chương Project ID khác. Chọn phạm vi cả bộ truyện; chỉ hai chương đầu được preview/ghi. Kiểm tra `BatchBackups`, báo cáo từng chương và file dự án khác không đổi.
7. Khi queue đang chạy, nhấn `Ctrl+H` hoặc nút quét: ứng dụng phải yêu cầu tạm dừng/chờ request xong. Pause rồi thực hiện; checkpoint và vị trí queue không đổi.
8. Bật tự làm sạch sau dịch. Với output chứa tên đã xác nhận và cụm chưa biết, kiểm tra AI gốc trong lịch sử, bản làm việc đã thay tên, cảnh báo chữ Hán còn lại và chỉ một request Ollama. Bản người dùng đã sửa/duyệt không bị ghi đè.
9. Reader phải hiện số cụm còn sót và nút xử lý, kể cả Dark Mode không tạo mảng trắng. Đánh dấu một cụm giữ nguyên, đóng/mở lại và xác nhận cụm đó không còn tính vào cảnh báo nhưng nội dung vẫn nguyên vẹn.

## Offline, lỗi và phục hồi

1. Sau khi cài đủ phần mềm/model, ngắt Wi-Fi/LAN rồi dịch một chương thử. API localhost phải vẫn chạy, không có yêu cầu tải thêm thành phần. Hoàn tất cùng quy trình sửa/duyệt/lưu.
2. Dừng Ollama giữa hàng đợi: đoạn lỗi phải rõ ràng, bản sửa còn nguyên và các đoạn lỗi có thể retry sau khi mở lại Ollama.
3. Nhập tên model không tồn tại hoặc một model cloud: không gửi nguyên văn, không tự kéo model/cloud fallback. Phục hồi tên `translategemma:12b`.
4. Đặt timeout ngắn (ví dụ 1 giây), dịch; thử Hủy ở lần khác. Giữ lịch sử lỗi và nháp; kiểm tra timeout khác việc người dùng hủy.
5. Giảm giới hạn đầu ra để model hết token: xem cảnh báo cắt, không tự duyệt. Khôi phục cấu hình rồi dịch lại thủ công.
6. Trên **bản sao dữ liệu thử**, đóng cưỡng bức ứng dụng khi đang dịch. Mở lại: lần dở dang báo gián đoạn, không tự gửi lại; sau khi bấm Tiếp tục chỉ phần chưa xong được gửi. Nháp có lần lưu hoàn tất gần nhất phải còn. Thử cả khi đang gõ liên tục để kiểm tra nhịp tự lưu.
7. Sau ít nhất hai lần lưu, sao lưu cả thư mục. Đóng ứng dụng, đổi tên file chính và tạo một file hỏng cùng tên. Mở tiến độ: báo phục hồi `.bak`, không giả vờ bản mới nhất còn nguyên. Khôi phục bản sao để tiếp tục.
8. Thử lỗi quyền ghi/dung lượng chỉ với thư mục hoặc tài khoản thử. Nếu chưa lưu được khi đóng, cửa sổ phải vẫn giữ nháp và báo lỗi. Không thử làm đầy/khóa ổ đĩa đang chứa dữ liệu thật.
9. Thử font Trung/Việt, IME, clipboard, Enter/Tab, DPI 100%/150%, màn hình nhỏ và dài nhiều dòng. Những phần này cần Windows thật.

## Điều kiện trước khi dùng hàng ngày

Các bước cơ bản/offline/sửa-dịch-lưu-phục hồi đạt; người dịch đã đánh giá đủ câu, tên và xưng hô trên nhiều mẫu; cấu hình đủ bộ nhớ/timeout; có bản sao lưu cục bộ bên ngoài; mọi lỗi chưa xử lý được ghi lại. Không coi unit test HTTP giả lập là đánh giá chất lượng TranslateGemma.
