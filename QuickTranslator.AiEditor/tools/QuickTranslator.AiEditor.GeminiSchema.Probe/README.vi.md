# Gemini Structured Outputs Compatibility Probe

Công cụ độc lập; không mở Project, không đọc nguyên tác/Resources/key vault, không ghi checkpoint hoặc history. Không thay đổi schema hay pipeline ứng dụng. Mặc định **offline**. Không dùng công cụ Preflight cũ (công cụ đó có budget/pool khác).

## Cách chạy trên Windows

Từ thư mục `QuickTranslator.AiEditor` của checkout:

```powershell
dotnet build tools/QuickTranslator.AiEditor.GeminiSchema.Probe -c Release

# Offline: không đọc key, không mở kết nối mạng. Xuất schema v2/config thực tế + thống kê.
dotnet run --project tools/QuickTranslator.AiEditor.GeminiSchema.Probe -c Release --no-build -- --model gemini-3.6-flash

# Chỉ khi chính bạn muốn tiêu hao tối đa một request:
dotnet run --project tools/QuickTranslator.AiEditor.GeminiSchema.Probe -c Release --no-build -- --model gemini-3.6-flash --max-output-tokens 4096 --live
```

Chọn `--max-output-tokens` bằng giới hạn đầu ra bạn dùng trong QT Studio (mặc định của probe là 4096); `generationConfig` được lấy từ cùng `GeminiSurveyClient`, không tự đổi giới hạn. Probe cũng cho phép chọn tường minh `gemini-3.8-flash`, không dùng Lite hoặc tự đổi model.

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
    dotnet run --project tools/QuickTranslator.AiEditor.GeminiSchema.Probe -c Release --no-build -- --model gemini-3.6-flash --max-output-tokens 4096 --live
}
finally {
    Remove-Item Env:QTSTUDIO_GEMINI_API_KEY -ErrorAction SilentlyContinue
    $probeCredential = $null
    $probeSecret.Dispose()
}
```

Không truyền key bằng đối số, dán vào mã nguồn, dùng `setx`, hoặc ghi key vào file/log. Tool không lưu file nào. Stdout chỉ có schema/config cố định, thống kê, mã diagnostic, usage/time nếu có; không có header, prompt hay response thô. Process có thể giữ secret và response trong bộ nhớ tạm để gửi/parse; đây không phải cam kết chống process dump/OS-level tracing. Không bật HTTP tracing hoặc transcript bên ngoài chứa secret.

## Công cụ thực sự kiểm tra gì?

`InspectAsync` gọi adapter thật với handler hoàn toàn trong bộ nhớ rồi lấy **chính `generationConfig.responseJsonSchema` và safetySettings** của request. Không tự dựng bản sao schema/config, không reflection. Schema lấy từ `SemanticSurveyContract.MultiEvidenceSchema` (v2), không phải schema v1 của Preflight cũ.

Request thật cũng dùng adapter này, với prompt trung tính cố định, hai GUID giả lập và `reviewed_evidence_ids=[]`, `findings=[]`. Không có dữ liệu Project, corpus hay nội dung người dùng nhập. Trước khi gửi, handler đối chiếu URI, prompt, config và safety với preview. Chỉ xuất schema/config, không xuất prompt. Có guard một POST duy nhất, chặn cả lỗi mạng/5xx trước cơ chế retry của adapter, không theo 3xx. Lớp kiểm thử dùng mock; phiên Codex không gọi Gemini thật.

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

Mẫu schema đơn giản HTTP 200 chứng minh cấu hình mẫu đó hoạt động, không chứng minh schema v2 hay nguyên tác đã được chấp nhận. Chưa gọi API thật với probe nên chưa xác nhận nguyên nhân HTTP 400. Số lượng required/enum, identity lặp, union nullable và array bounds là các chiều cần phân tích nếu provider báo complexity; không có ngưỡng công khai để khẳng định chỉ dựa trên số byte/node.

Theo [Structured Outputs của Google](https://ai.google.dev/gemini-api/docs/generate-content/structured-output), schema lớn/lồng sâu có thể bị từ chối; vẫn cần xác minh ngữ nghĩa cục bộ. [GenerationConfig](https://ai.google.dev/api/generate-content#v1beta.GenerationConfig) mô tả subset có `anyOf`, enum, required, maxItems, minimum/maximum; `maxLength` không được liệt kê trong subset responseJsonSchema. Đây là điểm cần xem xét, **không phải bằng chứng nó đã gây 400** (keyword không hỗ trợ cũng có thể bị bỏ qua). Tài liệu REST hiện có thêm dạng responseFormat, nhưng tool giữ nguyên wire hiện tại để tránh làm sai phép so sánh; không âm thầm đổi API/field.

Nếu probe trả 200 + response hợp lệ: schema/config được chấp nhận cho request trung tính này; chưa xác nhận chất lượng/coverage của khảo sát thực tế. Nếu vẫn `Http400Unclassified`: không được coi đó là xác nhận schema rejection. Chỉ chia sẻ report lọc an toàn, không chia sẻ key hoặc response thô.

## Đề xuất tinh gọn wire (chưa triển khai, chưa gửi thử)

1. Giữ nguyên toàn bộ trường/enum/evidence ID/range/hash, identity roles, actuality và timeline. Thử biểu diễn nullable bằng `type: [T,"null"]` thay `anyOf` tương đương, dưới một wire version/fingerprint riêng; không đổi internal contract v2 hoặc checkpoint cũ. Giữ required để missing khác với null tường minh.
2. Có thể đưa cấu trúc Identity lặp vào `$defs`/`$ref` nếu endpoint chấp nhận. Giảm byte gửi không bảo đảm giảm số trạng thái compiler (server có thể unroll); tuyệt đối không gộp giá trị identity hoặc speaker/listener.
3. Nếu bỏ `maxLength` **chỉ ở wire**, phải công khai danh sách thay đổi và giữ mọi giới hạn string trong parser strict hiện tại. Không bỏ enum, required, các giới hạn số/range, không hạ maxItems để cắt findings/coverage.
4. Mapping wire mới phải là bước tường minh, versioned và fail-closed trước `SemanticSurveyContract.Parse`/`SemanticQualityAuditor`. Thiếu trường quan trọng không được điền mặc định thành một dữ kiện; required fields phải bị reject, scope/coverage/support/timeline/identity vẫn audit riêng. Không dùng HTTP 200 để tự Confirm/Verified.
5. Mỗi biến thể chỉ thử theo một xác nhận riêng của người dùng; không tự retry schema/model hoặc né ProviderRefusal. Hiện **chưa có biến thể tinh gọn, mapper mới hay thay đổi production schema** trong patch này. Tests mapping kiểm tra contract v1/v2 hiện hữu vẫn round-trip và reject mọi finding thiếu trường.
