using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Segmentation;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit.Abstractions;

namespace QuickTranslator.AiEditor.Tests;

public sealed class TranslationAnalysisCacheTests(ITestOutputHelper output)
{
    [Fact]
    public void CachedAnalyses_MatchUncachedReferencesDialogueAndStaleOffsetGuards()
    {
        var chapter = Chapter();
        var cache = new ChapterAnalysisCache();
        var analysis = cache.Get(chapter);
        var tracker = new NarrativePronounTracker();
        var old = new LegacyNarrativePronounTracker();
        var references = old.Scan(chapter.OriginalText, chapter.CharacterProject);
        Assert.Equal(references, analysis.NarrativeReferences);
        var segments = chapter.Segments.Concat(new[]
        {
            new TranslationSegment { StartUtf16 = -1, SourceText = "他关门。" },
            new TranslationSegment { StartUtf16 = chapter.OriginalText.Length, SourceText = "他关门。" },
            new TranslationSegment { StartUtf16 = chapter.OriginalText.IndexOf('他'), SourceText = "她关门。" },
            new TranslationSegment { StartUtf16 = 0, SourceText = "" }
        });
        foreach (var segment in segments)
        {
            Assert.Equal(old.ForSegment(chapter.OriginalText, segment, references),
                tracker.ForSegment(chapter.OriginalText, segment, analysis.NarrativeReferences));
            Assert.Equal(old.HintForSegment(chapter.OriginalText, segment, references),
                tracker.HintForSegment(chapter.OriginalText, segment, analysis.NarrativeReferences));
            Assert.Equal(DialogueMapBuilder.Build(chapter, segment), DialogueMapBuilder.Build(chapter, segment, analysis.Dialogue));
        }
        Assert.Same(analysis, cache.Get(DocumentJson.Clone(chapter)));
        Assert.Equal(1, cache.AnalysisCount);
    }

    [Fact]
    public void ContentKey_DetectsSourceAliasGenderPronounRelationshipAndChapterChangesWithoutRevisionBumps()
    {
        var chapter = Chapter();
        var cache = new ChapterAnalysisCache();
        var first = cache.Get(chapter);
        int expectedScans = 1;
        var profile = chapter.CharacterProject.Profiles[0];
        DateTimeOffset unchangedRevision = chapter.CharacterProject.UpdatedAt;
        void Changed(Action change)
        {
            var before = cache.Get(chapter);
            change();
            Assert.NotSame(before, cache.Get(chapter));
            Assert.Equal(++expectedScans, cache.AnalysisCount);
            Assert.Equal(unchangedRevision, chapter.CharacterProject.UpdatedAt);
        }
        Changed(() => chapter.OriginalText = chapter.OriginalText.Replace("林风", "阿风", StringComparison.Ordinal));
        Assert.DoesNotContain(cache.Get(chapter).NarrativeReferences, r => r.Confidence == NarrativePronounConfidence.High && r.Gender == CharacterGender.Male);
        Changed(() => profile.Aliases.Add("阿风"));
        Assert.Contains(cache.Get(chapter).NarrativeReferences, r => r.Confidence == NarrativePronounConfidence.High && r.CharacterId == profile.Id);
        Changed(() => profile.Gender = CharacterGender.Female);
        Assert.DoesNotContain(cache.Get(chapter).NarrativeReferences, r => r.Gender == CharacterGender.Male && r.CharacterId is not null);
        Changed(() => profile.PrimaryNarrativePronoun = "bà");
        Changed(() => profile.NarrativePronounLocked = false);
        Changed(() => profile.Relationships[0] = profile.Relationships[0] with { AddresseeReference = "chị" });
        Changed(() => chapter.ChapterNumber++);
        Changed(() => profile.Relationships[0] = profile.Relationships[0] with { EffectiveFromChapter = 20 });
        Changed(() => profile.ChineseName = "林海");
        Assert.Equal("林风", first.Dialogue.Owners["林风"].ChineseName);
        Changed(() => profile.VietnameseName = "Lâm Hải");
        Changed(() => chapter.Id = Guid.NewGuid());
    }

    [Fact]
    public void Cache_IsBoundedAndUsesLeastRecentlyUsedEviction()
    {
        var cache = new ChapterAnalysisCache();
        var chapters = Enumerable.Range(0, 5).Select(_ => Chapter()).ToArray();
        var first = cache.Get(chapters[0]);
        for (int i = 1; i < 4; i++) cache.Get(chapters[i]);
        Assert.Same(first, cache.Get(chapters[0]));
        cache.Get(chapters[4]);
        Assert.Equal(4, cache.Count);
        Assert.Same(first, cache.Get(chapters[0]));
        Assert.Equal(5, cache.AnalysisCount);
        cache.Get(chapters[1]);
        Assert.Equal(6, cache.AnalysisCount);
        Assert.Equal(4, cache.Count);
    }

