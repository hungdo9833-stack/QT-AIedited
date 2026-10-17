using System.Text.Json;
using System.Text.Json.Nodes;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Segmentation;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ChapterFileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "qtaie-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FullWorkflow_SavesAndReopensOriginalDraftHistoryPromptAndSelection()
    {
        var store = new ChapterFileStore(_directory);
        var chapter = TranslationService.CreateChapter("Chương giả lập", "  你好。\r\n「朋友！」\n\n再见。😀  ",
            new() { MinimumHanCharacters = 2, TargetHanCharacters = 3, MaximumHanCharacters = 4 });
        var client = new FakeClient();
        var service = new TranslationService(chapter, client, store);
        var id = chapter.Segments[0].Id;
        await service.TranslateAsync(id, new() { Temperature = 0.2, ContextSize = 8192 });
        service.UpdateDraft(id, "Bản chỉnh sửa của người dùng.\r\nGiữ xuống dòng.");
        await service.ApproveAsync(id);
        await service.TranslateAsync(id);
        service.SelectSegment(chapter.Segments[1].Id);
        await service.SaveAsync();
        var loaded = await store.LoadAsync(store.GetPath(chapter.Id));
        Assert.Equal(JsonSerializer.Serialize(service.GetSnapshot(), DocumentJson.Options),
            JsonSerializer.Serialize(loaded.Chapter, DocumentJson.Options));
        Assert.Equal(chapter.OriginalText, TextSegmenter.Reconstruct(loaded.Chapter.Segments));
        Assert.Equal("Bản chỉnh sửa của người dùng.\r\nGiữ xuống dòng.", loaded.Chapter.Segments[0].UserDraft);
        Assert.Equal(2, loaded.Chapter.Segments[0].Attempts.Count);
        Assert.Single(loaded.Chapter.Segments[0].Approvals);
        Assert.Equal(8192, loaded.Chapter.Segments[0].Attempts[0].ModelOptions.ContextSize);
        Assert.NotEmpty(loaded.Chapter.Segments[0].Attempts[0].Prompt);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
    }

    [Fact]
    public async Task DamagedPrimary_ThrowsAndDoesNotReadOrChangeAnOldBackup()
    {
        var store = new ChapterFileStore(_directory);
        var chapter = TranslationService.CreateChapter("Thử phục hồi", "你好。再见。");
        chapter.Segments[0].UserDraft = "Nháp phiên bản 1.";
        await store.SaveAsync(chapter);
        chapter.Segments[0].UserDraft = "Nháp phiên bản 2.";
        await store.SaveAsync(chapter);
        var path = store.GetPath(chapter.Id);
        await File.WriteAllTextAsync(path + ".bak", "old backup is left untouched");
        await File.WriteAllTextAsync(path, "{damaged synthetic file");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(path));
        Assert.Equal("{damaged synthetic file", await File.ReadAllTextAsync(path));
        Assert.Equal("old backup is left untouched", await File.ReadAllTextAsync(path + ".bak"));
    }

    [Fact]
    public async Task ValidJsonModifiedOutsideTheApplicationLoadsWithoutChecksumValidation()
    {
        var store = new ChapterFileStore(_directory);
        var chapter = TranslationService.CreateChapter("Tiêu đề gốc", "你好。");
        await store.SaveAsync(chapter);
        chapter.Title = "Tiêu đề mới";
        await store.SaveAsync(chapter);
        var path = store.GetPath(chapter.Id);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        node["title"] = "Nội dung được sửa ngoài ứng dụng";
        await File.WriteAllTextAsync(path, node.ToJsonString());
        var loaded = await store.LoadAsync(path);
        Assert.Equal("Nội dung được sửa ngoài ứng dụng", loaded.Chapter.Title);
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public async Task BothFilesDamaged_ReturnsError_AndLeavesFilesUntouched()
    {
        var store = new ChapterFileStore(_directory);
        Directory.CreateDirectory(_directory);
        var path = store.GetPath(Guid.NewGuid());
        await File.WriteAllTextAsync(path, "synthetic primary damage");
        await File.WriteAllTextAsync(path + ".bak", "synthetic backup damage");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(path));
        Assert.Equal("synthetic primary damage", await File.ReadAllTextAsync(path));
        Assert.Equal("synthetic backup damage", await File.ReadAllTextAsync(path + ".bak"));
    }

    [Fact]
    public async Task CancelledSave_LeavesPreviousProgressValid()
    {
        var store = new ChapterFileStore(_directory);
        var chapter = TranslationService.CreateChapter("Thử hủy", "你好。");
        await store.SaveAsync(chapter);
        chapter.Segments[0].UserDraft = "Chưa được lưu.";
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(chapter, cancellation.Token));
        var loaded = await store.LoadAsync(store.GetPath(chapter.Id));
        Assert.Null(loaded.Chapter.Segments[0].UserDraft);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
    }

    [Fact]
    public async Task InvalidSegmentSequence_IsRejectedBeforeWriting()
    {
        var store = new ChapterFileStore(_directory);
        var chapter = TranslationService.CreateChapter("Thử sai dữ liệu", "你好。");
        chapter.Segments[0].SourceText = "再见。";
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(chapter));
        Assert.False(File.Exists(store.GetPath(chapter.Id)));
    }

    [Fact]
    public async Task IndependentChapterFiles_CanReachAtomicWriteConcurrently_AndRemainExact()
    {
        var store = new ChapterFileStore(_directory);
        ChapterDocument first = TranslationService.CreateChapter("Một", "你好。再见。");
        ChapterDocument second = TranslationService.CreateChapter("Hai", "天地玄黄。宇宙洪荒。");
        first.Segments[0].UserDraft = "Nháp GPU thứ nhất.";
        second.Segments[0].UserDraft = "Nháp GPU thứ hai.";
        using var rendezvous = new Barrier(2);

        async Task SaveWithProbe(ChapterDocument chapter)
        {
            SimpleFileStorage.FaultPoint.Value = point =>
            {
                if (point == "AfterTemporaryFlush" && !rendezvous.SignalAndWait(TimeSpan.FromSeconds(3)))
                    throw new TimeoutException("Hai chapter độc lập đã bị khóa tuần tự theo thư mục.");
            };
            try { await store.SaveAsync(chapter); }
            finally { SimpleFileStorage.FaultPoint.Value = null; }
        }

        await Task.WhenAll(SaveWithProbe(first), SaveWithProbe(second));

        ChapterDocument firstSaved = (await store.LoadAsync(store.GetPath(first.Id))).Chapter;
        ChapterDocument secondSaved = (await store.LoadAsync(store.GetPath(second.Id))).Chapter;
        Assert.Equal("Nháp GPU thứ nhất.", firstSaved.Segments[0].UserDraft);
        Assert.Equal("Nháp GPU thứ hai.", secondSaved.Segments[0].UserDraft);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
    }

    [Fact]
    public async Task ConcurrentWritesToSameChapter_AlwaysLeaveOneCompleteValidSnapshot()
    {
        var firstStore = new ChapterFileStore(_directory);
        var secondStore = new ChapterFileStore(_directory);
        ChapterDocument original = TranslationService.CreateChapter("Một chapter", "你好。再见。");
        ChapterDocument first = DocumentJson.Clone(original);
        ChapterDocument second = DocumentJson.Clone(original);
        first.Segments[0].UserDraft = "Toàn bộ snapshot A.";
        second.Segments[0].UserDraft = "Toàn bộ snapshot B.";

        await Task.WhenAll(firstStore.SaveAsync(first), secondStore.SaveAsync(second));

        ChapterDocument saved = (await firstStore.LoadAsync(firstStore.GetPath(original.Id))).Chapter;
        Assert.Contains(saved.Segments[0].UserDraft, new[] { "Toàn bộ snapshot A.", "Toàn bộ snapshot B." });
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
