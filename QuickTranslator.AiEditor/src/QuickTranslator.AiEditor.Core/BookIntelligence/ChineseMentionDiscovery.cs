using System.Text.RegularExpressions;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

[Flags]
internal enum MentionSignal { None = 0, Known = 1, Introduction = 2, Subject = 4, FamilyRole = 8, Vocative = 16, Object = 32, Boundary = 64, Alias = 128, Title = 256, Addressing = 512, ReviewSeed = 1024 }
public enum MentionCandidateCategory { PersonCandidate, AliasCandidate, TitleCandidate, AddressingTerm, NoiseOrUncertain }

/// <summary>Lexical/context hypotheses, not a Chinese word segmenter or an identity resolver.</summary>
internal static class ChineseMentionDiscovery
{
    private const string Han = @"[\p{IsCJKUnifiedIdeographs}]";
    private const string LeftBoundary = @"(?<![\p{IsCJKUnifiedIdeographs}])";
    // Common surname lexicon, not story-specific names. Compound surnames precede single surnames.
    internal const string Family = @"(?:欧阳|司马|上官|诸葛|慕容|东方|南宫|夏侯|皇甫|尉迟|公孙|独孤|令狐|[赵钱孙李周吴郑王刘冯陈褚卫蒋沈韩杨朱秦许何吕施张孔曹严华金魏陶姜谢邹苏潘葛范彭鲁韦马苗方俞任袁柳唐罗薛雷贺倪汤殷郝毕安常傅卞齐康伍余顾孟黄萧尹姚邵汪祁毛禹狄米贝明臧计伏成戴宋茅庞熊纪舒屈项祝董梁杜阮蓝闵席季麻强贾路娄江童颜郭梅盛林钟徐邱骆高夏蔡田胡霍万管卢莫房裘解应宗丁邓洪包左石崔龚程陆翁段靳诸楚闫温庄白廖曾谭叶易黎龙侯武洪江])";
    private const string Modifiers = @"(?:正在|已经|忽然|突然|连忙|立刻|轻轻|慢慢|刚刚|刚才|似乎|终于|只是|竟然|仍然|便|却|又|也|还|就|才|已|正|先|都)";
    private const string Action = @"(?:说道|问道|答道|喊道|叫道|笑道|说|问|答|喊|叫|笑|看|望|听|想|走|跑|站|坐|起身|转身|回头|点头|摇头|皱眉|抬头|低头|伸手|拿|取|递|放|推|拉|摸|抱|牵|拍|打|喝|吃|打开|关上|离开|回来|回到|进入|来到|发现|感觉|觉得|知道|认识|准备|决定|同意|拒绝|回答|解释|提醒|告诉|问起|停下|愣住|穿着|穿过|赶到|赶来|点燃|掏出|攥紧|抿嘴|眨眼)";
    private const string Roles = @"(?:父亲|母亲|爸爸|妈妈|哥哥|姐姐|弟弟|妹妹|儿子|女儿|丈夫|妻子|老公|老婆|表哥|表姐|表弟|表妹|堂哥|堂姐|堂弟|堂妹|叔叔|舅舅|阿姨|姑姑|小姨|小舅|小叔|大伯|二叔|伯父|伯母|大哥|二哥|三哥|大姐|二姐|三姐|大嫂|二嫂|嫂子|大姨|二姨|大舅|二舅|闺蜜|同学|同事|朋友|老师|师父)";
    private const string Titles = @"(?:老太爷|老太太|老先生|老夫人|大少爷|二少爷|三少爷|大小姐|二小姐|太爷|太太|小姐|公子|少爷|先生|夫人|老爷|长老|师父|姑娘|将军|医生|教授|老师|同学|队长|主任|院长|秘书|女士|阿姨|总裁|掌门|宗主|殿下|陛下|总|董|老)";
    // A soft-wrapped Han word is not a name boundary; see Discover's line-break check.
    private const string End = @"(?=[\s，。！？；：、,“”「」『』""!?;:…]|$|的|了|也|才|把|被|和|与|跟|对|向|给|在|是|有|你|您|今年|来自|身旁|心里)";
    private static Regex Rule(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(3));
    private static readonly (Regex Pattern, MentionSignal Signal)[] Rules =
    [
        (Rule(@"(?:名字叫|名叫|名为|叫做|姓名是|全名是|我叫|本名(?:叫|是|为)?|真名(?:叫|是|为)?)[：:\s“”]*(?<name>" + Han + @"{2,4}?)" + End), MentionSignal.Introduction),
        (Rule(@"(?:化名|自称|叫我)[：:\s“”]*(?<name>" + Han + @"{2,4}?)" + End), MentionSignal.Alias),
        (Rule(LeftBoundary + @"(?<name>" + Family + Han + @"{1,3}?)(?=(?:" + Modifiers + @"){0,2}" + Action + @")"), MentionSignal.Subject),
        (Rule(Roles + @"[：:\s、]*(?<name>" + Family + Han + @"{1,3}?)(?:" + End + @"|(?=(?:" + Modifiers + @"){0,2}" + Action + @"))"), MentionSignal.FamilyRole),
        (Rule(@"[“「『""，：:](?<name>" + Family + Han + @"{1,3}?|[小阿老]" + Han + @"{1,2}?)(?=[，、！!？?你您])"), MentionSignal.Vocative),
        (Rule(@"(?:你好|谢谢|叫住|叫来|喊来|喊了|看见|看到|见到|遇到|认识|通知|联系|告诉|望着|拉着|跟着|对着|等着|交给|递给|等)[，：:\s]*(?<name>" + Family + Han + @"{1,3}?|[小阿老]" + Han + @"{1,2}?)" + End), MentionSignal.Object),
        (Rule(LeftBoundary + @"(?<name>" + Family + Han + @"{1,3}?)(?=的)"), MentionSignal.Boundary),
        (Rule(LeftBoundary + @"(?<name>[小阿老]" + Han + @"{1,2}?)" + End), MentionSignal.Alias),
        (Rule(LeftBoundary + @"(?<name>[小阿老]" + Han + @"{1,2}?)(?=(?:" + Modifiers + @"){0,2}" + Action + @")"), MentionSignal.Alias),
        (Rule(LeftBoundary + @"(?<name>" + Family + Titles + @")"), MentionSignal.Title)
    ];
    private static readonly Regex TitleForm = Rule(@"^" + Family + Titles + "$");
    private static readonly Regex AliasForm = Rule(@"^[小阿老]" + Han + "{1,2}$");
    private static readonly Regex PredicateTail = Rule(@"(?:说道|问道|答道|喊道|笑道|走来|点头|摇头|回头|转身|起身|跑来|走去|看着|看向|进来|出去)$");
    private static readonly Regex GrammarFragment = Rule(@"^(?:应该|应当|应了|明明|明显|任何|何时|何苦|谢谢|所谓|过去|明年|今年|去年|明天|今天|昨天)|[的地得了们你我他她它呢吗吧呀啊]|(?:不大|起来|之间|依旧|并不|商量|回家|那边|里面|身上|面前|这里|那儿|自己|似乎|氏|氏内部)$");
    private static readonly Regex CommonNounPrefix = Rule(@"^(?:房间|强行|柳眉|柳腰|朱唇|小腿|小嘴|小脸|小脚)");
    private static readonly Regex Extension = Rule(@"^(?:" + Modifiers + "|" + Action + @"|一|不|刚|只|在|没|早|本|心|大|急|轻|温|现|长|生|压|送|摇|换|让|能|等|被|则|似|哭|饿|可|你|我|听说|依旧|似乎|之间|身后|身前|身边|面前|旁边|两人|三人|回家|商量|并不|应该)$");
    internal static readonly string[] Addressing = ["妈妈", "爸爸", "爹爹", "娘亲", "父亲", "母亲", "哥哥", "姐姐", "弟弟", "妹妹", "叔叔", "舅舅", "阿姨", "姑姑", "小姨", "小舅", "小叔", "大伯", "二叔", "伯父", "伯母", "爷爷", "奶奶", "老爸", "老妈", "老姐", "老哥", "老公", "老婆", "夫君", "娘子", "师父", "师尊", "老师", "师长", "徒儿", "小姐", "少爷", "先生", "夫人", "姑娘", "老爷", "老太爷", "老太太", "老先生", "老夫人", "老医生", "小少爷"];
    private static readonly HashSet<string> AddressWords = new(Addressing, StringComparer.Ordinal);
    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "自己", "他们", "我们", "你们", "这个", "那个", "一个", "什么", "怎么", "不是", "就是", "原来", "突然", "然后", "于是",
        "周围", "周围人", "周围的", "林中", "林间", "林间人", "王国", "王国人", "王朝", "张口", "张开", "张嘴", "白天", "白云", "白衣", "李子", "夏天", "方才", "方向", "何必", "何人", "其他", "所有人", "众人", "一群人", "那个人",
        "房门", "房间", "温柔", "严肃", "安慰", "解释", "郑重", "马路", "安静", "金项链", "高兴", "普通人", "年轻人", "小心", "小声", "小路", "小孩", "老人", "老实", "老家", "老头", "大小", "毛病", "毛衣", "刘海", "高山", "方法", "方位", "门口",
        "安排", "强烈", "成熟美艳", "房里", "房间里", "房顶上", "方式", "温和", "白皙", "白色", "胡乱", "胡思乱", "舒服", "舒爽", "莫名其妙", "项链", "高高", "周日", "卫生间", "小动作", "小嘴", "小嘴里", "小小", "小巧", "小脚丫", "小脸上", "小腿", "小腿纤", "老旧", "小事化", "柳眉弯弯", "柳眉琼鼻", "柳眉紧锁", "柳腰纤纤", "柳腰纤细", "朱唇微启", "明艳非常", "温柔漂亮"
    };

    public static void Discover(string source, Dictionary<string, MentionSignal> names, CancellationToken cancellationToken)
    {
        foreach (var (pattern, signal) in Rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (Match match in pattern.Matches(source))
            {
                string name = match.Groups["name"].Value;
                int start = match.Groups["name"].Index, end = start + name.Length;
                if (ContinuesLine(source, end, 1) ||
                    (signal is MentionSignal.Subject or MentionSignal.Boundary or MentionSignal.Alias or MentionSignal.Title && ContinuesLine(source, start - 1, -1))) continue;
                names[name] = names.GetValueOrDefault(name) | signal;
            }
        }
    }

    private static bool ContinuesLine(string text, int position, int direction)
    {
        bool newline = false;
        while (position >= 0 && position < text.Length && char.IsWhiteSpace(text[position]))
        {
            newline |= text[position] is '\r' or '\n'; position += direction;
        }
        return newline && position >= 0 && position < text.Length && text[position] is >= '\u4e00' and <= '\u9fff';
    }

    internal static bool IsGrammaticalExtension(string name, IReadOnlySet<string> anchored)
    {
        for (int length = 2; length < name.Length; length++)
            if (anchored.Contains(name[..length]) && Extension.IsMatch(name[length..])) return true;
        return false;
    }

    public static MentionCandidateCategory Classify(string name, MentionSignal signals, int frequency, bool knownPerson, bool knownAlias)
    {
        if (AddressWords.Contains(name)) return MentionCandidateCategory.AddressingTerm;
        if (knownPerson) return MentionCandidateCategory.PersonCandidate;
        if (knownAlias) return MentionCandidateCategory.AliasCandidate;
        if (name is "自己" or "他们" or "我们" or "你们" or "这个" or "那个" or "什么" or "怎么")
            return MentionCandidateCategory.NoiseOrUncertain;
        if (AddressWords.Any(x => name.StartsWith(x, StringComparison.Ordinal) && name[x.Length..].All(c => "啊呀呢吧".Contains(c))))
            return MentionCandidateCategory.AddressingTerm;
        bool strong = (signals & (MentionSignal.Introduction | MentionSignal.FamilyRole | MentionSignal.Vocative)) != 0;
        // Closed grammatical tails are not part of a newly guessed name. Do not
        // trim a person's spelling silently; retain it as uncertain for inspection.
        if ("的地得了们".Contains(name[^1]) ||
            (!signals.HasFlag(MentionSignal.Introduction) &&
                (GrammarFragment.IsMatch(name) || Noise.Contains(name) || PredicateTail.IsMatch(name) || CommonNounPrefix.IsMatch(name))))
            return MentionCandidateCategory.NoiseOrUncertain;
        if (TitleForm.IsMatch(name)) return MentionCandidateCategory.TitleCandidate;
        if (AliasForm.IsMatch(name)) return name.Length == 2 || strong || frequency >= 3 && signals.HasFlag(MentionSignal.Vocative)
            ? MentionCandidateCategory.AliasCandidate : MentionCandidateCategory.NoiseOrUncertain;
        if (signals.HasFlag(MentionSignal.Alias)) return MentionCandidateCategory.AliasCandidate;
        if (strong || frequency >= 3 || signals.HasFlag(MentionSignal.Known)) return MentionCandidateCategory.PersonCandidate;
        return MentionCandidateCategory.NoiseOrUncertain;
    }
}
