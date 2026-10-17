using System.Text.Json;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class CharacterConsistencyLockTests
{
    [Fact]
    public void GoldenCorpus_SelectsOnlyExpectedConfirmedRules()
    {
        CharacterProfile lam = Profile("林风", "Lâm Phong", "hắn");
        CharacterProfile to = Profile("苏婉", "Tô Uyển", "nàng");
        lam.Relationships.Add(Relation(lam, to, "huynh", "muội"));
        CharacterProject project = Project(lam, to);
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "CharacterConsistency", "golden-v1.json");
        GoldenCase[] cases = JsonSerializer.Deserialize<GoldenCase[]>(File.ReadAllText(path), DocumentJson.Options)!;

        foreach (GoldenCase item in cases)
        {
            CharacterConsistencySelection result = new CharacterConsistencyRuleSelector().Select(project, item.Source);
            Assert.All(item.ExpectedPromptTerms, term => Assert.Contains(term, result.PromptBlock));
            Assert.All(item.AbsentPromptTerms, term => Assert.DoesNotContain(term, result.PromptBlock));
            Assert.False(string.IsNullOrWhiteSpace(item.ReferenceTranslation));
        }
    }

    [Fact]
    public void LockOff_LeavesPromptUnchanged()
    {
        CharacterProject project = Project(Profile("林风", "Lâm Phong", "hắn"));
        project.ConsistencyLockEnabled = false;
        Assert.Equal("", new CharacterConsistencyRuleSelector().Select(project, "林风走了。 ").PromptBlock);
    }

    [Fact]
    public void SingleRelevantCharacter_InjectsOnlyPrimaryLockedPronoun()
    {
        CharacterProfile lam = Profile("林风", "Lâm Phong", "hắn");
        lam.SecondaryNarrativePronoun = "anh ấy";
        CharacterProfile to = Profile("苏婉", "Tô Uyển", "nàng");
        var result = new CharacterConsistencyRuleSelector().Select(Project(lam, to), "林风推门而入。");
        Assert.Contains("林风 = Lâm Phong", result.PromptBlock);
        Assert.Contains("narrative pronoun = hắn (LOCKED)", result.PromptBlock);
        Assert.DoesNotContain("anh ấy", result.PromptBlock);
        Assert.DoesNotContain("Tô Uyển", result.PromptBlock);
        Assert.Equal(1, result.NarrativeRuleCount);
    }

    [Fact]
    public void LongerNameWinsWhenNamesOverlap()
    {
        CharacterProfile shortName = Profile("林风", "Lâm Phong", "hắn");
        CharacterProfile longName = Profile("叶林风", "Diệp Lâm Phong", "y");
        var result = new CharacterConsistencyRuleSelector().Select(Project(shortName, longName), "叶林风回来了。");
        Assert.Contains("Diệp Lâm Phong", result.PromptBlock);
        Assert.DoesNotContain("= Lâm Phong;", result.PromptBlock);
    }

    [Fact]
    public void DuplicateConfirmedName_IsAmbiguousAndNotInjected()
    {
        CharacterProfile first = Profile("林风", "Lâm Phong", "hắn");
        CharacterProfile second = Profile("林风", "Lâm Phong khác", "y");
        var result = new CharacterConsistencyRuleSelector().Select(Project(first, second), "林风回来了。");
        Assert.Empty(result.PromptBlock);
    }

    [Fact]
    public void ConfirmedChineseAliasMatches_WhileEmptyProjectAddsNothing()
    {
        CharacterProfile profile = Profile("林风", "Lâm Phong", "hắn");
        profile.Aliases.Add("小风");
        Assert.Contains("Lâm Phong", new CharacterConsistencyRuleSelector().Select(Project(profile), "小风回来了。").PromptBlock);
        Assert.Empty(new CharacterConsistencyRuleSelector().Select(Project(), "小风回来了。").PromptBlock);
    }

    [Fact]
    public void MultipleCharactersMaySharePronounWithoutLosingIdentity()
    {
        CharacterProfile first = Profile("林风", "Lâm Phong", "hắn");
        CharacterProfile second = Profile("陈老", "Trần lão", "hắn");
        var result = new CharacterConsistencyRuleSelector().Select(Project(first, second), "林风拜见陈老。");
        Assert.Equal(2, result.NarrativeRuleCount);
        Assert.Contains("林风 = Lâm Phong", result.PromptBlock);
        Assert.Contains("陈老 = Trần lão", result.PromptBlock);
    }

    [Fact]
    public void DirectionalDialogueRules_AreIndependentAndRequireDialogueEvidence()
    {
        CharacterProfile lam = Profile("林风", "Lâm Phong", "hắn");
        CharacterProfile to = Profile("苏婉", "Tô Uyển", "nàng");
        lam.Relationships.Add(Relation(lam, to, "ta", "nàng"));
        to.Relationships.Add(Relation(to, lam, "thiếp", "chàng"));
        CharacterProject project = Project(lam, to);

        Assert.Equal(0, new CharacterConsistencyRuleSelector().Select(project, "林风 gặp 苏婉 ngoài sân.").DialogueRuleCount);
        var result = new CharacterConsistencyRuleSelector().Select(project, "林风问苏婉：“你回来了吗？”");
        Assert.Contains("Lâm Phong -> Tô Uyển: self = ta; address = nàng", result.PromptBlock);
        Assert.Contains("Tô Uyển -> Lâm Phong: self = thiếp; address = chàng", result.PromptBlock);
        Assert.Equal(2, result.DialogueRuleCount);
    }

    [Fact]
    public void BoundedSelector_DropsWholeRulesInsteadOfTruncatingText()
    {
        CharacterProfile[] profiles = Enumerable.Range(1, 30)
            .Select(index => Profile($"人物{index}", $"Nhân vật {index} với tên rất dài", index % 2 == 0 ? "hắn" : "nàng"))
            .ToArray();
        string source = string.Join('，', profiles.Select(profile => profile.ChineseName));
        var result = new CharacterConsistencyRuleSelector(4, 0, 520).Select(Project(profiles), source);
        Assert.InRange(result.CharacterIds.Count, 1, 4);
        Assert.True(result.OmittedRuleCount > 0);
        Assert.True(result.PromptBlock.Length <= 520);
        Assert.False(result.PromptBlock.EndsWith("narrative pron", StringComparison.Ordinal));
    }

    [Fact]
    public void CustomPreset_ReceivesConsistencyBlockWithoutDuplicatingNameGlossary()
    {
        CharacterProfile profile = Profile("林风", "Lâm Phong", "hắn");
        CharacterProject project = Project(profile);
        CharacterConsistencySelection selected = new CharacterConsistencyRuleSelector().Select(project, "林风回来。 ");
        var prompt = new TranslationPromptBuilder().Build("林风回来。", [], "", new PromptPreset
        {
            Name = "Tùy chỉnh", Template = "CUSTOM\n{{context}}\n{{source_text}}\n{{glossary}}"
        }, selected.PromptBlock);
        Assert.StartsWith("CHARACTER CONSISTENCY", prompt, StringComparison.Ordinal);
        Assert.Contains("CUSTOM", prompt);
        Assert.Equal(1, Count(prompt, "林风 = Lâm Phong"));
    }

    [Fact]
    public async Task TranslationUsesOneProviderRequestAndStoresExactConsistencyPrompt()
    {
        ChapterDocument chapter = TranslationService.CreateChapter("Thử", "林风推门而入。");
        var client = new FakeClient();
        var service = new TranslationService(chapter, client, new MemoryStore());
        await service.ConfigureCharacterProjectAsync(Project(Profile("林风", "Lâm Phong", "hắn")));
        TranslationAttempt attempt = await service.TranslateAsync(chapter.Segments[0].Id);
        Assert.Equal(1, client.Calls);
        Assert.Contains("CHARACTER CONSISTENCY", attempt.Prompt);
        Assert.Equal(attempt.Prompt, client.Prompts.Single());
        Assert.DoesNotContain("Reference glossary", attempt.Prompt);
    }

    [Fact]
    public void LockedSecondaryPronoun_IsResolvedToPrimaryOnlyWithClearAntecedent()
    {
        CharacterProfile profile = Profile("林风", "Lâm Phong", "hắn");
        profile.SecondaryNarrativePronoun = "anh ấy";
        string text = "Lâm Phong quay lại. Anh ấy đóng cửa.";
        var engine = new PronounResolutionEngine();
        var changes = engine.ScanSegment(Guid.NewGuid(), 1, "林风回头。", text, "", Project(profile), false);
        Assert.Equal("Lâm Phong quay lại. Hắn đóng cửa.", PronounResolutionEngine.Apply(text, changes));
    }

    [Fact]
    public void UnregisteredCharacterNearAntecedent_PreventsAutomaticPronounReplacement()
    {
        CharacterProfile profile = Profile("林风", "Lâm Phong", "hắn");
        string text = "Trương Tam nhìn Lâm Phong. Anh ấy đóng cửa.";

        IReadOnlyList<PronounSuggestion> suggestions = new PronounResolutionEngine().ScanSegment(
            Guid.NewGuid(), 1, "张三看着林风。", text, "", Project(profile), false);

        Assert.DoesNotContain(suggestions, x => x.Kind == PronounSuggestionKind.SafeReplacement);
        Assert.Equal(text, PronounResolutionEngine.Apply(text, suggestions));
    }

    [Fact]
    public void UnpairedStandaloneSingleQuote_PreventsAutomaticPronounReplacement()
    {
        CharacterProfile profile = Profile("林风", "Lâm Phong", "hắn");
        string text = "Lâm Phong' quay lại. Anh ấy đóng cửa.";

        IReadOnlyList<PronounSuggestion> suggestions = new PronounResolutionEngine().ScanSegment(
            Guid.NewGuid(), 1, "林风回头。", text, "", Project(profile), false);

        Assert.DoesNotContain(suggestions, x => x.Kind == PronounSuggestionKind.SafeReplacement);
        Assert.Equal(text, PronounResolutionEngine.Apply(text, suggestions));
    }

    [Fact]
    public async Task CurrentFieldsRoundTripAndIncompleteJsonIsRejected()
    {
        string directory = Path.Combine(Path.GetTempPath(), "qt-consistency-" + Guid.NewGuid().ToString("N"));
        try
        {
            CharacterProfile speaker = Profile("林风", "Lâm Phong", "hắn");
            CharacterProfile listener = Profile("苏婉", "Tô Uyển", "nàng");
            speaker.Relationships.Add(Relation(speaker, listener, "ta", "nàng"));
            CharacterProject project = Project(speaker, listener);
            var store = new CharacterProjectStore(directory);
            await store.SaveAsync(project);
            CharacterProject loaded = await store.LoadAsync(store.GetPath(project.Id));
            Assert.True(loaded.ConsistencyLockEnabled);
            Assert.True(loaded.Profiles[0].NarrativePronounLocked);
            Assert.Equal("ta", loaded.Profiles[0].Relationships[0].SelfReference);

            string incomplete = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, id = Guid.NewGuid(), name = "Cũ", updatedAt = DateTimeOffset.UtcNow,
                normalizeAfterTranslation = false,
                profiles = Array.Empty<object>()
            }, DocumentJson.Options);
            string path = Path.Combine(directory, "incomplete.characters.json");
            await File.WriteAllTextAsync(path, incomplete);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void JsonImportCannotOverwriteLockedPronounOrDirectionalRule()
    {
        CharacterProfile speaker = Profile("林风", "Lâm Phong", "hắn");
        CharacterProfile listener = Profile("苏婉", "Tô Uyển", "nàng");
        speaker.Relationships.Add(Relation(speaker, listener, "ta", "nàng"));
        CharacterProject current = Project(speaker, listener);

        CharacterProfile importedSpeaker = Profile("林风", "Lâm Phong", "anh ấy");
        CharacterProfile importedListener = Profile("苏婉", "Tô Uyển", "cô ấy");
        importedSpeaker.Id = speaker.Id; importedListener.Id = listener.Id;
        importedSpeaker.Relationships.Add(Relation(importedSpeaker, importedListener, "tôi", "cô"));
        CharacterProject incoming = Project(importedSpeaker, importedListener);

        CharacterConsistencyProtection.PreserveLockedRules(current, incoming);
        Assert.Equal("hắn", incoming.Profiles[0].PrimaryNarrativePronoun);
        Assert.Equal("ta", incoming.Profiles[0].Relationships[0].SelfReference);
        Assert.Equal("nàng", incoming.Profiles[0].Relationships[0].AddresseeReference);
    }

    private static CharacterProject Project(params CharacterProfile[] profiles) => new()
    {
        Name = "Synthetic", ConsistencyLockEnabled = true, Profiles = [.. profiles]
    };

    private static CharacterProfile Profile(string chinese, string vietnamese, string pronoun) => new()
    {
        ChineseName = chinese, VietnameseName = vietnamese, PrimaryNarrativePronoun = pronoun,
        SecondaryNarrativePronoun = pronoun + " ấy", NarrativePronounLocked = true,
        NormalizationEnabled = true, DisallowedNarrativePronouns = ["anh ấy", "cô ấy"]
    };

    private static CharacterRelationship Relation(CharacterProfile speaker, CharacterProfile listener,
        string self, string addressee) => new()
    {
        TargetCharacterId = listener.Id, SpeakerCharacterId = speaker.Id, ListenerCharacterId = listener.Id,
        Relationship = "đã xác nhận", SelfReference = self, AddresseeReference = addressee,
        Enabled = true, Locked = true
    };

    private static int Count(string value, string part) =>
        (value.Length - value.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;

    private sealed record GoldenCase(string Source, string ReferenceTranslation,
        string[] ExpectedPromptTerms, string[] AbsentPromptTerms);
}
