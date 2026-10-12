using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class CharacterPronounTests
{
    private readonly PronounResolutionEngine _engine = new();

    [Theory]
    [InlineData("Vương Tú Lan là mẹ của hai đứa trẻ. Bà nhìn con trai.", "Vương Tú Lan là mẹ của hai đứa trẻ. Nàng nhìn con trai.")]
    [InlineData("Vương Tú Lan đã kết hôn. Cô ấy quay đầu.", "Vương Tú Lan đã kết hôn. Nàng quay đầu.")]
    public void FemaleFamilyOrMarriageStatus_DoesNotOverrideConfirmedNarrativePronoun(string text, string expected)
    {
        var suggestions = Scan(text, Female());
        Assert.Equal(expected, PronounResolutionEngine.Apply(text, suggestions));
        Assert.Single(suggestions, s => s.Kind == PronounSuggestionKind.SafeReplacement);
    }

    [Fact]
    public void MaleWithChild_UsesHanWhenAntecedentIsExplicit()
    {
        string text = "Lâm Triết bế con lên. Anh ấy bước ra ngoài.";
        Assert.Equal("Lâm Triết bế con lên. Hắn bước ra ngoài.", PronounResolutionEngine.Apply(text, Scan(text, Male())));
    }

    [Fact]
    public void GrandmotherKinship_IsNeverChangedToNangNoi()
    {
        string text = "Vương Tú Lan quay lại. Bà nội hắn bước vào phòng.";
        var items = Scan(text, Female());
        Assert.DoesNotContain(items, i => i.Kind == PronounSuggestionKind.SafeReplacement);
        Assert.Contains(items, i => i.Original.Equals("Bà", StringComparison.OrdinalIgnoreCase) && i.Kind == PronounSuggestionKind.Valid);
        Assert.Equal(text, PronounResolutionEngine.Apply(text, items));
    }

    [Theory]
    [InlineData("Vương Tú Lan gọi: “Bà ơi, mẹ ở đâu?”")]
    [InlineData("Vương Tú Lan gọi: 'Bà ơi, mẹ ở đâu?'")]
    public void DirectDialogueAndFamilyAddress_ArePreserved(string text)
    {
        var items = Scan(text, Female());
        Assert.DoesNotContain(items, i => i.Kind == PronounSuggestionKind.SafeReplacement);
        Assert.Equal(text, PronounResolutionEngine.Apply(text, items));
    }

    [Fact]
    public void FirstPersonNarrator_IsUntouched()
    {
        string text = "Tôi quay đầu. Ta biết mình phải làm gì.";
        Assert.Empty(Scan(text, Female(), Male()));
    }

    [Fact]
    public void TwoFemaleCharacters_MakePronounAmbiguousInsteadOfGuessing()
    {
        string text = "Vương Tú Lan đứng cạnh Lý Mai. Bà nhìn về phía cửa.";
        var items = Scan(text, Female(), Female("Lý Mai", "李梅"));
        Assert.Contains(items, i => i.Kind == PronounSuggestionKind.Ambiguous);
        Assert.DoesNotContain(items, i => i.Kind == PronounSuggestionKind.SafeReplacement);
        Assert.Equal(text, PronounResolutionEngine.Apply(text, items));
    }

    [Fact]
    public void MultiplePronounsInOnePass_CanResolveDifferentCharactersByProfile()
    {
        string text = "Vương Tú Lan bảo Lâm Triết rằng bà sẽ chờ anh ấy ngoài cửa.";
        var items = Scan(text, Female(), Male());
        Assert.Equal("Vương Tú Lan bảo Lâm Triết rằng nàng sẽ chờ hắn ngoài cửa.", PronounResolutionEngine.Apply(text, items));
        Assert.Equal(2, items.Count(i => i.Kind == PronounSuggestionKind.SafeReplacement));
        Assert.Equal(2, items.Where(i => i.Kind == PronounSuggestionKind.SafeReplacement).Select(i => i.CharacterId).Distinct().Count());
    }

    [Fact]
    public void IndirectNarrationIsProcessed_ButQuotedExcerptIsNotMistakenForNarration()
    {
        string narrative = "Vương Tú Lan nói rằng bà sẽ trở lại.";
        Assert.Equal("Vương Tú Lan nói rằng nàng sẽ trở lại.", PronounResolutionEngine.Apply(narrative, Scan(narrative, Female())));
        string quotation = "Vương Tú Lan nhắc lại cụm từ “bà ấy sẽ trở lại”.";
        Assert.Equal(quotation, PronounResolutionEngine.Apply(quotation, Scan(quotation, Female())));
    }

    [Fact]
    public void NameMentionedOnlyInsideQuote_IsNotUsedAsNarrativeAntecedent()
    {
        string text = "“Vương Tú Lan đã đi rồi!” Người lạ nói. Bà nhìn ra cửa.";
        var items = Scan(text, Female());
        Assert.Contains(items, i => i.Original.Equals("Bà", StringComparison.OrdinalIgnoreCase) && i.Kind == PronounSuggestionKind.Ambiguous);
        Assert.DoesNotContain(items, i => i.Kind == PronounSuggestionKind.SafeReplacement);
    }

    [Theory]
    [InlineData("Vương Tú Lan gặp anh trai trong sân.")]
    [InlineData("Lâm Triết đứng cạnh cô em gái.")]
    public void KinshipCompoundOutsideDialogue_IsPreserved(string text)
    {
        var items = Scan(text, Female(), Male());
        Assert.DoesNotContain(items, i => i.Kind == PronounSuggestionKind.SafeReplacement);
        Assert.Equal(text, PronounResolutionEngine.Apply(text, items));
    }

    [Fact]
    public async Task AutomaticPipeline_KeepsRawAiAndStoresNormalizedVersionWithoutApproval()
    {
        var chapter = TranslationService.CreateChapter("Thử", "王秀兰回头。她看着儿子。");
        var client = new FakeClient { Respond = (_, _) => Task.FromResult(FakeClient.Completion("Vương Tú Lan quay đầu. Bà nhìn con trai.")) };
        var service = new TranslationService(chapter, client, new MemoryStore());
        await service.ConfigureCharacterProjectAsync(Project(Female()));
        var attempt = await service.TranslateAsync(chapter.Segments[0].Id);
        var segment = service.GetSnapshot().Segments[0];
        Assert.Equal("Vương Tú Lan quay đầu. Bà nhìn con trai.", attempt.AiText);
        Assert.Equal("Vương Tú Lan quay đầu. Nàng nhìn con trai.", segment.EditableText);
        Assert.NotNull(segment.NormalizedText);
        Assert.Single(segment.NormalizationHistory);
        Assert.NotEqual(SegmentStatus.Approved, segment.Status);
    }

    [Fact]
    public async Task ManualDraft_IsNeverOverwrittenOrAutoApplied()
    {
        var (service, chapter, client) = OldTranslatedService("Vương Tú Lan quay đầu. Bà nhìn con trai.");
        await service.ConfigureCharacterProjectAsync(Project(Female()));
        service.UpdateDraft(chapter.Segments[0].Id, "Bản người dùng đã sửa. Bà vẫn là danh xưng tôi chọn.");
        var report = service.ScanPronouns();
        Assert.Equal(0, report.Safe);
        Assert.True(report.Protected > 0 || report.Ambiguous > 0);
        Assert.Null(await service.ApplySafePronounChangesAsync(report));
        Assert.Equal("Bản người dùng đã sửa. Bà vẫn là danh xưng tôi chọn.", service.GetSnapshot().Segments[0].EditableText);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task ExistingChapterCanNormalizeWithoutCallingTranslateGemma_AndUndoWholeBatch()
    {
        var (service, _, client) = OldTranslatedService("Vương Tú Lan quay đầu. Bà nhìn con trai.");
        await service.ConfigureCharacterProjectAsync(Project(Female()));
        var report = service.ScanPronouns();
        Guid? batch = await service.ApplySafePronounChangesAsync(report);
        Assert.NotNull(batch);
        Assert.Equal("Vương Tú Lan quay đầu. Nàng nhìn con trai.", service.GetSnapshot().Segments[0].EditableText);
        Assert.Equal(0, client.Calls);
        Assert.Equal(batch, await service.UndoLatestPronounNormalizationAsync());
        Assert.Equal("Vương Tú Lan quay đầu. Bà nhìn con trai.", service.GetSnapshot().Segments[0].EditableText);
    }

    [Fact]
    public async Task UndoRefusesToOverwriteLaterHumanEdit()
    {
        var (service, chapter, _) = OldTranslatedService("Vương Tú Lan quay đầu. Bà nhìn con trai.");
        await service.ConfigureCharacterProjectAsync(Project(Female()));
        await service.ApplySafePronounChangesAsync(service.ScanPronouns());
        service.UpdateDraft(chapter.Segments[0].Id, "Người dùng sửa sau chuẩn hóa.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UndoLatestPronounNormalizationAsync());
        Assert.Equal("Người dùng sửa sau chuẩn hóa.", service.GetSnapshot().Segments[0].EditableText);
    }

    [Fact]
    public async Task CharacterProject_RoundTripsAsUtf8JsonWithStableIdsAndRelations()
    {
        string directory = Path.Combine(Path.GetTempPath(), "qtaie-characters-" + Guid.NewGuid().ToString("N"));
        try
        {
            var female = Female(); var male = Male();
            female.Relationships.Add(new() { TargetCharacterId = male.Id, Relationship = "vợ chồng", AddressingNotes = "giữ anh/em trong thoại" });
            var project = Project(female, male);
            var store = new CharacterProjectStore(directory);
            await store.SaveAsync(project);
            var loaded = await store.LoadAsync(store.GetPath(project.Id));
            Assert.Equal(project.Id, loaded.Id);
            Assert.Equal(female.Id, loaded.Profiles[0].Id);
            Assert.Equal(male.Id, loaded.Profiles[0].Relationships[0].TargetCharacterId);
            Assert.Equal("王秀兰", loaded.Profiles[0].ChineseName);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ChapterFile_RestoresCharacterProfilesAndNormalizationHistory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "qtaie-character-chapter-" + Guid.NewGuid().ToString("N"));
        try
        {
            var chapter = TranslationService.CreateChapter("Lưu", "王秀兰回头。");
            chapter.Segments[0].Attempts.Add(new TranslationAttempt { StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow,
                Outcome = AttemptOutcome.Completed, AiText = "Vương Tú Lan quay đầu. Bà mỉm cười.",
                ModelOptions = new(), PromptVersion = "legacy", Prompt = "legacy" });
            chapter.Segments[0].Status = SegmentStatus.AwaitingReview;
            var store = new ChapterFileStore(directory);
            var service = new TranslationService(chapter, new FakeClient(), store);
            await service.ConfigureCharacterProjectAsync(Project(Female()));
            await service.ApplySafePronounChangesAsync(service.ScanPronouns());
            await service.SaveAsync();
            var loaded = await store.LoadAsync(store.GetPath(chapter.Id));
            Assert.Equal(3, loaded.Chapter.SchemaVersion);
            Assert.Equal("Vương Tú Lan", loaded.Chapter.CharacterProject.Profiles[0].VietnameseName);
            Assert.Equal("Vương Tú Lan quay đầu. Nàng mỉm cười.", loaded.Chapter.Segments[0].EditableText);
            Assert.Single(loaded.Chapter.Segments[0].NormalizationHistory);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ConfirmedChineseNameIsReusedInGlossary_ButBlankChineseIsNeverInvented()
    {
        var chapter = TranslationService.CreateChapter("Thử", "王秀兰回头。林哲回答。");
        var client = new FakeClient(); var service = new TranslationService(chapter, client, new MemoryStore());
        var unknownChinese = Male(); unknownChinese.ChineseName = "";
        await service.ConfigureCharacterProjectAsync(Project(Female(), unknownChinese));
        var snapshot = service.GetSnapshot();
        Assert.Contains(snapshot.ConfirmedTerms, t => t.Chinese == "王秀兰" && t.Vietnamese == "Vương Tú Lan");
        Assert.DoesNotContain(snapshot.ConfirmedTerms, t => t.Vietnamese == "Lâm Triết");
        await service.TranslateAsync(chapter.Segments[0].Id);
        Assert.Contains("王秀兰 = Vương Tú Lan", client.Prompts[0]);
    }

    [Fact]
    public async Task UpdatingProfileMapping_RemovesItsOldGlossaryEntry()
    {
        var chapter = TranslationService.CreateChapter("Thử", "王秀兰回头。王兰回答。");
        var service = new TranslationService(chapter, new FakeClient(), new MemoryStore());
        var female = Female();
        await service.ConfigureCharacterProjectAsync(Project(female));
        female.ChineseName = "王兰";
        await service.ConfigureCharacterProjectAsync(Project(female));
        var terms = service.GetSnapshot().ConfirmedTerms;
        Assert.DoesNotContain(terms, t => t.Chinese == "王秀兰");
        Assert.Contains(terms, t => t.Chinese == "王兰" && t.Vietnamese == "Vương Tú Lan");
    }

    [Fact]
    public async Task ApplyingStaleMultiSegmentReport_IsAtomic()
    {
        var chapter = TranslationService.CreateChapter("Hai đoạn", "甲。\n乙。",
            new() { MinimumHanCharacters = 1, TargetHanCharacters = 1, MaximumHanCharacters = 1 });
        Assert.True(chapter.Segments.Count >= 2);
        foreach (var segment in chapter.Segments)
        {
            segment.Attempts.Add(new TranslationAttempt { StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow,
                Outcome = AttemptOutcome.Completed, AiText = "Vương Tú Lan quay lại. Bà nhìn ra cửa.",
                ModelOptions = new(), PromptVersion = "legacy", Prompt = "legacy" });
            segment.Status = SegmentStatus.AwaitingReview;
        }
        var service = new TranslationService(chapter, new FakeClient(), new MemoryStore());
        await service.ConfigureCharacterProjectAsync(Project(Female()));
        var report = service.ScanPronouns();
        Assert.True(report.Safe >= 2);
        service.UpdateDraft(chapter.Segments[1].Id, "Đoạn hai đã được người dùng sửa.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplySafePronounChangesAsync(report));
        Assert.Equal("Vương Tú Lan quay lại. Bà nhìn ra cửa.", service.GetSnapshot().Segments[0].EditableText);
    }

    [Fact]
    public async Task AmbiguityBecomesQualityWarning_AndQueuePresetDataRemainIntact()
    {
        var chapter = TranslationService.CreateChapter("Thử", "两个人说话。");
        var client = new FakeClient { Respond = (_, _) => Task.FromResult(FakeClient.Completion("Vương Tú Lan đứng cạnh Lý Mai. Bà nhìn ra cửa.")) };
        var service = new TranslationService(chapter, client, new MemoryStore());
        var preset = new PromptPreset { Name = "Preset thử", Template = "Dịch: {{source_text}}" };
        await service.ConfigureAsync(new(), preset);
        await service.ConfigureCharacterProjectAsync(Project(Female(), Female("Lý Mai", "李梅")));
        await new TranslationQueue(service).StartAsync();
        var snapshot = service.GetSnapshot();
        Assert.Equal(QueueStatus.CompletedWithWarnings, snapshot.Queue.Status);
        Assert.Equal(SegmentStatus.InspectionRequired, snapshot.Segments[0].Status);
        Assert.Contains(snapshot.Segments[0].Attempts[0].Issues, i => i.Code == IssueCode.AmbiguousPronoun);
        Assert.Equal(preset, snapshot.Segments[0].Attempts[0].PresetSnapshot);
        Assert.Equal("Vương Tú Lan đứng cạnh Lý Mai. Bà nhìn ra cửa.", snapshot.Segments[0].EditableText);
    }

    [Fact]
    public void NormalizationChangesOnlyPronounAndPreservesStoryText()
    {
        string text = "Vương Tú Lan mở chiếc hộp đỏ. Bà lấy đúng ba viên ngọc rồi đóng nắp.";
        var result = PronounResolutionEngine.Apply(text, Scan(text, Female()));
        Assert.Equal(text.Replace("Bà", "Nàng", StringComparison.Ordinal), result);
        Assert.Contains("đúng ba viên ngọc", result);
        Assert.Equal(text.Count(c => c == '.'), result.Count(c => c == '.'));
    }

    private IReadOnlyList<PronounSuggestion> Scan(string text, params CharacterProfile[] profiles) =>
        _engine.ScanSegment(Guid.NewGuid(), 1, "原文。", text, "", Project(profiles), false);

    private static CharacterProject Project(params CharacterProfile[] profiles) => new()
    {
        Name = "Truyện kiểm thử", Profiles = [.. profiles]
    };

    private static CharacterProfile Female(string name = "Vương Tú Lan", string chinese = "王秀兰") => new()
    {
        VietnameseName = name, ChineseName = chinese, Gender = CharacterGender.Female,
        PrimaryNarrativePronoun = "nàng", SecondaryNarrativePronoun = "nàng ấy",
        DisallowedNarrativePronouns = ["bà ấy", "cô ấy", "cô ta", "bà", "cô", "chị ấy"]
    };

    private static CharacterProfile Male() => new()
    {
        VietnameseName = "Lâm Triết", ChineseName = "林哲", Gender = CharacterGender.Male,
        PrimaryNarrativePronoun = "hắn", SecondaryNarrativePronoun = "hắn ta",
        DisallowedNarrativePronouns = ["ông ấy", "anh ấy", "cậu ấy", "ông", "anh", "cậu"]
    };

    private static (TranslationService Service, ChapterDocument Chapter, FakeClient Client) OldTranslatedService(string translation)
    {
        var chapter = TranslationService.CreateChapter("Cũ", "王秀兰回头。她看着儿子。");
        chapter.Segments[0].Attempts.Add(new TranslationAttempt
        {
            StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow,
            Outcome = AttemptOutcome.Completed, AiText = translation, ModelOptions = new(), PromptVersion = "legacy", Prompt = "legacy"
        });
        chapter.Segments[0].Status = SegmentStatus.AwaitingReview;
        var client = new FakeClient();
        return (new(chapter, client, new MemoryStore()), chapter, client);
    }
}
