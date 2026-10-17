using System.Text.Json;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class AuditPhase1ReliabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qt-audit-phase1-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task F01_ReopenSynchronizesManifestSummaryAfterChapterWriteSucceededButManifestWriteFailed()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "project"), "Synthetic");
        ChapterDocument chapter = TranslationService.CreateChapter("Tên cũ", "原文。");
        await repository.AddChapterAsync(session, chapter, 1, "旧标题");
        chapter = (await repository.LoadChapterAsync(session, chapter.Id)).Chapter;
        chapter.Title = "Tên mới đã lưu";
        chapter.ChineseTitle = "新标题";
        chapter.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1);
        repository.FaultPoint = point =>
        {
            if (point == "AfterFastChapterWrite") throw new IOException("synthetic manifest failure");
        };

        await Assert.ThrowsAsync<IOException>(() => repository.UpdateChapterAsync(session, chapter));
        repository.FaultPoint = null;

        ProjectSession reopened = (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session;
        ProjectChapterEntry entry = Assert.Single(reopened.Manifest.Chapters);
        Assert.Equal("Tên mới đã lưu", entry.VietnameseTitle);
        Assert.Equal("新标题", entry.ChineseTitle);
        Assert.Equal(chapter.UpdatedAt, entry.UpdatedAt);
        ProjectManifest persisted = await ProjectRepository.ReadManifestAsync(session.ManifestPath, CancellationToken.None);
        Assert.Equal("Tên mới đã lưu", Assert.Single(persisted.Chapters).VietnameseTitle);
    }

    [Fact]
    public async Task F02_ConcurrentCanonicalPathWritesAlwaysLeaveOneCompleteValidDocument()
    {
        string direct = Path.Combine(_root, "settings.json");
        string alias = Path.Combine(_root, ".", "settings.json");
        Task[] writes = Enumerable.Range(0, 100).Select(index => SimpleFileStorage.WriteAsync(
            index % 2 == 0 ? direct : alias,
            JsonSerializer.SerializeToUtf8Bytes(new ConcurrentValue(index, new string((char)('a' + index % 26), 2048))),
            Decode)).ToArray();

        await Task.WhenAll(writes);

        ConcurrentValue saved = await SimpleFileStorage.ReadRequiredAsync(direct, Decode);
        Assert.InRange(saved.Sequence, 0, 99);
        Assert.Equal(2048, saved.Payload.Length);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    private static ConcurrentValue Decode(byte[] bytes) =>
        JsonSerializer.Deserialize<ConcurrentValue>(bytes) ?? throw new InvalidDataException("empty");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record ConcurrentValue(int Sequence, string Payload);
}
