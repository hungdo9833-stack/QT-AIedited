using System.Diagnostics;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class CharacterMentionCensusTests
{
    [Fact]
    public void Missing_name_is_counted_in_all_source_including_stale_chapters_and_utf16_locations()
    {
        var (state, resources) = Empty();
        Guid chapter1 = Guid.NewGuid(), chapter19 = Guid.NewGuid();
        CensusSource[] source = [new(chapter1, 1, "𠀀沈夕颜说道：你好。沈夕颜转身。"), new(chapter19, 19, "沈夕颜走来。沈夕颜离去。")];
        state.Chapters.Add(new() { ChapterId = chapter19, Evidence = [new() { ChapterId = chapter19, IsCurrent = false }] });
        string before = JsonSerializer.Serialize(state);
        var report = CharacterMentionCensus.Scan(state.ProjectId, state.Revision, source, resources, state);
        var item = Assert.Single(report.MissingIdentities, x => x.ChineseName == "沈夕颜");
        Assert.Equal(4, item.Frequency); Assert.Equal(1, item.FirstChapter); Assert.Equal(19, item.LastChapter);
        Assert.Equal(2, item.Mentions[0].StartUtf16);
        foreach (var mention in item.Mentions)
        {
            var chapter = source.Single(x => x.ChapterId == mention.ChapterId);
            Assert.Equal(item.ChineseName, chapter.OriginalText.Substring(mention.StartUtf16, mention.LengthUtf16));
            Assert.Equal(CorpusIntegrityAuditor.SourceHash(chapter.OriginalText), mention.SourceSha256);
            Assert.Equal(item.ChineseName, mention.NavigationEvidence(item.ChineseName).Excerpt);
        }
        Assert.Equal(before, JsonSerializer.Serialize(state));
    }

    [Fact]
    public void Generic_phrases_and_unanchored_two_to_four_han_words_are_not_characters()
    {
        var (state, resources) = Empty();
        string text = string.Concat(Enumerable.Repeat("风景如画。周围说道。王国说道。林间说道。众人说道。自己说道。一片山河。", 12));
        var report = CharacterMentionCensus.Scan(state.ProjectId, 0, [new(Guid.NewGuid(), 1, text)], resources, state);
        Assert.Empty(report.MissingIdentities); Assert.All(report.Candidates, x => Assert.Equal(MentionCandidateCategory.NoiseOrUncertain, x.Category));
    }

    [Fact]
    public void Aliases_titles_and_body_identity_hypotheses_remain_independent_never_auto_merged()
    {
        var (state, resources) = Empty();
        string text = string.Concat(Enumerable.Repeat("沈夕颜说道：我如今在苏雨的身体里。苏雨说道：化名阿颜。沈小姐走来。", 3));
        var report = CharacterMentionCensus.Scan(state.ProjectId, 0, [new(Guid.NewGuid(), 3, text)], resources, state);
        Assert.Contains(report.MissingIdentities, x => x.ChineseName == "沈夕颜");
        Assert.Contains(report.MissingIdentities, x => x.ChineseName == "苏雨");
        Assert.Contains(report.Candidates, x => x.ChineseName == "阿颜" && x.Category == MentionCandidateCategory.AliasCandidate);
        Assert.Contains(report.Candidates, x => x.ChineseName == "沈小姐" && x.Category == MentionCandidateCategory.TitleCandidate);
        Assert.DoesNotContain(report.MissingIdentities, x => x.ChineseName is "阿颜" or "沈小姐");
        Assert.Empty(state.CandidateIdentities); Assert.Empty(state.Characters); Assert.Empty(resources.Characters.Profiles);
    }

    [Fact]
    public void Known_profile_and_pending_or_rejected_review_suppress_missing_identity_without_changing_decisions()
    {
        var (state, resources) = Empty();
        resources.Characters.Profiles.Add(new() { ChineseName = "沈夕颜", Aliases = ["阿颜"] });
        state.Characters.Add(new() { ChineseName = "苏雨", Status = IntelligenceReviewStatus.Rejected });
        string text = string.Concat(Enumerable.Repeat("沈夕颜说道：阿颜。苏雨走来。林哲说道：好。", 4));
        var report = CharacterMentionCensus.Scan(state.ProjectId, 0, [new(Guid.NewGuid(), 1, text)], resources, state);
        Assert.Equal("林哲", Assert.Single(report.MissingIdentities).ChineseName);
        Assert.Equal(IntelligenceReviewStatus.Rejected, state.Characters.Single().Status);
        Assert.True(report.Candidates.Single(x => x.ChineseName == "阿颜").HasProfile);
    }

    [Fact]
    public void Orphan_candidate_identity_without_profile_or_character_review_remains_missing()
    {
        var (state, resources) = Empty();
        state.CandidateIdentities.Add(new() { ChineseName = "沈夕颜" });
        var report = CharacterMentionCensus.Scan(state.ProjectId, 0,
            [new(Guid.NewGuid(), 1, "沈夕颜。沈夕颜。沈夕颜。")], resources, state);
        Assert.Equal("沈夕颜", Assert.Single(report.MissingIdentities).ChineseName);
        Assert.False(report.Candidates.Single().HasReview);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(1000)]
    public void Large_full_source_smoke_is_deterministic_and_cancellable(int chapters)
    {
        var (state, resources) = Empty();
        var sources = Enumerable.Range(1, chapters).Select(i => new CensusSource(Guid.NewGuid(), i,
            "沈夕颜说道：今晚出发。" + string.Concat(Enumerable.Repeat("月光落在地面上。沈夕颜继续前行。", 50)))).ToArray();
        var watch = Stopwatch.StartNew();
        var first = CharacterMentionCensus.Scan(state.ProjectId, 0, sources, resources, state);
        var second = CharacterMentionCensus.Scan(state.ProjectId, 0, sources.Reverse().ToArray(), resources, state);
        Assert.Equal(chapters * 51, first.Candidates.Single(x => x.ChineseName == "沈夕颜").Frequency);
        Assert.Equal(JsonSerializer.Serialize(first.Candidates), JsonSerializer.Serialize(second.Candidates));
        Assert.Equal(sources.Sum(x => (long)x.OriginalText.Length), first.ScannedUtf16);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => CharacterMentionCensus.Scan(state.ProjectId, 0, sources, resources, state, cancel.Token));
    }

    [Fact]
    public async Task Async_census_uses_only_active_chapters_and_versioned_sidecar_without_touching_survey_or_resources()
    {
        string root = Path.Combine(Path.GetTempPath(), "qtaie-census-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new ProjectRepository();
            var session = await repository.CreateAsync(root, "Census fixture");
            for (int i = 1; i <= 2; i++) await repository.AddChapterAsync(session,
                TranslationService.CreateChapter("Chương " + i, string.Concat(Enumerable.Repeat(i == 1 ? "沈夕颜说道。" : "苏雨走来。", 4))), i, "章节");
            session.Manifest.Chapters[1].DeletedAt = DateTimeOffset.UtcNow;
            var store = new BookIntelligenceStore(session);
            var state = await store.SaveAsync(await store.LoadAsync());
            byte[] stateBefore = await File.ReadAllBytesAsync(store.PathName);
            var resources = await new ProjectResourceStore(session).LoadAsync();
            var census = await CharacterMentionCensus.ScanAsync(session, repository, resources, state);
            Assert.Equal(1, census.ActiveChapters);
            Assert.DoesNotContain(census.Candidates, x => x.ChineseName == "苏雨");
            await CharacterMentionCensus.SaveReportAsync(session, census);
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(session.Resolve("Settings/abi3-character-census.v2.json")));
            Assert.Equal(2, saved.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(state.ProjectId, saved.RootElement.GetProperty("projectId").GetGuid());
            Assert.Equal(stateBefore, await File.ReadAllBytesAsync(store.PathName));
            Assert.False(File.Exists(session.Resolve(session.Manifest.Files.Resources)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static (BookIntelligenceState, ProjectResources) Empty()
    {
        Guid id = Guid.NewGuid();
        return (new() { ProjectId = id }, new() { ProjectId = id, Characters = new CharacterProject { Id = id } });
    }
}
