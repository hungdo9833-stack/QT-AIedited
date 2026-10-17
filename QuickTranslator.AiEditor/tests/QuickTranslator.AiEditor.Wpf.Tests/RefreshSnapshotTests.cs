using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Segmentation;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class RefreshSnapshotTests
{
    [Fact]
    public Task RefreshOfLargeChapter_UsesOneDocumentSnapshotBudget() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        string source = string.Join("\n", Enumerable.Repeat("王秀兰看见李明走进客栈。", 500));
        ChapterDocument chapter = TranslationService.CreateChapter("Refresh synthetic", source,
            new SegmenterOptions { MinimumHanCharacters = 1, TargetHanCharacters = 8,
                MaximumHanCharacters = 16, MaximumTextElements = 2000 });
        foreach (TranslationSegment segment in chapter.Segments)
        {
            segment.UserDraft = "Bản dịch tổng hợp cho phép đo Refresh.";
            segment.Status = SegmentStatus.AwaitingReview;
            segment.Attempts.Add(new TranslationAttempt
            {
                StartedAt = DateTimeOffset.UtcNow,
                FinishedAt = DateTimeOffset.UtcNow,
                Outcome = AttemptOutcome.Completed,
                AiText = segment.UserDraft,
                Prompt = "Synthetic regression prompt " + new string('x', 256),
                PromptVersion = "snapshot-budget"
            });
        }
        fixture.Attach(chapter);
        fixture.Call("Refresh");

        long before = GC.GetAllocatedBytesForCurrentThread();
        fixture.Call("Refresh");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 20_000_000,
            $"Refresh allocated {allocated:N0} bytes; repeated ChapterDocument snapshots are still occurring.");
        return Task.CompletedTask;
    });
}
