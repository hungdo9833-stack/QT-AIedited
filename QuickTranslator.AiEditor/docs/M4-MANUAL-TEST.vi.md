# Nghiệm thu M4 bằng Project tổng hợp

Chỉ dùng Project thử nghiệm mới. Không chọn Project truyện cá nhân.

## Chuẩn bị

1. Giải nén toàn bộ ZIP M4 vào một thư mục mới và chạy `QuickTranslator.AiEditor.exe`.
2. Bật Ollama và bảo đảm model đang chọn đã có trên máy. Trong `Cài đặt > AI & hiệu năng`, kiểm tra kết nối rồi lưu thiết lập.
3. Tạo Project mới, ví dụ `M4 Synthetic 50`.
4. Bấm `+ TXT`, chọn `M4-SAMPLE-50-CHAPTERS.txt` cạnh file EXE, giữ `Tự động · UTF-8 nghiêm ngặt`, xác nhận Preview và import đủ 50 chapter.

## Queue Plan và chạy tuần tự

1. Mở `Quản lý chương`, chọn chapter 1–5 bằng Ctrl/Shift và bấm `Đưa vào hàng đợi dịch`.
2. Kiểm tra Queue Plan có đúng 5 chapter, đúng thứ tự, model, prompt và resource revision. Chưa bấm Start thì Ollama chưa nhận request.
3. Bấm Start. Kiểm tra chỉ một chapter có trạng thái đang dịch và progress segment tăng thật.
4. Khi request đang chạy, bấm Pause. Queue phải hoàn tất/checkpoint request hiện tại rồi chuyển Paused; các chapter sau vẫn Pending.
5. Bấm Resume. Đợi hoàn tất và mở từng chapter để kiểm tra AI attempt nằm đúng ChapterId/SegmentId.

## Restart và phục hồi

1. Tạo queue mới trên một Project synthetic khác, Start rồi đóng ứng dụng bằng nút đóng cửa sổ.
2. Mở lại Project. Queue không được tự dịch tiếp; panel phải cho thấy Paused hoặc Interrupted.
3. Bấm Resume và xác nhận các checkpoint trước đó không bị dịch lặp hoặc mất.
4. Để thử crash thật, chỉ dùng Project synthetic: kết thúc process giữa request, mở lại Project và xác nhận phải bấm Resume thủ công.

## Lỗi và bảo vệ nội dung

1. Tắt Ollama trong một chapter đang chạy hoặc dùng timeout ngắn. Chapter phải hiện Failed/lỗi rõ ràng, các checkpoint trước đó còn nguyên.
2. Khởi động Ollama, bấm Retry failed và kiểm tra chỉ chapter lỗi được chạy lại.
3. Trước khi tạo plan mới, tạo UserDraft và Approval ở vài segment. Queue phải giữ nguyên chúng và chỉ dịch segment đủ điều kiện.
4. Tạo plan nhưng chưa Start, sửa nguyên văn hoặc Glossary/Context/Character Profiles rồi Start. Item liên quan phải NeedsReview hoặc queue từ chối revision, không ghi kết quả cũ.
5. Trong lúc queue chạy, thử mở chapter khác hoặc chuyển Project. Ứng dụng phải Pause và lưu checkpoint trước khi đổi context.

## Cancel pending

1. Tạo queue 5 chapter, Start rồi bấm Cancel khi chapter đầu đang chạy.
2. Request hiện tại được phép kết thúc và checkpoint; các item chưa bắt đầu thành Cancelled.
3. Đóng/mở lại Project, xác nhận chapter đã checkpoint còn nguyên và item pending không tự chạy lại.

Ghi lại model, dung lượng VRAM, thời gian chạy và thông báo lỗi nếu nghiệm thu Ollama thật không đạt. Không sao chép dữ liệu cá nhân vào báo cáo lỗi.
