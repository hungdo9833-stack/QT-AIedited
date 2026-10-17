# Gemini Multi-Model Book Survey (engine-only)

This is an experimental survey backend, not a new translation backend. Do not run live API calls until the Windows Release build and integration tests pass.

## App configuration (one API key, no PowerShell)

In QT Studio: **Settings → AI & Performance → Gemini API**, paste the API key in the masked field and select **Save API key**. It is stored under the signed-in Windows user in **Windows Credential Manager** (target `QTStudio.GeminiSurvey.ApiKey.v1`), not in the novel Project or repository. **Remove saved key** deletes that credential. The field does not display or reveal the saved secret.

Open a Project and go to **Characters & Addressing → Survey with AI**. When a saved key exists, the survey dialog defaults to `gemini-3.8-flash` unless a Gemini model was already selected. Confirm before the first cloud survey. All other Ollama translation settings stay unchanged.

For legacy scripts only, `QTSTUDIO_GEMINI_API_KEY` is an optional fallback when the Windows credential is absent; a saved app credential has priority. Remove previously stored Windows User environment keys after upgrading if they are no longer needed.

## Advanced overrides (optional environment variables)

- `QTSTUDIO_GEMINI_API_KEY`: optional legacy fallback for scripts; not needed in normal application use.
- `QTSTUDIO_GEMINI_PROJECT_ID`: actual Google AI project ID. Recommended to share rate limits across novels and multiple keys in the same project. If absent, a one-way fingerprint of the key is used to share limits across novels within the running process, but it cannot identify multiple keys from the same Google project.
- `QTSTUDIO_GEMINI_SURVEY_MODELS`: comma-separated Flash-only model IDs. Default: `gemini-3.8-flash,gemini-3.6-flash`. Flash Lite is rejected deliberately and reserved for a future translation API workflow.
- `QTSTUDIO_GEMINI_SURVEY_MAX_REQUESTS`: optional hard per-run HTTP request budget. It counts initial calls, retries and model fallbacks; when exhausted, the current batch remains uncommitted for Resume. Use `5` for the first live preflight.

As of 2026-10-17, Google's model documentation marks 3.7 Flash as deprecated and automatically routes its API ID to 3.8 Flash. It remains accepted when explicitly configured for compatibility, but is not in the default pool and must not be treated as independent provider capacity or quota. Confirm active limits in AI Studio before enabling live use.

In the existing Book Intelligence dialog, select **Gemini Flash Pool**. The preferred model is filled from the first configured pool entry (3.8 by default), while 3.6 remains available for fallback. The dialog shows whether the key and request budget are configured without displaying the key, and asks permission before uploading source excerpts.

## Tiếp tục và khảo sát lại

Trong cửa sổ `Khảo sát bằng AI`:

- **Bắt đầu / Tiếp tục** chỉ phân tích chứng cứ mới hoặc những batch chưa hoàn thành. Khi tất cả đoạn đã đánh dấu `AiAnalyzed=true`, không gửi request mới, kể cả khi đổi dropdown model. Đây là hành vi Resume an toàn.
- **Khảo sát lại** là hành động riêng có xác nhận, thực sự chạy lại AI cho các đoạn đủ điều kiện (FastAdaptive hoặc FullSemantic). Trước khi làm mới checkpoint, ứng dụng lưu bản sao trạng thái JSON vào `Project/Settings/ai-book-intelligence-snapshots/survey-*.json`. Không cần xóa Project.
- Đề xuất chưa duyệt/đã loại được dọn khỏi hàng chờ hiện tại sau khi tạo bản sao; tài nguyên đã chấp nhận, mốc xưng hô đã duyệt, thông tin quan hệ confirmed/accepted vẫn được giữ nguyên. Khi đối soát commit còn dở, ứng dụng từ chối khảo sát lại để bảo vệ dữ liệu.
- Quota API vẫn bị tiêu thụ khi khảo sát lại. Nếu hủy sau khi đã reset, `Bắt đầu / Tiếp tục` tiếp tục được lượt mới; snapshot cũ vẫn tồn tại.

