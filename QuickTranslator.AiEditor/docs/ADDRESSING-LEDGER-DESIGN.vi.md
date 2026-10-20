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
| Người nói `Explicit` | 423 (39,9%) |
| Người nói là đại từ (ứng viên) | 210 (19,8%) |
| Không xác định | 428 (40,3%) |
| Có ứng viên người nghe | 423 (39,9%) |
| Tên ứng viên từ census | 19 |
| Cặp có hướng | 35 |

Gợi ý bí danh tìm được: 小影姐/小影 ≈ 柳月影, 小锋 ≈ 柳轻锋, 阿岚 ≈ 秦梦岚. Hô gọi gom theo cặp khớp quan hệ trong truyện (柳轻锋 → 秦梦岚: `妈妈`×8; 柳月影 → 柳轻锋: `弟弟`; 柳轻锋 → 柳恒: `父亲`). Kiểm tra tay khoảng 25 mẫu `Explicit`: người nói đúng; một lỗi gán sau ngoặc (động từ trong `知道`, đoạn kế tiếp) đã được sửa.

Giới hạn quan sát được, để Bước 2–3 xử lý:

- Sau khi sửa nhận diện ngắt dòng cứng (đoạn dài nhiều dòng chỉ thụt lề dòng đầu), số người nói rõ ràng tăng từ 314 lên 423. Còn 40% lời thoại không có dấu hiệu người nói (thoại nối tiếp). Bước 1 không đoán; Bước 2 có thể xét theo lượt thoại xen kẽ trong cùng cảnh.
- Bí danh được báo riêng (`柳轻锋 → 小影姐` và `柳轻锋 → 柳月影` là hai dòng) cho tới khi người dùng duyệt gộp. Biến thể khác chữ (`小峰`) không có gợi ý.
- Tự xưng bằng danh xưng thân tộc (`陪陪姐姐`, người nói tự gọi mình là 姐姐) được ghi là "nhắc tới", không phải hô gọi; Bước 2 cần nhận diện kiểu tự xưng này vì nó quyết định cách dịch "chị/em".

### Bước 1 · kiểm chứng trên truyện thứ hai, khác thể loại và văn phong (2026-10-20)

Truyện ngôn tình đô thị *隐婚100分* (20 chương đầu): lời thoại dùng `“”`, không ngắt dòng cứng, và hay viết "câu kể → lời thoại đứng riêng một đoạn". Bản Bước 1 đầu tiên bỏ sót nữ chính 宁夕 (họ `宁` không có trong danh sách họ) và chỉ gán được người nói cho 10/300 lời thoại. Các sửa đổi đều theo hướng chung cho mọi thể loại, không chỉnh riêng cho truyện này:

- **Danh sách họ hai tầng** (`ChineseSurnames`):
  - Thêm khoảng 400 họ đơn và hơn 60 họ kép (võ hiệp, cổ đại).
  - Các chữ chủ yếu là hư từ (和 / 时 / 于 / 向 / 全 / 那 / 但 / 由 / 来 / 都) bị loại hẳn.
  - Họ **tầng mở rộng** (连 / 简 / 修 / 车 / 尤 / 丰 / 冷 / 花…) mở đầu nhiều từ thường (连忙, 简单, 修长, 车子…). Tên có họ này chỉ được nhận là người khi có bằng chứng chỉ người mới có: được giới thiệu tên, vai thân tộc, hoặc vừa làm chủ ngữ vừa được gọi tên trong thoại.
- **Nhiễu từ vựng:** thán từ (哈哈, 呵呵), cụm chỉ nơi chốn (沙发上, 车窗外), và một số từ thường mở đầu bằng họ tầng phổ biến (明白, 麻烦, 宁可, 岳父…).
- **Tổ chức, địa danh** bị lọc khỏi ứng viên: 集团 / 公司 / 酒店 / 宗 / 门派 / 阁 / 王朝….
  - Không lọc theo `国 / 城 / 山 / 宫 / 谷 / 楼 / 氏`, vì các chữ này kết thúc tên người: 张建国, 花满楼, 王氏 (= bà Vương trong truyện cổ đại).
