using QuickTranslator.AiEditor.Core.Models;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class EditorActionPolicyTests
{
    [Theory]
    [InlineData(SegmentStatus.Untranslated, EditorActionKind.TranslateChapter)]
    [InlineData(SegmentStatus.Translating, EditorActionKind.CancelRequest)]
    [InlineData(SegmentStatus.AwaitingReview, EditorActionKind.ApproveAndNext)]
    [InlineData(SegmentStatus.Approved, EditorActionKind.NextUnfinishedChapter)]
    [InlineData(SegmentStatus.Error, EditorActionKind.NextError)]
    [InlineData(SegmentStatus.InspectionRequired, EditorActionKind.NextInspection)]
    internal void Every_segment_status_selects_the_expected_primary(SegmentStatus status, EditorActionKind expected)
    {
        EditorActionContext context = Context(status) with
        {
            SingleRequestBusy = status == SegmentStatus.Translating,
            SelectedSegmentHasEditableDraft = status == SegmentStatus.AwaitingReview
        };

        Assert.Equal(expected, EditorActionPolicy.Decide(context).Primary.Kind);
    }

    [Fact]
    public void Local_and_project_queues_produce_distinct_plans()
    {
        EditorActionContext local = Context(SegmentStatus.Untranslated) with
        {
            LocalQueueRunning = true,
            LocalQueueStatus = QueueStatus.Running
        };
        EditorActionPlan localPlan = EditorActionPolicy.Decide(local);
        Assert.Equal(EditorActionKind.PauseQueue, localPlan.Primary.Kind);
        Assert.Contains(EditorActionKind.CancelQueue, localPlan.Overflow);

        EditorActionContext project = Context(SegmentStatus.Untranslated) with { ProjectQueueRunning = true };
        EditorActionPlan projectPlan = EditorActionPolicy.Decide(project);
        Assert.Equal(EditorActionKind.TranslateChapter, projectPlan.Primary.Kind);
        Assert.False(projectPlan.Primary.IsEnabled);
        Assert.Equal("Hàng đợi Project đang chạy; tạm dừng hàng đợi trước.", projectPlan.Primary.DisabledReason);
    }

    [Fact]
    public void Queue_controls_never_conflict_across_generated_contexts()
    {
        QueueStatus?[] statuses = [null, .. Enum.GetValues<QueueStatus>().Cast<QueueStatus?>()];
        foreach (QueueStatus? status in statuses)
        foreach (bool localRunning in new[] { false, true })
        foreach (int errors in Enumerable.Range(0, 3))
        foreach (int untranslated in Enumerable.Range(0, 3))
        {
            EditorActionContext context = BaseContext() with
            {
                LocalQueueStatus = status,
                LocalQueueRunning = localRunning,
                SegmentCounts = Counts((SegmentStatus.Error, errors), (SegmentStatus.Untranslated, untranslated))
            };
            EditorActionPlan plan = EditorActionPolicy.Decide(context);
            EditorActionKind[] visible = [plan.Primary.Kind, .. plan.Secondary];
            EditorActionKind[] queueKinds = visible.Where(kind => kind is EditorActionKind.PauseQueue or
                EditorActionKind.ResumeQueue or EditorActionKind.RetryErrors or EditorActionKind.CancelQueue).ToArray();
            Assert.True(queueKinds.Length <= 1);
            Assert.DoesNotContain(EditorActionKind.CancelQueue, plan.Secondary);
        }
    }

    [Fact]
    public void Every_disabled_primary_has_a_reason()
    {
        var contexts = new List<EditorActionContext>
        {
            BaseContext() with { UiAvailable = false },
            Context(SegmentStatus.Untranslated) with { PresetsReady = false },
            Context(SegmentStatus.Untranslated) with { ProjectQueueRunning = true },
            BaseContext() with { ProjectOpen = false, ChapterSegmented = false, RawSourceTextPresent = true, PresetsReady = false },
            Context(SegmentStatus.Approved) with { NextUnfinishedProjectChapterExists = false }
        };

        foreach (EditorActionContext context in contexts)
        {
            PrimaryAction primary = EditorActionPolicy.Decide(context).Primary;
            Assert.False(primary.IsEnabled);
            Assert.False(string.IsNullOrWhiteSpace(primary.DisabledReason));
        }
    }

    private static EditorActionContext Context(SegmentStatus status) => BaseContext() with
    {
        SegmentCounts = Counts((status, 1)),
        SelectedSegmentStatus = status
    };

    private static EditorActionContext BaseContext() => new()
    {
        ProjectOpen = true,
        ProjectChapterCount = 1,
        ChapterSegmented = true,
        RawSourceTextPresent = true,
        PresetsReady = true,
        UiAvailable = true,
        NextUnfinishedProjectChapterExists = false
    };

    private static IReadOnlyDictionary<SegmentStatus, int> Counts(params (SegmentStatus Status, int Count)[] values)
    {
        Dictionary<SegmentStatus, int> result = Enum.GetValues<SegmentStatus>().ToDictionary(value => value, _ => 0);
        foreach ((SegmentStatus status, int count) in values) result[status] = count;
        return result;
    }
}