Không tự động gọi lại API khi đổi model hoặc mở lại Project. Dùng `Khảo sát lại` khi cần benchmark model khác trên cùng nguyên tác.

## Phản hồi không có candidates / nội dung bị Google từ chối

Google HTTP 200 nhưng không có `candidates` không tự động đồng nghĩa với bị chặn. Adapter đọc `promptFeedback.blockReason` và `candidate.finishReason` theo whitelist, không in response thô hoặc API key.

- Nếu Google xác nhận một lý do chặn (ví dụ `SAFETY`, `PROHIBITED_CONTENT`), batch liên quan được lưu với `AiBlocked=true`, `AiBlockReason` là mã an toàn. Các batch khác tiếp tục khảo sát. **Không** tự retry batch bị chặn, tự đổi model để vượt bộ lọc, hoặc đánh dấu `AiAnalyzed=true` cho đoạn bị từ chối.
- Các đoạn bị chặn được liệt kê trong nút **Đoạn bị chặn** cùng chapter/vị trí UTF-16 để người dùng mở nguyên tác kiểm tra thủ công. Mỗi lần Resume không gửi lại chúng. Phải chọn **Khảo sát lại** có xác nhận (và sao lưu) mới xóa trạng thái chặn.
- Nếu HTTP 200 thiếu candidate nhưng **không có blockReason rõ ràng**, hoặc có `MAX_TOKENS`/lỗi định dạng, hiện lỗi có mã chẩn đoán và để batch chưa hoàn thành. Không tự coi là an toàn hay thành công. Người dùng có thể điều tra rồi bấm Resume.
- Con số `AiBlocked` là **số excerpt chưa khảo sát**, không phải số chứng cứ đã được xác nhận. Khi đánh giá precision/recall cần tính riêng tỷ lệ bỏ sót do provider từ chối.

## Scheduling

- Each model has its own 5 RPM / 250K input TPM / 20 RPD **initial, user-observed, configurable code defaults**. Actual Google limits depend on the AI project and model. The sum of model limits is only a theoretical ceiling if those buckets are genuinely independent and no stricter shared limits apply.
- The pool enforces at most two in-flight calls per model, even when a caller submits more parallel work. The adapter additionally enforces the same per-model bound.
- A single batch is submitted to one model. On 429, this model enters a cooldown of at least 60 seconds. On RPD exhaustion, 403 or 404, the model is disabled for the current survey session; another configured Flash may try the not-yet-committed batch.
- If all models become unavailable, the survey stops. The analyzer keeps previously committed evidence and does not automatically use Flash Lite or paid models.
- The Google API may have quota usage outside QT Studio. Counters are best-effort local reservations and are not proof that provider quota remains.

## Adaptive token batching

- Ollama retains its legacy maximum of 8 evidence excerpts/request.
- The Flash pool aims for <=40K estimated input tokens for simple evidence, with a 900-token overhead reservation; it also reserves space for structured output.
- Relationship/identity/dialogue evidence is capped at 12 excerpts/14K estimated input tokens. Other High evidence is capped at 20/19K; Medium at 36; Low at 48. The configured input budget and model context window further bound all of them.
- These are conservative heuristics, **not exact Gemini token counts**. Validate on real texts, inspect truncation, and adjust if extraction recall falls.

## Required acceptance before use with real billing

`dotnet test QuickTranslator.AiEditor/QuickTranslator.AiEditor.slnx`

`dotnet build QuickTranslator.AiEditor/QuickTranslator.AiEditor.slnx -c Release`

Then perform an explicitly authorized live test, beginning with small excerpts. Compare extracted relationships, aliases, evidence accuracy, rate-limit behavior and interruption/resume. No real API test was run while authoring this change.

For a deterministic 200,000-Chinese-character comparison without API quota:

`dotnet run --project QuickTranslator.AiEditor/tools/QuickTranslator.AiEditor.BookIntelligence.Benchmark -c Release -- 50 4000 QuickTranslator.AiEditor/artifacts/benchmarks/gemini-multimodel-adaptive-200k.json 5`

The report includes wall time, requests, token estimates, observed concurrency, checkpoint completion, and evidence-validated extraction precision/recall for sequential and parallel adaptive batching.
