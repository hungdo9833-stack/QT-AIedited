using System.Text;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed record SemanticBatchingOptions(int MaximumEvidencePerRequest = 4, int MaximumReservedTokensPerRequest = int.MaxValue)
{
    public void Validate()
    {
        if (MaximumEvidencePerRequest is < 1 or > 6 || MaximumReservedTokensPerRequest < 1) throw new ArgumentOutOfRangeException(nameof(MaximumEvidencePerRequest));
    }
}

/// <summary>Ordered, chapter-local packing of independent evidence. No identity unification.</summary>
public static class SemanticAdaptiveBatching
{
    public const string PromptVersion = "semantic-multi-evidence-2";
    public static IReadOnlyList<SemanticSurveyRequest> Pack(IEnumerable<SemanticSurveyRequest> inputs, ModelOptions options,
        SemanticBatchingOptions settings, CancellationToken ct = default)
    {
        settings.Validate(); options.Validate(); var result = new List<SemanticSurveyRequest>(); var pending = new List<SemanticSurveyRequest>();
        void Flush() { if (pending.Count > 0) result.Add(Combine(pending, options)); pending.Clear(); }
        foreach (var original in inputs.SelectMany(x => x.Units).OrderBy(x => x.ChapterNumber).ThenBy(x => x.Evidence.ChapterId).ThenBy(x => x.Evidence.StartUtf16))
        {
            ct.ThrowIfCancellationRequested();
            var unit = original with { MaximumEvidencePerBatch = settings.MaximumEvidencePerRequest, MaximumBatchReserve = settings.MaximumReservedTokensPerRequest };
            if (pending.Count > 0)
            {
                var first = pending[0];
                bool same = first.ProjectId == unit.ProjectId && first.Evidence.ChapterId == unit.Evidence.ChapterId && first.Evidence.SourceSha256 == unit.Evidence.SourceSha256 &&
                    first.BackendSignature == unit.BackendSignature && first.SharedContextJson.Length > 0 && first.SharedContextJson == unit.SharedContextJson &&
                    first.DependencyFingerprint == unit.DependencyFingerprint && unit.Evidence.StartUtf16 == pending[^1].Evidence.StartUtf16 + pending[^1].Evidence.LengthUtf16;
                int limit = pending.Any(x => x.Complex) || unit.Complex ? Math.Min(2, settings.MaximumEvidencePerRequest) : settings.MaximumEvidencePerRequest;
                if (!same || pending.Count >= limit) Flush();
                else
                {
                    var trial = Combine(pending.Append(unit).ToArray(), options);
                    // Leave headroom for estimation error and keep the existing output cap.
                    if (trial.EstimatedInputTokens + options.MaximumOutputTokens > options.ContextSize * .85 || trial.ReservedTokens > settings.MaximumReservedTokensPerRequest || trial.CpuCandidates.Count > 16) Flush();
                }
            }
            pending.Add(unit);
        }
        Flush(); return result;
    }

    public static SemanticSurveyRequest Combine(IReadOnlyList<SemanticSurveyRequest> units, ModelOptions options)
    {
        if (units.Count == 1) return units[0];
        var first = units[0];
        string instructions = first.Prompt[..first.Prompt.IndexOf("CONTEXT (", StringComparison.Ordinal)];
        // Keep the proven semantic instructions, changing only the scope/coverage contract.
        instructions = instructions.Replace("schema_version=1", "schema_version=2", StringComparison.Ordinal)
            .Replace($"{SemanticSurveyContract.Version} / {SemanticSurveyRequests.PromptVersion}", $"{SemanticSurveyContract.MultiEvidenceVersion} / {PromptVersion}", StringComparison.Ordinal)
            .Replace("together on this evidence.", "together on each evidence independently.", StringComparison.Ordinal)
            .Replace("within evidence " + first.Evidence.Id, "within the corresponding evidence_id", StringComparison.Ordinal);
        var text = new StringBuilder(instructions);
        text.AppendLine($"MULTI-EVIDENCE {PromptVersion}. Return reviewed_evidence_ids ONLY for evidence actually reviewed. Missing evidence stays Unprocessed.");
        text.AppendLine("Evidence IDs/ranges are separate. Never transfer a quote, identity, speaker or listener between excerpts. Co-occurrence is NOT a relationship. Homonyms remain separate local_id values unless explicit connecting proof is quoted. A cross-boundary event needs support from BOTH evidence IDs and an explicit connection, otherwise mark Unresolved. Do not infer continuity from adjacency alone.");
        text.AppendLine("CONTEXT (one identical confirmed, chapter-safe snapshot shared by all evidence):").AppendLine(first.SharedContextJson);
        foreach (var unit in units)
        {
            var e = unit.Evidence;
            text.AppendLine($"EVIDENCE project={unit.ProjectId} chapter={e.ChapterId} number={e.ChapterNumber} sha256={e.SourceSha256} id={e.Id} start_utf16={e.StartUtf16} length_utf16={e.LengthUtf16}");
            text.AppendLine("UNVERIFIED LOCAL HINTS: " + unit.EvidenceContextJson).AppendLine(unit.EvidenceText).AppendLine("END EVIDENCE");
        }
        string prompt = text.ToString(); var schema = SemanticSurveyContract.MultiEvidenceSchema.GetRawText();
        string key = EntityMemoryIdentity.Fingerprint(new { units = units.Select(x => x.Id).ToArray(), options, PromptVersion, schema = SemanticSurveyContract.MultiEvidenceVersion });
        return first with
        {
            Id = EntityMemoryIdentity.StableId(first.ProjectId, "semantic-multi-batch:" + key), Members = units.ToArray(), Prompt = prompt,
            ContextFingerprint = EntityMemoryIdentity.Fingerprint(units.Select(x => x.ContextFingerprint).ToArray()),
            EstimatedInputTokens = PromptTokenBudget.Estimate(prompt) + PromptTokenBudget.Estimate(schema),
            // UTF-8 byte ceiling is intentionally NOT an actual token count.
            ReservedTokens = checked(Encoding.UTF8.GetByteCount(prompt) + Encoding.UTF8.GetByteCount(schema) + 128 + options.MaximumOutputTokens),
            CpuCandidates = units.SelectMany(x => x.CpuCandidates).DistinctBy(x => x.Id).ToArray()
        };
    }
}
