using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ProjectLibrarySummaryTests
{
    [Fact]
    public async Task Read_summary_is_read_only_and_uses_manifest_metadata_only()
    {
        string root = Path.Combine(Path.GetTempPath(), "qtaie-m5-library-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new ProjectRepository();
            ProjectSession session = await repository.CreateAsync(Path.Combine(root, "Novel"), "Truyện tổng hợp");
            var chapter = TranslationService.CreateChapter("Chương một", "你好。世界。", new() { MinimumHanCharacters = 1,
                TargetHanCharacters = 2, MaximumHanCharacters = 4, MaximumTextElements = 100 });
            await repository.AddChapterAsync(session, chapter, 1);
            string[] before = Directory.GetFileSystemEntries(session.RootDirectory, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();

            ProjectLibrarySummary summary = await ProjectRepository.ReadLibrarySummaryAsync(session.ManifestPath);

            Assert.Equal(session.Manifest.ProjectId, summary.ProjectId);
            Assert.Equal("Truyện tổng hợp", summary.VietnameseTitle);
            Assert.Equal(1, summary.ChapterCount);
            Assert.Equal(chapter.Segments.Count, summary.TotalSegmentCount);
            Assert.Equal("Chương một", summary.CurrentChapterTitle);
            Assert.Equal(before, Directory.GetFileSystemEntries(session.RootDirectory, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal));
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("qtaie-m5-library-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to clean outside synthetic M5 root.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
