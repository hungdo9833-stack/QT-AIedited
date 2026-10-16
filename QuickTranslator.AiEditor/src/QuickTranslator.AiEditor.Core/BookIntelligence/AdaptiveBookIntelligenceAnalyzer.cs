using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed class AdaptiveBookIntelligenceAnalyzer(IOllamaClient client)
{
    public const int MaximumEvidencePerRequest = 8;

    public async Task<BookIntelligenceState> AnalyzeAsync(ProjectSession session, BookIntelligenceState state,
        BookIntelligenceStore store, ProjectResources resources, ModelOptions options,
        IProgress<AnalyzerProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(resources);
        options.Validate();
        if (state.ProjectId != session.Manifest.ProjectId || resources.ProjectId != session.Manifest.ProjectId)
            throw new InvalidOperationException("Analyzer nhận dữ liệu của Project khác.");

        state.AnalyzerModel = options.Model;
        CorpusEvidence[] all = state.Evidence.Where(x => x.IsCurrent)
            .OrderBy(x => state.Chapters.Single(c => c.ChapterId == x.ChapterId).ChapterNumber)
            .ThenBy(x => x.StartUtf16).ToArray();
        int processed = 0;
        foreach (CorpusEvidence[] batch in all.Chunk(MaximumEvidencePerRequest))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string key = AnalysisKey(batch, options.Model, state.AnalyzerVersion, resources.Revision);
            if (state.CompletedAnalysisKeys.Contains(key))
            {
                processed += batch.Length;
                progress?.Report(new(processed, all.Length, state.PendingCount));
                continue;
            }
            try
            {
                string prompt = BuildPrompt(batch, resources);
                OllamaCompletion completion = await client.TranslateAsync(prompt, options, cancellationToken).ConfigureAwait(false);
                AnalyzerResponse response = Parse(completion.Text);
                ApplyValidatedResponse(state, response, batch, resources);
                state.CompletedAnalysisKeys.Add(key);
                state.LastError = "";
                processed += batch.Length;
                state.NextEvidenceIndex = processed;
                state = await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
                progress?.Report(new(processed, all.Length, state.PendingCount));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                state.LastError = ex.Message;
                state.NextEvidenceIndex = processed;
                _ = await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        state.NextEvidenceIndex = all.Length;
        state.LastError = "";
        return await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
    }

    internal static string BuildPrompt(IReadOnlyCollection<CorpusEvidence> evidence, ProjectResources resources)
    {
        string knownCharacters = string.Join('\n', resources.Characters.Profiles.Select(p =>
            $"- {p.ChineseName} = {p.VietnameseName}; aliases=[{string.Join(", ", p.Aliases)}]; gender={p.Gender}"));
        string knownTerms = string.Join('\n', resources.Terms.Take(300).Select(t => $"- {t.Chinese} = {t.Vietnamese}"));
        string excerpts = string.Join("\n\n", evidence.Select(e =>
            $"EVIDENCE {e.Id:D} | chapter={e.ChapterId:D} | startUtf16={e.StartUtf16}\n{e.Excerpt}"));
        return $$"""
            Bạn là bộ trích xuất dữ kiện cho tiểu thuyết Trung văn. Chỉ dùng chứng cứ được cung cấp; không suy đoán.
            Trả về DUY NHẤT một JSON object đúng schema dưới đây. confidence chỉ nhận unknown, ambiguous, proposed.
            Mọi mục phải dẫn ít nhất một evidence_id. Tên/thuật ngữ Trung phải xuất hiện nguyên văn trong evidence được dẫn.
            Không biến danh xưng phụ thuộc ngữ cảnh thành glossary toàn cục. Không tự xác minh giới tính hoặc xưng hô nếu chứng cứ chưa rõ.
            Schema:
            {"characters":[{"chinese_name":"","vietnamese_name":"","aliases":[],"gender":"unknown|male|female|other","primary_pronoun":"","secondary_pronoun":"","confidence":"unknown|ambiguous|proposed","evidence_ids":[],"reason":""}],"relationships":[{"speaker_chinese_name":"","listener_chinese_name":"","relationship":"","self_reference":"","addressee_reference":"","reverse_self_reference":"","reverse_addressee_reference":"","confidence":"unknown|ambiguous|proposed","evidence_ids":[],"reason":""}],"glossary":[{"chinese":"","vietnamese":"","category":"person|place|organization|cultivation|item|term","context_dependent":false,"confidence":"unknown|ambiguous|proposed","evidence_ids":[],"reason":""}]}

            NHÂN VẬT ĐÃ CÓ:
            {{knownCharacters}}

            GLOSSARY ĐÃ CÓ:
            {{knownTerms}}

            CHỨNG CỨ:
            {{excerpts}}
            """;
    }

    internal static AnalyzerResponse Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Ollama trả phản hồi khảo sát rỗng.");
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidDataException("Ollama không trả JSON khảo sát hợp lệ.");
        try
        {
            AnalyzerResponse response = JsonSerializer.Deserialize<AnalyzerResponse>(text[start..(end + 1)],
                new JsonSerializerOptions(DocumentJson.Options) { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("JSON khảo sát rỗng.");
            response.Characters ??= [];
            response.Relationships ??= [];
            response.Glossary ??= [];
            foreach (CharacterFinding item in response.Characters) { item.Aliases ??= []; item.EvidenceIds ??= []; }
            foreach (RelationshipFinding item in response.Relationships) item.EvidenceIds ??= [];
            foreach (GlossaryFinding item in response.Glossary) item.EvidenceIds ??= [];
            return response;
        }
        catch (JsonException ex) { throw new InvalidDataException("Ollama trả JSON khảo sát không đúng schema.", ex); }
    }

    internal static void ApplyValidatedResponse(BookIntelligenceState state, AnalyzerResponse response,
        IReadOnlyCollection<CorpusEvidence> batch, ProjectResources resources)
    {
        Dictionary<Guid, CorpusEvidence> allowed = batch.ToDictionary(x => x.Id);
        foreach (CharacterFinding finding in response.Characters)
        {
            Guid[] ids = ValidateEvidenceIds(finding.EvidenceIds, allowed);
            if (ids.Length == 0) continue;
            string chinese = finding.ChineseName.Trim();
            if (chinese.Length == 0 || !Appears(chinese, ids, allowed)) continue;
            string[] aliases = finding.Aliases.Where(x => !string.IsNullOrWhiteSpace(x) && Appears(x.Trim(), ids, allowed))
                .Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToArray();
            CharacterProfile? existing = resources.Characters.Profiles.FirstOrDefault(p =>
                p.ChineseName == chinese || p.Aliases.Contains(chinese, StringComparer.Ordinal) ||
                aliases.Any(a => p.ChineseName == a || p.Aliases.Contains(a, StringComparer.Ordinal)));
            if (state.Characters.Any(x => x.Status != IntelligenceReviewStatus.Rejected &&
                (x.ChineseName == chinese || x.Aliases.Contains(chinese, StringComparer.Ordinal)))) continue;
            state.Characters.Add(new IntelligenceCharacterSuggestion
            {
                ChineseName = chinese,
                VietnameseName = finding.VietnameseName.Trim(),
                Aliases = aliases.ToList(),
                Gender = ParseGender(finding.Gender),
                PrimaryNarrativePronoun = finding.PrimaryPronoun.Trim(),
                SecondaryNarrativePronoun = finding.SecondaryPronoun.Trim(),
                Confidence = ParseConfidence(finding.Confidence),
                EvidenceIds = ids.ToList(),
                Reason = finding.Reason.Trim(),
                PossibleExistingCharacterId = existing?.Id
            });
        }

        foreach (RelationshipFinding finding in response.Relationships)
        {
            Guid[] ids = ValidateEvidenceIds(finding.EvidenceIds, allowed);
            if (ids.Length == 0) continue;
            string speaker = finding.SpeakerChineseName.Trim(), listener = finding.ListenerChineseName.Trim();
            if (speaker.Length == 0 || listener.Length == 0 || speaker == listener ||
                !Appears(speaker, ids, allowed) || !Appears(listener, ids, allowed)) continue;
            if (state.Relationships.Any(x => x.Status != IntelligenceReviewStatus.Rejected &&
                x.SpeakerChineseName == speaker && x.ListenerChineseName == listener)) continue;
            state.Relationships.Add(new IntelligenceRelationshipSuggestion
            {
                SpeakerChineseName = speaker, ListenerChineseName = listener,
                Relationship = finding.Relationship.Trim(), SelfReference = finding.SelfReference.Trim(),
                AddresseeReference = finding.AddresseeReference.Trim(),
                ReverseSelfReference = finding.ReverseSelfReference.Trim(),
                ReverseAddresseeReference = finding.ReverseAddresseeReference.Trim(),
                Confidence = ParseConfidence(finding.Confidence), EvidenceIds = ids.ToList(), Reason = finding.Reason.Trim()
            });
        }

        foreach (GlossaryFinding finding in response.Glossary)
        {
            Guid[] ids = ValidateEvidenceIds(finding.EvidenceIds, allowed);
            if (ids.Length == 0) continue;
            string chinese = finding.Chinese.Trim();
            if (chinese.Length == 0 || !Appears(chinese, ids, allowed) ||
                resources.Terms.Any(x => x.Chinese == chinese) ||
                resources.Characters.Profiles.Any(p => p.ChineseName == chinese || p.Aliases.Contains(chinese, StringComparer.Ordinal)) ||
                state.Glossary.Any(x => x.Status != IntelligenceReviewStatus.Rejected && x.Chinese == chinese)) continue;
            state.Glossary.Add(new IntelligenceGlossarySuggestion
            {
                Chinese = chinese, Vietnamese = finding.Vietnamese.Trim(), Category = finding.Category.Trim(),
                ContextDependent = finding.ContextDependent, Confidence = ParseConfidence(finding.Confidence),
                EvidenceIds = ids.ToList(), Reason = finding.Reason.Trim()
            });
        }
        state.Validate();
    }

    private static Guid[] ValidateEvidenceIds(List<Guid> ids, IReadOnlyDictionary<Guid, CorpusEvidence> allowed)
    {
        return ids.Where(allowed.ContainsKey).Distinct().ToArray();
    }
    private static bool Appears(string value, IEnumerable<Guid> ids, IReadOnlyDictionary<Guid, CorpusEvidence> evidence) =>
        ids.Any(id => evidence[id].Excerpt.Contains(value, StringComparison.Ordinal));
    private static CharacterGender ParseGender(string value) => value.Trim().ToLowerInvariant() switch
    { "male" => CharacterGender.Male, "female" => CharacterGender.Female, "other" => CharacterGender.Other, _ => CharacterGender.Unknown };
    private static IntelligenceConfidence ParseConfidence(string value) => value.Trim().ToLowerInvariant() switch
    { "proposed" => IntelligenceConfidence.Proposed, "ambiguous" => IntelligenceConfidence.Ambiguous, _ => IntelligenceConfidence.Unknown };
    private static string AnalysisKey(IEnumerable<CorpusEvidence> evidence, string model, string version, long resourceRevision)
    {
        string value = model + "|" + version + "|resources:" + resourceRevision + "|" +
            string.Join('|', evidence.Select(x => $"{x.Id:D}:{x.SourceSha256}:{x.StartUtf16}:{x.LengthUtf16}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}

public sealed class AnalyzerResponse
{
    public List<CharacterFinding> Characters { get; set; } = [];
    public List<RelationshipFinding> Relationships { get; set; } = [];
    public List<GlossaryFinding> Glossary { get; set; } = [];
}
public sealed class CharacterFinding
{
    [JsonPropertyName("chinese_name")]
    public string ChineseName { get; set; } = "";
    [JsonPropertyName("vietnamese_name")]
    public string VietnameseName { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
    public string Gender { get; set; } = "unknown";
    [JsonPropertyName("primary_pronoun")]
    public string PrimaryPronoun { get; set; } = "";
    [JsonPropertyName("secondary_pronoun")]
    public string SecondaryPronoun { get; set; } = "";
    public string Confidence { get; set; } = "unknown";
    [JsonPropertyName("evidence_ids")]
    public List<Guid> EvidenceIds { get; set; } = [];
    public string Reason { get; set; } = "";
}
public sealed class RelationshipFinding
{
    [JsonPropertyName("speaker_chinese_name")]
    public string SpeakerChineseName { get; set; } = "";
    [JsonPropertyName("listener_chinese_name")]
    public string ListenerChineseName { get; set; } = "";
    public string Relationship { get; set; } = "";
    [JsonPropertyName("self_reference")]
    public string SelfReference { get; set; } = "";
    [JsonPropertyName("addressee_reference")]
    public string AddresseeReference { get; set; } = "";
    [JsonPropertyName("reverse_self_reference")]
    public string ReverseSelfReference { get; set; } = "";
    [JsonPropertyName("reverse_addressee_reference")]
    public string ReverseAddresseeReference { get; set; } = "";
    public string Confidence { get; set; } = "unknown";
    [JsonPropertyName("evidence_ids")]
    public List<Guid> EvidenceIds { get; set; } = [];
    public string Reason { get; set; } = "";
}
public sealed class GlossaryFinding
{
    public string Chinese { get; set; } = "";
    public string Vietnamese { get; set; } = "";
    public string Category { get; set; } = "";
    [JsonPropertyName("context_dependent")]
    public bool ContextDependent { get; set; }
    public string Confidence { get; set; } = "unknown";
    [JsonPropertyName("evidence_ids")]
    public List<Guid> EvidenceIds { get; set; } = [];
    public string Reason { get; set; } = "";
}
