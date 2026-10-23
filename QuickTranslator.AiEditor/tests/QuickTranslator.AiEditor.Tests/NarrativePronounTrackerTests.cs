using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class NarrativePronounTrackerTests
{
    private readonly NarrativePronounTracker _tracker = new();

    [Fact]
    public void NearestMatchingGenderName_IsHighConfidenceAntecedent()
    {
        CharacterProfile male = Male("林风", "Lâm Phong");
        CharacterProfile female = Female("苏婉", "Tô Uyển");

        NarrativePronounReference reference = Assert.Single(_tracker.Scan("苏婉离开。林风回头。他关上门。", Project(male, female)));

        Assert.Equal(NarrativePronounConfidence.High, reference.Confidence);
        Assert.Equal(male.Id, reference.CharacterId);
    }

    [Fact]
    public void TwoSameGenderCharactersInAntecedentSentence_AreAmbiguous()
    {
        CharacterProfile first = Male("林风", "Lâm Phong");
        CharacterProfile second = Male("陈石", "Trần Thạch");

        NarrativePronounReference reference = Assert.Single(_tracker.Scan("林风看着陈石。他关上门。", Project(first, second)));

        Assert.Equal(NarrativePronounConfidence.Ambiguous, reference.Confidence);
        Assert.Null(reference.CharacterId);
    }

    [Fact]
    public void PronounsInsideQuotes_AreIgnored()
    {
        CharacterProfile male = Male("林风", "Lâm Phong");

        Assert.Empty(_tracker.Scan("林风说：“他已经走了。”", Project(male)));
    }

    [Theory]
    [InlineData("林风看着他们离开。")]
    [InlineData("苏婉看着她们的房间。")]
    [InlineData("林风看着其他的房间。")]
    [InlineData("林风不在乎他人的眼光。")]
    [InlineData("林风弹起吉他。")]
    public void PluralPronouns_AreNotMatched(string source)
    {
        Assert.Empty(_tracker.Scan(source, Project(Male("林风", "Lâm Phong"), Female("苏婉", "Tô Uyển"))));
    }

    [Fact]
    public void WholeChapterTracking_CarriesAntecedentAcrossSegmentBoundary()
    {
        CharacterProfile male = Male("林风", "Lâm Phong");
        const string chapter = "林风转过身。\n他关上门。";
        int start = chapter.IndexOf('他');
        var segment = new TranslationSegment { Id = Guid.NewGuid(), Order = 2, StartUtf16 = start, SourceText = chapter[start..] };

        IReadOnlyList<NarrativePronounReference> references = _tracker.Scan(chapter, Project(male));
        NarrativePronounReference reference = Assert.Single(_tracker.ForSegment(chapter, segment, references));

        Assert.Equal(male.Id, reference.CharacterId);
        Assert.Equal(NarrativePronounConfidence.High, reference.Confidence);
    }

    [Fact]
    public void SourceHint_ResolvesDisallowedPronounOnlyWhenVietnameseNameSearchHasNoAnswer()
    {
        CharacterProfile male = Male("林风", "Lâm Phong");
        CharacterProject project = Project(male);
        var hint = new NarrativePronounHint(male.Id, null);

        IReadOnlyList<PronounSuggestion> items = new PronounResolutionEngine().ScanSegment(
            Guid.NewGuid(), 1, "他关上门。", "Anh đóng cửa.", "", project, false, sourceHint: hint);

        Assert.Equal("Ông đóng cửa.", PronounResolutionEngine.Apply("Anh đóng cửa.", items));
        Assert.Single(items, item => item.Kind == PronounSuggestionKind.SafeReplacement && item.CharacterId == male.Id);
    }

    [Fact]
    public void SourceHint_DoesNotOverrideMultipleVietnameseNames()
    {
        CharacterProfile first = Male("林风", "Lâm Phong");
        CharacterProfile second = Male("陈石", "Trần Thạch");
        var hint = new NarrativePronounHint(first.Id, null);
        const string text = "Lâm Phong nhìn Trần Thạch. Anh đóng cửa.";

        IReadOnlyList<PronounSuggestion> items = new PronounResolutionEngine().ScanSegment(
            Guid.NewGuid(), 1, "他关上门。", text, "", Project(first, second), false, sourceHint: hint);

        Assert.Contains(items, item => item.Kind == PronounSuggestionKind.Ambiguous);
        Assert.DoesNotContain(items, item => item.Kind == PronounSuggestionKind.SafeReplacement);
    }

    [Fact]
    public void SourceHint_DoesNotOverrideUnregisteredProperNameGuard()
    {
        CharacterProfile male = Male("林风", "Lâm Phong");
        var hint = new NarrativePronounHint(male.Id, null);
        const string text = "Trương Tam nhìn Lâm Phong. Anh đóng cửa.";

        IReadOnlyList<PronounSuggestion> items = new PronounResolutionEngine().ScanSegment(
            Guid.NewGuid(), 1, "他关上门。", text, "", Project(male), false, sourceHint: hint);

        Assert.Contains(items, item => item.Kind == PronounSuggestionKind.Ambiguous &&
            item.Reason.StartsWith("Ngữ cảnh gần có tên riêng", StringComparison.Ordinal));
        Assert.DoesNotContain(items, item => item.Kind == PronounSuggestionKind.SafeReplacement);
    }

    [Fact]
    public void SourceHint_PreservesProtectedTextAndUnlockedSecondaryPolicy()
    {
        CharacterProfile male = Male("林风", "Lâm Phong");
        var hint = new NarrativePronounHint(male.Id, null);
        IReadOnlyList<PronounSuggestion> protectedItems = new PronounResolutionEngine().ScanSegment(
            Guid.NewGuid(), 1, "他关上门。", "Anh đóng cửa.", "", Project(male), true, sourceHint: hint);
        Assert.Single(protectedItems, item => item.Kind == PronounSuggestionKind.Protected);
        Assert.Equal("Anh đóng cửa.", PronounResolutionEngine.Apply("Anh đóng cửa.", protectedItems));

        CharacterProject unlocked = Project(male);
        unlocked.ConsistencyLockEnabled = false;
        IReadOnlyList<PronounSuggestion> secondary = new PronounResolutionEngine().ScanSegment(
            Guid.NewGuid(), 1, "他关上门。", "Hắn ta đóng cửa.", "", unlocked, false, sourceHint: hint);
        Assert.Single(secondary, item => item.Kind == PronounSuggestionKind.Valid);
    }

    [Fact]
    public void Selector_AddsLockedRuleForPronounReferredCharacter()
    {
        CharacterProfile male = Male("林风", "Lâm Phong");

        CharacterConsistencySelection selection = new CharacterConsistencyRuleSelector().Select(
            Project(male), "他关上门。", pronounReferencedCharacterIds: [male.Id]);

        Assert.Contains("narrative pronoun = ông (LOCKED)", selection.PromptBlock);
        Assert.Contains("referred to as 他 in this passage", selection.PromptBlock);
        Assert.Equal([male.Id], selection.CharacterIds);
    }

    private static CharacterProject Project(params CharacterProfile[] profiles) => new()
    {
        Name = "Synthetic", ConsistencyLockEnabled = true, Profiles = [.. profiles]
    };

    private static CharacterProfile Male(string chinese, string vietnamese) => new()
    {
        ChineseName = chinese, VietnameseName = vietnamese, Gender = CharacterGender.Male,
        PrimaryNarrativePronoun = "ông", SecondaryNarrativePronoun = "hắn ta",
        DisallowedNarrativePronouns = ["anh"], NarrativePronounLocked = true, NormalizationEnabled = true
    };

    private static CharacterProfile Female(string chinese, string vietnamese) => new()
    {
        ChineseName = chinese, VietnameseName = vietnamese, Gender = CharacterGender.Female,
        PrimaryNarrativePronoun = "nàng", SecondaryNarrativePronoun = "nàng ấy",
        DisallowedNarrativePronouns = ["cô"], NarrativePronounLocked = true, NormalizationEnabled = true
    };
}