    [Fact]
    public async Task ConcurrentRequests_ComputeOneAnalysisForEquivalentSnapshots()
    {
        var cache = new ChapterAnalysisCache();
        var chapter = Chapter();
        var snapshots = Enumerable.Range(0, 24).Select(_ => DocumentJson.Clone(chapter)).ToArray();
        var results = await Task.WhenAll(snapshots.Select(snapshot => Task.Run(() => cache.Get(snapshot))));
        Assert.All(results, result => Assert.Same(results[0], result));
        Assert.Equal(1, cache.AnalysisCount);
    }

    [Fact]
    public async Task Service_ReusesOneChapterScanAcrossPromptsAutomaticNormalizationAndManualSuggestions()
    {
        var chapter = Chapter();
        var client = new FakeClient { Respond = (_, _) => Task.FromResult(FakeClient.Completion("Anh đóng cửa.")) };
        var service = new TranslationService(chapter, client, new MemoryStore());
        var uncachedClient = new FakeClient { Respond = (_, _) => Task.FromResult(FakeClient.Completion("Anh đóng cửa.")) };
        var uncached = new TranslationService(chapter, uncachedClient, new MemoryStore()) { ChapterAnalyses = new(0) };
        var old = new LegacyNarrativePronounTracker();
        var references = old.Scan(chapter.OriginalText, chapter.CharacterProject);
        foreach (var segment in chapter.Segments)
        {
            var before = service.GetWorkingSnapshot();
            string previous = before.Segments.LastOrDefault(s => s.Order < segment.Order)?.EditableText ?? "";
            var expected = new PronounResolutionEngine().ScanSegment(segment.Id, segment.Order, segment.SourceText,
                "Anh đóng cửa.", previous, before.CharacterProject, false, before.ConfirmedTerms,
                old.HintForSegment(chapter.OriginalText, segment, references));
            string expectedText = PronounResolutionEngine.Apply("Anh đóng cửa.", expected);
            var expectedAttempt = await uncached.TranslateAsync(segment.Id);
            var actualAttempt = await service.TranslateAsync(segment.Id);
            Assert.Equal(expectedAttempt.Prompt, actualAttempt.Prompt);
            Assert.Equal(expectedAttempt.Issues, actualAttempt.Issues);
            Assert.Equal(expectedText, service.GetSnapshot().Segments.Single(s => s.Id == segment.Id).EditableText);
            Assert.Equal(uncached.GetSnapshot().Segments.Single(s => s.Id == segment.Id).EditableText,
                service.GetSnapshot().Segments.Single(s => s.Id == segment.Id).EditableText);
        }
        Assert.Equal(chapter.Segments.Count, client.Calls);
        var snapshot = service.GetSnapshot();
        var expectedSuggestions = new List<PronounSuggestion>();
        string prior = "";
        foreach (var segment in snapshot.Segments)
        {
            expectedSuggestions.AddRange(new PronounResolutionEngine().ScanSegment(segment.Id, segment.Order, segment.SourceText,
                segment.EditableText, prior, snapshot.CharacterProject, segment.IsProtectedFromAutomaticTranslation,
                snapshot.ConfirmedTerms, old.HintForSegment(snapshot.OriginalText, segment, references)));
            prior = segment.EditableText;
        }
        Assert.NotEmpty(expectedSuggestions);
        var actualSuggestions = service.ScanPronouns().Items.Select(WithoutRandomId).ToArray();
        Assert.Equal(expectedSuggestions.Select(WithoutRandomId), actualSuggestions);
        Assert.Equal(uncached.ScanPronouns().Items.Select(WithoutRandomId), actualSuggestions);
        Assert.Equal(2 * chapter.Segments.Count + 1, uncached.ChapterAnalyses.AnalysisCount);
        Assert.Equal(1, service.ChapterAnalyses.AnalysisCount);
        output.WriteLine($"{chapter.Segments.Count} translated segments plus manual suggestions: measured narrative scans {uncached.ChapterAnalyses.AnalysisCount} uncached -> {service.ChapterAnalyses.AnalysisCount} cached; identical prompts, issues, normalized text and suggestions.");
    }

