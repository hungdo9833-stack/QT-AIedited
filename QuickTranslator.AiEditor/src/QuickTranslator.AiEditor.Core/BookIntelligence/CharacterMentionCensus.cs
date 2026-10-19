using System.Text.Json;
using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public enum MentionHypothesisKind { KnownName, ContextualName, AliasHypothesis, TitleHypothesis }
public sealed record CensusSource(Guid ChapterId, int ChapterNumber, string OriginalText);
public sealed record CharacterMention(Guid ChapterId, int ChapterNumber, int StartUtf16, int LengthUtf16, string SourceSha256)
{
    public CorpusEvidence NavigationEvidence(string name) => new()
    {
        ChapterId = ChapterId, StartUtf16 = StartUtf16, LengthUtf16 = LengthUtf16,
        SourceSha256 = SourceSha256, Excerpt = name, IsCurrent = true,
        PriorityReason = "CPU mention hypothesis, not an AI finding or approved identity"
    };
}
public sealed record CharacterMentionCandidate(string ChineseName, MentionHypothesisKind Kind,
    IReadOnlyList<CharacterMention> Mentions, bool HasProfile, bool HasReview)
{
    public int Frequency => Mentions.Count;
    public int FirstChapter => Mentions.Min(x => x.ChapterNumber);
    public int LastChapter => Mentions.Max(x => x.ChapterNumber);
}
public sealed record MissingIdentityAuditItem(string ChineseName, MentionHypothesisKind Kind, int Frequency,
    int FirstChapter, int LastChapter, IReadOnlyList<CharacterMention> Mentions);
public sealed record CharacterCensusReport(int SchemaVersion, Guid ProjectId, long SurveyRevision,
    int ActiveChapters, long ScannedUtf16, IReadOnlyDictionary<Guid, string> SourceHashes,
    IReadOnlyList<CharacterMentionCandidate> Candidates, IReadOnlyList<MissingIdentityAuditItem> MissingIdentities);

