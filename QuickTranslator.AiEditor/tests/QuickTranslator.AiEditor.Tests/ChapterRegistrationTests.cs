using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ChapterRegistrationTests
{
    [Fact]
    public async Task F03_FailedManifestWriteDoesNotLeaveRegisteredEntryWithoutChapterFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "qtaie-registration-tests-" + Guid.NewGuid().ToString("N"));
        var repository = new ProjectRepository();
        try
        {
            var project = await repository.CreateAsync(root, "Dữ liệu thử");
            var chapter = TranslationService.CreateChapter("Một", "你好。");
            // Fail at the manifest destination without changing access rights or any real project.
            using (var locked = new FileStream(project.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await Assert.ThrowsAnyAsync<IOException>(() => repository.AddChapterAsync(project, chapter, 1));
            }
            Assert.Empty(project.Manifest.Chapters);
            Assert.Null(project.Manifest.CurrentChapterId);
            Assert.Empty(project.ChapterStore.EnumerateChapterPaths());
            Assert.Empty((await repository.OpenAsync(project.ManifestPath)).Session.Manifest.Chapters);
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid test root.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
