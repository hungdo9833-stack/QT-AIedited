# Nghiệm thu M3 bằng Project tổng hợp

Chỉ dùng Project thử nghiệm mới. Không chọn Project truyện cá nhân.

1. Giải nén toàn bộ ZIP M3 vào một thư mục mới và chạy `QuickTranslator.AiEditor.exe`.
2. Tạo Project mới, ví dụ `M3 Synthetic 50`.
3. Bấm `+ TXT`, chọn `M3-SAMPLE-50-CHAPTERS.txt` cạnh file EXE và giữ `Tự động · UTF-8 nghiêm ngặt`.
4. Trong Import Preview M2, xác nhận đủ 50 chương từ 1 đến 50, rồi bấm nhập.
5. Khi import xong, không bấm Refresh. Danh sách bên trái phải tự xuất hiện đủ 50 chương.
6. Bấm `Quản lý chương`. Kiểm tra tìm kiếm theo `25`, lọc trạng thái, đổi sort và chọn nhiều dòng bằng Ctrl/Shift.
7. Mở chương 25 trong Editor. Trở lại Chapter Manager, đổi tên chương này; Editor và danh sách phải cùng hiện tên mới, kể cả sau một lần lưu.
8. Chọn vài chapter tổng hợp, chuyển vào History. Đổi bộ lọc sang `Trong History`, chọn các chapter đó và phục hồi. Không dùng chapter có số trùng với chapter active.
9. Đóng Project, mở lại file `.qtproj`, kiểm tra đủ 50 chapter, tên đã đổi và trạng thái History/active đã chọn.
10. Nút `Dịch các chương đã chọn · M4` phải bị vô hiệu hóa. M3 chỉ chuẩn bị selection/command surface, chưa chạy queue nhiều chương.

Với Project lớn, kiểm tra scroll trong bảng Chapter Manager. Row virtualization/recycling phải giữ thao tác mượt và việc mở Chapter Manager không được tạo hoặc đọc file body của mọi chapter.
