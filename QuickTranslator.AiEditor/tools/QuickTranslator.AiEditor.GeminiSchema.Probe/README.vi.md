# Gemini Structured Outputs Compatibility Probe

Công cụ độc lập; không mở Project, không đọc nguyên tác/Resources/key vault, không ghi checkpoint hoặc history. Không thay đổi internal schema hay pipeline ứng dụng. Mặc định **offline**, wire schema mặc định vẫn là **production**. Chỉ cờ tường minh mới chọn wire thử nghiệm. Không dùng công cụ Preflight cũ (công cụ đó có budget/pool khác).

## Cách chạy trên Windows

Từ thư mục `QuickTranslator.AiEditor` của checkout:

```powershell
dotnet build tools/QuickTranslator.AiEditor.GeminiSchema.Probe -c Release

# Offline: không đọc key, không mở kết nối mạng. Xuất schema v2/config thực tế + thống kê.
dotnet run --project tools/QuickTranslator.AiEditor.GeminiSchema.Probe -c Release --no-build -- --model gemini-3.6-flash

# Offline: thử nghiệm chỉ bỏ maxLength ở wire; vẫn HTTP=0.
dotnet run --project tools/QuickTranslator.AiEditor.GeminiSchema.Probe -c Release --no-build -- --model gemini-3.6-flash --wire-omit-max-length

# Chỉ khi chính bạn muốn gửi tối đa một request với wire thử nghiệm:
dotnet run --project tools/QuickTranslator.AiEditor.GeminiSchema.Probe -c Release --no-build -- --model gemini-3.6-flash --max-output-tokens 4096 --wire-omit-max-length --live
```

Chọn `--max-output-tokens` bằng giới hạn đầu ra bạn dùng trong QT Studio (mặc định của probe là 4096); `generationConfig` được lấy từ cùng `GeminiSurveyClient`, không tự đổi giới hạn. Probe cũng cho phép chọn tường minh `gemini-3.8-flash`, không dùng Lite hoặc tự đổi model.

`--wire-schema production` (hoặc không có cờ) là **A**, giữ schema v2 gốc. Cờ **`--wire-omit-max-length`** là **B**, chọn projection chỉ bỏ keyword `maxLength` tại schema node, không xóa field hoặc giá trị enum tên `maxLength`. Tên chế độ trong report vẫn là `GeminiCompatibleWireSchema`, variant `probe-gemini-maxlength-only-1`; không phải cam kết Gemini sẽ chấp nhận. Cờ cũ `--wire-schema gemini-compatible` vẫn là alias tương thích, không phải phép biến đổi khác. Nếu chọn cả production và omit-max-length thì lỗi cấu hình trước khi đọc key/gửi HTTP, bất kể thứ tự cờ.

Mỗi lần `--live` chỉ gửi nhánh đã chọn sau một xác nhận; **không tự chạy cả A và B**. Baseline A đã có kết quả 400 từ người dùng, không cần gửi lại A nếu chưa muốn tiêu hao thêm quota. Preview luôn so sánh hai schema hoàn toàn offline. Ngoài `maxLength`, giữ nguyên mọi property, enum (kể cả numeric enum `schema_version=[2]`), required, anyOf, additionalProperties, maxItems, minimum, maximum và các bound khác. Không loại bỏ hàng loạt keyword được Google liệt kê là hỗ trợ.

`--live` **chưa gửi gì**: công cụ in model, endpoint cố định, schema/config, budget HTTP=1 rồi chờ bạn gõ chính xác:

```text
SEND ONE NEUTRAL SCHEMA PROBE
```

Enter/rỗng/câu khác sẽ hủy với HTTP=0. Không có `--yes`, không sử dụng biến cấp quyền test API của phiên trước. Key chỉ được đọc sau xác nhận. Không có HTTP countTokens/models/metadata, retry, redirect hoặc fallback. Mỗi process chỉ thực hiện một lần; muốn thử tiếp phải chạy và xác nhận lại. Không tự thử các biến thể schema khi bị từ chối.