- **Mức bằng chứng mới `PreviousNarration`** (yếu hơn `Explicit`):
  - Áp dụng khi lời thoại đứng riêng một đoạn ngay sau một đoạn kể thuần. Người nói là chủ ngữ là tên ở câu kể cuối.
  - Kiểm tay trên hai truyện: chỉ đúng khoảng một nửa. CPU không theo dõi được lượt đối đáp.
  - Vì vậy suy đoán bị **bỏ** nếu câu thoại gọi tên chính người được đoán là người nói, và lời thoại chỉ là tiếng động (砰, 啪…) thì không có người nói.
  - Dòng `PreviousNarration` chỉ vào nhiệm vụ AI theo cặp khi có người nghe được gọi tên trong thoại.

| Chỉ số | test cpu trước → sau | 隐婚100分 trước → sau |
|---|---|---|
| Tên ứng viên (sau lọc) | 19 → 20 (đều là tên thật) | 10 → 10 (thêm 宁夕, 宁雪落, 孟长歌; bỏ 陆氏集团 và nhiễu) |
| Người nói `Explicit` | 423 → 423 | 10 → 20 |
| Người nói `PreviousNarration` | — → 25 | — → 16 |
| Nhiệm vụ AI (cặp / nhân vật) | 13 / 11 → 13 / 12 | 0 / 7 → 1 / 8 |

**Giới hạn còn lại.** Với truyện viết theo kiểu đối đáp liên tục, không có động từ dẫn thoại (như 隐婚100分), CPU không gán được người nói cho đa số lời thoại. Bước 2b dưới đây xử lý trường hợp này.

### Bước 2b — AI theo cảnh: xác định lượt nói (đã triển khai, mới test bằng mock)

Dùng cho truyện viết đối đáp liên tục mà CPU không gán được người nói.

- **CPU dựng cảnh** (`AddressingDialogueSurvey.Scenes`): các lời thoại liên tiếp trong cùng chương.
  - Hai lời thoại thuộc cùng cảnh khi giữa chúng có tối đa **một** đoạn kể, dài tối đa 160 ký tự. Mỗi cảnh tối đa 10 lời thoại và khoảng 1.400 ký tự.
  - Giữ nguyên dấu xuống đoạn, vì xuống đoạn thường là đổi người nói.
  - Ngữ cảnh trước cảnh: phần dẫn của lời thoại đầu cùng tối đa 2 đoạn kể trước đó (≤240 ký tự). Phần này được cắt ở ranh giới câu và không bao giờ lấn sang lời thoại của cảnh trước.
- **Chọn cảnh** (`AddressingAiSurvey.PlanScenes`):
  - Chỉ chọn cảnh có ít nhất 2 lời thoại CPU chưa gán được người nói và ít nhất 2 người tham gia.
  - Người tham gia là tên có trong cảnh hoặc ngữ cảnh. Biến thể tên được quy về tên đầy đủ, tối đa 8 người.
  - Cảnh được trải đều theo chương (xoay vòng), mỗi lần chạy tối đa 40 cảnh (`MaximumScenes`).
- **AI nhận:**
  - nguyên văn cả cảnh, mỗi lời thoại có nhãn `⟦Qn⟧` (nhãn có sẵn trong văn bản gốc bị xóa nên không giả mạo được);
  - danh sách người tham gia, kèm biến thể tên do CPU tìm được.
- **AI trả lời từng lời thoại:**
  - người nói và người nghe, viết đúng một tên **trong danh sách**;
  - `""` nếu văn bản không đủ rõ hoặc người nói là nhân vật phụ không tên;
  - từ tự xưng và từ gọi tiếng Trung, chép nguyên văn từ chính câu đó.
