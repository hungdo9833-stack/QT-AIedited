using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class BookIntelligenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-intelligence-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly ModelOptions Options = new() { Model = "survey-model", ContextSize = 4096,
        MaximumOutputTokens = 1200, RequestTimeout = TimeSpan.FromSeconds(10) };

    [Fact]
    public void Structured_parser_accepts_fenced_json_and_rejects_malformed_output()
    {
        AnalyzerResponse parsed = AdaptiveBookIntelligenceAnalyzer.Parse("Đã phân tích:\n```json\n{\"characters\":[],\"relationships\":[],\"glossary\":[]}\n```");
        Assert.Empty(parsed.Characters);
        Assert.Throws<InvalidDataException>(() => AdaptiveBookIntelligenceAnalyzer.Parse("không có json"));
    }

    [Fact]
    public async Task Corpus_index_reads_original_text_records_utf16_evidence_and_does_not_call_ai()
    {
        (ProjectRepository repository, ProjectSession session) = await ProjectAsync("林朝看向小朝说道：师父来了。\r\n普通正文。", 1);
        var fake = new SurveyClient(_ => "{}");
        var store = new BookIntelligenceStore(session);
        BookIntelligenceState state = await store.LoadAsync();
        state = await new ProjectCorpusIndexer(repository).IndexAsync(session, state, store,
            (await new ProjectResourceStore(session).LoadAsync()).Characters);

        CorpusEvidence evidence = Assert.Single(state.Evidence, x => x.IsCurrent);
        Assert.Equal(evidence.Excerpt, (await repository.LoadChapterAsync(session, evidence.ChapterId)).Chapter.OriginalText
            .Substring(evidence.StartUtf16, evidence.LengthUtf16));
        Assert.Equal(0, fake.Calls);
        Assert.False(state.AutoApplyVerified);
    }

    [Fact]
    public async Task Incremental_rescan_skips_unchanged_chapters_and_retires_old_evidence()
    {
        (ProjectRepository repository, ProjectSession session) = await ProjectAsync("林朝说道：你好。", 1);
        var store = new BookIntelligenceStore(session);
        var resources = await new ProjectResourceStore(session).LoadAsync();
        BookIntelligenceState first = await new ProjectCorpusIndexer(repository).IndexAsync(session,
            await store.LoadAsync(), store, resources.Characters);
        DateTimeOffset scanned = first.Chapters.Single().ScannedAt;
        BookIntelligenceState unchanged = await new ProjectCorpusIndexer(repository).IndexAsync(session, first, store, resources.Characters);
        Assert.Equal(scanned, unchanged.Chapters.Single().ScannedAt);

        ChapterDocument chapter = (await repository.LoadChapterAsync(session, session.Manifest.Chapters.Single().ChapterId)).Chapter;
        SourceEditPreview preview = SourceEditPlanner.Preview(chapter, "林朝说道：你坏。");
        chapter = SourceEditPlanner.Apply(chapter, preview, confirmed: true, DateTimeOffset.UtcNow);
        await repository.UpdateChapterAsync(session, chapter);
        BookIntelligenceState changed = await new ProjectCorpusIndexer(repository).IndexAsync(session, unchanged, store, resources.Characters);
        Assert.Contains(changed.Chapters.Single().Evidence, x => !x.IsCurrent);
        Assert.Contains(changed.Chapters.Single().Evidence, x => x.IsCurrent && x.Excerpt.Contains("你坏"));
    }

    [Fact]
    public async Task Analyzer_validates_evidence_and_persists_review_queue_for_restart()
    {
        (ProjectRepository repository, ProjectSession session) = await ProjectAsync("林朝看着苏雨说道：小雨，你是我的妹妹。青云宗就在前方。", 1);
        var store = new BookIntelligenceStore(session);
        ProjectResources resources = await new ProjectResourceStore(session).LoadAsync();
        BookIntelligenceState state = await new ProjectCorpusIndexer(repository).IndexAsync(session,
            await store.LoadAsync(), store, resources.Characters);
        var fake = new SurveyClient(prompt =>
        {
            Guid id = Guid.Parse(Regex.Match(prompt, @"EVIDENCE ([0-9a-f-]{36})", RegexOptions.IgnoreCase).Groups[1].Value);
            return JsonSerializer.Serialize(new
            {
                characters = new[] { new { chinese_name = "林朝", vietnamese_name = "Lâm Triều", aliases = Array.Empty<string>(),
                    gender = "unknown", primary_pronoun = "", secondary_pronoun = "", confidence = "proposed",
                    evidence_ids = new[] { id }, reason = "Tên đứng trước động từ nói" } },
                relationships = Array.Empty<object>(),
                glossary = new[] { new { chinese = "青云宗", vietnamese = "Thanh Vân Tông", category = "organization",
                    context_dependent = false, confidence = "proposed", evidence_ids = new[] { id }, reason = "Tổ chức xuất hiện trực tiếp" } }
            }, DocumentJson.Options);
        });
        state = await new AdaptiveBookIntelligenceAnalyzer(fake).AnalyzeAsync(session, state, store, resources, Options);

        Assert.Equal(1, fake.Calls);
        Assert.Equal("林朝", Assert.Single(state.Characters).ChineseName);
        Assert.Equal("青云宗", Assert.Single(state.Glossary).Chinese);
        BookIntelligenceState reopened = await new BookIntelligenceStore(session).LoadAsync();
        Assert.Equal(2, reopened.PendingCount);
        Assert.False(reopened.AutoApplyVerified);
    }

    [Fact]
    public async Task Invalid_or_uncited_ai_finding_is_rejected_without_project_changes()
    {
        (ProjectRepository repository, ProjectSession session) = await ProjectAsync("林朝说道：你好。", 1);
        byte[] manifest = await File.ReadAllBytesAsync(session.ManifestPath);
        string chapterPath = session.Resolve(session.Manifest.Chapters.Single().RelativePath);
        byte[] chapter = await File.ReadAllBytesAsync(chapterPath);
        var store = new BookIntelligenceStore(session);
        ProjectResources resources = await new ProjectResourceStore(session).LoadAsync();
        BookIntelligenceState state = await new ProjectCorpusIndexer(repository).IndexAsync(session,
            await store.LoadAsync(), store, resources.Characters);
        var fake = new SurveyClient(_ => "{\"characters\":[{\"chinese_name\":\"不存在\",\"evidence_ids\":[\"" +
            state.Evidence.Single(x => x.IsCurrent).Id + "\"]}],\"relationships\":[],\"glossary\":[]}");
        state = await new AdaptiveBookIntelligenceAnalyzer(fake).AnalyzeAsync(session, state, store, resources, Options);
        Assert.Empty(state.Characters);
        Assert.Equal(manifest, await File.ReadAllBytesAsync(session.ManifestPath));
        Assert.Equal(chapter, await File.ReadAllBytesAsync(chapterPath));
    }

    [Fact]
    public async Task Failed_request_checkpoints_error_and_resume_only_retries_unfinished_batch()
    {
        (ProjectRepository repository, ProjectSession session) = await ProjectAsync(string.Join('\n',
            Enumerable.Range(1, 12).Select(i => $"林朝说道：第{i}次。")), 1);
        var store = new BookIntelligenceStore(session);
        ProjectResources resources = await new ProjectResourceStore(session).LoadAsync();
        BookIntelligenceState state = await new ProjectCorpusIndexer(repository).IndexAsync(session,
            await store.LoadAsync(), store, resources.Characters);
        var failing = new SurveyClient(_ => throw new OllamaRequestException(OllamaErrorKind.Connection, "offline"));
        await Assert.ThrowsAsync<OllamaRequestException>(() => new AdaptiveBookIntelligenceAnalyzer(failing)
            .AnalyzeAsync(session, state, store, resources, Options));
        BookIntelligenceState checkpoint = await store.LoadAsync();
        Assert.Contains("offline", checkpoint.LastError);
        var recovered = new SurveyClient(_ => "{\"characters\":[],\"relationships\":[],\"glossary\":[]}");
        BookIntelligenceState completed = await new AdaptiveBookIntelligenceAnalyzer(recovered)
            .AnalyzeAsync(session, checkpoint, store, resources, Options);
        Assert.Empty(completed.LastError);
        Assert.NotEmpty(completed.CompletedAnalysisKeys);
    }

    [Fact]
    public async Task Audit_discovery_expands_similar_low_priority_scope_without_marking_unchecked_evidence_complete()
    {
        string source = "阿九藏在暗处。" + string.Concat(Enumerable.Repeat("风吹过原野，夜色依旧安静。", 260));
        (ProjectRepository repository, ProjectSession session) = await ProjectAsync(source, 1);
        var store = new BookIntelligenceStore(session);
        ProjectResources resources = await new ProjectResourceStore(session).LoadAsync();
        BookIntelligenceState state = await new ProjectCorpusIndexer(repository).IndexAsync(session,
            await store.LoadAsync(), store, resources.Characters);
        CorpusEvidence seed = state.Evidence.First(x => x.IsCurrent && x.Excerpt.Contains("阿九", StringComparison.Ordinal));
        Assert.Equal(EvidencePriority.Low, seed.Priority);
        seed.IsAuditSample = true;
        state = await store.SaveAsync(state);
        int call = 0;
        var fake = new SurveyClient(prompt =>
        {
            call++;
            if (call > 1) return "{\"characters\":[],\"relationships\":[],\"glossary\":[]}";
            Guid id = Guid.Parse(Regex.Match(prompt, @"EVIDENCE ([0-9a-f-]{36})", RegexOptions.IgnoreCase).Groups[1].Value);
            return JsonSerializer.Serialize(new { characters = new[] { new { chinese_name = "阿九", vietnamese_name = "A Cửu",
                aliases = Array.Empty<string>(), gender = "unknown", primary_pronoun = "", secondary_pronoun = "",
                confidence = "proposed", evidence_ids = new[] { id }, reason = "Audit found a named person" } },
                relationships = Array.Empty<object>(), glossary = Array.Empty<object>() }, DocumentJson.Options);
        });
        state = await new AdaptiveBookIntelligenceAnalyzer(fake).AnalyzeAsync(session, state, store, resources, Options);
        Assert.Equal(1, state.Metrics.AuditDiscoveries);
        Assert.Contains(state.Evidence, x => x.AuditExpanded && x.AiAnalyzed);
        Assert.DoesNotContain(state.Evidence, x => !x.AiAnalyzed && x.AuditExpanded);
    }

    [Fact]
    public async Task Malformed_json_is_retried_once_then_checkpointed()
    {
        (ProjectRepository repository, ProjectSession session) = await ProjectAsync("林朝说道：她是我的妹妹。", 1);
        var store = new BookIntelligenceStore(session);
        ProjectResources resources = await new ProjectResourceStore(session).LoadAsync();
        BookIntelligenceState state = await new ProjectCorpusIndexer(repository).IndexAsync(session,
            await store.LoadAsync(), store, resources.Characters);
        int call = 0;
        var fake = new SurveyClient(_ => ++call == 1 ? "not-json" :
            "{\"characters\":[],\"relationships\":[],\"glossary\":[]}");
        state = await new AdaptiveBookIntelligenceAnalyzer(fake).AnalyzeAsync(session, state, store, resources, Options);
        Assert.Equal(2, fake.Calls);
        Assert.Equal(1, state.Metrics.Retries);
        Assert.True(state.Evidence.Single(x => x.IsCurrent).AiAnalyzed);
    }

    [Fact]
    public async Task Confirmed_character_and_glossary_are_saved_but_nothing_is_auto_applied()
    {
        (_, ProjectSession session) = await ProjectAsync("林朝说道：青云宗。", 1);
        var store = new BookIntelligenceStore(session);
        var state = await store.LoadAsync();
        string sourceHash = Hash("林朝说道：青云宗。");
        var evidence = new CorpusEvidence { ChapterId = session.Manifest.Chapters.Single().ChapterId, StartUtf16 = 0,
            LengthUtf16 = 9, SourceSha256 = sourceHash, Excerpt = "林朝说道：青云宗。" };
        state.Chapters.Add(new() { ChapterId = evidence.ChapterId, ChapterNumber = 1, SourceSha256 = sourceHash,
            SourceLengthUtf16 = 9, Evidence = [evidence] });
        var character = new IntelligenceCharacterSuggestion { ChineseName = "林朝", VietnameseName = "Lâm Triều",
            EvidenceIds = [evidence.Id], Confidence = IntelligenceConfidence.Proposed };
        var term = new IntelligenceGlossarySuggestion { Chinese = "青云宗", Vietnamese = "Thanh Vân Tông",
            EvidenceIds = [evidence.Id], Confidence = IntelligenceConfidence.Proposed };
        state.Characters.Add(character); state.Glossary.Add(term); state = await store.SaveAsync(state);
        ProjectResources before = await new ProjectResourceStore(session).LoadAsync();
        Assert.Empty(before.Characters.Profiles); Assert.Empty(before.Terms);

        var profile = new CharacterProfile { ChineseName = "林朝", VietnameseName = "Lâm Triều", Gender = CharacterGender.Male };
        profile.ApplyGenderDefaults();
        var review = new BookIntelligenceReviewService(session);
        IntelligenceCommitResult savedCharacter = await review.AcceptCharacterAsync(state, character.Id, profile, before.Revision);
        IntelligenceCommitResult savedTerm = await review.AcceptGlossaryAsync(savedCharacter.State, term.Id,
            "Thanh Vân Tông", savedCharacter.Resources.Revision);
        Assert.Equal("林朝", Assert.Single(savedTerm.Resources.Characters.Profiles).ChineseName);
        Assert.Contains(savedTerm.Resources.Terms, x => x.Chinese == "青云宗");
    }

    [Fact]
    public async Task Merge_adds_alias_without_overwriting_locked_pronouns_and_locked_relationship_is_protected()
    {
        (_, ProjectSession session) = await ProjectAsync("小朝对苏雨说道：姐姐。", 1);
        var resourceStore = new ProjectResourceStore(session);
        ProjectResources resources = await resourceStore.LoadAsync();
        var target = new CharacterProfile { ChineseName = "林朝", VietnameseName = "Lâm Triều", Gender = CharacterGender.Male,
            NarrativePronounLocked = true };
        target.ApplyGenderDefaults();
        var listener = new CharacterProfile { ChineseName = "苏雨", VietnameseName = "Tô Vũ", Gender = CharacterGender.Female };
        listener.ApplyGenderDefaults();
        target.Relationships.Add(new() { TargetCharacterId = listener.Id, SpeakerCharacterId = target.Id,
            ListenerCharacterId = listener.Id, Relationship = "chị em", SelfReference = "em", AddresseeReference = "chị",
            Enabled = true, Locked = true });
        resources.Characters.Profiles.AddRange([target, listener]);
        resources = await resourceStore.SaveAsync(resources);
        var stateStore = new BookIntelligenceStore(session);
        BookIntelligenceState state = await stateStore.LoadAsync();
        string sourceHash = Hash("小朝对苏雨说道：姐姐。");
        var evidence = new CorpusEvidence { ChapterId = session.Manifest.Chapters.Single().ChapterId, StartUtf16 = 0,
            LengthUtf16 = 11, SourceSha256 = sourceHash, Excerpt = "小朝对苏雨说道：姐姐。" };
        state.Chapters.Add(new() { ChapterId = evidence.ChapterId, ChapterNumber = 1, SourceSha256 = sourceHash, SourceLengthUtf16 = 11, Evidence = [evidence] });
        var alias = new IntelligenceCharacterSuggestion { ChineseName = "小朝", VietnameseName = "Tiểu Triều",
            EvidenceIds = [evidence.Id], PossibleExistingCharacterId = target.Id };
        var identity = new CandidateIdentity { ChineseName = "小朝", VietnameseName = "Tiểu Triều",
            EvidenceHistory = [evidence.Id], ProposedExistingCharacterId = target.Id,
            Status = CandidateIdentityStatus.ProposedAliasLink };
        alias.CandidateIdentityId = identity.Id; state.CandidateIdentities.Add(identity);
        var relation = new IntelligenceRelationshipSuggestion { SpeakerChineseName = "林朝", ListenerChineseName = "苏雨",
            Relationship = "mới", SelfReference = "ta", AddresseeReference = "ngươi", EvidenceIds = [evidence.Id] };
        state.Characters.Add(alias); state.Relationships.Add(relation); state = await stateStore.SaveAsync(state);
        var review = new BookIntelligenceReviewService(session);
        IntelligenceCommitResult merged = await review.MergeCharacterAsync(state, alias.Id, target.Id, resources.Revision);
        CharacterProfile after = merged.Resources.Characters.Profiles.Single(x => x.Id == target.Id);
        Assert.Contains("小朝", after.Aliases); Assert.True(after.NarrativePronounLocked); Assert.Equal("hắn", after.PrimaryNarrativePronoun);
        await Assert.ThrowsAsync<InvalidOperationException>(() => review.AcceptRelationshipAsync(merged.State,
            relation.Id, merged.Resources.Revision));
        Assert.True((await resourceStore.LoadAsync()).Characters.Profiles.Single(x => x.Id == target.Id).Relationships.Single().Locked);
        IdentityMergeRecord merge = Assert.Single(merged.State.CandidateIdentities.Single().MergeHistory);
        IntelligenceCommitResult undone = await review.UndoCharacterMergeAsync(merged.State, merge.Id, merged.Resources.Revision);
        Assert.DoesNotContain("小朝", undone.Resources.Characters.Profiles.Single(x => x.Id == target.Id).Aliases);
    }

    [Fact]
    public async Task Prepared_commit_is_reconciled_idempotently_after_resource_was_saved()
    {
        (_, ProjectSession session) = await ProjectAsync("青云宗位于山巅。", 1);
        var stateStore = new BookIntelligenceStore(session);
        BookIntelligenceState state = await stateStore.LoadAsync();
        string source = "青云宗位于山巅。", hash = Hash(source);
        var evidence = new CorpusEvidence { ChapterId = session.Manifest.Chapters.Single().ChapterId,
            StartUtf16 = 0, LengthUtf16 = source.Length, SourceSha256 = hash, Excerpt = source };
        state.Chapters.Add(new CorpusChapterIndex { ChapterId = evidence.ChapterId, ChapterNumber = 1,
            SourceSha256 = hash, SourceLengthUtf16 = source.Length, Evidence = [evidence] });
        var term = new IntelligenceGlossarySuggestion { Chinese = "青云宗", Vietnamese = "Thanh Vân Tông",
            EvidenceIds = [evidence.Id], Scope = GlossaryKnowledgeScope.Global };
        state.Glossary.Add(term);
        state.PendingResourceCommits.Add(new IntelligenceResourceCommit { Kind = ResourceCommitKind.Glossary,
            State = ResourceCommitState.Prepared, SuggestionId = term.Id,
            IntendedFingerprint = "glossary:青云宗:Thanh Vân Tông", CreatedAt = DateTimeOffset.UtcNow });
        state = await stateStore.SaveAsync(state);
        var resourceStore = new ProjectResourceStore(session);
        ProjectResources resources = await resourceStore.LoadAsync();
        resources.Terms.Add(new("青云宗", "Thanh Vân Tông"));
        _ = await resourceStore.SaveAsync(resources);

        IntelligenceCommitResult recovered = await new BookIntelligenceReviewService(session).ReconcileAsync();
        Assert.Equal(IntelligenceReviewStatus.Accepted, recovered.State.Glossary.Single().Status);
        Assert.Empty(recovered.State.PendingResourceCommits);
        IntelligenceCommitResult second = await new BookIntelligenceReviewService(session).ReconcileAsync();
        Assert.Equal(recovered.State.Revision, second.State.Revision);
    }

    [Fact]
    public async Task Accepted_two_way_addressing_is_added_to_timeline_with_evidence_chapter()
    {
        const string source = "林朝对苏雨说道：妹妹。";
        (_, ProjectSession session) = await ProjectAsync(source, 7);
        var resourceStore = new ProjectResourceStore(session);
        ProjectResources resources = await resourceStore.LoadAsync();
        var speaker = new CharacterProfile { ChineseName = "林朝", VietnameseName = "Lâm Triều", NormalizationEnabled = false };
        var listener = new CharacterProfile { ChineseName = "苏雨", VietnameseName = "Tô Vũ", NormalizationEnabled = false };
        resources.Characters.Profiles.AddRange([speaker, listener]);
        resources = await resourceStore.SaveAsync(resources);

        var stateStore = new BookIntelligenceStore(session);
        BookIntelligenceState state = await stateStore.LoadAsync();
        var evidence = new CorpusEvidence { ChapterId = session.Manifest.Chapters.Single().ChapterId,
            StartUtf16 = 0, LengthUtf16 = source.Length, SourceSha256 = Hash(source), Excerpt = source };
        state.Chapters.Add(new CorpusChapterIndex { ChapterId = evidence.ChapterId, ChapterNumber = 7,
            SourceSha256 = evidence.SourceSha256, SourceLengthUtf16 = source.Length, Evidence = [evidence] });
        var knowledge = new KnowledgeRelationship { SubjectCharacterId = speaker.Id, TargetCharacterId = listener.Id,
            RelationType = KnowledgeRelationType.UnknownSibling, EvidenceIds = [evidence.Id],
            DiscoveryMethod = RelationshipDiscoveryMethod.DirectAi, Status = KnowledgeRelationshipStatus.Proposed,
            FirstObservedChapter = 7, RevealedAtChapter = 7 };
        var suggestion = new IntelligenceRelationshipSuggestion { SpeakerChineseName = "林朝", ListenerChineseName = "苏雨",
            Relationship = "anh em", SelfReference = "anh", AddresseeReference = "em",
            ReverseSelfReference = "em", ReverseAddresseeReference = "anh", EvidenceIds = [evidence.Id],
            KnowledgeRelationshipId = knowledge.Id };
        state.RelationshipGraph.Add(knowledge); state.Relationships.Add(suggestion); state = await stateStore.SaveAsync(state);

        IntelligenceCommitResult result = await new BookIntelligenceReviewService(session)
            .AcceptRelationshipAsync(state, suggestion.Id, resources.Revision);

        Assert.Equal(KnowledgeRelationshipStatus.Confirmed, result.State.RelationshipGraph.Single().Status);
        Assert.Equal(2, result.State.AddressingTimeline.Count);
        Assert.All(result.State.AddressingTimeline, x => { Assert.True(x.Approved); Assert.Equal(7, x.RevealedAtChapter); });
        Assert.Contains(result.State.AddressingTimeline, x => x.SpeakerCharacterId == speaker.Id &&
            x.ListenerCharacterId == listener.Id && x.SelfReference == "anh" && x.AddresseeReference == "em");
        Assert.Contains(result.State.AddressingTimeline, x => x.SpeakerCharacterId == listener.Id &&
            x.ListenerCharacterId == speaker.Id && x.SelfReference == "em" && x.AddresseeReference == "anh");
    }

    [Fact]
    public async Task Review_rejects_evidence_when_original_source_changed_after_analysis()
    {
        (ProjectRepository repository, ProjectSession session) = await ProjectAsync("林朝说道：你好。", 1);
        var store = new BookIntelligenceStore(session);
        ProjectResources resources = await new ProjectResourceStore(session).LoadAsync();
        BookIntelligenceState state = await new ProjectCorpusIndexer(repository).IndexAsync(session,
            await store.LoadAsync(), store, resources.Characters);
        CorpusEvidence evidence = Assert.Single(state.Evidence, x => x.IsCurrent);
        var suggestion = new IntelligenceCharacterSuggestion { ChineseName = "林朝", VietnameseName = "Lâm Triều",
            EvidenceIds = [evidence.Id] };
        state.Characters.Add(suggestion); state = await store.SaveAsync(state);
        ChapterDocument chapter = (await repository.LoadChapterAsync(session, evidence.ChapterId)).Chapter;
        SourceEditPreview preview = SourceEditPlanner.Preview(chapter, "林朝说道：你坏。");
        await repository.UpdateChapterAsync(session, SourceEditPlanner.Apply(chapter, preview, true, DateTimeOffset.UtcNow));
        var profile = new CharacterProfile { ChineseName = "林朝", VietnameseName = "Lâm Triều", NormalizationEnabled = false };

        await Assert.ThrowsAsync<InvalidOperationException>(() => new BookIntelligenceReviewService(session)
            .AcceptCharacterAsync(state, suggestion.Id, profile, resources.Revision));
        Assert.Empty((await new ProjectResourceStore(session).LoadAsync()).Characters.Profiles);
    }

    private async Task<(ProjectRepository Repository, ProjectSession Session)> ProjectAsync(string source, int number)
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "Synthetic");
        ChapterDocument chapter = TranslationService.CreateChapter("Chương", source);
        await repository.AddChapterAsync(session, chapter, number, "章节");
        return (repository, session);
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public void Dispose()
    {
        string path = Path.GetFullPath(_root);
        if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("qtaie-intelligence-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid synthetic root.");
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    private sealed class SurveyClient(Func<string, string> response) : IOllamaClient
    {
        public int Calls { get; private set; }
        public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new OllamaCompletion(response(prompt), true, "stop", new()));
        }
    }
}
