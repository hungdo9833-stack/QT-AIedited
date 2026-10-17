using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ReviewSampleTests
{
    [Fact]
    public async Task SyntheticNovelRetainsDraftApprovalHistoryAndResourceRevisionAfterReopen()
    {
        string root = Path.Combine(Path.GetTempPath(), "qtaie-review-sample-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new ProjectRepository();
            var project = await repository.CreateAsync(root, "Mẫu nghiệm thu tổng hợp — không phải truyện thật");
            var one = TranslationService.CreateChapter("Chương 1 — kiểm tra bản nháp", "王秀兰来了。你好。");
            var two = TranslationService.CreateChapter("Chương 2 — chưa dịch", "王秀兰说话了。");
            await repository.AddChapterAsync(project, one, 1);
            await repository.AddChapterAsync(project, two, 2);
            var resources = new ProjectResourceStore(project);
            var edit = await resources.LoadAsync();
            edit.Characters.Profiles = [new() { ChineseName = "王秀兰", VietnameseName = "Vương Tú Lan", NormalizationEnabled = false }];
            edit.Context = "Dữ liệu tổng hợp cho kiểm thử. Không gửi Ollama thật.";
            var current = await resources.SaveAsync(edit);
            var registered = (await repository.LoadChapterAsync(project, one.Id)).Chapter;
            var service = new TranslationService(registered,
                new FakeClient { Respond = (_, _) => Task.FromResult(FakeClient.Completion("Vương Tú Lan đã đến. Xin chào.")) }, new RegisteredChapterStore(project));
            service.UseProjectResources(current);
            await service.TranslateAsync(registered.Segments[0].Id);
            service.UpdateDraft(registered.Segments[0].Id, "[NHÁP TỔNG HỢP] Vương Tú Lan đã đến. Xin chào.");
            await service.ApproveAsync(registered.Segments[0].Id);
            await service.RenameAsync("Chương 1 — đã đổi tên và duyệt mẫu", "第一章");
            await repository.UpdateChapterSummaryAsync(project, service.GetSnapshot());
            var reopened = await repository.OpenAsync(project.ManifestPath);
            var chapter = (await repository.LoadChapterAsync(reopened.Session, one.Id)).Chapter;
            Assert.Single(chapter.Segments[0].Approvals);
            Assert.Single(chapter.Segments[0].Attempts);
            Assert.NotEmpty(chapter.Segments[0].DraftHistory);
            Assert.Equal(current.Revision, chapter.Segments[0].Attempts[0].ProjectResourceRevision);
            Assert.StartsWith("[NHÁP TỔNG HỢP]", chapter.Segments[0].UserDraft);
            Assert.Equal(2, reopened.Session.Manifest.Chapters.Count);
            // Optional review artifact is copied only to an explicitly supplied fresh destination.
            string? destination = Environment.GetEnvironmentVariable("QTAIE_REVIEW_SAMPLE");
            if (!string.IsNullOrWhiteSpace(destination))
            {
                destination = Path.GetFullPath(destination);
                if (Directory.Exists(destination)) throw new IOException("Sample destination already exists; refusing overwrite.");
                Directory.CreateDirectory(destination);
                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    string target = Path.Combine(destination, Path.GetRelativePath(root, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: false);
                }
            }
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("qtaie-review-sample-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid test root.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