- **Kiểm chứng CPU theo từng lượt.** Lượt sai bị loại và ghi mã lỗi; các lượt khác vẫn dùng được.
  - `UnknownQuote` / `DuplicateQuote`: `quote_id` không có thật, hoặc một lời thoại được trả lời hai lần.
  - `UnlistedParticipant`: tên không có trong danh sách người tham gia.
  - `SpeakerIsListener`: người nói trùng người nghe.
  - `UnquotedChineseTerm`: từ tiếng Trung không có nguyên văn trong câu.
  - `SpeakerNamedInOwnQuote`: người nói bị gọi tên ngay trong câu của mình.
  - `ListenerConflictsWithVocative`: người nghe không phải người được gọi tên trong câu.
  - `ConflictsWithCpuSpeaker`: trái với người nói `Explicit` mà CPU đã tìm ra.
  - `TooManyRejectedTurns`: số lượt bị loại nhiều hơn số lượt dùng được, nên **bỏ cả cảnh**, vì có thể AI đã đọc sai cảnh.
- **Lượt đã qua kiểm chứng** (`AddressingLedgerState.SceneTurns`) trở thành bằng chứng `AiScene` khi lập lại nhiệm vụ theo cặp.
  - Lượt chỉ bổ sung chỗ CPU còn trống. Nó không thay người nói `Explicit` và không thay người nghe `Vocative`/`Clause`.
  - Lượt được khớp theo chương, offset **và hash nguyên văn lời thoại**. Nếu bạn sửa câu gốc, câu trả lời cũ không còn được áp dụng.
  - Mẫu cặp lấy từ cảnh được gắn `FROM-SCENE` trong prompt, và AI theo cặp được dặn trả `NotThisPair` nếu văn bản mâu thuẫn.
  - Cặp mới tiếp tục đi qua nhiệm vụ theo cặp (Bước 2) để quyết định xưng hô tiếng Việt, rồi tới bạn duyệt ở Bước 3 như bình thường.
- Cảnh **không** ghi trực tiếp vào hồ sơ nhân vật. Cửa sổ duyệt chỉ đếm số cảnh và số lượt, không cho duyệt từng cảnh.
- `PromptVersion` tăng lên `addressing-ledger-3`; schema có thêm mảng `scenes` (vẫn chỉ dùng tập con Gemini chấp nhận).

**Chạy thử bằng mock trên hai truyện (2026-10-20, HTTP = 0).** Mock là bộ giả lập tất định, không phải AI:

- nó giữ người nói CPU đã biết, luân phiên 2 người ở mỗi lần xuống đoạn, và trả `""` khi cảnh có hơn 2 người;
- nó cố ý cài lỗi: cứ 13 lượt có một tên lạ, cứ 17 lượt có một từ không có trong câu.

Số liệu dưới đây chỉ chứng minh đường đi và bộ kiểm chứng chạy đúng trên văn bản thật. **Không** đo độ chính xác của AI.

| | test cpu (19 ch.) | 隐婚100分 (20 ch.) |
|---|---:|---:|
| Cảnh liền mạch / được chọn | 316 / 40 | 136 / 40 |
| Lời thoại trong cảnh được chọn (CPU đã biết người nói) | 266 (69) | 166 (6) |
| Request dự kiến · token input ước tính | 7 · ~34,6 nghìn | 7 · ~20,8 nghìn |
| Cảnh qua kiểm chứng (mock) | 33/40 | 20/40 |
| Lượt dùng được / tổng | 98/266 | 52/166 |
| Lượt bị loại: tên lạ · từ không có · tự gọi tên mình · người nghe trái hô gọi | 20 · 14 · 12 · 12 | 12 · 9 · 1 · 1 |
| Nhiệm vụ cặp trước → sau | 13 → 13 | 1 → 8 |

Nhận xét:

- Mọi lỗi cài sẵn đều bị bắt.
- Ở test cpu, bộ kiểm chứng còn bắt 12 lượt mà cách luân phiên máy móc gán sai, vì người nói bị gọi tên trong chính câu của mình. Đây đúng là loại lỗi một mô hình thật có thể mắc.
- Với 隐婚100分, chỉ cần lượt nói theo cảnh là đã mở được 7 cặp mới (宁夕 ↔ 陆霆骁, 宁夕 ↔ 宁雪落, 陆霆骁 → 陆景礼…). Trước đó CPU chỉ lập được 1 cặp.
- test cpu là truyện có nội dung người lớn, nên Gemini có thể từ chối (`ProviderRefusal`) như lần chạy Bước 2 trước. Nhiệm vụ theo cảnh không thay đổi điều đó.

**Chưa làm:**

