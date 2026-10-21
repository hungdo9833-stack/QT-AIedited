using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class MockApiWorkflowTests
{
    [Fact]
    public async Task MockHttp_ToService_ToDisk_ToReview_WorksWithoutServerOrGpu()
    {
        string directory = Path.Combine(Path.GetTempPath(), "qtaie-api-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            int calls = 0;
            using var http = new HttpClient(new LlamaServerClientTests.FakeHandler((_, _) =>
            {
                calls++;
                return Task.FromResult(LlamaServerClientTests.JsonResponse("""
                    {"choices":[{"message":{"role":"assistant","content":"Xin chào, người bạn cũ."},"finish_reason":"stop"}],
                     "usage":{"prompt_tokens":100,"completion_tokens":20},"timings":{"prompt_ms":10,"predicted_ms":200}}
                    """));
            }));
            using var client = new LlamaServerClient(http, LlamaServerClient.DefaultEndpoint, 1);
            var store = new ChapterFileStore(directory);
            var chapter = TranslationService.CreateChapter("Chương giả lập", "你好，老朋友。\r\n再见。",
                new() { MinimumHanCharacters = 3, TargetHanCharacters = 5, MaximumHanCharacters = 6 });
            var service = new TranslationService(chapter, client, store);
            var id = chapter.Segments[0].Id;
            await service.TranslateAsync(id);
            service.UpdateDraft(id, "Chào người bạn cũ.");
            await service.ApproveAsync(id);
            var loaded = await store.LoadAsync(store.GetPath(chapter.Id));
            var reopened = new TranslationService(loaded.Chapter, client, store).GetSnapshot();
            Assert.Equal(1, calls);
            Assert.Equal(SegmentStatus.Approved, reopened.Segments[0].Status);
            Assert.Equal("Chào người bạn cũ.", reopened.Segments[0].UserDraft);
            Assert.Equal(20, reopened.Segments[0].Attempts[0].Metrics!.OutputTokens);
            Assert.All(reopened.Segments.Skip(1), s => Assert.Empty(s.Attempts));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
