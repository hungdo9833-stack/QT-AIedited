using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;
using QuickTranslator.AiEditor.Wpf;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class SemanticSurveyUiTests
{
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static SemanticSurveySelection Selection() => new(OllamaBackendProfile.CreateDefaultLocal(), new() { Model = "mock-only", ContextSize = 32768, MaximumOutputTokens = 4096 }, false);
    private sealed class Fake(Func<string, string> response) : IJsonSchemaOllamaClient
    {
        public int Calls { get; private set; }
        public Task<OllamaCompletion> TranslateAsync(string p, ModelOptions o, CancellationToken ct = default) => TranslateStructuredAsync(p, o, default, ct);
        public Task<OllamaCompletion> TranslateStructuredAsync(string p, ModelOptions o, JsonElement s, CancellationToken ct = default)
        { Calls++; return Task.FromResult(new OllamaCompletion(response(p), true, "stop", new() { InputTokens = 100, OutputTokens = 40 })); }
    }

    [Fact]
    public Task Semantic_tab_dry_run_is_offline_review_cancel_confirm_preview_undo_and_reopen_are_safe() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture(); await fixture.CreateProjectAsync(); await fixture.AddChapterAsync("Synthetic", 1);
        CorpusEvidence? opened = null; var window = new EntityMemoryReviewWindow(fixture.Project, fixture.Repository, e => { opened = e; return Task.CompletedTask; }, Selection());
        var panel = window.SemanticPanel; panel.ConfirmAction = _ => true; int factories = 0;
        try
        {
            Assert.Equal(2, ((TabControl)window.Content).Items.Count); await panel.LoadAsync(); await panel.DryRunAsync(); Assert.Null(panel.LastError);
            var plan = Field<SemanticDryRun>(panel, "_plan"); var r = Assert.Single(plan.Requests); var source = (await EntityMemoryProjectInputs.LoadSourcesAsync(fixture.Project, fixture.Repository)).Single();
            var fake = new Fake(_ => JsonSerializer.Serialize(new SemanticResponse(1, r.ProjectId, r.Evidence.ChapterId, [r.Evidence.Id], [new SemanticFinding {
                FindingType = SemanticFindingType.CharacterIdentification, SubjectIdentity = new(null, "p", "王秀兰", MemoryEntityKind.Character, SemanticResolution.NewIdentity),
                IdentityResolutionStatus = SemanticResolution.NewIdentity, Value = "王秀兰", SourceEvidenceIds = [r.Evidence.Id], SupportSpans = [new(r.Evidence.Id, source.OriginalText.IndexOf("王秀兰", StringComparison.Ordinal), 3, "王秀兰")], Confidence = .8, Reason = "Mock, not provider", EffectiveChapter = 1, RevealedChapter = 1 }]), SemanticSurveyContract.JsonOptions));
            panel.BackendFactory = (_, _) => { factories++; return fake; }; Assert.Equal(0, factories);
            panel.ConfirmAction = _ => false; await panel.SurveyAsync(); Assert.Equal(0, factories);
            panel.ConfirmAction = _ => true; await panel.SurveyAsync(); Assert.Null(panel.LastError); Assert.Equal(1, fake.Calls); Assert.Single(panel.State.Proposals);
            var grid = Field<DataGrid>(panel, "_grid"); grid.SelectedIndex = 0; long revision = panel.State.Revision;
            panel.EditPromotion = (_, _) => null; await panel.ReviewAsync(MemoryReviewStatus.Confirmed); Assert.Equal(revision, panel.State.Revision);
            panel.EditPromotion = (_, _) => new(new(1, null, 1, 1), "Human checks mock source", new Dictionary<string, Guid>());
            await panel.ReviewAsync(MemoryReviewStatus.Accepted); Assert.Null(panel.LastError); Assert.Empty((await new EntityMemoryStore(fixture.Project).LoadAsync()).Facts);
            grid.SelectedIndex = 0; await panel.OpenAsync(); Assert.Null(panel.LastError); Assert.Equal("王秀兰", opened?.Excerpt);
            grid.SelectedIndex = 0; await panel.ReviewAsync(MemoryReviewStatus.Confirmed); Assert.Null(panel.LastError);
            Field<TextBox>(panel, "_chapter").Text = "1"; await panel.PreviewAsync(); Assert.Null(panel.LastError);
            Assert.Contains("PREVIEW ONLY", Field<TextBox>(panel, "_details").Text); Assert.Contains("王秀兰", JsonSerializer.Serialize(new EntityMemoryQuery(await new EntityMemoryStore(fixture.Project).LoadAsync(), [source]).AtChapter(1), new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            grid.SelectedIndex = -1; grid.SelectedIndex = 0; await panel.UndoAsync(); Assert.Null(panel.LastError);
            Assert.Empty(new EntityMemoryQuery(await new EntityMemoryStore(fixture.Project).LoadAsync(), [source]).AtChapter(1).Entities);
            Assert.False((await new ProjectSettingsStore(fixture.Project).LoadTranslationAsync()).BookKnowledgeContextEnabled); Assert.Equal(0, fixture.Client.Calls);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Changing_budget_invalidates_dry_run_and_does_not_instantiate_backend() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture(); await fixture.CreateProjectAsync(); await fixture.AddChapterAsync("Mock", 1);
        var panel = new SemanticSurveyPanel(fixture.Project, fixture.Repository, _ => Task.CompletedTask, Selection());
        panel.BackendFactory = (_, _) => throw new InvalidOperationException("Never instantiate from a dry-run");
        await panel.DryRunAsync(); Assert.Null(panel.LastError); Assert.NotNull(Field<object?>(panel, "_plan"));
        Field<TextBox>(panel, "_requests").Text = "2"; Assert.Null(Field<object?>(panel, "_plan"));
        await panel.SurveyAsync(); Assert.Null(panel.LastError); Assert.Empty(panel.State.Attempts);
    });
}