- Chưa đo độ chính xác lượt nói với AI thật.
- Mẫu "Tên + hành động：「…」" không có động từ nói, rất phổ biến trong 隐婚100分, hiện CPU vẫn để trống người nói; AI theo cảnh phải tự xác định.

### Bước 1b — CPU: cách lời kể gọi nhân vật (ngôi thứ 3)

Xưng hô trong thoại chưa đủ: lời kể ngôi thứ 3 phải gọi một nhân vật nhất quán (hắn / nàng / y / cô ấy / lão…), đúng giới tính, và có thể đổi theo truyện. Trình dịch đã có chỗ chứa: đại từ kể chuyện có khóa (`PrimaryNarrativePronoun`, `NarrativePronounLocked`) trong hồ sơ nhân vật.

Với mỗi tên ứng viên, CPU lấy các **câu kể ngoài ngoặc thoại** có nhắc tên, đếm đại từ `他`/`她`/`它` xuất hiện sau tên trong cùng câu hoặc câu kế tiếp, nhưng chỉ khi chưa có tên nhân vật khác chen vào. Kết quả là tín hiệu giới tính khách quan, kèm mẫu câu có chương và offset.

### Bước 2 — AI theo cặp và theo nhân vật (đang triển khai, chưa gọi AI thật)

Mỗi request gom vài **nhiệm vụ**, có hai loại:

- **Cặp** (người nói → người nghe): 6–12 mẫu thoại, có mệnh đề dẫn, trải đều theo chương. AI trả về:
  - trạng thái (`Consistent` / `Changes` / `Ambiguous` / `NotThisPair`);
  - quan hệ;
  - các **giai đoạn**, mỗi giai đoạn gồm: chương từ–đến, tự xưng và gọi người bằng tiếng Việt, từ gốc tiếng Trung (tự xưng, hô gọi), và các mẫu chứng minh.
- **Nhân vật** (lời kể): 6–10 câu kể, có số đếm 他/她 của CPU. AI trả về:
  - trạng thái (`Consistent` / `Changes` / `Ambiguous` / `NotACharacter`);
  - giới tính;
  - các giai đoạn, mỗi giai đoạn gồm: chương từ–đến, đại từ kể chính và phụ tiếng Việt, và các mẫu chứng minh.

Schema chỉ dùng `type/properties/required/items/enum` chuỗi (an toàn với Gemini). Số request tỷ lệ với số cặp và số nhân vật, thường vài chục cho cả truyện, không phụ thuộc số cửa sổ.

**Kiểm chứng CPU (fail-closed, không tự xác nhận):**

- `task_id` phải thuộc đúng request, mỗi nhiệm vụ chỉ được trả lời một lần.
- `sample_ids` phải thuộc đúng nhiệm vụ đó. Mỗi giai đoạn cần ít nhất một mẫu, và chương của mẫu phải nằm trong khoảng chương của giai đoạn.
- Từ gốc tiếng Trung (tự xưng, hô gọi) phải xuất hiện nguyên văn trong lời thoại của mẫu được trích.
- Khoảng chương hợp lệ (`from` ≤ `to`, `to = 0` là mở), không vượt ngoài phạm vi chương quan sát được.
- Giới tính trái với tín hiệu 他/她 rõ rệt của CPU bị đánh `GenderConflictsWithSource`.
- `Consistent`/`Changes` phải có cách gọi tiếng Việt cho từng giai đoạn. `Ambiguous`/`NotThisPair`/`NotACharacter` không tạo quy tắc.

Checkpoint theo request (`Settings/addressing-ledger.v1.json`, chỉ ghi thêm). Một nhiệm vụ là xong khi:
- đã có một đề xuất qua kiểm chứng; hoặc
- đã được trả lời 2 lần mà vẫn không qua kiểm chứng (không gửi lại mãi); hoặc
- bị provider từ chối (không bao giờ tự gửi lại, để người dùng xem tay).

Mỗi lần chạy có ngân sách request; dừng khi provider lỗi dịch vụ hoặc hết quota. Vận chuyển dùng lại `SemanticSurveyBackend`: một model đã chọn, không retry, không fallback.

