using System.Text.RegularExpressions;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

[Flags]
internal enum MentionSignal { None = 0, Known = 1, Introduction = 2, Subject = 4, FamilyRole = 8, Vocative = 16, Object = 32, Boundary = 64, Alias = 128, Title = 256, Addressing = 512, ReviewSeed = 1024 }
public enum MentionCandidateCategory { PersonCandidate, AliasCandidate, TitleCandidate, AddressingTerm, NoiseOrUncertain }

/// <summary>Lexical/context hypotheses, not a Chinese word segmenter or an identity resolver.</summary>
internal static class ChineseMentionDiscovery
{
    private const string Han = @"[\p{IsCJKUnifiedIdeographs}]";
    // Common surname lexicon, not story-specific names. Compound surnames precede single surnames.
    internal const string Family = @"(?:欧阳|司马|上官|诸葛|慕容|东方|南宫|夏侯|皇甫|尉迟|公孙|独孤|令狐|[赵钱孙李周吴郑王刘冯陈褚卫蒋沈韩杨朱秦许何吕施张孔曹严华金魏陶姜谢邹苏潘葛范彭鲁韦马苗方俞任袁柳唐罗薛雷贺倪汤殷郝毕安常傅卞齐康伍余顾孟黄萧尹姚邵汪祁毛禹狄米贝明臧计伏成戴宋茅庞熊纪舒屈项祝董梁杜阮蓝闵席季麻强贾路娄江童颜郭梅盛林钟徐邱骆高夏蔡田胡霍万管卢莫房裘解应宗丁邓洪包左石崔龚程陆翁段靳诸楚闫温庄白廖曾谭叶易黎龙侯武洪江])";
    private const string Modifiers = @"(?:正在|已经|忽然|突然|连忙|立刻|轻轻|慢慢|刚刚|刚才|似乎|终于|只是|竟然|仍然|便|却|又|也|还|就|才|已|正|先|都)";
    private const string Action = @"(?:说道|问道|答道|喊道|叫道|笑道|说|问|答|喊|叫|笑|看|望|听|想|走|跑|站|坐|起身|转身|回头|点头|摇头|皱眉|抬头|低头|伸手|拿|取|递|放|推|拉|摸|抱|牵|拍|喝|吃|打开|关上|离开|回来|回到|进入|来到|发现|感觉|觉得|知道|认识|准备|决定|同意|拒绝|回答|解释|提醒|告诉|问起|停下|愣住|穿着|穿过|赶到|赶来|点燃|掏出|攥紧|抿嘴|眨眼)";
    private const string Roles = @"(?:父亲|母亲|爸爸|妈妈|哥哥|姐姐|弟弟|妹妹|儿子|女儿|丈夫|妻子|老公|老婆|表哥|表姐|表弟|表妹|堂哥|堂姐|堂弟|堂妹|叔叔|舅舅|阿姨|姑姑|同学|同事|朋友|老师|师父)";
    private const string Titles = @"(?:老太爷|老太太|老先生|老夫人|大少爷|二少爷|三少爷|太爷|太太|小姐|公子|少爷|先生|夫人|老爷|长老|师父|姑娘|将军|医生|教授|老师|队长|主任|总裁|掌门|宗主|殿下|陛下)";
    private const string End = @"(?=[\s，。！？；：、,“”「」『』""!?;:]|$|的|了|也|才|把|被|和|与|跟|对|向|给|在|是|有|今年|来自|身旁|心里)";
    private static Regex Rule(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(3));
    private static readonly (Regex Pattern, MentionSignal Signal)[] Rules =
    [
        (Rule(@"(?:名字叫|名叫|名为|叫做|姓名是|全名是|我叫|他叫|她叫|本名(?:叫|是|为)?|真名(?:叫|是|为)?)[：:\s“”]*(?<name>" + Han + @"{2,4}?)" + End), MentionSignal.Introduction),
        (Rule(@"(?:化名|自称|叫我)[：:\s“”]*(?<name>" + Han + @"{2,4}?)" + End), MentionSignal.Alias),
        (Rule(@"(?<name>" + Family + Han + @"{1,3}?)(?=(?:" + Modifiers + @"){0,2}" + Action + @")"), MentionSignal.Subject),
        (Rule(Roles + @"[：:\s、]*(?<name>" + Family + Han + @"{1,3}?)" + End), MentionSignal.FamilyRole),
        (Rule(@"[“「『""，：:](?<name>" + Family + Han + @"{1,3}?|[小阿老]" + Han + @"{1,2}?)(?=[，、！!？?])"), MentionSignal.Vocative),
        (Rule(@"(?:你好|谢谢|叫住|叫来|喊来|喊了|看见|看到|见到|遇到|认识|通知|联系|告诉|望着|拉着|跟着|对着|等着)[，：:\s]*(?<name>" + Family + Han + @"{1,3}?)" + End), MentionSignal.Object),
        (Rule(@"(?<name>" + Family + Han + @"{1,3}?)(?=的|与|跟|和|被|把|对|向|给|在|是|有|身旁|心里)"), MentionSignal.Boundary),
        (Rule(@"(?:^|(?<=[\s，。！？；：、“”「」『』""!?;:]))(?<name>" + Family + Han + @"{1,3}?)" + End), MentionSignal.Boundary),
        (Rule(@"(?<name>[小阿老]" + Han + @"{1,2}?)" + End), MentionSignal.Alias),
        (Rule(@"(?<name>[小阿老]" + Han + @"{1,2}?)(?=(?:" + Modifiers + @"){0,2}" + Action + @")"), MentionSignal.Alias),
        (Rule(@"(?<name>" + Family + Titles + @")"), MentionSignal.Title)
    ];
    private static readonly Regex TitleForm = Rule(@"^" + Family + Titles + "$");
    private static readonly Regex AliasForm = Rule(@"^[小阿老]" + Han + "{1,2}$");
    private static readonly Regex PredicateTail = Rule(@"(?:说道|问道|答道|喊道|笑道|走来|点头|摇头|回头|转身|起身|跑来|走去|看着|看向|进来|出去)$");
    internal static readonly string[] Addressing = ["妈妈", "爸爸", "爹爹", "娘亲", "父亲", "母亲", "哥哥", "姐姐", "弟弟", "妹妹", "叔叔", "舅舅", "阿姨", "姑姑", "爷爷", "奶奶", "老爸", "老妈", "老公", "老婆", "夫君", "娘子", "师父", "师尊", "徒儿", "小姐", "少爷", "先生", "夫人", "姑娘", "老爷", "老太爷", "老太太", "老先生", "老夫人"];
    private static readonly HashSet<string> AddressWords = new(Addressing, StringComparer.Ordinal);
    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "自己", "他们", "我们", "你们", "这个", "那个", "一个", "什么", "怎么", "不是", "就是", "原来", "突然", "然后", "于是",
        "周围", "周围人", "周围的", "林中", "林间", "林间人", "王国", "王国人", "王朝", "张口", "张开", "张嘴", "白天", "白云", "白衣", "李子", "夏天", "方才", "方向", "何必", "何人", "其他", "所有人", "众人", "一群人", "那个人",
        "房门", "房间", "温柔", "严肃", "安慰", "解释", "郑重", "马路", "安静", "金项链", "高兴", "普通人", "年轻人", "小心", "小声", "小路", "小孩", "老人", "老实", "老家", "老头", "大小", "毛病", "毛衣", "刘海", "高山", "方法", "方位", "门口"
    };

    public static void Discover(string source, Dictionary<string, MentionSignal> names, CancellationToken cancellationToken)
    {
        foreach (var (pattern, signal) in Rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (Match match in pattern.Matches(source))
            {
                string name = match.Groups["name"].Value;
                names[name] = names.GetValueOrDefault(name) | signal;
            }
        }
    }

    public static MentionCandidateCategory Classify(string name, MentionSignal signals, int frequency, bool knownPerson, bool knownAlias)
    {
        if (AddressWords.Contains(name)) return MentionCandidateCategory.AddressingTerm;
        if (knownPerson) return MentionCandidateCategory.PersonCandidate;
        if (knownAlias) return MentionCandidateCategory.AliasCandidate;
        bool strong = (signals & (MentionSignal.Introduction | MentionSignal.FamilyRole | MentionSignal.Vocative)) != 0;
        // Closed grammatical tails are not part of a newly guessed name. Do not
        // trim a person's spelling silently; retain it as uncertain for inspection.
        if ("的地得了们".Contains(name[^1]) ||
            (!signals.HasFlag(MentionSignal.Introduction) && (Noise.Contains(name) || PredicateTail.IsMatch(name))))
            return MentionCandidateCategory.NoiseOrUncertain;
        if (TitleForm.IsMatch(name)) return MentionCandidateCategory.TitleCandidate;
        if (AliasForm.IsMatch(name) || signals.HasFlag(MentionSignal.Alias)) return MentionCandidateCategory.AliasCandidate;
        if (strong || frequency >= 3 || signals.HasFlag(MentionSignal.Known)) return MentionCandidateCategory.PersonCandidate;
        return MentionCandidateCategory.NoiseOrUncertain;
    }
}
