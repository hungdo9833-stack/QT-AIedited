using System.Diagnostics;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using Xunit.Abstractions;

namespace QuickTranslator.AiEditor.Tests;

[Collection("Census metrics isolated")]
public sealed class EntityMemoryScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Five_hundred_to_one_thousand_chapters_have_bounded_shared_evidence_and_linear_storage_without_excerpt_copies()
    {
        string root = Path.Combine(Path.GetTempPath(), "qtaie-memory-scale-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = await new ProjectRepository().CreateAsync(root, "Synthetic scale only"); Guid project = session.Manifest.ProjectId;
            var store = new EntityMemoryStore(session); var scanner = new TranslationImpactScanner();
            var resources = new ProjectResources { ProjectId = project, Characters = new CharacterProject { Id = project } };
            var survey = new BookIntelligenceState { ProjectId = project }; var audit = new MissingIdentityReviewState { ProjectId = project };
            const string header = "我叫王明。妹妹王宁来了。原来是他。后来改名为王宁。被任命为队长。他们结婚。后来离婚。过继之后改口叫叔叔。最后恢复记忆。BENCH_NEVER_COPY";
            var sources = Enumerable.Range(1, 1000).Select(i => new CensusSource(Guid.NewGuid(), i, header + i + new string('中', 1500))).ToArray();
            long retainedBefore = GC.GetTotalMemory(true); var watch = Stopwatch.StartNew();
            var half = await scanner.ScanAsync(store, sources.Take(500).ToArray(), resources, survey, audit);
            double halfSeconds = watch.Elapsed.TotalSeconds; long halfBytes = new FileInfo(store.PathName).Length;
            Assert.Equal(4000, half.Events.Count); Assert.Equal(500, half.Checkpoints.Count);
            watch.Restart(); var full = await scanner.ScanAsync(store, sources, resources, survey, audit);
            double additionalSeconds = watch.Elapsed.TotalSeconds; long fullBytes = new FileInfo(store.PathName).Length;
            Assert.Equal(8000, full.Events.Count); Assert.Equal(1000, full.Checkpoints.Count);
            Assert.All(half.Events, e => Assert.Contains(full.Events, x => x.Id == e.Id));
            Assert.True(full.Evidence.Count < full.Events.Count / 2, "Proof refs are shared by event types, not copied per candidate.");
            Assert.All(full.Events, e => Assert.Equal(MemoryReviewStatus.Proposed, e.InitialStatus)); Assert.Empty(full.Reviews);
            Assert.DoesNotContain("BENCH_NEVER_COPY", Encoding.UTF8.GetString(EntityMemoryStore.Serialize(full)));
            Assert.InRange((double)fullBytes / halfBytes, 1.7, 2.5); Assert.InRange(fullBytes, 1, 16 * 1024 * 1024);
            watch.Restart(); var again = await scanner.ScanAsync(store, sources, resources, survey, audit);
            double resumeSeconds = watch.Elapsed.TotalSeconds;
            Assert.Equal(full.Revision, again.Revision); Assert.Equal(full.Events.Select(x => x.Id), again.Events.Select(x => x.Id));
            // Drop benchmark comparison snapshots. Measure one retained audit state, not three UI sessions.
            half = null!; again = null!;
            long retainedDelta = GC.GetTotalMemory(true) - retainedBefore;
            output.WriteLine(JsonSerializer.Serialize(new { chapters = 1000, utf16 = sources.Sum(x => x.OriginalText.Length),
                events = full.Events.Count, sharedEvidence = full.Evidence.Count, bytes500 = halfBytes, bytes1000 = fullBytes,
                secondsFirst500 = halfSeconds, secondsAdditional500 = additionalSeconds, secondsNoopResume = resumeSeconds,
                retainedManagedBytesDelta = retainedDelta, note = "Synthetic triggers, no semantic quality claim or provider calls; full snapshot save per checkpoint batch." }));
            Assert.InRange(retainedDelta, -64 * 1024 * 1024, 64 * 1024 * 1024);
            Assert.True(halfSeconds + additionalSeconds < 120, "Synthetic CPU/storage smoke budget exceeded; investigate before scaling.");
            GC.KeepAlive(full);
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("qtaie-memory-scale-", StringComparison.Ordinal))
                Directory.Delete(resolved, true);
        }
    }
}