### Bước 1c — CPU: các cách gọi khác nhau của một nhân vật

Một nhân vật có nhiều cách gọi (王秀兰 / 秀兰 / 小兰 / 兰儿 / 王小姐 → Vương Tú Lan / Tú Lan / Tiểu Lan…). Nếu không gom lại, quan hệ bị tách theo cách viết và tín hiệu 他/她 bị chia nhỏ.

- **Sinh biến thể từ tên đầy đủ:** tên không họ; `小/阿` + chữ cuối (hoặc chữ đầu); chữ cuối + `儿`; lặp chữ; `小X姐`; tên + `姐/哥/妹/弟`; họ + `小姐/先生/老师/公子/姑娘/夫人/少/总/姐/哥`; `老` + họ.
- **Lọc biến thể:**
  - chỉ giữ biến thể thực sự xuất hiện, không tính lần nằm bên trong tên đầy đủ;
  - bỏ biến thể trùng từ hô gọi hoặc tự xưng thông dụng (`小子`, `老子`);
  - biến thể có hơn một chủ có thể được báo là *mơ hồ* và không gán cho ai.
- **Khi lập kế hoạch Bước 2:** lời thoại và lời kể của biến thể có đúng một chủ được tính cho chủ đó (`秀兰姐` → 王秀兰). Prompt ghi rõ đây là giả thuyết CPU. AI chỉ được chọn tên trong danh sách ứng viên (`name_forms`), kèm bản dịch tiếng Việt của từng cách gọi. Bước 3 do người dùng duyệt gộp.

**Đếm 他/她 cho lời kể (bản chặt):** chỉ đếm khi
- tên đứng đầu một vế câu (vai chủ ngữ);
- đại từ mở đầu một vế sau đó hoặc câu kế tiếp trong cùng đoạn;
- không có tên khác hay danh xưng (`妈妈`, `姐姐`…) chen giữa.

Bản đếm đầu tiên cho 柳轻锋 (nam chính) ra 他 190 / 她 99. Bản chặt ra 132 / 7 (sau khi bỏ ngắt dòng cứng đúng cách).

**Kế hoạch offline cho "test cpu":** 24 nhiệm vụ (13 cặp, 11 nhân vật), 4 request, khoảng 19.300 token đầu vào ước tính. Giới tính theo nguyên văn: 柳轻锋 nam (132:7), 柳月影 nữ (0:25), 秦梦岚 nữ (2:27), 沈夕颜 nữ (2:17), 柳恒 nam (8:2). Biến thể: 小锋×179 và 轻锋×24 → 柳轻锋; 小影×70, 小影姐×55, 月影×8 → 柳月影; 夕颜×93, 夕颜姐×79 → 沈夕颜; 梦岚, 阿岚, 小岚 → 秦梦岚; 陈老师 → 陈馨玉. `柳少/柳少爷/柳总` là mơ hồ (cả 柳轻锋 và 柳月影 đều có thể là chủ).

### Bước 3 — Duyệt theo nhân vật/cặp và ghi vào hồ sơ (đang triển khai, mock)

Cửa sổ **Sổ xưng hô** mở từ *Thiết lập dịch → Nhân vật & xưng hô*. Có hai tab: **Nhân vật** và **Cặp xưng hô**. Mỗi dòng là một đề xuất của Bước 2, kèm kết quả kiểm chứng, lỗi (nếu có) và các mẫu nguyên văn AI đã dùng. Người dùng có thể sửa trước khi chấp nhận. Chỉ đề xuất đã qua kiểm chứng mới được chấp nhận. Mỗi quyết định (Chấp nhận / Bác bỏ / Để sau) được ghi thêm vào ledger.

Chấp nhận **nhân vật** ghi vào hồ sơ:

- tìm hồ sơ theo tên Trung hoặc bí danh; chưa có thì tạo mới;
- giới tính, tên Việt;
- đại từ kể chính/phụ, rồi **khóa đại từ kể**;
- bí danh: chỉ những cách gọi AI đã chọn trong danh sách CPU và người dùng giữ lại;
- glossary: mỗi cách gọi kèm bản dịch tiếng Việt.

