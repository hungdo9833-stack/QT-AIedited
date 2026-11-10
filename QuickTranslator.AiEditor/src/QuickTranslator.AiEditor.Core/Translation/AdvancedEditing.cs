using System.Text;
using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

/// <summary>What the editing request may not change: names, the characters' narrative pronouns, the story setting.</summary>
/// <param name="AddressRules">The book's relationship rules as names and terms (A Xuân → Thẩm Duy Trinh: "muội", "huynh"):
/// an edit may bring these terms into a paragraph near the rule's characters.</param>
public sealed record AdvancedEditingContext(StorySetting Setting, IReadOnlyList<string> Names,
    IReadOnlyList<string> PronounLines, IReadOnlySet<string> NarrativePronouns,
    IReadOnlyList<AdvancedEditingAddressRule>? AddressRules = null, IReadOnlyList<string>? FixedNames = null)
{
    public static AdvancedEditingContext Empty { get; } = new(StorySetting.Unspecified, [], [], new HashSet<string>());

    /// <summary>The names and narrative pronouns of the characters and confirmed terms this draft actually uses.</summary>
    public static AdvancedEditingContext For(CharacterProject project, IEnumerable<ConfirmedTerm> terms, string source,
        string draft, int? chapter = null)
    {
        CharacterProfile[] present = project.Profiles.Where(profile => profile.VietnameseName.Trim().Length > 0 &&
            draft.Contains(profile.VietnameseName.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        string[] names = present.Select(profile => profile.VietnameseName.Trim())
            .Concat(terms.Where(term => source.Contains(term.Chinese, StringComparison.Ordinal) &&
                    draft.Contains(term.Vietnamese.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(term => term.Vietnamese.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] lines = present.Where(profile => profile.NarrativePronounFor(chapter).Trim().Length > 0)
            .Select(profile => $"{profile.VietnameseName.Trim()} = {profile.NarrativePronounFor(chapter).Trim()}").ToArray();
        var pronouns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CharacterProfile profile in project.Profiles)
            foreach (string value in new[] { profile.NarrativePronounFor(chapter), profile.SecondaryNarrativePronoun })
                foreach (string word in Words(value)) pronouns.Add(word);
        Dictionary<Guid, string> namesById = project.Profiles.Where(profile => profile.VietnameseName.Trim().Length > 0)
            .GroupBy(profile => profile.Id).ToDictionary(group => group.Key, group => group.First().VietnameseName.Trim());
        var rules = new List<AdvancedEditingAddressRule>();
        foreach (CharacterProfile owner in project.Profiles)
            foreach (CharacterRelationship rule in owner.Relationships.Where(rule => rule.Enabled && rule.AppliesTo(chapter)))
            {
                var ruleTerms = AdvancedEditing.AddressTermsIn(rule.SelfReference + " " + rule.AddresseeReference)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (ruleTerms.Count > 0 && namesById.TryGetValue(rule.EffectiveSpeakerCharacterId(owner.Id), out string? speaker) &&
                    namesById.TryGetValue(rule.EffectiveListenerCharacterId, out string? listener))
                    rules.Add(new(speaker, listener, ruleTerms));
            }
        // Every name and glossary rendering of the book, longest first, so "Thẩm Lâm Anh" goes before "Lâm Anh".
        string[] fixedNames = project.Profiles.Select(profile => profile.VietnameseName.Trim())
            .Concat(terms.Select(term => term.Vietnamese.Trim()))
            .Where(name => name.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(name => name.Length).ToArray();
        return new(project.Setting, names, lines, pronouns, rules, fixedNames);
    }

    private static IEnumerable<string> Words(string? value) =>
        (value ?? "").Split([' ', ',', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>One relationship rule of the book: who speaks to whom, and the forms of address it sets.</summary>
public sealed record AdvancedEditingAddressRule(string Speaker, string Listener, IReadOnlySet<string> Terms);

/// <summary>The edited text to use (null: keep the draft), why, and how many paragraphs kept the draft's wording.</summary>
public sealed record AdvancedEditingOutcome(string? Text, string? Reason, int KeptParagraphs);

/// <summary>
/// "Biên tập nâng cao": a request that rewrites an AI draft into natural Vietnamese prose, with the Chinese source alongside
/// so meaning is checked, not guessed. Used after each translation when a book has Dịch nâng cao on, or on saved
/// translations by the Biên tập nâng cao command. The edit is used only when it keeps the draft's paragraphs and length,
/// and a paragraph where the editor brought in a form of address the draft did not use keeps the draft's wording: a
/// 2026-11 run turned "ngài" into "ông", "con" into "cháu" and narration into "huynh ấy" despite being told not to.
/// </summary>
public static partial class AdvancedEditing
{
    /// <summary>Opens every editing prompt; backends with their own translation instruction (Codex) skip it for these.</summary>
    public const string Marker = "【BIÊN TẬP NÂNG CAO】";

    // Pronouns and forms of address an editor must not bring into a paragraph. Words that are mostly something else
    // (ca, mình, họ, chúng) are left out; a false alarm only keeps one paragraph as the draft had it.
    private static readonly string[] AddressTerms =
    [
        "ta", "ngươi", "tôi", "tao", "mày", "đệ", "huynh", "muội", "tỷ", "ngài", "ông", "bà", "con", "cháu", "cô", "cậu",
        "chú", "bác", "dì", "anh", "em", "chị", "nàng", "hắn", "y", "gã", "ả", "lão", "chàng", "thiếp", "nô tỳ", "tiểu nhân",
        "tại hạ", "bổn cung", "trẫm", "thần", "nô tài", "lão phu", "bản tọa", "tiểu thư", "công tử", "cô nương", "phu nhân",
        "ca ca", "tỷ tỷ", "muội muội", "đệ đệ", "gia gia", "lão gia", "thiếu gia"
    ];
    // Not address, in any genre: kinship nouns (em gái, con trai, huynh trưởng), generic "người ta"/"chúng ta", and
    // compounds that only share a syllable (chăm chú, cô đơn, y thuật, con đường, anh hùng, ông trời, bà con).
    private static readonly Regex AddressPattern = new(
        @"(?<![\p{L}\p{M}])(?<!(?:chăm|người|chúng) )(" + string.Join("|", AddressTerms.OrderByDescending(t => t.Length).Select(Regex.Escape)) +
        @")(?![\p{L}\p{M}])(?! (?:gái|trai|trưởng|út|ruột|họ|cả|thứ|rể|dâu|đơn|độc|lập|thuật|phục|quán|đường|người|hùng|trời|con|số|bé)(?![\p{L}\p{M}]))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string BuildPrompt(string source, string draft, StorySetting setting, IReadOnlyCollection<string> names) =>
        BuildPrompt(source, draft, new AdvancedEditingContext(setting, names.ToArray(), [], new HashSet<string>()));

    public static string BuildPrompt(string source, string draft, AdvancedEditingContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(draft);
        int paragraphs = Paragraphs(draft).Count;
        var prompt = new StringBuilder();
        prompt.AppendLine(Marker);
        prompt.AppendLine("Bạn là biên tập viên văn học Trung → Việt. Bên dưới là nguyên văn tiếng Trung và bản dịch nháp tiếng Việt " +
            "của cùng một đoạn truyện. Hãy biên tập bản nháp thành văn tiếng Việt tự nhiên, trôi chảy, như tiểu thuyết do người Việt viết.");
        prompt.AppendLine("Yêu cầu:");
        prompt.AppendLine("1. Giữ đúng nghĩa và mọi chi tiết của nguyên văn; không thêm, không bớt, không tóm tắt. Nếu bản nháp dịch sai " +
            "hoặc sót ý rõ ràng so với nguyên văn thì sửa lại cho đúng.");
        prompt.AppendLine("2. XƯNG HÔ LÀ BẤT DI BẤT DỊCH: giữ đúng từng đại từ và từ xưng hô của bản nháp (ta, ngươi, huynh, muội, tỷ, ngài, " +
            "ông, con, cháu, nàng, hắn, y…). Không đổi từ này sang từ khác, không thêm xưng hô mới (ví dụ không biến \"ca ca\" hay tên " +
            "người thành \"huynh ấy\", không đổi \"ngài\" thành \"ông\", không gọi ai là \"tỷ\" nếu bản nháp không gọi). Được phép bỏ " +
            "một chủ ngữ lặp lại nếu câu vẫn rõ ai làm gì. Giữ nguyên tên riêng, thuật ngữ và danh xưng.");
        prompt.AppendLine("3. Sửa câu dịch sát chữ Hán: sắp lại trật tự từ theo tiếng Việt, bỏ chữ thừa, tách câu quá dài, " +
            "không lặp liên từ (thậm chí, lại còn…) chỉ vì nguyên văn lặp.");
        prompt.AppendLine("4. Thay từ Hán-Việt khó hiểu bằng từ tiếng Việt thông dụng cùng nghĩa (ví dụ: biểu di mẫu → dì họ, " +
            "phụ thân sinh thành → cha ruột) khi không làm mất sắc thái.");
        prompt.AppendLine(context.Setting switch
        {
            StorySetting.Historical => "5. Truyện cổ trang: giữ các danh xưng và xưng hô cổ quen thuộc (phụ thân, mẫu thân, cô nương, " +
                "công tử, ta, ngươi…), nhưng lời kể vẫn phải dễ đọc.",
            StorySetting.Modern => "5. Truyện hiện đại: lời kể và lời thoại dùng tiếng Việt đời thường, hạn chế từ Hán-Việt.",
            _ => "5. Giọng văn hợp với bối cảnh truyện; lời thoại tự nhiên như người thật nói."
        });
        prompt.AppendLine($"6. Giữ đúng {paragraphs} đoạn và thứ tự đoạn của bản nháp; mỗi đoạn trên một dòng riêng.");
        prompt.AppendLine("7. Chỉ trả về bản đã biên tập. Không lời dẫn, không giải thích, không Markdown, không chép lại nguyên văn.");
        if (context.Names.Count > 0)
            prompt.AppendLine("Tên và danh xưng phải giữ nguyên: " + string.Join(", ", context.Names.Take(80)) + ".");
        if (context.PronounLines.Count > 0)
            prompt.AppendLine("Đại từ lời kể của nhân vật (khi kể, chỉ dùng đúng đại từ này): " +
                string.Join("; ", context.PronounLines.Take(60)) + ".");
        prompt.AppendLine();
        prompt.AppendLine("【NGUYÊN VĂN】");
        prompt.AppendLine(source.Trim());
        prompt.AppendLine();
        prompt.AppendLine("【BẢN NHÁP】");
        prompt.Append(draft.Trim());
        return prompt.ToString();
    }

    public static bool IsEditingPrompt(string prompt) => prompt.StartsWith(Marker, StringComparison.Ordinal);

    /// <summary>The edited text when it is safe to use instead of the draft, otherwise null with the reason.</summary>
    public static string? Accept(string draft, string edited, out string? reason)
    {
        AdvancedEditingOutcome outcome = Review(draft, edited, AdvancedEditingContext.Empty);
        reason = outcome.Reason;
        return outcome.Text;
    }

    /// <summary>
    /// Checks the whole edit (no lost or merged paragraphs, length within 70–140 %, no Chinese the draft did not have),
    /// then each paragraph: one that uses a form of address absent from the same draft paragraph (other than a
    /// character's narrative pronoun) keeps the draft's wording.
    /// </summary>
    public static AdvancedEditingOutcome Review(string draft, string edited, AdvancedEditingContext context)
    {
        string text = Clean(edited);
        List<string> before = Paragraphs(draft), after = Paragraphs(text);
        string? reason = null;
        if (after.Count == 0) reason = "bản biên tập rỗng";
        else if (after.Count != before.Count) reason = $"bản biên tập có {after.Count} đoạn, bản nháp có {before.Count} đoạn";
        else
        {
            double ratio = (double)text.Length / Math.Max(1, draft.Trim().Length);
            if (ratio < 0.7 || ratio > 1.4) reason = $"độ dài bản biên tập lệch {ratio:P0} so với bản nháp";
            else if (HanCount(text) > HanCount(draft)) reason = "bản biên tập còn chữ Hán";
        }
        if (reason is not null) return new(null, reason, 0);
        int kept = 0;
        for (int i = 0; i < after.Count; i++)
        {
            if (!IntroducesAddress(before[i], after[i], context.NarrativePronouns, ProfileAddressNear(context, before, i),
                    context.FixedNames)) continue;
            after[i] = before[i];
            kept++;
        }
        if (kept == after.Count) return new(null, "mọi đoạn biên tập đều đổi xưng hô", kept);
        // Paragraphs keep the draft's spacing (a blank line between them, or none).
        string separator = Regex.IsMatch(draft, @"\S[ \t]*(\r\n|\r|\n)[ \t]*(\r\n|\r|\n)\s*\S")
            ? Environment.NewLine + Environment.NewLine : Environment.NewLine;
        return new(string.Join(separator, after), null, kept);
    }

    /// <summary>Address terms of a profile rule ("Tĩnh Huy cô nương" → "cô nương").</summary>
    internal static IEnumerable<string> AddressTermsIn(string text) =>
        AddressPattern.Matches(text ?? "").Select(match => match.Value.ToLowerInvariant()).Distinct();

    /// <summary>
    /// The profile's forms of address for the characters named in this paragraph or the three before it (a reply
    /// often names nobody). A rule that A Xuân says "muội" to Thẩm Duy Trinh lets the editor turn a draft's modern
    /// "Em" into "Muội" there, while "tỷ" for a maid no rule mentions stays blocked.
    /// </summary>
    private static HashSet<string> ProfileAddressNear(AdvancedEditingContext context, List<string> draftParagraphs, int index)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (context.AddressRules is not { Count: > 0 } rules) return allowed;
        string window = string.Join("\n", draftParagraphs.Skip(Math.Max(0, index - 3)).Take(Math.Min(index, 3) + 1));
        // Both characters of the rule must be around. With one enough, a run on 花中娇客 let Thẩm Duy Trinh's "ngươi"
        // (a rule for someone else) replace the "muội" he says to A Xuân.
        foreach (AdvancedEditingAddressRule rule in rules)
            if (window.Contains(rule.Speaker, StringComparison.OrdinalIgnoreCase) &&
                window.Contains(rule.Listener, StringComparison.OrdinalIgnoreCase))
                allowed.UnionWith(rule.Terms);
        return allowed;
    }

    /// <summary>The edited paragraph uses a form of address the draft paragraph did not use at all.</summary>
    internal static bool IntroducesAddress(string draft, string edited, IReadOnlySet<string> narrativePronouns,
        IReadOnlySet<string>? profileAddress = null, IReadOnlyList<string>? fixedNames = null)
    {
        // Names and glossary renderings are never forms of address, even when they contain one (Thẩm Lâm Anh, Đàm phu nhân).
        draft = Mask(draft, fixedNames);
        edited = Mask(edited, fixedNames);
        // "tỷ tỷ" in the draft also covers a plain "tỷ" in the edit.
        HashSet<string> had = AddressPattern.Matches(draft).Select(match => match.Value.ToLowerInvariant())
            .SelectMany(term => term.Split(' ').Append(term)).ToHashSet();
        // In narration only a narrative pronoun is free; inside a quote, so is a form of address the profile sets for
        // the characters around (never the other way: a "huynh ấy" in narration stays blocked). That only repairs a
        // draft term no rule backs ("Em" → "Muội"): when the edit drops a term a rule does back ("Muội" → "Ngươi"),
        // the draft was already right and nothing new is allowed.
        HashSet<string> kept = AddressPattern.Matches(edited).Select(match => match.Value.ToLowerInvariant())
            .SelectMany(term => term.Split(' ').Append(term)).ToHashSet();
        if (profileAddress is not null && AddressPattern.Matches(draft).Select(match => match.Value.ToLowerInvariant())
                .Any(term => !kept.Contains(term) && profileAddress.Contains(term)))
            profileAddress = null;
        (int Start, int End)[] quotes = PronounResolutionEngine.DialogueRanges(edited).ToArray();
        return AddressPattern.Matches(edited).Any(match =>
        {
            string term = match.Value.ToLowerInvariant();
            if (had.Contains(term)) return false;
            bool quoted = quotes.Any(range => match.Index >= range.Start && match.Index < range.End);
            return quoted ? profileAddress?.Contains(term) != true : !narrativePronouns.Contains(term);
        });
    }

    /// <summary>Blanks the names out with same-length filler, so positions (dialogue ranges) stay where they were.</summary>
    private static string Mask(string text, IReadOnlyList<string>? names)
    {
        if (names is not { Count: > 0 }) return text;
        foreach (string name in names)
        {
            if (name.Length < 2) continue;
            int index = 0;
            while ((index = text.IndexOf(name, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                text = string.Concat(text.AsSpan(0, index), new string('§', name.Length), text.AsSpan(index + name.Length));
                index += name.Length;
            }
        }
        return text;
    }

    private static string Clean(string text)
    {
        string value = text.Trim();
        if (value.StartsWith("```", StringComparison.Ordinal))
        {
            int firstLine = value.IndexOf('\n');
            int lastFence = value.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine) value = value[(firstLine + 1)..lastFence].Trim();
        }
        // A model may echo the section label before its text.
        return Regex.Replace(value, @"^【[^】\n]{1,20}】\s*", "");
    }

    private static List<string> Paragraphs(string text) =>
        Regex.Split(text, @"\r\n|\r|\n").Select(line => line.Trim()).Where(line => line.Length > 0).ToList();

    private static int HanCount(string text) => text.Count(c => c is >= '一' and <= '鿿');
}