### API key chỉ trong phiên PowerShell

Nếu `QTSTUDIO_GEMINI_API_KEY` đã có trong phiên hiện tại thì dùng trực tiếp; đừng in giá trị. Nếu key chỉ lưu trong QT Studio, probe không tự giải mã key đó. Có thể nhập an toàn như sau:

```powershell
$probeSecret = Read-Host 'Gemini API key (ẩn)' -AsSecureString
$probeCredential = [System.Net.NetworkCredential]::new('', $probeSecret)
try {
    $env:QTSTUDIO_GEMINI_API_KEY = $probeCredential.Password
    dotnet run --project tools/QuickTranslator.AiEditor.GeminiSchema.Probe -c Release --no-build -- --model gemini-3.6-flash --max-output-tokens 4096 --wire-omit-max-length --live
}
finally {
    Remove-Item Env:QTSTUDIO_GEMINI_API_KEY -ErrorAction SilentlyContinue
    $probeCredential = $null
    $probeSecret.Dispose()
}
```

Không truyền key bằng đối số, dán vào mã nguồn, dùng `setx`, hoặc ghi key vào file/log. Tool không lưu file nào. Stdout chỉ có schema/config cố định, thống kê, mã diagnostic, usage/time nếu có; không có header, prompt hay response thô. Process có thể giữ secret và response trong bộ nhớ tạm để gửi/parse; đây không phải cam kết chống process dump/OS-level tracing. Không bật HTTP tracing hoặc transcript bên ngoài chứa secret.

## Công cụ thực sự kiểm tra gì?

`InspectAsync` gọi adapter thật với handler hoàn toàn trong bộ nhớ rồi lấy **chính `generationConfig.responseJsonSchema` và safetySettings** của request. Không tự dựng generationConfig, không reflection. Schema gốc lấy từ `SemanticSurveyContract.MultiEvidenceSchema` (v2), không phải schema v1 của Preflight cũ. Ở chế độ thử nghiệm, tạo bản sao schema bỏ `maxLength` trước khi truyền cho adapter; không sửa body sau khi adapter tạo request và không đụng singleton schema gốc.

Request thật cũng dùng adapter này, với prompt trung tính cố định, hai GUID giả lập và `reviewed_evidence_ids=[]`, `findings=[]`. Không có dữ liệu Project, corpus hay nội dung người dùng nhập. Trước khi gửi, handler đối chiếu URI, prompt, config và safety với preview. Chỉ xuất schema/config, không xuất prompt. Có guard một POST duy nhất, chặn cả lỗi mạng/5xx trước cơ chế retry của adapter, không theo 3xx. Lớp kiểm thử dùng mock; phiên Codex không gọi Gemini thật.

Response vẫn phải đi qua **`SemanticSurveyContract.Parse` với internal schema gốc**, kể cả khi wire không còn `maxLength`. Text quá dài hoặc thiếu trường vẫn bị reject. Không có mapper điền mặc định, đổi evidence IDs, gộp identity/quan hệ hoặc tự Confirm. `SemanticQualityAuditor`, source provenance, timeline và checkpoint không thay đổi. `Comparison` trong preview là metadata cục bộ, không gửi sang provider; request chỉ chứa schema wire được chọn cùng cấu hình production còn lại.

`ProviderAcceptedRequest=true` chỉ nghĩa là HTTP 2xx, **không** nghĩa dữ kiện đúng. `ResponseContractValid=true` chỉ khi JSON được parser strict hiện tại chấp nhận, đúng v2/GUID giả lập và không bịa findings/evidence. Không tạo proposal, không gọi promotion/confirm. 200 nhưng sai/thiếu trường vẫn báo lỗi contract. Usage chỉ là token provider trả về; `Utf8Bytes` của schema **không phải số token**.

## Phân loại HTTP 400 an toàn

Đọc tối đa 32 KiB error envelope, độ sâu JSON tối đa 24. Không lưu/in message, field description, response, URL do provider trả về, mã request/project hoặc header. Chỉ dùng:

