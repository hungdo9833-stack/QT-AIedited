# Kiểm tra thực tế M2 với 50 chương tổng hợp

Chỉ dùng Project thử nghiệm mới. Không chọn Project truyện cá nhân để nghiệm thu bản M2.

1. Giải nén toàn bộ ZIP vào một thư mục mới, rồi chạy `QuickTranslator.AiEditor.exe`.
2. Tạo Project thử nghiệm mới và đặt tên dễ nhận biết, ví dụ `M2 Synthetic 50`.
3. Trong khu vực Project, bấm `+ TXT`. Chọn `M2-SAMPLE-50-CHAPTERS.txt` nằm cạnh file EXE.
4. Giữ encoding `Tự động · UTF-8 nghiêm ngặt`, rồi bấm `Phân tích` nếu preview chưa tự chạy.
5. Kiểm tra các số liệu: 50 chương sẽ nhập, các chương mang số 1–50 và thuộc quyển `风起`.
6. Chọn vài dòng ở đầu, giữa và cuối danh sách. Đối chiếu hai vùng quanh điểm bắt đầu/kết thúc; nguyên văn phải đúng với file TXT.
7. Thử sửa tên một chương, bấm áp dụng rồi bấm `Hoàn tác`. Thử loại một chương khỏi import và đưa lại vào import.
8. Bấm `Nhập các chương đã chọn`. Không tắt ứng dụng trong lúc một chunk đang rollback; nút `Hủy an toàn` sẽ chờ transaction kết thúc.
9. Sau báo cáo hoàn tất, đóng Project và mở lại. Kiểm tra manifest hiển thị đủ 50 chương, mở chương 1, 25 và 50 để đối chiếu nguyên văn.
10. Mở lại cùng file TXT. Preview phải nhận diện collision với Project và không cho ghi đè. Đây là hành vi bảo vệ mong đợi.

Khi muốn thử resume, tạo một Project tổng hợp khác, bắt đầu import rồi bấm `Hủy an toàn` sau khi có ít nhất một chunk hoàn tất. Mở lại cùng TXT và xác nhận nhập lần nữa. Báo cáo phải phân biệt chương đã có từ lần trước với chương mới, không tạo bản sao.

Nếu ứng dụng báo transaction cần recovery sau khi máy hoặc process bị dừng đột ngột, dùng màn hình recovery của Project để đối chiếu và chọn rollback/confirm. Không xóa thủ công thư mục `History/Transactions` hoặc ZIP trong `Backups`.
