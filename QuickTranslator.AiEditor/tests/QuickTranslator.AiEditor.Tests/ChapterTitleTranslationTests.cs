using System.Text.Json;
using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.Importing;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ChapterTitleTranslationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-title-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly ModelOptions Options = new() { Model = "title-test", ContextSize = 4096,
        MaximumOutputTokens = 1024, RequestTimeout = TimeSpan.FromSeconds(30) };

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(300)]
    [InlineData(1000)]
    public async Task Translates_1_50_300_or_1000_titles_in_bounded_batches(int count)
    {
        ChapterTitleTranslationDraft draft = Draft(count);
        var backend = new EchoBackend(reverse: true);

        await new ChapterTitleTranslationService().TranslateAsync(draft, backend, Options);

        Assert.All(draft.Items, item =>
        {
            Assert.Equal(ChapterTitleTranslationStatus.Completed, item.Status);
            Assert.StartsWith($"Chương {item.ChapterNumber}: Việt ", item.VietnameseTitle);
        });
        Assert.InRange(backend.Calls, (int)Math.Ceiling(count / 30d), count);
        Assert.All(backend.BatchSizes, size => Assert.InRange(size, 1, 30));
    }

    [Fact]
    public async Task Reordered_output_is_mapped_by_stable_id_and_duplicate_titles_are_allowed()
    {
        var sources = new[]
        {
            new ChapterTitleTranslationSource(Guid.NewGuid(), 1, "第一章 风波"),
            new ChapterTitleTranslationSource(Guid.NewGuid(), 2, "第2章 风波"),
            new ChapterTitleTranslationSource(Guid.NewGuid(), null, "番外")
        };
        ChapterTitleTranslationDraft draft = ChapterTitleTranslationDraft.Create(Guid.NewGuid(), "source", ChapterTitleTranslationMethod.Ollama, sources);

        await new ChapterTitleTranslationService().TranslateAsync(draft, new EchoBackend(reverse: true), Options);

        Assert.Equal("Chương 1: Việt 风波", draft.Items[0].VietnameseTitle);
        Assert.Equal("Chương 2: Việt 风波", draft.Items[1].VietnameseTitle);
        Assert.Equal("Việt 番外", draft.Items[2].VietnameseTitle);
    }

    [Fact]
    public async Task Keep_chinese_preserves_the_original_title_exactly()
    {
        string original = "第一章　青木堂";
        ChapterTitleTranslationDraft draft = ChapterTitleTranslationDraft.Create(Guid.NewGuid(), "keep",
            ChapterTitleTranslationMethod.KeepChinese,
            [new ChapterTitleTranslationSource(Guid.NewGuid(), 1, original)]);

        await new ChapterTitleTranslationService().TranslateAsync(draft,
            new KeepChineseChapterTitleTranslationBackend(), Options);

        Assert.Equal(original, draft.Items[0].VietnameseTitle);
    }

    [Fact]
    public async Task Numbered_chapter_without_a_name_is_composed_without_calling_ai()
    {
        ChapterTitleTranslationDraft draft = ChapterTitleTranslationDraft.Create(Guid.NewGuid(), "missing-name",
            ChapterTitleTranslationMethod.Ollama,
            [new ChapterTitleTranslationSource(Guid.NewGuid(), 12, "第十二章")]);
        var backend = new EchoBackend();

        await new ChapterTitleTranslationService().TranslateAsync(draft, backend, Options);

        Assert.Equal(0, backend.Calls);
        Assert.Equal("Chương 12", draft.Items[0].VietnameseTitle);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("empty")]
    public async Task Invalid_structured_output_rejects_the_whole_batch(string failure)
    {
        ChapterTitleTranslationDraft draft = Draft(5);
        var backend = new InvalidBackend(failure);

        await new ChapterTitleTranslationService().TranslateAsync(draft, backend, Options);

        Assert.All(draft.Items, item => Assert.Equal(ChapterTitleTranslationStatus.Failed, item.Status));
        Assert.All(draft.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.Error)));
        Assert.All(draft.Items, item => Assert.Empty(item.VietnameseTitle));
    }

    [Fact]
    public async Task Retry_only_failed_batch_does_not_translate_successful_batch_again()
    {
        ChapterTitleTranslationDraft draft = Draft(50);
        var backend = new FailFirstBatchBackend();
        var service = new ChapterTitleTranslationService();
        int failedBatchSize = ChapterTitleBatchPlanner.Plan(draft.Items, Options)[0].Length;

        await service.TranslateAsync(draft, backend, Options);
        Assert.Equal(failedBatchSize, draft.Items.Count(item => item.Status == ChapterTitleTranslationStatus.Failed));
        Assert.Equal(50 - failedBatchSize, draft.Items.Count(item => item.Status == ChapterTitleTranslationStatus.Completed));
        int callsBeforeRetry = backend.Calls;

        await service.TranslateAsync(draft, backend, Options, retryFailedOnly: true);

        Assert.Equal(callsBeforeRetry + 1, backend.Calls);
        Assert.All(draft.Items, item => Assert.Equal(ChapterTitleTranslationStatus.Completed, item.Status));
        Assert.All(draft.Items.Skip(30), item => Assert.Equal(1, item.Attempts));
    }

    [Fact]
    public async Task Cancel_keeps_completed_batches_and_returns_current_batch_to_pending()
    {
        ChapterTitleTranslationDraft draft = Draft(50);
        using var cancellation = new CancellationTokenSource();
        var backend = new CancelSecondBatchBackend(cancellation);
        int firstBatchSize = ChapterTitleBatchPlanner.Plan(draft.Items, Options)[0].Length;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ChapterTitleTranslationService().TranslateAsync(draft, backend, Options,
                cancellationToken: cancellation.Token));

        Assert.Equal(firstBatchSize, draft.Items.Count(item => item.Status == ChapterTitleTranslationStatus.Completed));
        Assert.Equal(50 - firstBatchSize, draft.Items.Count(item => item.Status == ChapterTitleTranslationStatus.Pending));
    }

    [Fact]
    public async Task Ollama_prompt_uses_confirmed_glossary_and_parser_accepts_out_of_order_json()
    {
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        string response = JsonSerializer.Serialize(new[]
        {
            new { id = second, translated_title = "Phong Ba" },
            new { id = first, translated_title = "Thanh Mộc Đường" }
        }, DocumentJson.Options);
        var client = new CaptureClient(response);
        var backend = new OllamaChapterTitleTranslationBackend(client, Options, [new("青木堂", "Thanh Mộc Đường")]);

        IReadOnlyList<ChapterTitleBackendOutput> output = await backend.TranslateBatchAsync(
            [new(first, "青木堂"), new(second, "风波")]);

        Assert.Equal(second, output[0].Id);
        Assert.Contains("青木堂 => Thanh Mộc Đường", client.Prompt);
        Assert.Contains("translated_title", client.Prompt);
    }

    [Fact]
    public void Ollama_parser_accepts_prose_fence_container_and_common_property_casing()
    {
        Guid id = Guid.NewGuid();
        string response = $$"""
            Tôi đã dịch xong:
            ```json
            {"results":[{"id":"{{id:D}}","translatedTitle":"Thanh Mộc Đường"}]}
            ```
            """;

        ChapterTitleBackendOutput item = Assert.Single(OllamaChapterTitleTranslationBackend.Parse(response));

        Assert.Equal(id, item.Id);
        Assert.Equal("Thanh Mộc Đường", item.TranslatedTitle);
    }

    [Fact]
    public async Task Ollama_backend_retries_malformed_large_response_as_smaller_verified_batches()
    {
        ChapterTitleBackendInput[] input = Enumerable.Range(1, 20)
            .Select(number => new ChapterTitleBackendInput(Guid.NewGuid(), $"标题{number}"))
            .ToArray();
        var client = new MalformedThenValidClient();
        var backend = new OllamaChapterTitleTranslationBackend(client, Options);

        IReadOnlyList<ChapterTitleBackendOutput> output = await backend.TranslateBatchAsync(input);

        Assert.Equal(input.Select(item => item.Id).Order(), output.Select(item => item.Id).Order());
        Assert.Equal(3, client.Calls);
        Assert.Equal([20, 10, 10], client.RequestSizes);
    }

    [Fact]
    public void Batch_planner_reduces_batch_size_when_output_budget_is_small()
    {
        ChapterTitleTranslationItem[] items = Draft(30).Items.ToArray();
        ModelOptions constrained = Options with { MaximumOutputTokens = 512 };

        IReadOnlyList<ChapterTitleTranslationItem[]> batches = ChapterTitleBatchPlanner.Plan(items, constrained);

        Assert.True(batches.Count > 2);
        Assert.All(batches, batch => Assert.InRange(batch.Length, 1, 10));
        Assert.Equal(items.Select(item => item.Id), batches.SelectMany(batch => batch).Select(item => item.Id));
    }

    [Fact]
    public async Task Draft_checkpoint_round_trips_completed_and_failed_batches()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "title-draft.json");
        ChapterTitleTranslationDraft draft = Draft(3);
        draft.Items[0].Status = ChapterTitleTranslationStatus.Completed;
        draft.Items[0].VietnameseTitle = "Chương 1: Một";
        draft.Items[1].Status = ChapterTitleTranslationStatus.Failed;
        draft.Items[1].Error = "timeout";
        var store = new ChapterTitleTranslationDraftStore(path);

        await store.SaveAsync(draft);
        ChapterTitleTranslationDraft loaded = Assert.IsType<ChapterTitleTranslationDraft>(await store.LoadAsync());

        Assert.Equal("Chương 1: Một", loaded.Items[0].VietnameseTitle);
        Assert.Equal(ChapterTitleTranslationStatus.Failed, loaded.Items[1].Status);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Bulk_import_stores_vietnamese_title_separately_without_changing_source()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "import"), "Titles");
        string text = "第一章 青木堂\r\n正文一。\r\n第二章 风波\r\n正文二。";
        ImportPreviewEditor editor = ImportPreviewEditor.Create(new ChapterDetector().Detect(RawSourceReader.FromText(text)));
        ImportPreviewPlan before = editor.Current;
        editor.ApplyVietnameseTitles(before.ImportableChapters.ToDictionary(item => item.Id,
            item => $"Chương {item.Number}: Việt {item.Title}"));

        BulkImportReport report = await new SafeBulkImportService(repository).ImportAsync(session, editor.Current,
            new(new SegmenterOptions(), Options, PromptPreset.Default));

        Assert.True(report.Completed);
        Assert.Equal(text, editor.Current.ReconstructSource());
        foreach (ProjectChapterEntry entry in session.Manifest.Chapters)
        {
            Assert.StartsWith("Chương ", entry.VietnameseTitle);
            Assert.DoesNotContain("Việt", entry.ChineseTitle);
            ChapterDocument chapter = (await repository.LoadChapterAsync(session, entry.ChapterId)).Chapter;
            Assert.Equal(entry.ChineseTitle, chapter.ChineseTitle);
            Assert.Equal(entry.VietnameseTitle, chapter.Title);
        }
    }

    [Fact]
    public async Task Project_bulk_title_update_is_atomic_and_preserves_translation_data()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "project"), "Titles");
        ChapterDocument first = TranslationService.CreateChapter("第一章 青木堂", "原文一。", model: Options);
        first.ChineseTitle = "第一章 青木堂";
        TranslationSegment segment = first.Segments[0];
        segment.UserDraft = "Bản nháp"; segment.DraftRevision = 2;
        segment.DraftHistory.Add(new() { RecordedAt = DateTimeOffset.UtcNow, Revision = 1, Text = "cũ", Reason = "test" });
        segment.Approvals.Add(new() { ApprovedAt = DateTimeOffset.UtcNow, DraftRevision = 2, Text = "đã duyệt" });
        segment.Status = SegmentStatus.Approved;
        await repository.AddChapterAsync(session, first, 1, first.ChineseTitle);
        ChapterDocument second = TranslationService.CreateChapter("Tên Việt thủ công", "原文二。", model: Options);
        second.ChineseTitle = "第二章 风波";
        await repository.AddChapterAsync(session, second, 2, second.ChineseTitle);
        ChapterDocument before = (await repository.LoadChapterAsync(session, first.Id)).Chapter;

        ProjectChapterTitleUpdatePreview preview = await repository.PreviewChapterTitleUpdateAsync(session,
            [new(first.Id, "Chương 1: Thanh Mộc Đường"), new(second.Id, "Chương 2: Phong Ba")]);
        Assert.Equal(1, preview.ApplyCount);
        Assert.Equal(1, preview.ProtectedCount);
        await repository.ApplyChapterTitleUpdateAsync(session, preview);

        ChapterDocument after = (await repository.LoadChapterAsync(session, first.Id)).Chapter;
        ChapterDocument protectedAfter = (await repository.LoadChapterAsync(session, second.Id)).Chapter;
        Assert.Equal("Chương 1: Thanh Mộc Đường", after.Title);
        Assert.Equal(before.OriginalText, after.OriginalText);
        Assert.Equal(JsonSerializer.Serialize(before.Segments, DocumentJson.Options),
            JsonSerializer.Serialize(after.Segments, DocumentJson.Options));
        Assert.Equal(JsonSerializer.Serialize(before.Queue, DocumentJson.Options),
            JsonSerializer.Serialize(after.Queue, DocumentJson.Options));
        Assert.Equal("Tên Việt thủ công", protectedAfter.Title);
    }

    [Fact]
    public async Task Project_title_fault_rolls_back_all_chapters()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "fault"), "Titles");
        var ids = new List<Guid>();
        for (int number = 1; number <= 3; number++)
        {
            ChapterDocument chapter = TranslationService.CreateChapter($"第{number}章 标题", $"原文{number}。", model: Options);
            chapter.ChineseTitle = chapter.Title; ids.Add(chapter.Id);
            await repository.AddChapterAsync(session, chapter, number, chapter.ChineseTitle);
        }
        ProjectChapterTitleUpdatePreview preview = await repository.PreviewChapterTitleUpdateAsync(session,
            ids.Select((id, index) => new ProjectChapterTitleProposal(id, $"Chương {index + 1}: Mới")).ToArray());
        repository.FaultPoint = point => { if (point == "AfterFile:2") throw new IOException("disk full injected"); };

        await Assert.ThrowsAsync<IOException>(() => repository.ApplyChapterTitleUpdateAsync(session, preview));

        Assert.All(session.Manifest.Chapters, entry => Assert.StartsWith("第", entry.VietnameseTitle));
        foreach (Guid id in ids) Assert.StartsWith("第", (await repository.LoadChapterAsync(session, id)).Chapter.Title);
    }

    private static ChapterTitleTranslationDraft Draft(int count) => ChapterTitleTranslationDraft.Create(Guid.NewGuid(),
        "synthetic", ChapterTitleTranslationMethod.Ollama, Enumerable.Range(1, count).Select(number =>
            new ChapterTitleTranslationSource(Guid.NewGuid(), number, $"第{number}章 标题{number}")));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class EchoBackend(bool reverse = false) : IChapterTitleTranslationBackend
    {
        public int Calls { get; private set; }
        public List<int> BatchSizes { get; } = [];
        public Task<IReadOnlyList<ChapterTitleBackendOutput>> TranslateBatchAsync(IReadOnlyList<ChapterTitleBackendInput> titles,
            CancellationToken cancellationToken = default)
        {
            Calls++; BatchSizes.Add(titles.Count);
            IEnumerable<ChapterTitleBackendInput> ordered = reverse ? titles.Reverse() : titles;
            return Task.FromResult<IReadOnlyList<ChapterTitleBackendOutput>>(ordered
                .Select(title => new ChapterTitleBackendOutput(title.Id, "Việt " + title.ChineseName)).ToArray());
        }
    }

    private sealed class InvalidBackend(string failure) : IChapterTitleTranslationBackend
    {
        public Task<IReadOnlyList<ChapterTitleBackendOutput>> TranslateBatchAsync(IReadOnlyList<ChapterTitleBackendInput> titles,
            CancellationToken cancellationToken = default)
        {
            List<ChapterTitleBackendOutput> output = titles.Select(title => new ChapterTitleBackendOutput(title.Id,
                failure == "empty" ? "" : "Việt")).ToList();
            if (failure == "missing") output.RemoveAt(0);
            if (failure == "duplicate") output.Add(output[0]);
            if (failure == "extra") output.Add(new(Guid.NewGuid(), "Thừa"));
            return Task.FromResult<IReadOnlyList<ChapterTitleBackendOutput>>(output);
        }
    }

    private sealed class FailFirstBatchBackend : IChapterTitleTranslationBackend
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<ChapterTitleBackendOutput>> TranslateBatchAsync(IReadOnlyList<ChapterTitleBackendInput> titles,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == 1) throw new OllamaRequestException(OllamaErrorKind.Timeout, "timeout");
            return Task.FromResult<IReadOnlyList<ChapterTitleBackendOutput>>(titles
                .Select(title => new ChapterTitleBackendOutput(title.Id, "Việt " + title.ChineseName)).ToArray());
        }
    }

    private sealed class CancelSecondBatchBackend(CancellationTokenSource cancellation) : IChapterTitleTranslationBackend
    {
        private int _calls;
        public Task<IReadOnlyList<ChapterTitleBackendOutput>> TranslateBatchAsync(IReadOnlyList<ChapterTitleBackendInput> titles,
            CancellationToken cancellationToken = default)
        {
            _calls++;
            if (_calls == 2) { cancellation.Cancel(); cancellationToken.ThrowIfCancellationRequested(); }
            return Task.FromResult<IReadOnlyList<ChapterTitleBackendOutput>>(titles
                .Select(title => new ChapterTitleBackendOutput(title.Id, "Việt " + title.ChineseName)).ToArray());
        }
    }

    private sealed class CaptureClient(string response) : IOllamaClient
    {
        public string Prompt { get; private set; } = "";
        public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options,
            CancellationToken cancellationToken = default)
        {
            Prompt = prompt;
            return Task.FromResult(new OllamaCompletion(response, true, "stop", new GenerationMetrics()));
        }
    }

    private sealed class MalformedThenValidClient : IOllamaClient
    {
        public int Calls { get; private set; }
        public List<int> RequestSizes { get; } = [];

        public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Guid[] ids = Regex.Matches(prompt, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")
                .Select(match => Guid.Parse(match.Value)).Distinct().ToArray();
            RequestSizes.Add(ids.Length);
            string response = Calls == 1 ? "JSON bị cắt: [{\"id\":" :
                JsonSerializer.Serialize(ids.Select(id => new { id, translated_title = "Tên Việt" }), DocumentJson.Options);
            return Task.FromResult(new OllamaCompletion(response, true, "stop", new GenerationMetrics()));
        }
    }
}

