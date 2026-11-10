# AI Book Intelligence — Session 2 Benchmark

## Method

Tool: `tools/QuickTranslator.AiEditor.BookIntelligence.Benchmark`.

The fixture deterministically generates 500 chapters and exactly 2,000,000 UTF-16 source characters. It runs the CPU scanner and compares request plans for Full Semantic and Fast Adaptive. It serializes the resulting knowledge state and samples managed memory.

This benchmark does **not** call Ollama. Request counts assume the analyzer maximum of eight evidence windows per request. Input tokens are estimated at two Chinese UTF-16 characters per token; they are not tokenizer or provider measurements.

## Result from the acceptance run

| Metric | Full Semantic | Fast Adaptive |
|---|---:|---:|
| Evidence sent/planned | 2,000 | 769 |
| Requests at 8 evidence/request | 250 | 97 |
| Estimated input tokens | 1,000,000 | 368,773 |
| Low windows skipped pending audit | 0 | 1,231 |
| Low audit samples | n/a | 269 |

- CPU scan/planning: 262 ms.
- Request reduction: approximately 61.2%.
- Estimated input-token reduction: approximately 63.1%.
- Approximate managed-memory sample: 4,822,328 bytes.
- Serialized knowledge state: 26,204,050 bytes.
- AI time and output tokens: 0 by design.

The configured Low audit probability is 15%, selected by stable hash. This highly repetitive fixture realized 269 samples, so it must not be interpreted as an exact percentage guarantee for every corpus.

## Quality metrics

The code includes reusable precision/recall/false-positive/false-negative/evidence-accuracy evaluation. The deterministic planner fixture proves metric plumbing and reports perfect Character/evidence values for its one CPU-known target, but Alias/Relationship/Glossary recall is zero because no model predictions are generated. These are not production quality claims.

A meaningful quality benchmark requires a frozen human-reviewed corpus and a named Ollama model/profile. Record that separately so model output, tokenizer, GPU, temperature, and prompt version are reproducible.

## Reproduction

```powershell
dotnet run --project tools\QuickTranslator.AiEditor.BookIntelligence.Benchmark\QuickTranslator.AiEditor.BookIntelligence.Benchmark.csproj -c Release
```

The machine-readable result is written to `artifacts/ai-book-intelligence/session2-benchmark.json`.
