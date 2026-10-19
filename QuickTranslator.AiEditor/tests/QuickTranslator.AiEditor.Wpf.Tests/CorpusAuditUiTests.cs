using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class CorpusAuditUiTests
{
    [Fact]
    public Task Four_cpu_audit_groups_navigate_exact_source_without_accepting_or_changing_project_data() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        for (int i = 1; i <= 4; i++)
            await fixture.Repository.AddChapterAsync(fixture.Project, TranslationService.CreateChapter("Chương " + i,
                i == 1 ? "𠀀沈夕颜说道：好。沈夕颜走来。沈夕颜点头。小锋的信。小锋了。小影笑了。秦老太爷不在。妈妈说：好。温柔的光。" : "林哲说道：第" + i + "章。"), i, "章节");
        var store = new BookIntelligenceStore(fixture.Project);
        var resources = await new ProjectResourceStore(fixture.Project).LoadAsync();
        var state = await new ProjectCorpusIndexer(fixture.Repository).IndexAsync(fixture.Project, await store.LoadAsync(), store, resources.Characters);
        state.Chapters[0].Evidence[0].AiAnalyzed = true;
        state.Chapters[1].Evidence[0].IsCurrent = false;
        state.Chapters[2].Evidence[0].AiBlocked = true;
        state.Chapters[2].Evidence[0].AiBlockReason = "SAFETY";
        state = await store.SaveAsync(state);
        byte[] surveyBefore = await System.IO.File.ReadAllBytesAsync(store.PathName);
        byte[] manifestBefore = await System.IO.File.ReadAllBytesAsync(fixture.Project.ManifestPath);
        CorpusEvidence? opened = null;
        int resourceWrites = 0;
        var window = new BookIntelligenceReviewWindow(fixture.Project, fixture.Repository, resources, [],
            _ => throw new InvalidOperationException("CPU hypotheses must not open an acceptance editor"),
            _ => resourceWrites++, evidence => { opened = evidence; return Task.CompletedTask; }, (_, _) => { });
        try
        {
            window.Show();
            for (int i = 0; i < 500 && (Get<bool>(window, "_loading") || Get<CorpusReviewAudit?>(window, "_corpusAudit") is null); i++)
                await Task.Delay(10);
            var audit = Assert.IsType<CorpusReviewAudit>(Get<CorpusReviewAudit?>(window, "_corpusAudit"));
            Assert.False(audit.Complete);
            Assert.Contains("NeedsReindex", Get<TextBlock>(window, "_auditSummary").Text);
            Assert.Contains("active 3/4", Get<TextBlock>(window, "_auditSummary").Text);
            Assert.False(Get<CheckBox>(window, "_translationContext").IsChecked);
            var groups = Get<ComboBox>(window, "_auditGroup");
            foreach (string name in new[] { "Missing identity", "Stale index", "Blocked excerpts", "Unverified", "Alias candidates", "Title candidates", "Addressing terms", "Noise / Uncertain" })
            {
                groups.SelectedItem = name;
                var queue = Get<DataGrid>(window, "_queue");
                Assert.NotEmpty(queue.Items.Cast<object>());
                queue.SelectedIndex = 0;
                foreach (string action in new[] { "_save", "_merge", "_later", "_reject" }) Assert.False(Get<Button>(window, action).IsEnabled);
                if (name == "Addressing terms")
                {
                    Assert.Contains(queue.Items.Cast<object>(), x => IdentityRow(x).Title.StartsWith("妈妈", StringComparison.Ordinal));
                    Assert.Equal(Visibility.Collapsed, Get<WrapPanel>(window, "_identityActions").Visibility);
                }
                if (name is "Missing identity" or "Stale index")
                {
                    await Invoke(window, "OpenSelectedEvidenceAsync");
                    Assert.NotNull(opened);
                    var chapter = (await fixture.Repository.LoadChapterAsync(fixture.Project, opened.ChapterId)).Chapter;
                    Assert.Equal(opened.Excerpt, chapter.OriginalText.Substring(opened.StartUtf16, opened.LengthUtf16));
                    Assert.Equal(CorpusIntegrityAuditor.SourceHash(chapter.OriginalText), opened.SourceSha256);
                }
            }
            groups.SelectedItem = "Missing identity";
            var identityQueue = Get<DataGrid>(window, "_queue");
            identityQueue.SelectedItem = identityQueue.Items.Cast<object>().Single(x => IdentityRow(x).MissingIdentityName == "沈夕颜");
            Assert.Equal(Visibility.Visible, Get<WrapPanel>(window, "_identityActions").Visibility);
            Assert.True(Get<Button>(window, "_identityNotCharacter").IsEnabled);
            Assert.True(Get<Button>(window, "_identityQueue").IsEnabled);
            Assert.False(Get<Button>(window, "_identityLink").IsEnabled); // no implicit target/link selection
            var identityStore = new MissingIdentityReviewStore(fixture.Project);
            var decided = await identityStore.DecideAsync(Get<CorpusReviewAudit>(window, "_corpusAudit").Census,
                "沈夕颜", MissingIdentityDecisionKind.NotCharacter, 0, resources.Revision);
            window.Close();
            window = new BookIntelligenceReviewWindow(fixture.Project, fixture.Repository, resources, [],
                _ => throw new InvalidOperationException("No automatic Character creation"),
                _ => resourceWrites++, _ => Task.CompletedTask, (_, _) => { });
            window.Show();
            for (int i = 0; i < 500 && (Get<bool>(window, "_loading") || Get<CorpusReviewAudit?>(window, "_corpusAudit") is null); i++)
                await Task.Delay(10);
            groups = Get<ComboBox>(window, "_auditGroup");
            groups.SelectedItem = "Identity đã xử lý";
            Assert.Contains(Get<DataGrid>(window, "_queue").Items.Cast<object>(), x => IdentityRow(x).MissingIdentityName == "沈夕颜");
            await identityStore.DecideAsync(Get<CorpusReviewAudit>(window, "_corpusAudit").Census,
                "沈夕颜", MissingIdentityDecisionKind.VerifyNewCharacter, decided.Revision, resources.Revision);
            await Invoke(window, "RefreshCorpusAuditAsync");
            groups.SelectedItem = "Unverified";
            Assert.Contains(Get<DataGrid>(window, "_queue").Items.Cast<object>(), x => IdentityRow(x).Title.Contains("Chờ xác minh nhân vật mới"));
            Assert.False(Get<CorpusReviewAudit>(window, "_corpusAudit").ManualReviewComplete);
            Assert.Contains("Accuracy: chưa được kiểm chứng", Get<TextBlock>(window, "_auditSummary").Text);
            Assert.DoesNotContain("/ Verified", Get<TextBlock>(window, "_auditSummary").Text);
            Assert.Equal(0, resourceWrites);
            Assert.Equal(surveyBefore, await System.IO.File.ReadAllBytesAsync(store.PathName));
            Assert.Equal(manifestBefore, await System.IO.File.ReadAllBytesAsync(fixture.Project.ManifestPath));
            Assert.Equal(0, fixture.Client.Calls);
        }
        finally { window.Close(); }
    });

    private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field,
        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static CorpusAuditReviewItem IdentityRow(object row) => (CorpusAuditReviewItem)row.GetType().GetProperty("Value")!.GetValue(row)!;
    private static Task Invoke(object target, string method) => (Task)target.GetType().GetMethod(method,
        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null)!;
}
