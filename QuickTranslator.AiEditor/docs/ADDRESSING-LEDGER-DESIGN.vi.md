# Thiết kế: Sổ xưng hô theo cặp nhân vật (Addressing Ledger)

Trạng thái: **đã duyệt hướng (2026-10-20)**. Bước 1 (CPU) đang triển khai; Bước 2–5 chưa làm.

## 1. Mục tiêu

QT Studio là phần mềm dịch tự động. Khảo sát truyện chỉ có giá trị nếu nó làm **xưng hô toàn truyện nhất quán trong bản dịch**. Đơn vị đúng của xưng hô là **cặp có hướng (người nói → người nghe) theo thời gian**: A tự xưng gì và gọi B là gì, từ chương nào đến chương nào.

Đích duy nhất của mọi bước khảo sát là **sổ xưng hô đã được người dùng duyệt**, lưu trong hồ sơ nhân vật mà trình dịch đang dùng (Character Project, khóa nhất quán). Không có đích trung gian nào khác.

## 2. Vì sao đổi hướng (tóm tắt đánh giá)

- Có ba hệ khảo sát chồng chéo: ABI v2 (nhân vật/quan hệ/glossary), 2A Entity Memory (CPU) và 2B Semantic Survey. **Chỉ ABI v2 ghi được vào hồ sơ nhân vật**; 2A/2B không được nối vào prompt dịch.
- 2B khảo sát theo **cửa sổ 900 chữ**: cùng một sự thật bị lặp ở hàng trăm cửa sổ; số request tỷ lệ với độ dài truyện (vài nghìn request cho 1.000 chương); contract 25 trường và offset UTF-16 khiến mỗi lần chạy thật lộ một kiểu lỗi mới.
- Project thử "test cpu" (19 chương): khoảng 5 MB dữ liệu khảo sát, **0 hồ sơ, 0 quy tắc xưng hô**, khóa nhất quán tắt.
- `CharacterRelationship` không có mốc chương; trình dịch chỉ chèn quy tắc khi tên xuất hiện nguyên văn trong chính đoạn 300–500 chữ, và quy tắc thoại cần cả hai tên trong đoạn đó.

## 3. Quy tắc an toàn (giữ nguyên từ hệ cũ)

- Không tự xác nhận dữ kiện AI; mọi quy tắc vào hồ sơ phải qua người duyệt.
- Chứng cứ là **câu trích nguyên văn**, được định vị lại bằng CPU trong đúng chương (dùng lại `SemanticQualityAuditor.QuotePattern`).
- Không gọi AI khi chưa có xác nhận, có ngân sách request, không tự retry, không tự đổi model.
- Không xóa dữ liệu khảo sát cũ. 2A/2B **tạm dừng mở rộng**, giữ nguyên để tham khảo.
- Không sửa pipeline dịch cho tới Bước 4; Bước 1–3 chỉ đọc nguyên văn.

## 4. Các bước

### Bước 1 — CPU: tách lời thoại và gom theo cặp (không gọi AI)

Đầu vào: nguyên văn các chương đang hoạt động; danh sách tên ứng viên từ `CharacterMentionCensus` (PersonCandidate/AliasCandidate) cộng hồ sơ nhân vật sẵn có.

1. **Chuẩn hóa ngắt dòng cứng.** Truyện web thường ngắt dòng mỗi khoảng 38 ký tự, đoạn mới thụt lề. Nếu đa số dòng bắt đầu bằng thụt lề, xuống dòng không thụt lề được coi là ngắt dòng cứng và bị bỏ khi phân tích; mọi vị trí vẫn ánh xạ về offset UTF-16 gốc.
2. **Tách lời thoại**: `「…」`, `“…”`, `『…』` (cặp ngoặc cân bằng), có thể kéo dài nhiều dòng.
3. **Gán người nói**, kèm mức tin cậy:
   - `Explicit`: mệnh đề ngay trước lời thoại có động từ nói (`说道`, `问道`, `笑道`, `说`, `道`…) và tên nhân vật đứng đầu mệnh đề, ví dụ `柳月影…说道：「`. Hoặc tên + động từ nói ngay sau `」`.
   - `Pronoun`: mệnh đề có động từ nói nhưng chủ ngữ là `他/她`; ghi người nói gần nhất làm *ứng viên*, không coi là chắc chắn.
   - `Unattributed`: không có dấu hiệu; chỉ ghi những người có mặt trong cảnh.
4. **Gán người nghe**:
   - `Vocative`: lời thoại gọi tên/bí danh đã biết (`柳轻锋，…`).
   - `Clause`: tên khác trong mệnh đề dẫn thoại (`白了柳轻锋一眼说道`, `对X说`).
   - `Scene`: đúng một nhân vật khác được nhắc trong đoạn trước/sau (yếu).
5. **Từ xưng hô trong lời thoại**:
   - Tự xưng: `我`, `人家`, `本座`, `老夫`, `在下`, `小女子`, `本王`, `朕`, `妾身`, `奴婢`, `老子`, `俺`, `弟子`, `晚辈`, `贫道`, `本宫`…
   - Gọi người: danh xưng thân tộc/thầy trò/địa vị (`老姐`, `姐姐`, `弟弟`, `妈妈`, `师兄`, `前辈`, `公子`…). Phân biệt **hô gọi** (từ đứng trước `，！？…` hoặc trước `你/您`) với **nhắc tới** (`跟妈妈说`), vì chỉ hô gọi mới là cách gọi người nghe.
   - Đếm `你`/`您` (độ trang trọng).
