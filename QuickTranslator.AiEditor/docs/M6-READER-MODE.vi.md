# M6 — Reader Mode tinh gọn

## Phạm vi

M6 hoàn thiện Reader như một workspace phụ để đọc lại bản dịch và quay về Editor. Reader tiếp tục dùng `ChapterDocument`, ProjectCatalog M3, transaction/recovery và shell Dark Navy M5 hiện có. Không đổi schema Project, không sửa Repository, Queue, engine dịch, Ollama, TranslateGemma, TraditionalTranslator hoặc VietPhrase.

## Chức năng

- Reader hiển thị bản Việt đang biên tập của chapter hiện hành và cảnh báo đoạn thiếu, lỗi, cần kiểm tra hoặc còn chữ Hán.
- Mục lục trong Reader lấy metadata trực tiếp từ ProjectCatalog, có virtualization, tìm theo số hoặc tên và không đọc body các chapter khác.
- Chuyển chapter bằng mục lục, nút Trước/Sau; mọi chuyển context vẫn đi qua guard lưu và queue của ứng dụng.
- Mục lục có thể ẩn/hiện. Focus Mode tự ẩn mục lục mà không làm mất lựa chọn đã lưu.
- Tùy chỉnh font, cỡ chữ, giãn dòng, khoảng cách đoạn, lề, chiều rộng, căn lề và theme sáng/ấm/tối/cổ điển được giữ lại từ Reader hiện có.
- Vị trí đọc lưu theo ChapterId, SegmentId, offset trong segment và fallback offset/scroll cũ. Khi phần trước segment thay đổi, Reader vẫn tìm lại đúng segment.
- `Mở trong Editor` và nhấp đúp đưa người dùng về đúng chapter/segment, giữ nguyên draft và khôi phục chế độ hai/bốn ô đã chọn trong M5.
- Reader chỉ giữ `ReaderDocument` của chapter hiện hành. Danh sách hàng nghìn chapter chỉ giữ summary từ manifest.

## Quy tắc văn bản thống nhất

Trước M6, TXT Export ghép hai segment bằng hai ký tự xuống dòng (`\n\n`) còn Reader nối trực tiếp. M6 giữ nguyên output TXT hiện hữu và dùng `ChapterTranslationComposer` làm nguồn canonical cho cả Reader lẫn Export. Golden tests khóa output Export cũ, UTF-8 và offset từng segment; M6 không đổi nội dung file TXT đã xuất theo quy tắc cũ.

`ReaderDisplayLayout` chỉ thay cách vẽ paragraph, chẳng hạn thu gọn dòng trống dư khi hiển thị. Nó không sửa canonical text, draft hay export.

## Tương thích dữ liệu

- `.qtproj` và chapter schema không đổi.
- `reader.json` giữ `SchemaVersion = 1`. Hai trường tùy chọn tương thích ngược được thêm: trạng thái mục lục và offset tương đối trong segment. File cũ thiếu trường vẫn đọc bằng defaults/fallback offset.
- Giới hạn position tăng từ 1.000 lên 20.000 chapter để hỗ trợ tiểu thuyết dài; kiểm thử xác nhận 10.000 chapter hợp lệ.
- Reader preferences tiếp tục dùng atomic write, backup và recovery Phase 3.

## Giới hạn chủ đích

- Mỗi lần chỉ hiển thị một chapter; không có continuous multi-chapter viewport.
- Đánh dấu đơn giản hiện có được giữ, không có hệ thống bookmark nâng cao.
- Không có animation, TTS, AI tóm tắt hoặc Library mới.
- Kéo cửa sổ giữa nhiều màn hình, IME và font cài riêng cần nghiệm thu thủ công trên máy người dùng.

## Kết quả nghiệm thu

Nghiệm thu Release ngày 13/10/2026: Core 486/486, WPF 56/56, tổng 542/542; 0 failed, 0 skipped; build 0 lỗi và 0 cảnh báo.
