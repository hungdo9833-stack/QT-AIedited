# Xuất sách EPUB

Trong **Xuất bản**, chọn **EPUB · Sách điện tử**, chọn toàn bộ truyện, dải chương hoặc một chương, rồi **Chọn nơi lưu và xuất**.

Ở **Ngôn ngữ**, chọn **Bản dịch tiếng Việt** (mặc định) hoặc **Bản gốc tiếng Trung** cho TXT, PDF và EPUB. Bản gốc dùng tên truyện, tên chương tiếng Trung và nội dung gốc; không cần xác nhận xuất nháp dù chưa dịch. Tên file gợi ý có hậu tố `-zh`, EPUB dùng mã ngôn ngữ `zh`.

EPUB có mục lục nhấn được ở đầu sách và trong bảng điều hướng của ứng dụng đọc. Cuối mỗi chương có liên kết quay về mục lục. Mục lục dùng đúng thứ tự đọc và chỉ chứa những chương thuộc phạm vi xuất, không gồm chương đã xóa mềm.

Bố cục tự co giãn, cỡ chữ tương đối, giãn dòng 1,6, đoạn văn căn trái và chiều rộng đọc tối đa 38em. File không ép phông chữ hoặc màu nền/chữ, để người đọc chỉnh cỡ chữ và chế độ sáng/tối trong ứng dụng đọc. Mỗi chương là một tài liệu riêng, có ngôn ngữ theo lựa chọn xuất bản. File gồm cả EPUB navigation và NCX cho trình đọc cũ.

- **Gồm tên Project**: thêm trang tên sách. Metadata EPUB luôn cần tên sách.
- **Gồm tiêu đề từng chương**: hiển thị tiêu đề trong nội dung; bỏ chọn vẫn giữ tên chương trong mục lục.
- **Cho phép xuất bản nháp/chương cần kiểm tra**: giữ nguyên cơ chế xác nhận của TXT/PDF.

Bản dịch lấy từ cùng bộ ghép bản dịch với Reader/TXT; bản gốc giữ nguyên văn bản tiếng Trung. Xuất bản không sửa dữ liệu Project. Khi hủy, đọc chương bị lỗi hoặc Project thay đổi trong lúc xuất, file đích cũ được giữ nguyên và file tạm được dọn.

Kiểm thử tự động kiểm tra cấu trúc ZIP/XML, MIME không nén, manifest/spine, liên kết mục lục, NCX, tiếng Việt, escape nội dung, phạm vi chương, quyền xuất nháp, hủy và lỗi. Chưa chạy EPUBCheck hoặc kiểm tra trực tiếp trên thiết bị đọc sách.

Tham chiếu định dạng: [W3C EPUB 3.3](https://www.w3.org/TR/epub-33/).
