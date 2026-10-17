# Gemini Multi-Model Book Survey (engine-only)

This is an experimental survey backend, not a new translation backend. Do not run live API calls until the Windows Release build and integration tests pass.

## Configuration (Windows user environment)

- `QTSTUDIO_GEMINI_API_KEY`: Google AI Studio key. Required to invoke Gemini. Never save in a project, log or prompt.
- `QTSTUDIO_GEMINI_PROJECT_ID`: actual Google AI project ID. Recommended to share rate limits across novels and multiple keys in the same project. If absent, a one-way fingerprint of the key is used to share limits across novels within the running process, but it cannot identify multiple keys from the same Google project.
- `QTSTUDIO_GEMINI_SURVEY_MODELS`: comma-separated Flash-only model IDs. Default: `gemini-3.6-flash,gemini-3.7-flash,gemini-3.8-flash`. Flash Lite is rejected deliberately and reserved for a future translation API workflow.

In the existing Book Intelligence dialog, enter a model that occurs in the configured survey pool (for example `gemini-3.7-flash`). The dialog remains otherwise unchanged and asks permission before uploading source excerpts.

## Scheduling

- Each model has its own 5 RPM / 250K input TPM / 20 RPD **initial, user-observed, configurable code defaults**. Actual Google limits depend on the AI project and model. The sum 15 RPM / 750K TPM / 60 RPD is a theoretical ceiling only if those buckets are genuinely independent and no stricter shared limits apply.
- The pool can run up to two in-flight calls per model (six across three models), not six calls regardless of quota.
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
