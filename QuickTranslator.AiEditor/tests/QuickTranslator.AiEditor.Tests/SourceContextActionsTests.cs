using QuickTranslator.AiEditor.Core.Editing;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class SourceContextActionsTests
{
    [Fact]
    public void SelectionSnapshot_PreservesReadonlyTextboxCoordinatesAndText()
    {
        const string source = "前文张三后文";
        var selected = TextSelectionSnapshot.Capture(source, 2, 2);

        Assert.Equal(2, selected.Start);
        Assert.Equal(2, selected.Length);
        Assert.Equal("张三", selected.Text);
        Assert.Equal(selected.Text, source.Substring(selected.Start, selected.Length));
    }

    [Fact]
    public void ContextMenuCapabilities_AreAvailableOnReadonlyAndEditableSource()
    {
        var readOnly = SourceContextMenuPolicy.Evaluate("张三", workActive: false, isReadOnly: true);
        var editable = SourceContextMenuPolicy.Evaluate("张三", workActive: false, isReadOnly: false);

        Assert.Equal(editable, readOnly);
        Assert.True(readOnly.AddCharacter);
        Assert.True(readOnly.AddGlossary);
        Assert.True(readOnly.TranslateSelection);
        Assert.True(readOnly.Copy);
    }

    [Fact]
    public void QuickCharacter_CreatesConfirmedProfile_AndExistingNameIsFoundInsteadOfDuplicated()
    {
        var project = new CharacterProject();
        var created = SourceContextActions.CreateQuickCharacter("张三", "Trương Tam", CharacterGender.Male, "", "");
        project.Profiles.Add(created);
        project.Validate();

        var existing = SourceContextActions.FindCharacter(project, "张三");

        Assert.Same(created, existing);
        Assert.Equal("hắn", created.PrimaryNarrativePronoun);
        Assert.Single(project.Profiles);
    }

    [Theory]
    [InlineData("")]
    [InlineData("không có chữ Hán")]
    [InlineData("张三走进房间，说了一句话。")]
    public void InvalidCharacterSelection_IsRejectedWithoutMutatingProject(string selected)
    {
        var project = new CharacterProject();

        Assert.Throws<ArgumentException>(() => SourceContextActions.ValidateCharacterName(selected));
        Assert.Empty(project.Profiles);
    }

    [Fact]
    public void FindOccurrences_ReturnsEveryStableSourcePosition()
    {
        Assert.Equal([0, 7, 10], SourceContextActions.FindOccurrences("张三见到李四。张三说张三好。", "张三"));
    }

    [Fact]
    public async Task SavingGlossary_DoesNotModifyProtectedUserTranslation()
    {
        var chapter = TranslationService.CreateChapter("Chương", "张三走了。");
        var segment = chapter.Segments.Single();
        segment.UserDraft = "Trương Tam đã rời đi.";
        segment.Status = SegmentStatus.AwaitingReview;
        var service = new TranslationService(chapter, new FakeClient(), new MemoryStore());

        await service.ConfigureAsync(chapter.DefaultModelOptions, chapter.SelectedPreset, [new("张三", "Trương Tam")], "");

        var saved = service.GetSnapshot().Segments.Single();
        Assert.Equal("Trương Tam đã rời đi.", saved.UserDraft);
        Assert.Equal(SegmentStatus.AwaitingReview, saved.Status);
        Assert.Single(service.GetSnapshot().ConfirmedTerms);
    }

    [Fact]
    public void TranslationSelection_AllowsExcerptButCharacterRuleRejectsIt()
    {
        string excerpt = string.Concat(Enumerable.Repeat("张三走进房间。", 20));

        Assert.Equal(excerpt, SourceContextActions.ValidateTranslationSelection(excerpt));
        Assert.Throws<ArgumentException>(() => SourceContextActions.ValidateCharacterName(excerpt));
    }
}
