using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using Xunit.Abstractions;

namespace QuickTranslator.AiEditor.Tests;

[CollectionDefinition("Census metrics isolated", DisableParallelization = true)]
public sealed class CensusMetricsCollection;

[Collection("Census metrics isolated")]
public sealed class CharacterCensusQualityTests(ITestOutputHelper output)
{
    // Human-labelled regressions from the bug report, not production dictionaries.
    private static readonly string[] PreviouslySupported = ["林朝", "苏雨", "王秀兰", "林哲", "安瑶", "林悦", "赵明", "陈雪", "周宁", "杨过"];
    private static readonly string[] Persons = ["毛亮", "王松", "刘刚", "秦梦芸", "欧阳青川", "星河", "赵宁", .. PreviouslySupported];
    private static readonly string[] Aliases = ["小锋", "小影"];
    private static readonly string[] Titles = ["秦老太爷"];
    private static readonly string[] Noise = ["房门", "温柔", "温柔的", "张的", "严肃的", "安慰", "解释", "郑重", "马路", "安静的", "金项链", "高兴的", "小锋了", "方的"];
    private static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));
    private static string ReportedNameFixture() => Repeat("毛亮的座位一直空着。", 48) + Repeat("今天他联系王松。", 17) +
        Repeat("哥哥刘刚是一名工程师。", 3) + Repeat("妹妹秦梦芸今年十八岁。", 3) + Repeat("秦老太爷一直没有出现。", 24) +
        Repeat("他们都在等小锋了。", 179) + Repeat("她把信交给小影。", 70);
    private static (BookIntelligenceState State, ProjectResources Resources) Empty()
    {
        Guid id = Guid.NewGuid(); return (new() { ProjectId = id }, new() { ProjectId = id, Characters = new CharacterProject { Id = id } });
    }
    private static CharacterCensusReport Scan(string source)
    {
        var (state, resources) = Empty();
        return CharacterMentionCensus.Scan(state.ProjectId, 0, [new(Guid.NewGuid(), 1, source)], resources, state);
    }

    [Fact]
    public void Reported_omissions_are_discovered_and_counted_without_old_action_regex_context()
    {
        string source = ReportedNameFixture();
        var report = Scan(source);
        string[] expected = ["毛亮", "王松", "刘刚", "秦梦芸", "秦老太爷", "小锋", "小影"];
        int[] counts = [48, 17, 3, 3, 24, 179, 70];
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(counts[i], report.Candidates.Single(x => x.ChineseName == expected[i]).Frequency);
        Assert.All(report.Candidates.Where(x => Aliases.Contains(x.ChineseName)), x => Assert.Equal(MentionCandidateCategory.AliasCandidate, x.Category));
        Assert.Equal(MentionCandidateCategory.TitleCandidate, report.Candidates.Single(x => x.ChineseName == "秦老太爷").Category);
        Assert.DoesNotContain(report.MissingIdentities, x => Aliases.Contains(x.ChineseName) || Titles.Contains(x.ChineseName));
        Assert.DoesNotContain(report.Candidates, x => x.ChineseName == "小锋了" && x.Category != MentionCandidateCategory.NoiseOrUncertain);
        var before = LegacyP01Discovery(source);
        output.WriteLine(JsonSerializer.Serialize(new { fixture = "reported-counts-synthetic-not-original-TXT", before, after = report.Candidates.Select(x => new { x.ChineseName, x.Category, x.Frequency }) }));
        Assert.All(expected, name => Assert.DoesNotContain(name, before));
    }

    [Fact]
    public void Labelled_precision_recall_and_false_positive_negative_are_measured_separately_by_category()
    {
        string source = ReportedNameFixture() + "他的全名是欧阳青川。她说：“我叫星河。”父亲赵宁是一位医生。" +
            string.Join("", PreviouslySupported.Select(x => Repeat($"姓名是{x}。{x}说道：好。", 3))) +
            string.Join("", Noise.Select(x => Repeat($"自称{x}。{x}说道：这不是人名。", 3))) + Repeat("妈妈说：先吃饭。", 4);
        var report = Scan(source);
        Quality Measure(IEnumerable<string> truth, IEnumerable<string> predicted)
        {
            var expected = truth.ToHashSet(StringComparer.Ordinal); var actual = predicted.ToHashSet(StringComparer.Ordinal);
            string[] fp = actual.Except(expected).Order(StringComparer.Ordinal).ToArray(), fn = expected.Except(actual).Order(StringComparer.Ordinal).ToArray();
            int tp = actual.Intersect(expected).Count();
            return new(tp, fp, fn, actual.Count == 0 ? 0 : (double)tp / actual.Count, (double)tp / expected.Count);
        }
        var person = Measure(Persons, report.Candidates.Where(x => x.Category == MentionCandidateCategory.PersonCandidate).Select(x => x.ChineseName));
        var alias = Measure(Aliases, report.Candidates.Where(x => x.Category == MentionCandidateCategory.AliasCandidate).Select(x => x.ChineseName));
        var title = Measure(Titles, report.Candidates.Where(x => x.Category == MentionCandidateCategory.TitleCandidate).Select(x => x.ChineseName));
        var before = Measure(Persons.Concat(Aliases).Concat(Titles), LegacyP01Discovery(source));
        var after = Measure(Persons.Concat(Aliases).Concat(Titles), report.Candidates.Where(x => x.Category is MentionCandidateCategory.PersonCandidate or MentionCandidateCategory.AliasCandidate or MentionCandidateCategory.TitleCandidate).Select(x => x.ChineseName));
        output.WriteLine(JsonSerializer.Serialize(new { fixture = "labelled-context-regression-not-production-accuracy", before, after, person, alias, title }));
        Assert.True(person.Recall >= 0.95); Assert.True(person.Precision >= 0.9);
        Assert.Equal(1, alias.Recall); Assert.Equal(1, title.Recall);
        Assert.Empty(after.FalsePositive); Assert.Empty(after.FalseNegative);
        Assert.True(after.Recall > before.Recall); Assert.True(after.Precision > before.Precision);
        Assert.DoesNotContain(report.Candidates, x => Noise.Contains(x.ChineseName) && x.Category is not (MentionCandidateCategory.NoiseOrUncertain or MentionCandidateCategory.AddressingTerm));
        var mom = report.Candidates.Single(x => x.ChineseName == "妈妈");
        Assert.Equal(MentionCandidateCategory.AddressingTerm, mom.Category); Assert.Equal(4, mom.Frequency);
        Assert.DoesNotContain(report.MissingIdentities, x => x.ChineseName == "妈妈");
    }

    [Theory]
    [InlineData("妹妹秦梦芸今年十八岁。", "秦梦芸")]
    [InlineData("她说：“刘刚，快来！”", "刘刚")]
    [InlineData("他的名字叫欧阳青川。", "欧阳青川")]
    [InlineData("我叫星河。", "星河")]
    [InlineData("她名叫温柔。", "温柔")] // a lexical homonym needs explicit identity context
    [InlineData("我叫任我行。", "任我行")] // an introduction can override an internal grammatical character
    [InlineData("这是他的小姨秦梦芸…", "秦梦芸")]
    [InlineData("今天大哥刘庆峰回来了。", "刘庆峰")]
    public void Rare_two_to_four_character_names_need_not_repeat_three_times(string text, string name)
    {
        var candidate = Scan(text).Candidates.Single(x => x.ChineseName == name);
        Assert.Equal(MentionCandidateCategory.PersonCandidate, candidate.Category); Assert.Equal(1, candidate.Frequency);
    }

    [Fact]
    public void A_name_may_end_in_a_character_also_used_as_a_grammar_particle()
    {
        var candidate = Scan(Repeat("杨过说道：好。", 3)).Candidates.Single(x => x.ChineseName == "杨过");
        Assert.Equal(MentionCandidateCategory.PersonCandidate, candidate.Category);
        Assert.Equal(3, candidate.Frequency);
    }

    [Fact]
    public void Context_fragments_soft_wrapped_names_and_common_nouns_do_not_inflate_identity_queues()
    {
        string text = Repeat("姓名是沈夕颜。沈夕颜说道：好。沈夕颜轻轻走来。妹妹沈夕颜换了衣服。妹妹沈夕颜三人走过来。妹妹沈夕颜身后站着朋友。" +
            "女儿柳月\r\n影站在门口。姓名是柳月影。柳月影说道：好。" +
            "妈妈应该回家。妈妈安排了任务。妈妈强行叫回孩子。妈妈房间一趟。" +
            "秦氏内部的安排。高高的房顶上的灯。白色的项链。房间里的声音。" +
            "“何苦呢？”“卫生间！”“谢谢妈妈！”“明年等你。”" +
            "“柳总听说你回来了。”柳总说道：好。“小锋能请你帮忙吗？”小锋的信还在。" +
            "她把信交给小影。她让我叫自己。", 10);
        var result = Scan(text);
        string[] fragments = ["沈夕颜轻", "沈夕颜换", "沈夕颜三人", "沈夕颜身后", "柳月", "应该", "安排", "任务", "强行叫回", "房间一趟", "秦氏", "秦氏内部", "高高", "房顶上", "白色", "项链", "房间里", "何苦呢", "卫生间", "谢谢妈妈", "明年等", "柳总听说", "小锋能", "自己"];
        Assert.DoesNotContain(result.Candidates, x => fragments.Contains(x.ChineseName) &&
            x.Category is MentionCandidateCategory.PersonCandidate or MentionCandidateCategory.AliasCandidate);
        Assert.Contains(result.Candidates, x => x.ChineseName == "沈夕颜" && x.Category == MentionCandidateCategory.PersonCandidate);
        Assert.Contains(result.Candidates, x => x.ChineseName == "柳月影" && x.Category == MentionCandidateCategory.PersonCandidate);
        Assert.Contains(result.Candidates, x => x.ChineseName == "小影" && x.Category == MentionCandidateCategory.AliasCandidate);
        Assert.All(result.Candidates.Where(x => x.Category == MentionCandidateCategory.NoiseOrUncertain),
            x => Assert.True(x.Mentions.Count <= CharacterMentionCensus.MaximumStoredUncertainMentions));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(1000)]
    public void Large_census_keeps_exact_counts_but_bounds_memory_and_persisted_samples(int chapters)
    {
        var (state, resources) = Empty();
        var sources = Enumerable.Range(1, chapters).Select(i => new CensusSource(Guid.NewGuid(), i,
            Repeat("毛亮的座位空着。王松拿起书。小锋的信还在。妈妈说：回家吧。", 100))).ToArray();
        long retained = GC.GetTotalMemory(true), allocated = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
        var result = CharacterMentionCensus.Scan(state.ProjectId, 0, sources, resources, state);
        watch.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        retained = Math.Max(0, GC.GetTotalMemory(true) - retained);
        GC.KeepAlive(sources);
        byte[] json = CharacterMentionCensus.SerializeReport(result);
        output.WriteLine(JsonSerializer.Serialize(new { chapters, utf16 = result.ScannedUtf16, elapsedMs = watch.ElapsedMilliseconds,
            allocatedBytes = allocated, retainedBytes = retained, sidecarBytes = json.Length, candidates = result.Candidates.Count, storedMentions = result.Candidates.Sum(x => x.Mentions.Count) }));
        foreach (string name in new[] { "毛亮", "王松", "小锋", "妈妈" })
        {
            var candidate = result.Candidates.Single(x => x.ChineseName == name);
            Assert.Equal(chapters * 100, candidate.Frequency); Assert.Equal(1, candidate.FirstChapter); Assert.Equal(chapters, candidate.LastChapter);
            Assert.Equal(CharacterMentionCensus.MaximumStoredMentions, candidate.Mentions.Count);
            Assert.Equal(chapters, candidate.MentionedChapterIds!.Count);
        }
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30));
        Assert.True(allocated < 256L * 1024 * 1024); Assert.True(json.Length < 2 * 1024 * 1024);
        Assert.True(retained < 16L * 1024 * 1024);
        Assert.All(result.Candidates, x => Assert.True(x.Mentions.Count <= CharacterMentionCensus.MaximumStoredMentions));
    }

    // Frozen P0.1 discovery on empty resources/review, copied only into tests for a
    // repeatable before/after comparison. Production never calls this baseline.
    private static string[] LegacyP01Discovery(string text)
    {
        const string family = "(?:欧阳|司马|上官|诸葛|慕容|东方|南宫|夏侯|[赵钱孙李周吴郑王冯陈褚卫蒋沈韩杨朱秦许何吕施张孔曹严华金魏陶姜谢邹苏潘葛范彭鲁韦马苗方俞任袁柳唐罗薛雷贺倪汤殷郝毕安常傅卞齐康伍余顾孟黄萧尹姚邵汪祁毛禹狄米贝明臧计伏成戴宋茅庞熊纪舒屈项祝董梁杜阮蓝闵席季麻强贾路娄江童颜郭梅盛林钟徐邱骆高夏蔡田胡霍万管卢莫房裘解应宗丁邓洪包左石崔龚程陆翁段靳诸楚闫温庄白廖曾谭叶易黎龙夏侯])";
        string[] rules = [@"(?<name>" + family + @"[\p{IsCJKUnifiedIdeographs}]{1,2})(?=说道|问道|答道|喊道|说[：:“”]|看着|看向|皱眉|点头|摇头|走来|笑道)",
            @"(?:名叫|名为|本名|真名|化名|自称)[：:\s“”]*(?<name>[\p{IsCJKUnifiedIdeographs}]{2,4})(?=[，。！？、：:\s“”]|$)",
            @"(?<name>" + family + @"(?:小姐|公子|少爷|先生|夫人|老爷|长老|师父|姑娘))"];
        string[] ignored = ["自己", "他们", "我们", "你们", "这个", "那个", "一个", "什么", "怎么", "不是", "就是", "原来", "突然", "然后", "于是", "周围", "周围人", "周围的", "林中", "林间", "林间人", "王国", "王国人", "王朝", "张口", "张开", "张嘴", "白天", "白云", "白衣", "李子", "夏天", "方才", "方向", "何必", "何人", "其他", "所有人", "众人", "一群人", "那个人"];
        return rules.SelectMany(rule => Regex.Matches(text, rule).Select(x => x.Groups["name"].Value))
            .Where(x => !ignored.Contains(x)).Distinct().Order(StringComparer.Ordinal).ToArray();
    }
    private sealed record Quality(int TruePositive, string[] FalsePositive, string[] FalseNegative, double Precision, double Recall);
}
