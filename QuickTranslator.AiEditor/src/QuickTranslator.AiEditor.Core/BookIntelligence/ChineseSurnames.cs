namespace QuickTranslator.AiEditor.Core.BookIntelligence;

/// <summary>
/// Genre-independent surname lexicon for CPU name discovery: common mainland/Taiwan single surnames
/// (the Bách gia tính core plus modern frequency lists) and classical compound surnames used in
/// wuxia/xianxia/historical novels. Single characters that are far more often function words or
/// common morphemes (和, 时, 于, 向, 全, 那, 但, 由, 来, 都…) are deliberately excluded: matching
/// them would turn "于是看…" or "和他说…" into names in every genre. Compound surnames come first so
/// a regex alternation prefers 欧阳 over 欧.
/// </summary>
internal static class ChineseSurnames
{
    internal static readonly string[] Compound =
    [
        "欧阳", "司马", "上官", "诸葛", "慕容", "东方", "南宫", "夏侯", "皇甫", "尉迟", "公孙", "独孤", "令狐", "长孙", "宇文", "轩辕",
        "端木", "澹台", "公冶", "宗政", "濮阳", "淳于", "单于", "太叔", "申屠", "钟离", "闾丘", "司徒", "司空", "呼延", "百里", "东郭",
        "西门", "南门", "拓跋", "赫连", "万俟", "闻人", "夹谷", "宰父", "谷梁", "梁丘", "左丘", "段干", "公羊", "羊舌", "乐正", "漆雕",
        "壤驷", "公良", "巫马", "公西", "颛孙", "亓官", "仲孙", "子车", "微生", "司寇", "鲜于", "第五", "东门", "北堂", "南荣", "叱干"
    ];

    internal const string Single =
        "王李张刘陈杨黄赵吴周徐孙马朱胡郭何高林罗郑梁谢宋唐许韩冯邓曹彭曾肖萧田董袁潘蔡蒋余杜叶程苏魏吕丁任沈姚卢姜崔钟谭陆汪范金石" +
        "廖贾夏韦付傅方白邹孟熊秦邱江尹薛闫阎段雷侯龙史陶黎贺顾毛郝龚邵万钱严覃武戴莫孔汤常温康施文牛樊葛邢安齐易乔伍庞颜倪庄聂章" +
        "鲁岳翟殷詹申欧耿关兰焦俞左柳甘祝包宁尚符舒阮柯纪梅童凌毕单季裴霍涂成苗谷盛曲翁冉骆蓝路游辛靳管柴蒙鲍华喻祁蒲房滕屈饶解牟" +
        "艾尤穆农司卓古吉缪简车项连芦麦褚娄窦戚岑景党宫费卜冷晏席卫米柏宗瞿桂佟应臧闵苟邬边卞姬师仇栾隋商刁沙荣巫寇桑郎甄丛仲虞" +
        "敖巩明佘池查麻苑迟邝官封谈匡鞠惠荆冀郁胥班储栗燕楚鄢劳谌奚皮粟冼蔺楼盘闻厉伊仝郜阚花权帅屠豆朴盖练廉禹井祖漆巴丰卿狄" +
        "计索宣晋初云容敬扈晁芮普阙浦戈伏鹿薄邸雍辜羊乌母裘亓修邰赫杭况宿鲜印逯隆茹诸慕玉银亢嵇哈湛宾戎勾茅揭干尉斯束檀昝智幸" +
        "奉植衡尧闭洪蓟鄂饶钮龙凤";

    /// <summary>
    /// Common tier: surnames frequent enough, and rarely enough a word-initial morpheme, that the usual CPU
    /// evidence suffices. Every other single surname above is "extended": 连/简/修/车/尤/丰/冷/花… start
    /// ordinary words (连忙, 简单, 修长, 车子, 尤其, 丰满, 冷声, 花白), so a candidate with an extended
    /// surname needs strong evidence (introduction, kinship role, or subject + vocative) to be a person.
    /// </summary>
    internal const string CommonSingle =
        "赵钱孙李周吴郑王刘冯陈褚卫蒋沈韩杨朱秦许何吕施张孔曹严华金魏陶姜谢邹苏潘葛范彭鲁韦马苗方俞任袁柳唐罗薛雷贺倪汤殷郝毕安常傅卞齐康" +
        "伍余顾孟黄萧尹姚邵汪祁毛禹狄米贝明臧计伏成戴宋茅庞熊纪舒屈项祝董梁杜阮蓝闵席季麻强贾路娄江童颜郭梅盛林钟徐邱骆高夏蔡田胡霍万" +
        "管卢莫房裘解应宗丁邓洪包左石崔龚程陆翁段靳诸楚闫温庄白廖曾谭叶易黎龙侯武" +
        "宁肖付覃史邢聂詹耿裴冉鲍滕牟艾穆缪岳阎欧";

    /// <summary>Regex fragment matching one surname (compound first).</summary>
    internal static string Pattern { get; } = "(?:" + string.Join("|", Compound) + "|[" + new string(Single.Concat(CommonSingle).Distinct().ToArray()) + "])";

    /// <summary>True when the name starts with a compound or common-tier surname (not only an extended one).</summary>
    internal static bool HasCommonSurname(string name) =>
        Compound.Any(c => name.StartsWith(c, StringComparison.Ordinal)) || name.Length > 0 && CommonSingle.Contains(name[0]);

    /// <summary>True when the name starts with an extended-tier surname only.</summary>
    internal static bool HasExtendedSurnameOnly(string name) => name.Length > 1 && !HasCommonSurname(name) && Single.Contains(name[0]);
}
