using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.TextCleaning;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class BatchReplacementTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "qtaie-batch-tests-" + Guid.NewGuid().ToString("N"));
    private readonly BatchReplacementEngine _engine = new();
    private readonly ChineseTextScanner _scanner = new();

    [Fact]
    public void Scanner_GroupsRepeatedHanWithExactUtf16PositionsAndCounts()
    {
        var chapter = Chapter("Tôi gặp 王秀兰. 王秀兰 nói chuyện với 林哲.");
        chapter.ConfirmedTerms = [new("王秀兰", "Vương Tú Lan"), new("林哲", "Lâm Triết")];
        chapter.CharacterProject.Profiles = [Profile("王秀兰", "Vương Tú Lan")];

        var report = _scanner.Scan(chapter);

        Assert.Equal(3, report.TotalOccurrences);
        var wang = Assert.Single(report.Groups, x => x.Text == "王秀兰");
        Assert.Equal(2, wang.Count);
        Assert.Equal(ChineseTextClassification.ConfirmedCharacter, wang.Classification);
        Assert.Equal(chapter.Segments[0].EditableText.IndexOf("王秀兰", StringComparison.Ordinal), wang.Occurrences[0].StartUtf16);
        Assert.Equal(ChineseTextClassification.ConfirmedGlossary,
            Assert.Single(report.Groups, x => x.Text == "林哲").Classification);
    }

    [Fact]
    public void Scanner_DetectsCjkExtensionRuneAndUnknownTextBetweenVietnamese()
    {
        string extensionB = char.ConvertFromUtf32(0x20000);
        var chapter = Chapter("Đầu " + extensionB + "王 cuối.");

        var item = Assert.Single(_scanner.Scan(chapter).Occurrences);

        Assert.Equal(extensionB + "王", item.Text);
        Assert.Equal(3, item.LengthUtf16);
        Assert.Equal(ChineseTextClassification.Unknown, item.Classification);
    }

    [Fact]
    public void Scanner_IgnoresOnlyTextExplicitlyMarkedKeepUnchanged()
    {
        var chapter = Chapter("Giữ 王秀兰 nhưng kiểm tra 林哲.");
        chapter.TextCleaner.IgnoredChineseText = ["王秀兰"];

        var report = _scanner.Scan(chapter);

        Assert.Equal(ChineseTextClassification.Ignored, report.Occurrences.Single(x => x.Text == "王秀兰").Classification);
        Assert.Equal(ChineseTextClassification.Unknown, report.Occurrences.Single(x => x.Text == "林哲").Classification);
    }

    [Fact]
    public void Preview_UsesLongestMatchWithoutEnglishWordBoundaries()
    {
        var chapter = Chapter("A王秀兰B 王秀 王秀兰");
        var rules = new[] { Rule("王秀", "Vương Tú"), Rule("王秀兰", "Vương Tú Lan") };

        var preview = _engine.Preview([chapter], rules, BatchReplacementScope.CurrentChapter);
        string changed = BatchReplacementEngine.Apply(chapter.Segments[0].EditableText, preview);

        Assert.Equal(3, preview.Count);
        Assert.Equal("AVương Tú LanB Vương Tú Vương Tú Lan", changed);
        Assert.Equal(2, preview.Count(x => x.Original == "王秀兰"));
    }

    [Fact]
    public void Preview_DoesNotReplaceShortNameInsideConfirmedLongerName()
    {
        var chapter = Chapter("林哲宇 gặp 林哲.");
        var preview = _engine.Preview([chapter],
            [Rule("林哲", "Lâm Triết"), Rule("林哲宇", "Lâm Triết Vũ")], BatchReplacementScope.CurrentChapter);

        Assert.Equal(2, preview.Count);
        Assert.Equal("Lâm Triết Vũ gặp Lâm Triết.", BatchReplacementEngine.Apply(chapter.Segments[0].EditableText, preview));
    }

    [Fact]
    public void ConflictingMappingsArePreviewedButCannotBeApplied()
    {
        var chapter = Chapter("王秀兰 xuất hiện.");
        var rules = new[] { Rule("王秀兰", "Vương Tú Lan"), Rule("王秀兰", "Vương Tú Lân") };

        var item = Assert.Single(_engine.Preview([chapter], rules, BatchReplacementScope.CurrentChapter));

        Assert.True(item.Conflict);
        Assert.Null(item.Replacement);
        Assert.Throws<InvalidOperationException>(() => BatchReplacementEngine.Apply(item.AnalysedText, [item]));
    }

    [Fact]
    public void ScannerClassifiesConflictingConfirmedMappingsWithoutGuessing()
    {
        var chapter = Chapter("王秀兰 xuất hiện.");
        chapter.ConfirmedTerms = [new("王秀兰", "Vương Tú Lan"), new("王秀兰", "Vương Tú Lân")];

        var item = Assert.Single(_scanner.Scan(chapter).Occurrences);

        Assert.Equal(ChineseTextClassification.Conflict, item.Classification);
        Assert.All(item.Candidates, candidate => Assert.True(candidate.Conflict));
    }

    [Fact]
    public void ManualRuleMayReplaceWithEmptyText()
    {
        var chapter = Chapter("Bỏ 王秀兰 khỏi câu.");
        var rule = Assert.Single(ReplacementRuleCatalog.ParseManualRules("王秀兰 =>"));
        var preview = _engine.Preview([chapter], [rule], BatchReplacementScope.CurrentChapter);

        Assert.Equal("Bỏ  khỏi câu.", BatchReplacementEngine.Apply(chapter.Segments[0].EditableText, preview));
    }

    [Fact]
    public void CatalogSeparatesConfirmedMappingsAndTraditionalSuggestions()
    {
        var chapter = Chapter("王秀兰");
        chapter.ConfirmedTerms = [new("王秀兰", "Vương Tú Lan")];
        var catalog = ReplacementRuleCatalog.FromChapter(chapter,
            [new() { Find = "林哲", Replacement = "Lâm Triết", Source = ReplacementRuleSource.TraditionalNames,
                Confirmed = false, SourceLabel = "Names2" }]);

        Assert.Contains(catalog.Rules, x => x.Find == "王秀兰" && x.Confirmed);
        Assert.Contains(catalog.Rules, x => x.Find == "林哲" && !x.Confirmed && x.SourceLabel == "Names2");
    }

    [Fact]
    public void ProtectedDraftIsNotSelectedAutomaticallyAndRequiresExplicitOverride()
    {
        var chapter = Chapter("王秀兰 đến.");
        chapter.Segments[0].UserDraft = chapter.Segments[0].EditableText;
        chapter.Segments[0].DraftRevision = 2;
        var item = Assert.Single(_engine.Preview([chapter], [Rule("王秀兰", "Vương Tú Lan")], BatchReplacementScope.CurrentChapter));

        Assert.True(item.Protected);
        Assert.Throws<InvalidOperationException>(() => BatchReplacementEngine.ApplyToChapter(chapter, [item], false,
            BatchReplacementScope.CurrentChapter, Guid.NewGuid(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ExplicitProtectedReplacementCreatesRevisionAndPreservesAiHistorySourceAndApproval()
    {
        var chapter = Chapter("王秀兰 đến.");
        var segment = chapter.Segments[0];
        string source = segment.SourceText;
        string ai = segment.Attempts[0].AiText!;
        segment.UserDraft = segment.EditableText;
        segment.DraftRevision = 1;
        segment.Approvals.Add(new() { ApprovedAt = DateTimeOffset.UtcNow, DraftRevision = 1, Text = segment.UserDraft });
        segment.Status = SegmentStatus.Approved;
        var item = Assert.Single(_engine.Preview([chapter], [Rule("王秀兰", "Vương Tú Lan")], BatchReplacementScope.CurrentChapter));

        var applied = BatchReplacementEngine.ApplyToChapter(chapter, [item], true,
            BatchReplacementScope.CurrentChapter, Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Equal("Vương Tú Lan đến.", segment.UserDraft);
        Assert.Equal(ai, segment.Attempts[0].AiText);
        Assert.Equal(source, segment.SourceText);
        Assert.Single(segment.Approvals);
        Assert.Single(segment.DraftHistory);
        Assert.Equal(SegmentStatus.AwaitingReview, segment.Status);
        Assert.Equal(1, applied.OccurrencesChanged);
    }

    [Fact]
    public void UndoRestoresRepresentationStatusAndTextForWholeBatch()
    {
        var chapter = Chapter("王秀兰 gặp 林哲.");
        var segment = chapter.Segments[0];
        var preview = _engine.Preview([chapter], [Rule("王秀兰", "Vương Tú Lan"), Rule("林哲", "Lâm Triết")],
            BatchReplacementScope.CurrentChapter);
        var applied = BatchReplacementEngine.ApplyToChapter(chapter, preview, false,
            BatchReplacementScope.CurrentChapter, Guid.NewGuid(), DateTimeOffset.UtcNow);

        BatchReplacementEngine.UndoLatest(chapter, DateTimeOffset.UtcNow, applied.BatchId);

        Assert.Null(segment.UserDraft);
        Assert.Equal("王秀兰 gặp 林哲.", segment.EditableText);
        Assert.Equal(SegmentStatus.AwaitingReview, segment.Status);
        Assert.True(chapter.BatchReplacementHistory.Single().Reverted);
    }

    [Fact]
    public async Task ServiceRejectsStalePreviewAndDoesNotCallOllamaForReplacement()
    {
        var chapter = Chapter("王秀兰 đến.");
        var client = new FakeClient();
        var service = new TranslationService(chapter, client, new MemoryStore());
        var preview = service.PreviewBatchReplacement([Rule("王秀兰", "Vương Tú Lan")], BatchReplacementScope.CurrentChapter);
        service.UpdateDraft(chapter.Segments[0].Id, "Đã đổi sau preview 王秀兰.");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyBatchReplacementAsync(preview, true,
            BatchReplacementScope.CurrentChapter));
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task AutomaticCleanerStoresRawAiAndUsesConfirmedSnapshotWithoutSecondOllamaCall()
    {
        var chapter = TranslationService.CreateChapter("Tự làm sạch", "王秀兰来了。");
        chapter.ConfirmedTerms = [new("王秀兰", "Vương Tú Lan")];
        chapter.TextCleaner.AutoApplyConfirmedMappings = true;
        var client = new FakeClient { Respond = (_, _) => Task.FromResult(FakeClient.Completion("王秀兰 đã đến.")) };
        var service = new TranslationService(chapter, client, new MemoryStore());

        await service.TranslateAsync(chapter.Segments[0].Id);
        var saved = service.GetSnapshot().Segments[0];

        Assert.Equal(1, client.Calls);
        Assert.Equal("王秀兰 đã đến.", saved.Attempts[0].AiText);
        Assert.Equal("Vương Tú Lan đã đến.", saved.EditableText);
        Assert.Single(service.GetSnapshot().BatchReplacementHistory, x => x.Automatic);
    }

    [Fact]
    public async Task AutomaticCleanerMarksUnknownChineseForInspectionAndDoesNotOverwriteManualDraft()
    {
        var chapter = TranslationService.CreateChapter("Còn sót", "王秀兰来了。");
        chapter.TextCleaner.AutoApplyConfirmedMappings = true;
        chapter.Segments[0].UserDraft = "Bản người dùng 王秀兰.";
        chapter.Segments[0].DraftRevision = 1;
        var client = new FakeClient { Respond = (_, _) => Task.FromResult(FakeClient.Completion("AI trả 林哲.")) };
        var service = new TranslationService(chapter, client, new MemoryStore());

        await service.TranslateAsync(chapter.Segments[0].Id);
        var saved = service.GetSnapshot().Segments[0];

        Assert.Equal("Bản người dùng 王秀兰.", saved.UserDraft);
        Assert.Equal("AI trả 林哲.", saved.Attempts[^1].AiText);
        Assert.Contains(saved.Attempts[^1].Issues, x => x.Code == IssueCode.UntranslatedChinese);
        Assert.Empty(service.GetSnapshot().BatchReplacementHistory);
    }

    [Fact]
    public async Task BatchReplacementIsBlockedWhileQueueRequestIsActive()
    {
        var chapter = TranslationService.CreateChapter("Queue", "你好。");
        chapter.Segments[0].PreservedText = "王秀兰";
        chapter.Segments[0].Status = SegmentStatus.Error;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient
        {
            Respond = async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return FakeClient.Completion("never");
            }
        };
        var service = new TranslationService(chapter, client, new MemoryStore());
        var preview = service.PreviewBatchReplacement([Rule("王秀兰", "Vương Tú Lan")], BatchReplacementScope.CurrentChapter);
        var queue = new TranslationQueue(service);
        Task running = queue.StartAsync(QueueMode.FailedSegments);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyBatchReplacementAsync(preview, false,
            BatchReplacementScope.CurrentChapter));
        await queue.CancelAsync();
        await running;
    }

    [Fact]
    public async Task ProjectReplacementOnlyTouchesMatchingProjectWithoutCreatingBackup()
    {
        var store = new ChapterFileStore(_directory);
        var first = Chapter("王秀兰 chương một.", "Một");
        var second = Chapter("王秀兰 chương hai.", "Hai");
        second.CharacterProject.Id = first.CharacterProject.Id;
        var foreign = Chapter("王秀兰 dự án khác.", "Khác");
        await store.SaveAsync(first); await store.SaveAsync(second); await store.SaveAsync(foreign);
        var coordinator = new BatchProjectReplacementService(store);
        var chapters = await coordinator.LoadProjectChaptersAsync(first.CharacterProject.Id);
        var preview = _engine.Preview(chapters, [Rule("王秀兰", "Vương Tú Lan")], BatchReplacementScope.CurrentProject);

        var report = await coordinator.ApplyAsync(chapters, preview, false);

        Assert.Equal(2, report.AppliedChapters);
        Assert.Equal(0, report.FailedChapters);
        Assert.False(Directory.Exists(Path.Combine(_directory, "Backups")));
        Assert.Equal("Vương Tú Lan chương một.", (await store.LoadAsync(store.GetPath(first.Id))).Chapter.Segments[0].EditableText);
        Assert.Equal("王秀兰 dự án khác.", (await store.LoadAsync(store.GetPath(foreign.Id))).Chapter.Segments[0].EditableText);

        var changed = await coordinator.LoadProjectChaptersAsync(first.CharacterProject.Id);
        var undone = await coordinator.UndoAsync(changed, report.BatchId);
        Assert.Equal(2, undone.AppliedChapters);
        Assert.Equal("王秀兰 chương một.", (await store.LoadAsync(store.GetPath(first.Id))).Chapter.Segments[0].EditableText);
    }

    [Fact]
    public async Task ProjectReportIdentifiesSuccessfulAndFailedChaptersWithoutHidingPartialWrite()
    {
        var store = new ChapterFileStore(_directory);
        var first = Chapter("王秀兰 chương một.", "Một");
        var second = Chapter("王秀兰 chương hai.", "Hai");
        second.CharacterProject.Id = first.CharacterProject.Id;
        await store.SaveAsync(first); await store.SaveAsync(second);
        var coordinator = new BatchProjectReplacementService(store);
        var chapters = (await coordinator.LoadProjectChaptersAsync(first.CharacterProject.Id)).ToList();
        var preview = _engine.Preview(chapters, [Rule("王秀兰", "Vương Tú Lan")], BatchReplacementScope.CurrentProject);
        chapters.Single(x => x.Id == second.Id).Segments[0].SourceText = "损坏";

        var report = await coordinator.ApplyAsync(chapters, preview, false);

        Assert.Equal(1, report.AppliedChapters);
        Assert.Equal(1, report.FailedChapters);
        Assert.True(report.Chapters.Single(x => x.ChapterId == first.Id).Applied);
        Assert.False(report.Chapters.Single(x => x.ChapterId == second.Id).Applied);
        Assert.False(string.IsNullOrWhiteSpace(report.Chapters.Single(x => x.ChapterId == second.Id).Error));
        Assert.Equal("王秀兰 chương hai.", (await store.LoadAsync(store.GetPath(second.Id))).Chapter.Segments[0].EditableText);
    }

    [Fact]
    public async Task CleanerSettingsAndBatchHistoryRoundTripThroughSafeStorage()
    {
        var store = new ChapterFileStore(_directory);
        var chapter = Chapter("王秀兰 đến.");
        chapter.TextCleaner.AutoApplyConfirmedMappings = true;
        chapter.TextCleaner.IgnoredChineseText = ["保留"];
        var preview = _engine.Preview([chapter], [Rule("王秀兰", "Vương Tú Lan")], BatchReplacementScope.CurrentChapter);
        BatchReplacementEngine.ApplyToChapter(chapter, preview, false, BatchReplacementScope.CurrentChapter,
            Guid.NewGuid(), DateTimeOffset.UtcNow);

        await store.SaveAsync(chapter);
        var loaded = (await store.LoadAsync(store.GetPath(chapter.Id))).Chapter;

        Assert.True(loaded.TextCleaner.AutoApplyConfirmedMappings);
        Assert.Equal(["保留"], loaded.TextCleaner.IgnoredChineseText);
        Assert.Single(loaded.BatchReplacementHistory);
        Assert.Equal("Vương Tú Lan đến.", loaded.Segments[0].EditableText);
    }

    [Fact]
    public async Task FailedSafeWriteLeavesLastSavedChapterReadable()
    {
        var chapter = Chapter("王秀兰 đến.");
        var store = new MemoryStore();
        await store.SaveAsync(chapter);
        var service = new TranslationService(chapter, new FakeClient(), store);
        var preview = service.PreviewBatchReplacement([Rule("王秀兰", "Vương Tú Lan")], BatchReplacementScope.CurrentChapter);
        store.BeforeSave = (_, _) => throw new IOException("synthetic write failure");

        await Assert.ThrowsAsync<IOException>(() => service.ApplyBatchReplacementAsync(preview, false,
            BatchReplacementScope.CurrentChapter));

        Assert.Equal("王秀兰 đến.", store.LastSaved!.Segments[0].EditableText);
    }

    private static ChapterDocument Chapter(string translated, string title = "Chương thử")
    {
        var chapter = TranslationService.CreateChapter(title, "你好。");
        var segment = chapter.Segments[0];
        segment.Attempts.Add(new()
        {
            StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow,
            Outcome = AttemptOutcome.Completed, AiText = translated, Prompt = "test", PromptVersion = "test"
        });
        segment.Status = SegmentStatus.AwaitingReview;
        return chapter;
    }

    private static CharacterProfile Profile(string chinese, string vietnamese) => new()
    {
        ChineseName = chinese, VietnameseName = vietnamese, NormalizationEnabled = false
    };

    private static BatchReplacementRule Rule(string find, string replacement) => new()
    {
        Find = find, Replacement = replacement, Source = ReplacementRuleSource.Manual,
        Confirmed = true, SourceLabel = "test"
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