    [Fact]
    public async Task Service_InvalidatesAfterSourceAndCharacterConfigurationChanges()
    {
        var chapter = Chapter();
        var service = new TranslationService(chapter, new FakeClient(), new MemoryStore());
        var segmentId = chapter.Segments[1].Id;
        string before = service.PreviewPrompt(segmentId, chapter.SelectedPreset);
        Assert.Contains("narrative pronoun = ông (LOCKED)", before);

        var project = service.GetSnapshot().CharacterProject;
        project.Profiles[0].PrimaryNarrativePronoun = "hắn";
        await service.ConfigureCharacterProjectAsync(project);
        string after = service.PreviewPrompt(segmentId, chapter.SelectedPreset);
        Assert.Contains("narrative pronoun = hắn (LOCKED)", after);
        Assert.NotEqual(before, after);
        Assert.Equal(2, service.ChapterAnalyses.AnalysisCount);

        string updatedSource = chapter.OriginalText.Replace("林风", "阿风", StringComparison.Ordinal);
        await service.ApplySourceEditAsync(service.PreviewSourceEdit(updatedSource), confirmed: true);
        var changed = service.GetSnapshot();
        var changedSegment = changed.Segments[0];
        service.PreviewPrompt(changedSegment.Id, changed.SelectedPreset);
        Assert.Equal(3, service.ChapterAnalyses.AnalysisCount);

        project = changed.CharacterProject;
        project.Profiles[0].Aliases.Add("阿风");
        await service.ConfigureCharacterProjectAsync(project);
        Assert.Equal(new TranslationService(service.GetSnapshot(), new FakeClient(), new MemoryStore())
            .PreviewPrompt(changedSegment.Id, changed.SelectedPreset), service.PreviewPrompt(changedSegment.Id, changed.SelectedPreset));
        Assert.Equal(4, service.ChapterAnalyses.AnalysisCount);
    }

    [Fact]
    public void Service_InvalidatesProjectResourceContentEvenWhenItsRevisionAndTimestampAreUnchanged()
    {
        var chapter = Chapter();
        chapter.ProjectId = chapter.CharacterProject.Id;
        var resources = new ProjectResources { ProjectId = chapter.ProjectId.Value, Revision = 7, Characters = chapter.CharacterProject };
        var service = new TranslationService(chapter, new FakeClient(), new MemoryStore());
        service.UseProjectResources(resources);
        var segmentId = chapter.Segments[1].Id;
        service.UpdateDraft(segmentId, "Anh đóng cửa.");
        Assert.Contains("narrative pronoun = ông (LOCKED)", service.PreviewPrompt(segmentId, chapter.SelectedPreset));
        Assert.Contains(service.ScanPronouns().Items, i => i.Kind == PronounSuggestionKind.Protected);
        Assert.Equal(1, service.ChapterAnalyses.AnalysisCount);

        resources.Characters.Profiles[0].Aliases.Add("阿风");
        service.UseProjectResources(resources);
        service.PreviewPrompt(segmentId, chapter.SelectedPreset);
        Assert.Equal(2, service.ChapterAnalyses.AnalysisCount);

        resources.Characters.Profiles[0].Gender = CharacterGender.Female;
        service.UseProjectResources(resources);
        service.PreviewPrompt(segmentId, chapter.SelectedPreset);
        Assert.Equal(3, service.ChapterAnalyses.AnalysisCount);

        resources.Characters.Profiles[0].NarrativePronounLocked = false;
        service.UseProjectResources(resources);
        Assert.Empty(service.ScanPronouns().Items);
        Assert.Equal(4, service.ChapterAnalyses.AnalysisCount);
        Assert.Equal(7, resources.Revision);
    }

    private static PronounSuggestion WithoutRandomId(PronounSuggestion item) => item with { Id = Guid.Empty };

    private static ChapterDocument Chapter()
    {
        string[] passages = ["林风回头。", "他关门。😀\n", "林风说道：", "“苏婉，你来了。”\n", "苏婉停下。", "她看着林风。"];
        var chapter = TranslationService.CreateChapter("Cache fixture", string.Concat(passages));
        chapter.ChapterNumber = 4;
        chapter.Segments.Clear();
        int start = 0;
        foreach (string passage in passages)
        {
            chapter.Segments.Add(new()
            {
                Id = TextSegmenter.CreateStableId(chapter.Id, start, passage), Order = chapter.Segments.Count + 1,
                StartUtf16 = start, SourceText = passage, HanCharacterCount = TextSegmenter.CountHanCharacters(passage)
            });
            start += passage.Length;
        }
        chapter.SelectedSegmentId = chapter.Segments[0].Id;
        var male = new CharacterProfile
        {
            ChineseName = "林风", VietnameseName = "Lâm Phong", Gender = CharacterGender.Male,
            PrimaryNarrativePronoun = "ông", SecondaryNarrativePronoun = "hắn ta", DisallowedNarrativePronouns = ["anh"],
            NarrativePronounLocked = true
        };
        var female = new CharacterProfile
        {
            ChineseName = "苏婉", VietnameseName = "Tô Uyển", Gender = CharacterGender.Female,
            PrimaryNarrativePronoun = "nàng", SecondaryNarrativePronoun = "nàng ấy", DisallowedNarrativePronouns = ["cô"],
            NarrativePronounLocked = true
        };
        male.Relationships.Add(new() { TargetCharacterId = female.Id, SelfReference = "tôi", AddresseeReference = "cô", Enabled = true });
        chapter.CharacterProject = new() { Name = "Cache fixture", ConsistencyLockEnabled = true, Profiles = [male, female] };
        return chapter;
    }
}
