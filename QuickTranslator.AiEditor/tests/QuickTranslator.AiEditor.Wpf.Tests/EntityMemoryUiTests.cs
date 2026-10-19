using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;
using QuickTranslator.AiEditor.Wpf;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class EntityMemoryUiTests
{
    private static T Field<T>(object window, string name) => (T)window.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static MemoryReviewRow Select(EntityMemoryReviewWindow window, Func<MemoryReviewRow, bool> predicate)
    {
        var grid = Field<DataGrid>(window, "_rows"); var row = grid.Items.Cast<MemoryReviewRow>().First(predicate); grid.SelectedItem = row; return row;
    }
    private static Dictionary<string, byte[]> Legacy(ProjectSession session) => Directory.GetFiles(session.RootDirectory, "*", SearchOption.AllDirectories)
        .Where(x => !x.Contains("abi3-entity-event-memory.v1.json", StringComparison.Ordinal)).ToDictionary(x => x, File.ReadAllBytes);
    private static async Task SeedKnownCharacter(WorkspaceFixture fixture)
    {
        var store = new ProjectResourceStore(fixture.Project); var resources = await store.LoadAsync();
        var profile = new CharacterProfile { ChineseName = "王秀兰", VietnameseName = "Vương Tú Lan", Gender = CharacterGender.Female, NarrativePronounLocked = true };
        profile.ApplyGenderDefaults(); resources.Characters.Profiles.Add(profile);
        await store.SaveAsync(resources);
    }

    [Fact]
    public Task Memory_form_opens_filters_reviews_rolls_back_and_reopens_without_api_or_legacy_writes() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture(); await fixture.CreateProjectAsync(); await fixture.AddChapterAsync("Một", 1);
        await SeedKnownCharacter(fixture);
        var legacy = Legacy(fixture.Project); CorpusEvidence? opened = null;
        var window = new EntityMemoryReviewWindow(fixture.Project, fixture.Repository, e => { opened = e; return Task.CompletedTask; }) { ConfirmAction = _ => true };
        try
        {
            window.Show(); await window.ReloadAsync(); await window.ScanAsync(); Assert.Null(window.LastError); Assert.True(window.IsVisible);
            var row = Select(window, x => !x.IsEvent && x.Title.Contains("王秀兰", StringComparison.Ordinal));
            Guid id = row.Id;
            Assert.Equal(MemoryReviewStatus.Accepted, row.Status); Assert.True(Field<Button>(window, "_open").IsEnabled);
            await window.OpenEvidenceAsync(); Assert.NotNull(opened); Assert.Equal("王秀兰", opened.Excerpt);
            var sources = await EntityMemoryProjectInputs.LoadSourcesAsync(fixture.Project, fixture.Repository);
            Assert.Equal(CorpusIntegrityAuditor.SourceHash(sources.Single().OriginalText), opened.SourceSha256);

            long revision = window.Memory!.Revision;
            Select(window, x => x.Id == id); window.EditReview = (_, _, _) => null;
            await window.ReviewSelectedAsync(MemoryReviewStatus.Confirmed); Assert.Equal(revision, window.Memory!.Revision);
            window.EditReview = (r, _, _) => new(new(1, null, 1, 1), "Checked synthetic source and chronology", default, MemoryEventEffect.None, r.EntityIds, []);
            await window.ReviewSelectedAsync(MemoryReviewStatus.Accepted); Assert.Null(window.LastError);
            Assert.Equal(MemoryReviewStatus.Accepted, window.Query!.Status(id)); Assert.Empty(window.Query.AtChapter(1).Entities);
            Select(window, x => x.Id == id); await window.ReviewSelectedAsync(MemoryReviewStatus.Confirmed); Assert.Null(window.LastError);
            Assert.Equal(MemoryReviewStatus.Confirmed, window.Query!.Status(id)); Assert.NotEmpty(window.Query.AtChapter(1).Entities);

            var status = Field<ComboBox>(window, "_status"); status.SelectedItem = "Confirmed";
            Assert.Single(Field<DataGrid>(window, "_rows").Items.Cast<MemoryReviewRow>());
            Field<TextBox>(window, "_chapter").Text = "0"; Assert.Empty(Field<DataGrid>(window, "_rows").Items);
            Field<TextBox>(window, "_chapter").Text = "1"; Select(window, x => x.Id == id);
            await window.ReviewSelectedAsync(MemoryReviewStatus.Rejected); Assert.Null(window.LastError);
            status.SelectedItem = "Rejected"; Select(window, x => x.Id == id); await window.UndoAsync(); Assert.Null(window.LastError);
            var reopened = await new EntityMemoryStore(fixture.Project).LoadAsync();
            Assert.Equal(4, reopened.Reviews.Count); Assert.Equal(MemoryReviewStatus.Proposed, reopened.Reviews.Last().Status);
            Assert.NotNull(reopened.Reviews.Last().RevertsDecisionId);
            foreach (var pair in legacy) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
            Assert.False((await new ProjectSettingsStore(fixture.Project).LoadTranslationAsync()).BookKnowledgeContextEnabled);
            Assert.Equal(0, fixture.Client.Calls);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Source_edit_blocks_navigation_and_confirmation_without_erasing_human_history() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture(); await fixture.CreateProjectAsync(); var chapter = await fixture.AddChapterAsync("Một", 1);
        await SeedKnownCharacter(fixture);
        int navigations = 0;
        var window = new EntityMemoryReviewWindow(fixture.Project, fixture.Repository, _ => { navigations++; return Task.CompletedTask; });
        try
        {
            await window.ReloadAsync(); await window.ScanAsync(); var row = Select(window, x => !x.IsEvent);
            // The source can change while the review window is still open. Navigation must re-read it.
            var edit = SourceEditPlanner.Preview(chapter, "原文已修改。");
            await fixture.Repository.UpdateChapterAsync(fixture.Project, SourceEditPlanner.Apply(chapter, edit, true, DateTimeOffset.UtcNow));
            await window.OpenEvidenceAsync(); Assert.NotNull(window.LastError); Assert.Equal(0, navigations);
            Select(window, x => x.Id == row.Id);
            Assert.Equal(MemoryReviewStatus.Stale, window.Query!.Status(row.Id)); Assert.False(Field<Button>(window, "_confirm").IsEnabled);
            Assert.False(Field<Button>(window, "_open").IsEnabled); Assert.Empty(window.Query.AtChapter(10).Entities);
            Assert.Equal(0, fixture.Client.Calls);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Review_dialog_requires_known_reveal_explicit_event_participants_and_separate_alias_choice() => StaHost.Run(() =>
    {
        Guid project = Guid.NewGuid(), entity = Guid.NewGuid(), eventId = Guid.NewGuid();
        var state = new EntityMemoryState { ProjectId = project, Entities = [new(entity, project, MemoryEntityKind.Character, "A")] };
        state.Events.Add(new() { Id = eventId, ProjectId = project, EntityIds = [entity], Type = TranslationImpactEventType.RevealIdentity,
            Provenance = new(MemoryOrigin.CpuTrigger, "cpu", "No inferred participants") });
        var row = new MemoryReviewRow(eventId, true, "RevealIdentity", "A", MemoryReviewStatus.Proposed, new(DiscoveredAtChapter: 1), [entity], [], "cpu");
        var dialog = new EntityMemoryReviewDialog(row, state, MemoryReviewStatus.Confirmed, false);
        try
        {
            dialog.Note.Text = "Human proof"; Assert.False(dialog.TryBuild(out _, out _));
            dialog.From.Text = dialog.Reveal.Text = "1";
            Assert.Empty(dialog.Participants.SelectedItems); Assert.False(dialog.TryBuild(out _, out _));
            dialog.Participants.SelectedIndex = 0; Assert.True(dialog.TryBuild(out var result, out _)); Assert.Single(result!.EntityIds);
            dialog.From.Text = "0"; Assert.False(dialog.TryBuild(out _, out _)); Assert.Null(dialog.Result);
        }
        finally { dialog.Close(); }
        var promote = new EntityMemoryReviewDialog(row with { IsEvent = false }, state, MemoryReviewStatus.Confirmed, true);
        try
        {
            promote.From.Text = promote.Reveal.Text = "1"; promote.Note.Text = "Explicit promotion";
            Assert.Equal(-1, promote.Promotion.SelectedIndex); Assert.False(promote.TryBuild(out _, out _));
            promote.Promotion.SelectedItem = MemoryFactKind.Alias; Assert.True(promote.TryBuild(out var result, out _));
            Assert.Equal(MemoryFactKind.Alias, result!.PromotionKind); Assert.Null(promote.Result); // parsing alone is not a save
        }
        finally { promote.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task Existing_survey_window_has_separate_memory_entry_without_enabling_translation_context() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture(); await fixture.CreateProjectAsync();
        var window = new BookIntelligenceReviewWindow(fixture.Project, fixture.Repository, await new ProjectResourceStore(fixture.Project).LoadAsync(),
            [], _ => null, _ => { }, _ => Task.CompletedTask, (_, _) => { });
        Assert.Contains("2A", Field<Button>(window, "_memory").Content.ToString()); window.Close();
        Assert.False((await new ProjectSettingsStore(fixture.Project).LoadTranslationAsync()).BookKnowledgeContextEnabled);
        Assert.Equal(0, fixture.Client.Calls);
    });

    [Fact]
    public Task Cpu_event_needs_explicit_refinement_and_is_filterable_by_identity_chapter_kind_and_status() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture(); await fixture.CreateProjectAsync(); var chapter = await fixture.AddChapterAsync("Một", 1);
        await SeedKnownCharacter(fixture);
        await fixture.Repository.UpdateChapterAsync(fixture.Project, SourceEditPlanner.Apply(chapter,
            SourceEditPlanner.Preview(chapter, "王秀兰说：原来是她。"), true, DateTimeOffset.UtcNow));
        var window = new EntityMemoryReviewWindow(fixture.Project, fixture.Repository, _ => Task.CompletedTask) { ConfirmAction = _ => true };
        try
        {
            await window.ReloadAsync(); await window.ScanAsync(); Assert.Null(window.LastError);
            var original = Select(window, x => x.IsEvent);
            Guid character = window.Memory!.Entities.Single(x => x.Kind == MemoryEntityKind.Character).Id;
            window.EditReview = (r, _, _) => new(new(1, null, 1, 1), "Human identified participant and reveal", TranslationImpactEventType.RevealIdentity, MemoryEventEffect.None, [character], []);
            await window.ReviewSelectedAsync(MemoryReviewStatus.Confirmed); Assert.Null(window.LastError);
            Assert.Equal(MemoryReviewStatus.Rejected, window.Query!.Status(original.Id)); Assert.Single(window.Query.AtChapter(1).Events);
            Field<ComboBox>(window, "_kind").SelectedIndex = 2; Field<ComboBox>(window, "_status").SelectedItem = "Confirmed";
            Field<TextBox>(window, "_chapter").Text = "1";
            var identities = Field<ComboBox>(window, "_character"); identities.SelectedItem = identities.Items.Cast<MemoryChoice>().Single(x => x.Id == character);
            Assert.Single(Field<DataGrid>(window, "_rows").Items.Cast<MemoryReviewRow>());
            Assert.Empty((await new BookIntelligenceStore(fixture.Project).LoadAsync()).RelationshipGraph);
            Assert.False((await new ProjectSettingsStore(fixture.Project).LoadTranslationAsync()).BookKnowledgeContextEnabled); Assert.Equal(0, fixture.Client.Calls);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Pause_button_cancels_cpu_and_resume_finishes_without_duplicate_candidates() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture(); await fixture.CreateProjectAsync(); await fixture.AddChapterAsync("Một", 1);
        var window = new EntityMemoryReviewWindow(fixture.Project, fixture.Repository, _ => Task.CompletedTask);
        try
        {
            await window.ReloadAsync(); Task scan = window.ScanAsync();
            var pause = Field<Button>(window, "_pause"); Assert.True(pause.IsEnabled); pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await scan; Assert.False(window.Busy); Assert.Null(window.LastError); Assert.False(pause.IsEnabled);
            await window.ScanAsync(); Assert.Null(window.LastError); Assert.Single(window.Memory!.Checkpoints);
            long revision = window.Memory.Revision; await window.ScanAsync(); Assert.Equal(revision, window.Memory!.Revision); Assert.Equal(0, fixture.Client.Calls);
        }
        finally { window.Close(); }
    });
}
