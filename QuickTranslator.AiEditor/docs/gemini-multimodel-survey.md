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

As of 2026-10-17, Google's model documentation marks 3.7 Flash as deprecated and automatically routes its API ID to 3.8 Flash. Keep it configurable for compatibility, but do not assume 3.7 and 3.8 provide independent provider capacity or independent effective quota. Confirm active limits in AI Studio before enabling live use.

In the existing Book Intelligence dialog, enter a model that occurs in the configured survey pool (for example `gemini-3.8-flash`). The dialog remains otherwise unchanged and asks permission before uploading source excerpts.

## Scheduling

- Each model has its own 5 RPM / 250K input TPM / 20 RPD **initial, user-observed, configurable code defaults**. Actual Google limits depend on the AI project and model. The sum 15 RPM / 750K TPM / 60 RPD is a theoretical ceiling only if those buckets are genuinely independent and no stricter shared limits apply.
- The pool enforces at most two in-flight calls per model (six across three models), even when a caller submits more parallel work. The adapter additionally enforces the same per-model bound.
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
