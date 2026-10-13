# Nghiệm thu M6 Reader Mode

Chỉ dùng Project tổng hợp hoặc bản sao thử nghiệm. Không nghiệm thu trực tiếp trên Project cá nhân chưa có backup.

1. Giải nén toàn bộ ZIP M6 và chạy `QuickTranslator.AiEditor.exe`.
2. Tạo Project `M6 Reader Synthetic`, nhập `M6-SAMPLE-50-CHAPTERS.txt`, rồi tạo vài bản dịch/nháp tổng hợp ở ít nhất ba chapter.
3. Chọn **Đọc** ở sidebar. Reader phải hiện bản Việt của chapter hiện hành và mục lục 50 chapter.
4. Gõ số hoặc tên vào ô tìm mục lục. Chọn kết quả, dùng **Trước/Sau**, rồi kiểm tra chapter mở đúng và không tạo request dịch.
5. Bấm **Ẩn mục lục**, đổi font, cỡ chữ, giãn dòng, khoảng cách đoạn, chiều rộng và theme. Đóng/mở lại Project để xác nhận preferences được khôi phục.
6. Cuộn và đặt caret giữa một segment. Chuyển chapter, quay lại rồi đóng/mở ứng dụng; Reader phải trở lại đúng chapter/segment gần vị trí cũ.
7. Chọn một câu và bấm **Mở trong Editor** hoặc nhấp đúp. Editor phải mở đúng segment và giữ nguyên draft.
8. Xuất chapter thành TXT. So sánh nội dung TXT với nội dung canonical trong Reader; ranh giới segment phải dùng cùng quy tắc hai newline. Bật/tắt thu gọn dòng trống chỉ được thay cách hiển thị.
9. Thử ở 1920×1080, 2560×1440 và scale 150%/200%. Ở cửa sổ hẹp, lệnh Mục lục, Trước/Sau, Kiểu đọc, Tìm và Mở trong Editor phải còn sử dụng được.
10. Nếu Queue M4 đang chạy, thử đổi chapter trong Reader. Guard phải yêu cầu Pause và không cho hai luồng ghi cùng chapter.

Kiểm tra thủ công thêm với font cài riêng, bàn phím/IME và nhiều màn hình DPI khác nhau. M6 không yêu cầu Ollama thật vì Reader không gửi request AI.