/// <summary>Conservative CPU discovery followed by exact literal counting over ALL active source.
/// No statistical name recognition, identity merge, or AI/network/resource writes.</summary>
public static class CharacterMentionCensus
{
    private const string Han = @"[\p{IsCJKUnifiedIdeographs}]";
    private const string Surnames = "赵钱孙李周吴郑王冯陈褚卫蒋沈韩杨朱秦许何吕施张孔曹严华金魏陶姜谢邹苏潘葛范彭鲁韦马苗方俞任袁柳唐罗薛雷贺倪汤殷郝毕安常傅卞齐康伍余顾孟黄萧尹姚邵汪祁毛禹狄米贝明臧计伏成戴宋茅庞熊纪舒屈项祝董梁杜阮蓝闵席季麻强贾路娄江童颜郭梅盛林钟徐邱骆高夏蔡田胡霍万管卢莫房裘解应宗丁邓洪包左石崔龚程陆翁段靳诸楚闫温庄白廖曾谭叶易黎龙夏侯";
    private const string Family = @"(?:欧阳|司马|上官|诸葛|慕容|东方|南宫|夏侯|[" + Surnames + "])";
    private static readonly TimeSpan RegexLimit = TimeSpan.FromSeconds(3);
    private static readonly Regex NameAction = new(@"(?<name>" + Family + Han + @"{1,2})(?=说道|问道|答道|喊道|说[：:“”]|看着|看向|皱眉|点头|摇头|走来|笑道)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexLimit);
    private static readonly Regex Introduction = new(@"(?:名叫|名为|本名|真名|化名|自称)[：:\s“”]*(?<name>" + Han + @"{2,4})(?=[，。！？、：:\s“”]|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexLimit);
    private static readonly Regex Titled = new(@"(?<name>" + Family + @"(?:小姐|公子|少爷|先生|夫人|老爷|长老|师父|姑娘))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexLimit);
    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "自己", "他们", "我们", "你们", "这个", "那个", "一个", "什么", "怎么", "不是", "就是", "原来", "突然", "然后", "于是",
        "周围", "周围人", "周围的", "林中", "林间", "林间人", "王国", "王国人", "王朝", "张口", "张开", "张嘴", "白天", "白云", "白衣", "李子", "夏天", "方才", "方向", "何必", "何人", "其他", "所有人", "众人", "一群人", "那个人"
    };

    public static CharacterCensusReport Scan(Guid projectId, long surveyRevision, IReadOnlyList<CensusSource> sources,
        ProjectResources resources, BookIntelligenceState state, CancellationToken cancellationToken = default)
    {
        if (projectId != resources.ProjectId || projectId != state.ProjectId) throw new InvalidOperationException("Census belongs to another project.");
        if (sources.Select(x => x.ChapterId).Distinct().Count() != sources.Count) throw new InvalidDataException("Duplicate census chapter identity.");
        var profiles = resources.Characters.Profiles.SelectMany(x => new[] { x.ChineseName }.Concat(x.Aliases))
            .Where(Useful).ToHashSet(StringComparer.Ordinal);
        var reviewed = state.Characters.SelectMany(x => new[] { x.ChineseName }.Concat(x.Aliases))
            .Concat(state.CandidateIdentities.SelectMany(x => new[] { x.ChineseName }.Concat(x.Aliases)))
            .Where(Useful).ToHashSet(StringComparer.Ordinal);
        var names = new Dictionary<string, MentionHypothesisKind>(StringComparer.Ordinal);
        foreach (string name in profiles.Concat(reviewed)) names.TryAdd(name, MentionHypothesisKind.KnownName);
        var hashes = new Dictionary<Guid, string>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hashes.Add(source.ChapterId, CorpusIntegrityAuditor.SourceHash(source.OriginalText));
            Discover(NameAction, source.OriginalText, MentionHypothesisKind.ContextualName);
            Discover(Introduction, source.OriginalText, MentionHypothesisKind.AliasHypothesis);
            Discover(Titled, source.OriginalText, MentionHypothesisKind.TitleHypothesis);
        }
        void Discover(Regex pattern, string text, MentionHypothesisKind kind)
        {
            foreach (Match match in pattern.Matches(text))
            {
                string name = match.Groups["name"].Value;
                if (!Useful(name)) continue;
                if (kind == MentionHypothesisKind.TitleHypothesis && names.GetValueOrDefault(name) != MentionHypothesisKind.KnownName)
                    names[name] = kind;
                else names.TryAdd(name, kind);
            }
        }
        // Multi-pattern trie scans each UTF-16 source position once. This also
        // counts literal mentions outside action/introduction contexts and across
        // windows that the previous index missed or marked stale.
        var trie = new Node();
        foreach (string name in names.Keys)
        {
            Node node = trie;
            foreach (char c in name)
            {
                if (!node.Children.TryGetValue(c, out Node? next)) node.Children.Add(c, next = new());
                node = next;
            }
            node.Name = name;
        }
        var mentions = names.Keys.ToDictionary(x => x, _ => new List<CharacterMention>(), StringComparer.Ordinal);
        foreach (var source in sources.OrderBy(x => x.ChapterNumber).ThenBy(x => x.ChapterId))
        {
            for (int start = 0; start < source.OriginalText.Length; start++)
            {
                if ((start & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                Node node = trie;
                for (int i = start; i < source.OriginalText.Length && node.Children.TryGetValue(source.OriginalText[i], out Node? next); i++)
                {
                    node = next;
                    if (node.Name is string name)
                        mentions[name].Add(new(source.ChapterId, source.ChapterNumber, start, name.Length, hashes[source.ChapterId]));
                }
            }
        }
        var candidates = names.Where(x => mentions[x.Key].Count > 0).OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new CharacterMentionCandidate(x.Key, x.Value, mentions[x.Key], profiles.Contains(x.Key), reviewed.Contains(x.Key))).ToArray();
        var missing = candidates.Where(x => !x.HasProfile && !x.HasReview && x.Frequency >= 3)
            .Select(x => new MissingIdentityAuditItem(x.ChineseName, x.Kind, x.Frequency, x.FirstChapter, x.LastChapter, x.Mentions))
            .OrderByDescending(x => x.Frequency).ThenBy(x => x.ChineseName, StringComparer.Ordinal).ToArray();
        return new(1, projectId, surveyRevision, sources.Count, sources.Sum(x => (long)x.OriginalText.Length), hashes, candidates, missing);
    }

    public static async Task<CharacterCensusReport> ScanAsync(ProjectSession session, ProjectRepository repository,
        ProjectResources resources, BookIntelligenceState state, CancellationToken cancellationToken = default)
    {
        var sources = new List<CensusSource>();
        foreach (var entry in session.Manifest.Chapters.Where(x => x.DeletedAt is null).OrderBy(x => x.Number).ThenBy(x => x.ChapterId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chapter = (await repository.LoadChapterAsync(session, entry.ChapterId, cancellationToken).ConfigureAwait(false)).Chapter;
            if (chapter.Id != entry.ChapterId || chapter.ProjectId != session.Manifest.ProjectId) throw new InvalidDataException("Census chapter identity mismatch.");
            sources.Add(new(entry.ChapterId, entry.Number, chapter.OriginalText ?? ""));
        }
        return Scan(session.Manifest.ProjectId, state.Revision, sources, resources, state, cancellationToken);
    }

    // Optional, versioned diagnostics sidecar. Never part of Project Resources or a
    // translation prompt. UI rebuilds from source rather than trusting stale stats.
    public static Task SaveReportAsync(ProjectSession session, CharacterCensusReport report, CancellationToken cancellationToken = default)
    {
        if (report.SchemaVersion != 1 || report.ProjectId != session.Manifest.ProjectId) throw new InvalidDataException("Invalid census report.");
        return SimpleFileStorage.WriteAsync(session.Resolve("Settings/abi3-character-census.v1.json"),
            JsonSerializer.SerializeToUtf8Bytes(report, DocumentJson.Options),
            bytes => JsonSerializer.Deserialize<CharacterCensusReport>(bytes, DocumentJson.Options)
                ?? throw new InvalidDataException("Empty census report."), cancellationToken);
    }
    private static bool Useful(string name) => !string.IsNullOrWhiteSpace(name) && name.Length >= 2 && !Noise.Contains(name);
    private sealed class Node { public Dictionary<char, Node> Children { get; } = []; public string? Name { get; set; } }
}