- `Http400SchemaRejected`: violation có prefix schema hoặc message có tín hiệu schema bị từ chối; `SchemaHints` nêu rõ nguồn suy luận (`SchemaFieldViolation`, `SchemaComplexityRejected`, `SchemaRejectedByMessage`). Không suy diễn chính xác keyword gây lỗi từ một message chung.
- `SchemaPaths`: chỉ đường dẫn tồn tại trong schema của repository; suffix lạ được thay bằng `$schema`, không phản chiếu giá trị provider.
- `Keyword:maxLength` và các keyword biết trước: chỉ xuất khi provider nhắc keyword đã nằm trong schema; không xuất token lạ.
- `Http400GenerationConfigRejected`, `Http400SafetySettingsRejected`: field violation thuộc phần tương ứng, chưa kết luận lỗi nội dung schema.
- `Http400Unclassified`: kể cả `INVALID_ARGUMENT` nếu không có bằng chứng cụ thể. Response không phải JSON cũng giữ mã này; quá 32 KiB là `Http400BodyOversized`.

HTTP 401/403/404/429/5xx, mạng, timeout/refusal đều dừng sau request này. `ApprovalAlreadyUsed` ngăn dùng lại cùng session sau lỗi/refusal. Mã HTTP 400 cũ không có response đã lọc không thể khôi phục nguyên nhân.

## Kiểm kê schema v2 và phân tích hiện tại

SHA-256: `8011DE631259296A22FEF08A211C3B82FAEA12A559D206869EB3FFBB569D93E2`.

- 6.183 byte UTF-8, 94 schema node, độ sâu cấu trúc tối đa 6 (không phải tổng số JSON container).
- 5 trường ở envelope, 25 trường mỗi finding; 59 lượt thuộc tính và 59 lượt required trên 8 object đóng (`additionalProperties=false`).
- 18 enum, tổng 111 lựa chọn khi tính cả phần identity lặp lại; enum lớn nhất là relationship_type với 23 giá trị.
- 13 `anyOf` nullable. Identity có cấu trúc lặp ở subject/target/speaker/listener/verified participants; không thể tự coi các vai đó là cùng một nhân vật.
- 28 `maxLength`, 6 `minimum`, 2 `maximum`, 8 `maxItems`. Findings tối đa 64, reviewed evidence tối đa 16; support/participants/source IDs tối đa 8, CPU IDs 16.
- Request Flash 3.6/3.8 hiện dùng `responseJsonSchema`, `responseMimeType=application/json`, `thinkingLevel=low`, 4 adjustable safety filters `OFF`; không gửi thêm `responseSchema` hoặc temperature. Đây là cấu hình hiện tại được tái hiện, không phải cách tránh core safety.

Theo kết quả thực tế người dùng cung cấp: Gemini 3.6 Flash với schema đơn giản trả 200, production v2 trả 400 INVALID_ARGUMENT/Http400Unclassified, không có path/hint. Phiên Codex không gọi API thật. Hiện chưa xác nhận `maxLength` là nguyên nhân 400. Số lượng required/enum, identity lặp, union nullable và array bounds là các chiều cần phân tích nếu provider báo complexity; không có ngưỡng công khai để khẳng định chỉ dựa trên số byte/node.

