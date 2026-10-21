using System.Security.Cryptography;
using System.Text;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class BookIntelligenceSession2Tests
{
    [Fact]
    public void Translation_context_feature_flag_defaults_off()
    {
        Assert.False(new ProjectTranslationSettings().BookKnowledgeContextEnabled);
    }

    [Fact]
    public void Obsolete_book_intelligence_schema_is_rejected()
    {
        Guid project = Guid.NewGuid();
        BookIntelligenceState state = State(project, "林朝说道：你好。");
        state.SchemaVersion = 1;
        Assert.Throws<InvalidDataException>(state.Validate);
    }

    [Fact]
    public void Adaptive_scanner_covers_long_regex_free_chapter_and_audit_is_deterministic()
    {
        Guid chapter = Guid.NewGuid();
        string source = string.Concat(Enumerable.Repeat("风吹过原野，夜色安静。", 500));
        string hash = Hash(source);
        List<CorpusEvidence> first = ProjectCorpusIndexer.ScanChapter(chapter, source, hash, [], 0.5);
        List<CorpusEvidence> second = ProjectCorpusIndexer.ScanChapter(chapter, source, hash, [], 0.5);
        Assert.True(first.Count > 4);
        Assert.All(first, x => Assert.Equal(x.Excerpt, source.Substring(x.StartUtf16, x.LengthUtf16)));
        Assert.Equal(first.Where(x => x.IsAuditSample).Select(x => x.StartUtf16),
            second.Where(x => x.IsAuditSample).Select(x => x.StartUtf16));
        Assert.Contains(first, x => x.Priority == EvidencePriority.Low && x.IsAuditSample);
    }

    [Fact]
    public void Scanner_detects_multiline_identity_and_relationship_signals()
    {
        string source = "那人缓缓开口，自称\r\n林朝。\r\n苏雨其实是他的亲生妹妹。";
        List<CorpusEvidence> evidence = ProjectCorpusIndexer.ScanChapter(Guid.NewGuid(), source, Hash(source), []);
        Assert.Contains(evidence, x => x.Priority == EvidencePriority.High && x.PriorityReason.Length > 0);
        Assert.Contains(evidence.SelectMany(x => x.Signals), x => x == "relationship-signal");
    }


    [Fact]
    public void Entity_resolution_aggregates_evidence_and_does_not_auto_merge_similar_names()
    {
        Guid project = Guid.NewGuid();
        BookIntelligenceState state = State(project, "小朝见到林朝。", "林朝说小朝是乳名。");
        ProjectResources resources = Resources(project);
        CorpusEvidence[] evidence = state.Evidence.ToArray();
        CandidateIdentity a = EntityResolutionService.Resolve(state, resources, "小朝", "Tiểu Triều", [], [evidence[0].Id]);
        CandidateIdentity b = EntityResolutionService.Resolve(state, resources, "小朝", "Tiểu Triều", ["林朝"], [evidence[1].Id]);
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(2, b.EvidenceHistory.Count);
        Assert.Equal(CandidateIdentityStatus.Candidate, b.Status);
        Assert.Null(b.ProposedExistingCharacterId);
    }



    [Fact]
    public void Timeline_does_not_leak_relationship_or_addressing_before_reveal()
    {
        Guid project = Guid.NewGuid(), a = Guid.NewGuid(), b = Guid.NewGuid();
        BookIntelligenceState state = State(project);
        KnowledgeRelationship relationship = Rel(a, b, KnowledgeRelationType.BiologicalFather);
        relationship.RevealedAtChapter = 300;
        state.RelationshipGraph.Add(relationship);
        state.AddressingTimeline.Add(new AddressingRulePeriod { SpeakerCharacterId = b, ListenerCharacterId = a,
            SelfReference = "con", AddresseeReference = "cha", Approved = true, RevealedAtChapter = 300 });
        Assert.Empty(RelationshipTimelineService.ValidRelationshipsAt(state, 10));
        Assert.Empty(RelationshipTimelineService.ApprovedAddressingAt(state, 10));
        Assert.Single(RelationshipTimelineService.ValidRelationshipsAt(state, 300));
        Assert.Single(RelationshipTimelineService.ApprovedAddressingAt(state, 300));
    }

    [Fact]
    public async Task Translation_knowledge_snapshot_is_immutable_and_only_approved_chapter_scoped_data_enters_prompt()
    {
        Guid project = Guid.NewGuid();
        var profile = Profile("林朝", "Lâm Triều"); profile.Gender = CharacterGender.Male;
        profile.ApplyGenderDefaults(); profile.NormalizationEnabled = true; profile.NarrativePronounLocked = true;
        ProjectResources resources = Resources(project, profile);
        ChapterDocument chapter = TranslationService.CreateChapter("Chương", "林朝说道：青云宗。",
            model: new ModelOptions { Model = "fake" });
        chapter.ProjectId = project; chapter.ChapterNumber = 10; chapter.CharacterProject.Id = project;
        BookIntelligenceState state = State(project, chapter.OriginalText);
        state.Chapters[0].ChapterId = chapter.Id;
        foreach (CorpusEvidence item in state.Chapters[0].Evidence) item.ChapterId = chapter.Id;
        state.Glossary.Add(new IntelligenceGlossarySuggestion { Chinese = "青云宗", Vietnamese = "Thanh Vân Tông",
            Status = IntelligenceReviewStatus.Accepted, Scope = GlossaryKnowledgeScope.Global,
            EvidenceIds = [state.Evidence.First().Id] });
        var provider = new BookKnowledgeContextProvider(state, resources);
        var client = new FakeClient(); var service = new TranslationService(chapter, client, new MemoryStore());
        service.UseProjectResources(resources); service.UseBookKnowledgeContext(provider);
        TranslationSegment segment = chapter.Segments[0];
        Task<TranslationAttempt> request = service.TranslateAsync(segment.Id, chapter.DefaultModelOptions);
        state.Glossary[0].Vietnamese = "Bị đổi sau request";
        await request;
        Assert.Contains("Thanh Vân Tông", client.Prompts.Single());
        Assert.DoesNotContain("Bị đổi sau request", client.Prompts.Single());
        Assert.Contains("BOOK KNOWLEDGE", client.Prompts.Single());
    }

    [Fact]
    public async Task Translation_without_knowledge_provider_preserves_existing_prompt_path()
    {
        ChapterDocument chapter = TranslationService.CreateChapter("Chương", "林朝说道：你好。",
            model: new ModelOptions { Model = "fake" });
        var client = new FakeClient();
        var service = new TranslationService(chapter, client, new MemoryStore());

        await service.TranslateAsync(chapter.Segments[0].Id, chapter.DefaultModelOptions);

        Assert.DoesNotContain("BOOK KNOWLEDGE", client.Prompts.Single());
    }

    [Fact]
    public void Scene_context_marks_unnamed_dialogue_ambiguous_instead_of_forcing_speaker()
    {
        Guid project = Guid.NewGuid();
        ProjectResources resources = Resources(project, Profile("林朝", "Lâm Triều"), Profile("苏雨", "Tô Vũ"), Profile("王兰", "Vương Lan"));
        ChapterDocument chapter = TranslationService.CreateChapter("Chương", "林朝、苏雨和王兰走进房间。\n“你回来了？”");
        chapter.ProjectId = project; chapter.ChapterNumber = 1;
        BookIntelligenceState state = State(project, chapter.OriginalText);
        state.Chapters[0].ChapterId = chapter.Id; foreach (CorpusEvidence e in state.Chapters[0].Evidence) e.ChapterId = chapter.Id;
        IReadOnlyList<SceneContextSnapshot> scenes = new SceneContextService().RebuildChapter(state, chapter, resources);
        Assert.Contains(scenes, x => x.ResolutionStatus == SceneResolutionStatus.Ambiguous && x.SpeakerCharacterId is null);
    }

    [Fact]
    public void Knowledge_block_carries_ledger_addressing_by_name_without_forbidding_dialogue_rules()
    {
        Guid project = Guid.NewGuid();
        CharacterProfile mother = Profile("王秀兰", "Vương Tú Lan"), wife = Profile("苏雨", "Tô Vũ");
        mother.Relationships.Add(new CharacterRelationship { TargetCharacterId = wife.Id, ListenerCharacterId = wife.Id,
            Relationship = "mẹ chồng - con dâu", SelfReference = "mẹ", AddresseeReference = "con", Enabled = true });
        ProjectResources resources = Resources(project, mother, wife);
        ChapterDocument chapter = TranslationService.CreateChapter("Chương", "王秀兰指着苏雨：“你这个疯女人！”",
            model: new ModelOptions { Model = "fake" });
        chapter.ProjectId = project;
        BookIntelligenceState state = State(project, chapter.OriginalText);
        state.Chapters[0].ChapterId = chapter.Id;
        foreach (CorpusEvidence item in state.Chapters[0].Evidence) item.ChapterId = chapter.Id;

        string block = new BookKnowledgeContextProvider(state, resources).GetSnapshot(chapter, chapter.Segments[0]).PromptBlock;

        Assert.Contains("- Dialogue Vương Tú Lan -> Tô Vũ: self=mẹ; address=con", block);
        Assert.DoesNotContain("do not force", block);
        Assert.DoesNotContain(wife.Id.ToString("D"), block);

        // With the consistency lock on, the CHARACTER CONSISTENCY block carries the rule; it is not repeated here.
        resources.Characters.ConsistencyLockEnabled = true;
        Assert.DoesNotContain("- Dialogue", new BookKnowledgeContextProvider(state, resources).GetSnapshot(chapter, chapter.Segments[0]).PromptBlock);
    }

    private static BookIntelligenceState State(Guid project, params string[] excerpts)
    {
        var state = new BookIntelligenceState { ProjectId = project };
        int number = 1;
        foreach (string excerpt in excerpts)
        {
            Guid chapter = Guid.NewGuid(); var evidence = Evidence(excerpt); evidence.ChapterId = chapter;
            state.Chapters.Add(new CorpusChapterIndex { ChapterId = chapter, ChapterNumber = number++,
                SourceSha256 = evidence.SourceSha256, SourceLengthUtf16 = excerpt.Length, Evidence = [evidence] });
        }
        return state;
    }

    private static CorpusEvidence Evidence(string excerpt) => new() { ChapterId = Guid.NewGuid(), StartUtf16 = 0,
        LengthUtf16 = excerpt.Length, SourceSha256 = Hash(excerpt), Excerpt = excerpt, Priority = EvidencePriority.High,
        PriorityReason = "test" };
    private static KnowledgeRelationship Rel(Guid subject, Guid target, KnowledgeRelationType type) => new()
    { SubjectCharacterId = subject, TargetCharacterId = target, RelationType = type, Status = KnowledgeRelationshipStatus.Confirmed };
    private static ProjectResources Resources(Guid project, params CharacterProfile[] profiles) => new()
    { ProjectId = project, Characters = new CharacterProject { Id = project, Profiles = profiles.ToList() } };
    private static CharacterProfile Profile(string chinese, string vietnamese)
    {
        var profile = new CharacterProfile { ChineseName = chinese, VietnameseName = vietnamese,
            Gender = CharacterGender.Unknown, NormalizationEnabled = false };
        profile.Validate(); return profile;
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