6. **Gom theo cặp** (người nói, người nghe); cặp chưa rõ người nghe được gom vào `(người nói, ?)`. Mỗi cặp có: số lời thoại theo từng mức tin cậy, chương đầu/cuối, thống kê tự xưng và hô gọi, và tối đa N mẫu thoại (ưu tiên mẫu `Explicit` có hô gọi), mỗi mẫu kèm chương và offset để kiểm chứng.

Đầu ra Bước 1 là **báo cáo chỉ đọc** (JSON và Markdown), không ghi vào Project. Mục đích: người dùng xem các cặp chính có hợp lý không, trước khi tốn request AI.

**Kết quả chạy thử Bước 1 trên "test cpu" (2026-10-20, bản sao, Project không đổi một byte):** 19 chương, 172.119 ký tự, 1,2 giây.

| Chỉ số | Giá trị |
|---|---:|
| Lời thoại tách được (`「」`) | 1.061 / 1.062 cặp ngoặc |
| Người nói `Explicit` | 314 (29,6%) |
| Người nói là đại từ (ứng viên) | 105 (9,9%) |
| Không xác định | 642 (60,5%) |
| Có ứng viên người nghe | 227 (21,4%) |
| Tên ứng viên từ census | 19 |
| Cặp có hướng | 32 (24 cặp có người nghe) |

Gợi ý bí danh tìm được: 小影姐/小影 ≈ 柳月影, 小锋 ≈ 柳轻锋, 阿岚 ≈ 秦梦岚. Hô gọi gom theo cặp khớp quan hệ trong truyện (柳轻锋 → 秦梦岚: `妈妈`×8; 柳月影 → 柳轻锋: `弟弟`; 柳轻锋 → 柳恒: `父亲`). Kiểm tra tay khoảng 25 mẫu `Explicit`: người nói đúng; một lỗi gán sau ngoặc (động từ trong `知道`, đoạn kế tiếp) đã được sửa.

Giới hạn quan sát được, để Bước 2–3 xử lý:

- 60% lời thoại không có dấu hiệu người nói (thoại nối tiếp). Bước 1 không đoán; Bước 2 có thể xét theo lượt thoại xen kẽ trong cùng cảnh.
- Bí danh được báo riêng (`柳轻锋 → 小影姐` và `柳轻锋 → 柳月影` là hai dòng) cho tới khi người dùng duyệt gộp. Biến thể khác chữ (`小峰`) không có gợi ý.
- Tự xưng bằng danh xưng thân tộc (`陪陪姐姐`, người nói tự gọi mình là 姐姐) được ghi là "nhắc tới", không phải hô gọi; Bước 2 cần nhận diện kiểu tự xưng này vì nó quyết định cách dịch "chị/em".

### Bước 2 — AI theo cặp (chưa làm)

Với mỗi cặp quan trọng (lọc theo số lời thoại `Explicit`/`Vocative`), gửi 10–20 mẫu thoại ngắn kèm mệnh đề dẫn thoại, schema nhỏ: tự xưng, gọi người, quan hệ, chương bắt đầu/đổi, câu trích chứng minh. Số request tỷ lệ với số cặp (thường 50–200 cho cả truyện), không phải số cửa sổ. Câu trích được kiểm chứng nguyên văn bằng CPU.

### Bước 3 — Tổng hợp và duyệt theo cặp (chưa làm)

Gộp kết quả theo cặp; cặp đồng thuận trở thành một đề xuất duy nhất; cặp mâu thuẫn hoặc đổi theo thời gian được tách theo khoảng chương. Người dùng duyệt **từng cặp** (khoảng 100 cặp), không phải hàng nghìn đề xuất. Quy tắc đã duyệt ghi vào hồ sơ nhân vật.

### Bước 4 — Nối vào dịch (chưa làm)

- Bổ sung mốc chương (`EffectiveFromChapter`/`EffectiveToChapter`) cho quy tắc xưng hô; chỉ chèn quy tắc đúng chương.
- Chọn quy tắc theo **nhân vật có mặt trong cảnh/chương**, không chỉ trong đoạn; nếu Bước 1 gán được người nói cho lời thoại của đoạn thì ưu tiên cặp đó.
- Mở rộng kiểm tra sau dịch: đánh dấu lời thoại có cách gọi lệch sổ.

### Bước 5 — Đo bằng dữ liệu thật (chưa làm)

Người dùng gán nhãn tay 2–3 chương: người nói, người nghe, tự xưng, gọi người. Đo độ chính xác gán người nói của Bước 1, độ chính xác sổ xưng hô sau Bước 2–3, và tỷ lệ lời thoại dịch đúng sổ trước/sau Bước 4.

## 5. Không nằm trong phạm vi

- Không xây lại Entity Memory/Semantic Survey; không xóa dữ liệu của chúng.
- Không tự suy luận người nói từ đại từ thành dữ kiện chắc chắn.
- Không tự ghi Project Resources ở Bước 1–2.
