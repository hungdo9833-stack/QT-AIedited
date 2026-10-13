# Nghiệm thu M5 bằng Project tổng hợp

## Chuẩn bị

1. Giải nén toàn bộ ZIP M5 vào một thư mục mới và chạy `QuickTranslator.AiEditor.exe`.
2. Tạo Project mới tên `M5 Synthetic 50`. Không chọn thư mục Project cá nhân.
3. Chọn **Nhập TXT**, mở `M5-SAMPLE-50-CHAPTERS.txt` cạnh file EXE, giữ `Tự động · UTF-8 nghiêm ngặt`.
4. Kiểm tra Preview nhận đủ 50 chương, exact source coverage hợp lệ rồi xác nhận import.

## Luồng tích hợp chính

1. Mở **Chương** và xác nhận 50 chương xuất hiện tự động, tìm kiếm/lọc không cần Refresh.
2. Chọn 5 chương chưa dịch, mở Queue Plan, kiểm tra đúng ChapterId rồi tạo hàng đợi. Không cần chạy Ollama nếu máy chưa cấu hình model.
3. Mở chương 1 trong **Biên dịch**. Ứng dụng phải vào chế độ hai ô, nguyên văn bên trái và bản Việt bên phải.
4. Gõ một câu tiếng Việt bằng IME, đổi sang **Bốn ô**, rồi quay lại **Hai ô**. Nội dung và segment đang chọn phải giữ nguyên.
5. Kéo splitter, ẩn/hiện mục lục và Inspector, đổi Light/Dark rồi đóng/mở lại ứng dụng. Bố cục phải được khôi phục mà không sửa schema Project.
6. Bấm **Lưu**, đóng/mở Project và kiểm tra draft. Chỉ bấm **Duyệt đoạn** trên dữ liệu tổng hợp; approval/history phải được giữ sau khi mở lại.
7. Mở **Hàng đợi** và xác nhận trạng thái shell khớp Queue Progress. Với Ollama đã cấu hình, thử Start, Pause, Resume và Retry một chapter lỗi.
8. Mở **Thư viện**, tìm `M5 Synthetic 50`, kiểm tra tiến độ và **Tiếp tục biên dịch** mở đúng Project/chương.

## Layout và khả năng nhập liệu

- Thử ở 1920×1080 và 2560×1440 với Windows scale 100% và 150%.
- Thử 1920×1080 ở 200%: mục lục và Inspector phải tự thu gọn; command chính vẫn nhìn thấy và dùng được.
- Dùng Tab/Shift+Tab trong command bar và editor; dùng `Ctrl+1`, `Ctrl+2`, `Ctrl+S`, `Ctrl+Shift+I`.
- Thử Microsoft Pinyin hoặc bộ gõ tiếng Việt trong ô bản dịch; caret, composition và text sau khi chuyển hai/bốn ô phải giữ đúng.

## Recovery

1. Sửa draft nhưng chưa Save, thử đổi chapter/Project. Guard phải yêu cầu xử lý dữ liệu chưa lưu theo cơ chế Data Safety.
2. Khi queue đang chạy, thử đổi Project. Guard M4 phải chặn hoặc yêu cầu pause an toàn.
3. Tắt ứng dụng sau khi đã Save, mở lại Project và kiểm tra draft, approval, history, queue checkpoint cùng chapter hiện hành.

Ollama/TranslateGemma thực, IME cụ thể của máy và thao tác kéo cửa sổ giữa nhiều màn hình cần nghiệm thu thủ công vì automated suite dùng fake provider và WPF renderer tổng hợp.