Danh sách cách gọi cần tránh lấy theo giới tính; đại từ vừa chọn được loại khỏi danh sách đó.

Chấp nhận **cặp** ghi một quan hệ có hướng vào hồ sơ người nói: tự xưng, gọi người, nhãn quan hệ, **Bật** và **Khóa**. Hồ sơ của cả hai người phải có sẵn, nên cần duyệt nhân vật trước.

Không bao giờ ghi đè dữ liệu người dùng đã khóa hoặc đã có:

- quan hệ đã **khóa** có cách gọi khác → báo xung đột, không ghi đè;
- bí danh đã thuộc hồ sơ khác → xung đột;
- glossary đã có bản dịch khác → xung đột;
- tài nguyên đã đổi kể từ lúc nạp → phải nạp lại (kiểm tra revision của Project Resources).

**Nhiều giai đoạn:** hồ sơ hiện chưa có mốc chương, nên người dùng chọn giai đoạn áp dụng mặc định. Các giai đoạn khác được ghi vào ghi chú của hồ sơ/quan hệ và vào ledger, để Bước 4 áp dụng theo chương.

**Khóa nhất quán Project** (để trình dịch dùng hồ sơ) được bật bằng một ô chọn riêng, không tự bật khi chấp nhận.

### Bước 4 — Nối vào dịch (đang triển khai)

- **Mốc chương (đã làm).** Các trường mới đều tùy chọn; dữ liệu cũ không có chúng vẫn hoạt động như trước.
  - `CharacterRelationship` có thêm `EffectiveFromChapter`/`EffectiveToChapter`. Không có mốc thì quy tắc áp dụng cả truyện.
  - `CharacterProfile` có thêm `NarrativePhases`: đại từ kể theo khoảng chương. `PrimaryNarrativePronoun` vẫn là giá trị mặc định.
  - Khi dịch, bộ chọn quy tắc dùng số chương của chương đang dịch:
    - quy tắc có mốc chỉ áp dụng trong khoảng của nó, và không bao giờ áp dụng khi không biết số chương;
    - cùng một hướng nói, quy tắc có mốc được ưu tiên hơn quy tắc cả truyện.
  - Bước 3 ghi mọi giai đoạn AI đề xuất thành quy tắc có mốc chương. Giai đoạn được duyệt dùng giá trị bạn đã sửa.
- **Nhân vật trong cảnh (đã làm).**
  - Cảnh = 2 đoạn ngay trước trong cùng chương. Nhân vật được gọi tên ở đó là "có mặt trong cảnh".
  - Quy tắc thoại áp dụng khi đoạn đang dịch có lời thoại, và người nói cùng người nghe đều có mặt trong đoạn hoặc trong cảnh. Trước đây cả hai tên phải nằm trong chính đoạn 300–500 chữ, nên thoại nối tiếp không có quy tắc.
  - Nhân vật chỉ có mặt trong cảnh được ghi chú "(in scene, not named in this passage)". Hướng dẫn cho AI dịch vẫn yêu cầu chỉ dùng quy tắc khi vai trò rõ ràng.
- **Trình sửa hồ sơ** hiển thị và giữ nguyên khoảng chương (cột "Từ ch." / "Đến ch."). Sao chép hồ sơ và cơ chế bảo vệ quy tắc khóa đều giữ các giai đoạn đại từ kể.
- **Chưa làm:** đánh dấu sau dịch các lời thoại có cách gọi lệch sổ. Bộ chuẩn hóa đại từ sau dịch vẫn dùng đại từ kể mặc định, chưa theo chương.

### Bước 5 — Đo bằng dữ liệu thật (chưa làm)

Người dùng gán nhãn tay 2–3 chương: người nói, người nghe, tự xưng, gọi người. Đo độ chính xác gán người nói của Bước 1, độ chính xác sổ xưng hô sau Bước 2–3, và tỷ lệ lời thoại dịch đúng sổ trước/sau Bước 4.

## 5. Không nằm trong phạm vi

- Không xây lại Entity Memory/Semantic Survey; không xóa dữ liệu của chúng.
- Không tự suy luận người nói từ đại từ thành dữ kiện chắc chắn.
- Không tự ghi Project Resources ở Bước 1–2.
