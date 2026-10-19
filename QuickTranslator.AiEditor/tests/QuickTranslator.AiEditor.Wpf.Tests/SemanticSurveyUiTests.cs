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

    [Theory]
    [InlineData(401, "Http401", "API key")]
    [InlineData(503, "Http503", "dịch vụ")]
    [InlineData(0, "NetworkFailure", "mạng")]
    public Task Failed_batch_diagnostic_is_visible_without_proposals_survives_reload_and_has_safe_manual_resume(int http, string code, string hint) => StaHost.Run(async () =>
    {
        const string secret = "DO_NOT_DISPLAY_PRIVATE_KEY_OR_SOURCE";
        using var fixture = new WorkspaceFixture(); await fixture.CreateProjectAsync(); await fixture.AddChapterAsync("Mock diagnostics", 1);
        var panel = new SemanticSurveyPanel(fixture.Project, fixture.Repository, _ => Task.CompletedTask, Selection());
        panel.ConfirmAction = _ => true; Field<TextBox>(panel, "_requests").Text = "1";
        var failed = new Fake(_ => http == 0 ? throw new System.Net.Http.HttpRequestException(secret) : throw new GeminiSurveyHttpException((System.Net.HttpStatusCode)http));
        panel.BackendFactory = (_, _) => failed; await panel.DryRunAsync(); await panel.SurveyAsync();
        Assert.Equal(1, failed.Calls); Assert.Empty(panel.State.Proposals); Assert.Equal(code, panel.State.Attempts.Last().ErrorCode);
        await panel.LoadAsync(); string summary = Field<TextBlock>(panel, "_summary").Text, details = Field<TextBox>(panel, "_details").Text;
        Assert.Contains(code, summary); Assert.Contains(hint, summary); Assert.Contains(code, details); Assert.DoesNotContain(secret, summary + details);
        Assert.Equal(http != 401, SemanticProviderDiagnostics.Describe(code).CanRetry);
        await panel.DryRunAsync(); var plan = Field<SemanticDryRun>(panel, "_plan"); var r = Assert.Single(plan.ExecutionRequests);
        var good = new Fake(_ => JsonSerializer.Serialize(new SemanticResponse(r.WireVersion, r.ProjectId, r.Evidence.ChapterId, r.Units.Select(x => x.Evidence.Id).ToArray(), []), SemanticSurveyContract.JsonOptions));
        panel.BackendFactory = (_, _) => good; await panel.SurveyAsync(); Assert.Equal(1, good.Calls);
        Assert.Equal(SemanticBatchStatus.Validated, panel.State.Status(r.Id)); Assert.Contains(panel.State.Attempts, x => x.ErrorCode == code);
        Assert.DoesNotContain(code, Field<TextBlock>(panel, "_summary").Text); Assert.Equal(0, fixture.Client.Calls);
    });

    [Fact]
    public Task Adaptive_dry_run_shows_evidence_distribution_and_cost_units_without_constructing_backend() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture(); await fixture.CreateProjectAsync();
        var chapter = TranslationService.CreateChapter("Synthetic multi-evidence", string.Concat(Enumerable.Range(0, 8).Select(_ => "林青来了。" + new string('云', 895))));
        await fixture.Repository.AddChapterAsync(fixture.Project, chapter, 1);
        var panel = new SemanticSurveyPanel(fixture.Project, fixture.Repository, _ => Task.CompletedTask, Selection());
        panel.BackendFactory = (_, _) => throw new InvalidOperationException("No API from preview");
        await panel.DryRunAsync(); Assert.Null(panel.LastError); var plan = Field<SemanticDryRun>(panel, "_plan");
        Assert.Equal(8, plan.TotalEvidence); Assert.Equal(2, plan.PendingRequests); Assert.Equal(2, plan.EvidencePerBatch[4]);
        string summary = Field<TextBlock>(panel, "_summary").Text;
        Assert.Contains("8 evidence", summary); Assert.Contains("Evidence/batch", summary); Assert.Contains("vượt context=0", summary); Assert.Contains("KHÔNG phải token thực tế", summary);
        Field<ComboBox>(panel, "_batchSize").SelectedItem = 6; Assert.Null(Field<object?>(panel, "_plan"));
        await panel.DryRunAsync(); Assert.Null(panel.LastError); Assert.Equal(new[] { 6, 2 }, Field<SemanticDryRun>(panel, "_plan").Requests.Select(x => x.Units.Count));
        Assert.Empty(panel.State.Attempts); Assert.Equal(0, fixture.Client.Calls);
    });

    [Fact]
    public Task Partial_multi_evidence_response_resumes_missing_only_and_opens_confirms_the_correct_later_evidence() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture(); await fixture.CreateProjectAsync(); string[] names = ["王秀兰", "陈海", "周宁", "林青"];
        var chapter = TranslationService.CreateChapter("Synthetic", string.Concat(names.Select(n => n + "来了。" + new string('云', 900 - n.Length - 3))));
        await fixture.Repository.AddChapterAsync(fixture.Project, chapter, 1);
        CorpusEvidence? opened = null; var panel = new SemanticSurveyPanel(fixture.Project, fixture.Repository, e => { opened = e; return Task.CompletedTask; }, Selection());
        panel.ConfirmAction = _ => true; await panel.DryRunAsync(); Assert.Null(panel.LastError);
        var units = Field<SemanticDryRun>(panel, "_plan").Requests.SelectMany(x => x.Units).ToDictionary(x => x.Evidence.Id); int responses = 0;
        var fake = new Fake(p =>
        {
            var ids = System.Text.RegularExpressions.Regex.Matches(p, @"(?m)^EVIDENCE project=.*? id=([a-fA-F0-9-]{36}) start_utf16=").Select(m => Guid.Parse(m.Groups[1].Value)).ToArray();
            if (responses++ == 0) ids = ids.Take(3).ToArray();
            var findings = ids.Select(id =>
            {
                var u = units[id]; string name = names[u.Evidence.StartUtf16 / 900];
                return new SemanticFinding { FindingType = SemanticFindingType.CharacterIdentification, IdentityResolutionStatus = SemanticResolution.NewIdentity,
                    SubjectIdentity = new(null, "p_" + id.ToString("N"), name, MemoryEntityKind.Character, SemanticResolution.NewIdentity), Value = name,
                    SourceEvidenceIds = [id], SupportSpans = [new(id, u.Evidence.StartUtf16, name.Length, name)], Reason = "Mock exact source", EffectiveChapter = 1, RevealedChapter = 1 };
            }).ToArray();
            return JsonSerializer.Serialize(new SemanticResponse(p.Contains("MULTI-EVIDENCE", StringComparison.Ordinal) ? 2 : 1, fixture.Project.Manifest.ProjectId, chapter.Id, ids, findings), SemanticSurveyContract.JsonOptions);
        });
        panel.BackendFactory = (_, _) => fake;
        await panel.SurveyAsync(); Assert.Null(panel.LastError); Assert.Equal(3, panel.State.Proposals.Count);
        await panel.DryRunAsync(); Assert.Null(panel.LastError); var resume = Field<SemanticDryRun>(panel, "_plan");
        Assert.Equal(3, resume.CompletedEvidence); Assert.Equal(1, resume.PendingEvidence); Assert.Equal(1, resume.PendingRequests);
        await panel.SurveyAsync(); Assert.Null(panel.LastError); Assert.Equal(2, fake.Calls); Assert.Equal(4, panel.State.Proposals.Count);
        var grid = Field<DataGrid>(panel, "_grid"); grid.SelectedIndex = 3;
        await panel.OpenAsync(); Assert.Null(panel.LastError); Assert.Equal("林青", opened!.Excerpt); Assert.Equal(2700, opened.StartUtf16);
        grid.SelectedIndex = 3; panel.EditPromotion = (_, _) => new(new(1, null, 1, 1), "Human checked later evidence", new Dictionary<string, Guid>());
        await panel.ReviewAsync(MemoryReviewStatus.Confirmed); Assert.Null(panel.LastError);
        var memory = await new EntityMemoryStore(fixture.Project).LoadAsync(); Assert.Equal(2700, Assert.Single(memory.Evidence).StartUtf16);
        await panel.LoadAsync(); Assert.Equal(MemoryReviewStatus.Confirmed, panel.State.Reviews.Last().Status);
        Assert.False((await new ProjectSettingsStore(fixture.Project).LoadTranslationAsync()).BookKnowledgeContextEnabled); Assert.Equal(0, fixture.Client.Calls);
    });
}