Theo [Structured Outputs của Google](https://ai.google.dev/gemini-api/docs/generate-content/structured-output), schema lớn/lồng sâu có thể bị từ chối; vẫn cần xác minh ngữ nghĩa cục bộ. [GenerationConfig](https://ai.google.dev/api/generate-content#v1beta.GenerationConfig) mô tả subset có `anyOf`, enum, required, maxItems, minimum/maximum; `maxLength` không được liệt kê trong subset responseJsonSchema. Đây là điểm cần xem xét, **không phải bằng chứng nó đã gây 400** (keyword không hỗ trợ cũng có thể bị bỏ qua). Tài liệu REST hiện có thêm dạng responseFormat, nhưng tool giữ nguyên wire hiện tại để tránh làm sai phép so sánh; không âm thầm đổi API/field.

Nếu probe trả 200 + response hợp lệ: schema/config được chấp nhận cho request trung tính này; chưa xác nhận chất lượng/coverage của khảo sát thực tế. Nếu vẫn `Http400Unclassified`: không được coi đó là xác nhận schema rejection. Chỉ chia sẻ report lọc an toàn, không chia sẻ key hoặc response thô.

## Kiểm kê keyword đối chiếu tài liệu (snapshot 2026-10-20)

Đối chiếu subset `responseJsonSchema` trong [GenerationConfig chính thức](https://ai.google.dev/api/generate-content#v1beta.GenerationConfig). `ListedByGemini` chỉ nghĩa là được liệt kê, không chứng minh tổ hợp schema/model thực tế sẽ được chấp nhận. Snapshot được biên dịch trong tool, không tải tài liệu hoặc metadata qua HTTP lúc chạy.

| Keyword v2 | Số lần | Được liệt kê | Wire thử nghiệm |
| --- | ---: | --- | --- |
| `additionalProperties` | 8 | Có | Giữ nguyên |
| `anyOf` | 13 | Có | Giữ nguyên |
| `enum` | 18 | Có | Giữ nguyên |
| `items` | 8 | Có | Giữ nguyên |
| `maxItems` | 8 | Có | Giữ nguyên |
| `maxLength` | 28 | Không | Chỉ bỏ ở wire |
| `maximum` | 2 | Có | Giữ nguyên |
| `minimum` | 6 | Có | Giữ nguyên |
| `properties` | 8 | Có | Giữ nguyên |
| `required` | 8 | Có | Giữ nguyên |
| `type` | 81 | Có | Giữ nguyên |

Không có keyword v2 nào khác. Tài liệu nói keyword không hỗ trợ có thể bị bỏ qua; bỏ `maxLength` là phép thử có kiểm soát, không phải kết luận nguyên nhân. Không tự loại những keyword khác nếu danh sách hỗ trợ thay đổi.

## Diff schema A/B offline (chỉ khác maxLength)

| Thuộc tính | A: Production v2 | B: --wire-omit-max-length |
| --- | ---: | ---: |
| UTF-8 bytes (không phải token) | 6.183 | 5.751 |
| Schema node / depth | 94 / 6 | 94 / 6 |
| Field mỗi finding / lượt required | 25 / 59 | 25 / 59 |
| Keyword khác nhau | 11 | 10 |
| `maxLength` | 28 | 0 |
| `anyOf` / enum / maxItems | 13 / 18 / 8 | 13 / 18 / 8 |

Production SHA-256: `8011DE631259296A22FEF08A211C3B82FAEA12A559D206869EB3FFBB569D93E2`.

Wire thử nghiệm SHA-256: `73BB1273C63AC1911362116627851E7647E560C160BE32F9C667F6EA7039567D`.

Diff duy nhất là 28 constraint `maxLength`, tổng 432 byte. Preview có danh sách từng path/giá trị bị bỏ, hai hash, kích thước, keyword inventory và các bound còn lại. Không bỏ field, enum, required entry, giới hạn mảng/số; không đổi schema_version=2 trong JSON response. Variant version/hash là metadata của probe, không migration hay fingerprint checkpoint production. Tất cả 28 giới hạn string vẫn giữ trong internal parser.

## Nếu bỏ maxLength vẫn trả 400: thiết kế chẩn đoán tiếp theo (chưa triển khai/gửi)

1. Đối chiếu report mode/hash với wire 5.751 byte và đúng model/cap/low/safety/prompt trung tính trước. Một lỗi 400 chung không đủ chứng minh compiler complexity hoặc xác định keyword gây lỗi; không tự thử lại hay tự lấy metadata.
   Không kết luận anyOf, additionalProperties, maxItems, minimum/maximum hoặc numeric enum là unsupported: chúng vẫn được giữ nguyên và có trong tài liệu. Không có phép biến đổi bổ sung nào được triển khai trong bản vá cờ A/B này.
2. Nếu người dùng cho phép một thử nghiệm tiếp theo, ưu tiên biến thể **nullable type-array** thay từng `anyOf` nullable tương đương. Giữ đủ field, required, enum, role identity, timeline và mọi bound khác; so sánh đầy đủ schema cùng tests local-equivalence trước một xác nhận/POST riêng. Không tự chuyển sang biến thể này sau lỗi.
3. Một hướng khác, chỉ thử riêng sau xác nhận: **`$defs/$ref` cho Identity** thay phần cấu trúc lặp. Không hợp nhất giá trị subject/target/speaker/listener; không giảm findings=64 hay các giới hạn evidence/participants để làm schema dễ hơn. Ref có thể bị compiler unroll nên không đảm bảo giảm complexity.
4. Giữ ma trận một biến thay đổi/lần, model và generationConfig còn lại cố định; báo hash, node/depth, union, enum cardinality và response code đã lọc. Không có vòng lặp A/B, retry, đổi model hoặc né ProviderRefusal. Nếu cần gửi thông tin cho provider, chỉ dùng inventory/hash/code an toàn sau khi người dùng đồng ý.
5. Không áp dụng vào production chỉ vì probe trả 200. Cần kết quả Gemini thực tế, regression evidence/coverage/identity/timeline, checkpoint compatibility và quyết định cập nhật riêng. Không nới parser/auditor, bỏ required, giảm enum hay cắt coverage trong bất kỳ bước nào.

## Các nguyên tắc cho wire tương lai

1. Giữ nguyên toàn bộ trường/enum/evidence ID/range/hash, identity roles, actuality và timeline. Thử biểu diễn nullable bằng `type: [T,"null"]` thay `anyOf` tương đương, dưới một wire version/fingerprint riêng; không đổi internal contract v2 hoặc checkpoint cũ. Giữ required để missing khác với null tường minh.
2. Có thể đưa cấu trúc Identity lặp vào `$defs`/`$ref` nếu endpoint chấp nhận. Giảm byte gửi không bảo đảm giảm số trạng thái compiler (server có thể unroll); tuyệt đối không gộp giá trị identity hoặc speaker/listener.
3. Biến thể `maxLength` hiện có **chỉ ở probe wire** đã công khai danh sách thay đổi; mọi giới hạn string giữ trong parser strict. Không bỏ enum, required, các giới hạn số/range, không hạ maxItems để cắt findings/coverage.
4. Mapping wire mới phải là bước tường minh, versioned và fail-closed trước `SemanticSurveyContract.Parse`/`SemanticQualityAuditor`. Thiếu trường quan trọng không được điền mặc định thành một dữ kiện; required fields phải bị reject, scope/coverage/support/timeline/identity vẫn audit riêng. Không dùng HTTP 200 để tự Confirm/Verified.
5. Mỗi biến thể chỉ thử theo một xác nhận riêng của người dùng; không tự retry schema/model hoặc né ProviderRefusal. Hiện chỉ có **một projection bỏ maxLength trong probe**, chưa có nullable/ref variant, mapper mới hoặc thay đổi production schema. Tests so sánh toàn bộ cây, kiểm tra mode gửi thực tế, contract v1/v2 round-trip, required/giới hạn string và quality audit không bị nới.

## Sửa production: Gemini wire `gemini-semantic-wire-1` (2026-10-20)

Nguyên nhân gốc HTTP 400: schema nội bộ v2 được gửi nguyên văn làm `responseJsonSchema`. Nó dùng các cấu trúc nằm ngoài tập lõi mà tài liệu Structured Outputs hiện tại mô tả (`anyOf` không được liệt kê; nullable được mô tả bằng type-array): 13 union `anyOf` nullable (5 bản sao object Identity lồng nhau), numeric `enum` `schema_version=[2]`, và mảng có `maxItems=64` của object 25 trường bắt buộc. Bỏ riêng `maxLength` (thử nghiệm B) không đủ. Trình biên dịch grammar của Gemini từ chối với `INVALID_ARGUMENT` không kèm path.

Sửa: `SemanticSurveyBackend.Gemini` không gửi schema nội bộ nữa mà gửi `GeminiSemanticWire.Schema` (3.327 byte, 45 node, 0 `anyOf`, chỉ `type/properties/required/items` + enum chuỗi, không bound), kèm phần hướng dẫn wire nối vào cuối prompt. Ánh xạ:

- `subject/target/speaker/listener_identity` + `verified_participants` → một mảng `identities[]` có `role` (Subject/Target/Speaker/Listener tối đa 1 lần; Participant theo thứ tự). Trùng role → reject, không tự chọn.
- `entity_id` null → `""`; chương null → `0`; `event_type` null → `"None"`.
- Mọi giới hạn (độ dài, maxItems, min/max, GUID, enum) vẫn do `SemanticSurveyContract.Parse` kiểm tra **sau** khi `GeminiSemanticWire.Decode` dựng lại JSON nội bộ v2. Decoder strict: thiếu/thừa/trùng trường, sai version, role lạ → `SemanticSchemaViolation`, không có proposal.
- Engine, batch ID, checkpoint/resume, `SemanticQualityAuditor` và Ollama không đổi. Proposal AI vẫn chưa được xác nhận (không có Review).

## Smoke test thật (chỉ chạy sau khi người dùng xác nhận)

```powershell
# Offline, HTTP=0: xem schema/config của wire production
dotnet run --project tools/QuickTranslator.AiEditor.GeminiSchema.Probe -c Release --no-build -- --model gemini-3.6-flash --wire-schema gemini-semantic-wire

# (1) Một POST trung tính, không dữ liệu Project — gõ: SEND ONE NEUTRAL SCHEMA PROBE
dotnet run --project tools/QuickTranslator.AiEditor.GeminiSchema.Probe -c Release --no-build -- --model gemini-3.6-flash --wire-schema gemini-semantic-wire --live

# (2) Một POST end-to-end qua engine thật trên Project TỔNG HỢP MỚI trong %TEMP% (không mở Project của bạn) — gõ: SEND ONE SYNTHETIC SEMANTIC SURVEY
dotnet run --project tools/QuickTranslator.AiEditor.GeminiSchema.Probe -c Release --no-build -- --model gemini-3.6-flash --synthetic-survey --live
```

(2) chạy đúng Plan → Engine → Backend Gemini → Decode → Parse → Auditor → checkpoint, với 2 evidence (wire v2). Có 3 lớp chặn để chỉ gửi tối đa 1 request (ngân sách engine 1, transport 1, guard 1 POST); không retry/fallback. Report chỉ có mã/số đếm, không có key, prompt hay response thô. Project tạm được giữ lại để kiểm tra.

### Kết quả thực tế (người dùng chạy, 2026-10-20, gemini-3.6-flash, 4096 output)

- (1) Neutral `--wire-schema gemini-semantic-wire --live`: **HTTP 200**, `SchemaAcceptedNeutralResponseValid`, `ResponseContractValid=true`, 1 request, wire SHA-256 `5AAF58AB5A36900DAB6209AC1346EB7318EADC88D2145E30AA0224422BF45272`, 492 input / 99 output token, 1,8 s. Cùng model/config/safety, schema v2 cũ trả 400 → xác nhận lỗi 400 do schema và đã được sửa.
- (2) `--synthetic-survey --live` lần 1: **HTTP 503** (dịch vụ Gemini không khả dụng, không phải từ chối schema), 1 request, không retry; checkpoint ghi `ReservedBeforeSend` → `Http503`, batch `Failed`, 0 proposal, không có key trong file. Chưa xác minh end-to-end findings/evidence/proposal với API thật; cần chạy lại (2) một lần khi dịch vụ ổn định.
