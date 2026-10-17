# Character Consistency Lock V1

## Phạm vi

V1 lưu quy tắc cố định trong `ProjectResources.Characters`. `CharacterProject` vẫn dùng schema 1 và Project manifest không đổi. Các trường mới đều có giá trị mặc định tương thích với JSON cũ.

Khi khóa Project bật, mỗi segment chỉ nhận khối `CHARACTER CONSISTENCY` nếu nguyên văn chứa tên Trung hoặc bí danh chữ Hán đã xác nhận. Bộ chọn chạy trên CPU, không I/O và không gọi model. Tên trùng giữa nhiều hồ sơ bị coi là mơ hồ; tên dài thắng tên ngắn khi hai tên chồng lấn. Quy tắc hội thoại chỉ được đưa vào khi cả hai nhân vật xuất hiện và segment có dấu hiệu lời thoại. Hai hướng A → B và B → A độc lập.

Giới hạn mặc định là 16 nhân vật, 16 quy tắc hội thoại và 2.400 ký tự. Quy tắc chỉ được thêm nguyên dòng; nếu hết ngân sách, cả quy tắc bị bỏ và không có dòng bị cắt dở.

## Cách sử dụng

1. Mở **Thiết lập dịch → Nhân vật & xưng hô**.
2. Bật **Khóa nhất quán Project**.
3. Nhập đại từ chính trong bảng và bật **Khóa**, hoặc mở **Sửa** để chỉnh hồ sơ đầy đủ.
4. Trong hồ sơ nhân vật, chọn người nghe, nhập **Tự xưng**, **Gọi đối phương**, bật quy tắc và khóa nếu cần bảo vệ khỏi JSON import.
5. Bấm **Lưu hồ sơ**. Thao tác này tăng revision của Project Resources.
6. Chọn một segment rồi bấm **Xem prompt khóa** để xem chính xác khối quy tắc được chọn. Chức năng Preview Prompt hiện hữu cũng hiển thị khối này trong prompt hoàn chỉnh.
7. Queue Plan đã tạo trước khi sửa tài nguyên sẽ bị từ chối khi Start/Resume; hãy tạo Queue Plan mới để dùng revision mới.

Đại từ phụ tiếp tục hoạt động như trước khi khóa Project tắt. Khi khóa bật và nhân vật được khóa, đại từ phụ không được coi là lựa chọn hợp lệ thay thế đại từ chính. Bộ chuẩn hóa cũ vẫn chỉ sửa trường hợp có tiền ngữ rõ ràng, giữ nguyên lời thoại trực tiếp và không ghi đè UserDraft/Approval.

## Benchmark CPU và fake provider

Máy kiểm thử Windows, .NET 10 Release; 300 chương × 10 segment tổng hợp. Mỗi segment nhắc hai nhân vật. Token là ước lượng ký tự/4, không phải tokenizer của TranslateGemma.

| Hồ sơ | Chọn luật p50 | Chọn luật p95 | BuildPrompt cũ p50 | BuildPrompt mới p50 | Prompt tăng TB | Request fake cũ/mới |
|---:|---:|---:|---:|---:|---:|---:|
| 50 | 9,30 µs | 16,80 µs | 6,60 µs | 6,80 µs | 380 ký tự ≈ 95 token | 3.000 / 3.000 |
| 200 | 11,50 µs | 19,40 µs | 6,60 µs | 6,70 µs | 380 ký tự ≈ 95 token | 3.000 / 3.000 |
| 1.000 | 19,40 µs | 33,80 µs | 6,50 µs | 6,70 µs | 380 ký tự ≈ 95 token | 3.000 / 3.000 |

Đây chỉ là số đo CPU/fake provider. Chưa có phép đo TranslateGemma 12B trên GPU thật nên chưa thể kết luận mục tiêu end-to-end ≤5%.

## Checklist đo 20–50 segment trên TranslateGemma 12B

- Dùng Project tổng hợp/bản sao thử nghiệm; giữ nguyên model, backend, temperature, context, output tokens và segmentation.
- Chọn 20–50 segment đại diện: không có nhân vật, một nhân vật, nhiều nhân vật, hội thoại rõ và hội thoại mơ hồ.
- Chạy một lượt với khóa tắt, ghi tổng thời gian, p50/p95, input/output token và số request.
- Tạo Queue Plan mới sau khi bật khóa; chạy đúng các segment đó và ghi cùng số liệu.
- Xác nhận số request bằng nhau và so sánh prompt trong lịch sử attempt.
- Kiểm tra thủ công đại từ trần thuật, hai hướng xưng hô, lời thoại mơ hồ, segment không liên quan và output không lặp lại nội dung hướng dẫn.
- Nếu tổng thời gian hoặc p95 tăng quá 5%, lưu prompt/metrics và giảm số luật hoặc ngân sách trước khi nghiệm thu hiệu năng GPU.

## Giới hạn xác định

- V1 không suy luận người nói; nếu vai thoại không rõ, prompt chỉ yêu cầu model không tự bịa vai.
- Tên/bí danh trùng giữa nhiều hồ sơ không được chèn tự động.
- Nhân vật không xuất hiện bằng tên/bí danh đã xác nhận không nhận quy tắc.
- Quan hệ thay đổi theo chương, cảm xúc, AI discovery và lượt AI kiểm tra thứ hai nằm ngoài V1.
- Việc fake provider nhận đúng prompt chứng minh đường tích hợp, không chứng minh TranslateGemma luôn dịch đúng xưng hô.
