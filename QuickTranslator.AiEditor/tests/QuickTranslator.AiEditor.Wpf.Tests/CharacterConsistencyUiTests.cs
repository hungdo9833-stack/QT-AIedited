using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class CharacterConsistencyUiTests
{
    [Fact]
    public Task Character_panel_exposes_project_lock_and_quick_edit_columns() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        var project = new CharacterProject
        {
            Name = "Synthetic", ConsistencyLockEnabled = true,
            Profiles = [new CharacterProfile
            {
                ChineseName = "林风", VietnameseName = "Lâm Phong", PrimaryNarrativePronoun = "hắn",
                SecondaryNarrativePronoun = "hắn ta", NarrativePronounLocked = true,
                DisallowedNarrativePronouns = ["anh ấy"]
            }]
        };
        fixture.Call("LoadCharacterProject", project);

        Assert.True(((CheckBox)fixture.Window.FindName("ConsistencyLockCheck")).IsChecked);
        var grid = (DataGrid)fixture.Window.FindName("CharactersGrid");
        Assert.False(grid.IsReadOnly);
        Assert.Contains(grid.Columns, column => Equals(column.Header, "Đại từ chính") && !column.IsReadOnly);
        Assert.Contains(grid.Columns, column => Equals(column.Header, "Khóa") && !column.IsReadOnly);
        Assert.Contains(grid.Columns, column => Equals(column.Header, "Quan hệ & xưng hô") && column.IsReadOnly);
        Assert.Single(grid.Items);
        return Task.CompletedTask;
    });

    [Fact]
    public Task BL07_Confirmed_reverse_relationship_sync_respects_independence_and_locked_rules() => StaHost.Run(() =>
    {
        var lam = new CharacterProfile
        {
            ChineseName = "林哲", VietnameseName = "Lâm Triết", PrimaryNarrativePronoun = "anh",
            NormalizationEnabled = false
        };
        var to = new CharacterProfile
        {
            ChineseName = "苏雨", VietnameseName = "Tô Vũ", PrimaryNarrativePronoun = "cô",
            NormalizationEnabled = false
        };
        var project = new CharacterProject
        {
            Name = "ReverseSyncTest",
            ConsistencyLockEnabled = true,
            Profiles = [lam, to]
        };

        // 1. Confirmed reverse rule creates B -> A on target listener
        bool applied = CharacterRelationshipSync.TryApplyConfirmedReverseRule(
            project, lam.Id, to.Id, "Vợ - Chồng", "em", "anh", enabled: true, locked: true, "Đã xác nhận", out string? warning);
        Assert.True(applied);
        Assert.Null(warning);
        Assert.Single(to.Relationships);
        Assert.Equal("em", to.Relationships[0].SelfReference);
        Assert.Equal("anh", to.Relationships[0].AddresseeReference);
        Assert.True(to.Relationships[0].Locked);

        // 2. Adjustment A: Attempting to overwrite a Locked reverse rule on target character is rejected safely
        bool overwriteLocked = CharacterRelationshipSync.TryApplyConfirmedReverseRule(
            project, lam.Id, to.Id, "Bạn bè", "tôi", "cậu", enabled: true, locked: false, "", out string? lockedWarning);
        Assert.False(overwriteLocked);
        Assert.NotNull(lockedWarning);
        Assert.Equal("em", to.Relationships[0].SelfReference);
        Assert.Equal("anh", to.Relationships[0].AddresseeReference);
        return Task.CompletedTask;
    });

    [Fact]
    public Task BL08_Inspector_displays_only_CharacterConsistencyRuleSelector_selected_rules_when_lock_enabled() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Call("InitializeChapterView");
        await fixture.CreateProjectAsync();

        var lam = new CharacterProfile
        {
            ChineseName = "林风", VietnameseName = "Lâm Phong", PrimaryNarrativePronoun = "hắn",
            NarrativePronounLocked = true, NormalizationEnabled = false
        };
        var to = new CharacterProfile
        {
            ChineseName = "苏婉", VietnameseName = "Tô Uyển", PrimaryNarrativePronoun = "nàng",
            NarrativePronounLocked = true, NormalizationEnabled = false
        };
        var outsider = new CharacterProfile
        {
            ChineseName = "陈老", VietnameseName = "Trần Lão", PrimaryNarrativePronoun = "lão",
            NarrativePronounLocked = true, NormalizationEnabled = false
        };
        lam.Relationships.Add(new CharacterRelationship
        {
            TargetCharacterId = to.Id, SpeakerCharacterId = lam.Id, ListenerCharacterId = to.Id,
            Relationship = " Sư huynh - Sư muội", SelfReference = "huynh", AddresseeReference = "muội",
            Enabled = true, Locked = true
        });
        fixture.Call("LoadCharacterProject", new CharacterProject
        {
            Id = fixture.Project.Manifest.ProjectId, Name = "CC-Inspector",
            ConsistencyLockEnabled = true, Profiles = [lam, to, outsider]
        });
        await fixture.TaskCall("SaveProjectResourcesAsync");

        var chapter = TranslationService.CreateChapter("Thử Inspector", "林风问苏婉：“你回来了吗？”");
        await fixture.Repository.AddChapterAsync(fixture.Project, chapter, 1);
        chapter = (await fixture.Repository.LoadChapterAsync(fixture.Project, chapter.Id)).Chapter;
        fixture.Attach(chapter);
        fixture.Call("Refresh");

        var items = ((ItemsControl)fixture.Window.FindName("InspectorCharacterItems")).Items.Cast<string>().ToArray();
        Assert.Contains(items, line => line.Contains("CHARACTER 林风 = Lâm Phong"));
        Assert.Contains(items, line => line.Contains("DIALOGUE Lâm Phong -> Tô Uyển: self = huynh; address = muội"));
        Assert.DoesNotContain(items, line => line.Contains("Trần Lão"));
    });
}
