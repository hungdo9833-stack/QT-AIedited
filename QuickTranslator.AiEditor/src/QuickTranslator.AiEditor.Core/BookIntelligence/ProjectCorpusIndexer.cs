using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed class ProjectCorpusIndexer(ProjectRepository repository)
{
    private const int WindowSize = 900;
    private static readonly Regex NameBeforeAction = new(
        @"(?<name>[\p{IsCJKUnifiedIdeographs}]{2,4})(?:说(?:道)?|问(?:道)?|答(?:道)?|喊(?:道)?|叫(?:道)?|看(?:向|着)|笑(?:道)?|皱眉|点头|摇头|走来|出现|进入)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IntroductionSignal = new(
        @"(?:名(?:叫|为)|姓|自称|本名|真名|化名|乃是|正是|原来(?:是)?|身份(?:是)?|其实(?:是)?)[：:\s“”""]*(?<name>[\p{IsCJKUnifiedIdeographs}]{2,6})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RelationshipSignal = new(
        @"(?:亲生|养父|养母|生父|生母|父亲|母亲|爹|娘|哥哥|姐姐|弟弟|妹妹|兄妹|姐弟|同父|同母|异父|异母|夫妻|夫君|娘子|妻子|丈夫|未婚|师父|师尊|徒儿|徒弟|主子|主人|少爷|小姐|陛下|殿下|前辈|长老|宗主|掌门|上司|下属)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TermSignal = new(
        @"[\p{IsCJKUnifiedIdeographs}]{2,12}(?:宗|派|门|宫|阁|城|国|山|谷|家|族|功|诀|法|术|剑|刀|丹|药|阵|符|兽|殿|会|盟|境|期|阶)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DialogueSignal = new(
        "(?:^|[\\r\\n])\\s*[“\\\"「『].{2,400}?[”\\\"」』]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    public async Task<BookIntelligenceState> IndexAsync(ProjectSession session, BookIntelligenceState state,
        BookIntelligenceStore store, CharacterProject characters,
        IProgress<CorpusIndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(characters);
        if (state.ProjectId != session.Manifest.ProjectId) throw new InvalidOperationException("Chỉ mục thuộc Project khác.");

        ProjectChapterEntry[] active = session.Manifest.Chapters.Where(x => x.DeletedAt is null)
            .OrderBy(x => x.Number).ThenBy(x => x.ChapterId).ToArray();
        HashSet<Guid> activeIds = active.Select(x => x.ChapterId).ToHashSet();
        foreach (CorpusChapterIndex removed in state.Chapters.Where(x => !activeIds.Contains(x.ChapterId)))
            foreach (CorpusEvidence evidence in removed.Evidence) evidence.IsCurrent = false;

        int processed = 0, rescanned = 0;
        string[] knownNames = characters.Profiles.SelectMany(p => new[] { p.ChineseName }.Concat(p.Aliases))
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();

        foreach (ProjectChapterEntry entry in active)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChapterDocument chapter = (await repository.LoadChapterAsync(session, entry.ChapterId, cancellationToken)
                .ConfigureAwait(false)).Chapter;
            string source = chapter.OriginalText ?? "";
            string hash = Hash(source);
            CorpusChapterIndex? existing = state.Chapters.SingleOrDefault(x => x.ChapterId == entry.ChapterId);
            if (existing is null || !string.Equals(existing.SourceSha256, hash, StringComparison.Ordinal))
            {
                rescanned++;
                if (existing is not null)
                    foreach (CorpusEvidence evidence in existing.Evidence) evidence.IsCurrent = false;
                List<CorpusEvidence> current = ScanChapter(entry.ChapterId, source, hash, knownNames, state.LowPriorityAuditRate);
                if (existing is null)
                {
                    existing = new CorpusChapterIndex { ChapterId = entry.ChapterId };
                    state.Chapters.Add(existing);
                }
                existing.ChapterNumber = entry.Number;
                existing.SourceSha256 = hash;
                existing.SourceLengthUtf16 = source.Length;
                existing.ScannedAt = DateTimeOffset.UtcNow;
                existing.Evidence.AddRange(current);
                state.NextEvidenceIndex = 0;
                state.LastError = "";
                state = await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }
            else existing.ChapterNumber = entry.Number;
            processed++;
            progress?.Report(new(processed, active.Length, rescanned, state.Evidence.Count(x => x.IsCurrent)));
        }
        state.Validate();
        return state;
    }

    public static List<CorpusEvidence> ScanChapter(Guid chapterId, string source, string sourceHash,
        IReadOnlyCollection<string> knownNames, double auditRate = 0.15)
    {
        if (source.Length == 0) return [];
        var candidates = NameBeforeAction.Matches(source).Select(m => m.Groups["name"].Value)
            .Concat(IntroductionSignal.Matches(source).Select(m => m.Groups["name"].Value))
            .Where(IsUsefulCandidate).GroupBy(x => x, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var result = new List<CorpusEvidence>();
        int ordinal = 0;
        foreach ((int start, int length) in SourceWindows(source))
        {
            string excerpt = source.Substring(start, length);
            string[] known = knownNames.Where(x => excerpt.Contains(x, StringComparison.Ordinal)).ToArray();
            string[] names = candidates.Where(x => x.Value >= 2 && excerpt.Contains(x.Key, StringComparison.Ordinal))
                .Select(x => x.Key).ToArray();
            MatchCollection introduced = IntroductionSignal.Matches(excerpt);
            MatchCollection terms = TermSignal.Matches(excerpt);
            bool relation = RelationshipSignal.IsMatch(excerpt);
            bool dialogue = DialogueSignal.IsMatch(excerpt);
            EvidencePriority priority;
            string reason;
            string group;
            IntelligenceEvidenceKind kind;
            if (known.Length > 0 && (relation || dialogue))
            {
                priority = EvidencePriority.High; reason = "Known character with relationship/dialogue signal";
                group = relation ? "relationship" : "dialogue"; kind = relation ? IntelligenceEvidenceKind.Relationship : IntelligenceEvidenceKind.Character;
            }
            else if (introduced.Count > 0 || relation)
            {
                priority = EvidencePriority.High; reason = introduced.Count > 0 ? "Identity introduction or reveal" : "Kinship/social relationship signal";
                group = introduced.Count > 0 ? "identity" : "relationship"; kind = relation ? IntelligenceEvidenceKind.Relationship : IntelligenceEvidenceKind.Character;
            }
            else if (names.Length > 0 || known.Length > 0 || dialogue || terms.Count > 0)
            {
                priority = EvidencePriority.Medium;
                reason = names.Length > 0 ? "Repeated name candidate" : known.Length > 0 ? "Known character occurrence" :
                    dialogue ? "Dialogue structure requires semantic check" : "Story-specific term candidate";
                group = names.Length > 0 || known.Length > 0 ? "character" : dialogue ? "dialogue" : "glossary";
                kind = terms.Count > 0 && names.Length == 0 && known.Length == 0 ? IntelligenceEvidenceKind.Term : IntelligenceEvidenceKind.Mixed;
            }
            else
            {
                priority = EvidencePriority.Low; reason = "Coverage window without a strong heuristic signal";
                group = "low-coverage"; kind = IntelligenceEvidenceKind.Mixed;
            }
            var signals = known.Concat(names).Concat(introduced.Select(m => m.Groups["name"].Value))
                .Concat(terms.Select(m => m.Value)).Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal).Take(48).ToList();
            if (relation) signals.Add("relationship-signal");
            if (dialogue) signals.Add("dialogue-signal");
            bool sampled = priority == EvidencePriority.Low && IsAuditSample(chapterId, ordinal, start, auditRate);
            result.Add(new CorpusEvidence
            {
                ChapterId = chapterId, StartUtf16 = start, LengthUtf16 = length,
                SourceSha256 = sourceHash, Excerpt = excerpt, Kind = kind, Signals = signals,
                CandidateNames = known.Concat(names).Distinct(StringComparer.Ordinal).ToList(),
                Priority = priority, PriorityReason = reason, SemanticGroup = group, IsAuditSample = sampled
            });
            ordinal++;
        }
        return CoalesceEquivalent(result, source);
    }

    private static List<CorpusEvidence> CoalesceEquivalent(List<CorpusEvidence> input, string source)
    {
        var output = new List<CorpusEvidence>();
        foreach (CorpusEvidence item in input.OrderBy(x => x.StartUtf16))
        {
            CorpusEvidence? last = output.LastOrDefault();
            if (last is not null && last.Priority == item.Priority && last.SemanticGroup == item.SemanticGroup &&
                item.StartUtf16 <= last.StartUtf16 + last.LengthUtf16 &&
                Math.Max(last.StartUtf16 + last.LengthUtf16, item.StartUtf16 + item.LengthUtf16) - last.StartUtf16 <= 1400)
            {
                int end = Math.Max(last.StartUtf16 + last.LengthUtf16, item.StartUtf16 + item.LengthUtf16);
                last.LengthUtf16 = end - last.StartUtf16;
                last.Excerpt = source.Substring(last.StartUtf16, last.LengthUtf16);
                last.IsAuditSample |= item.IsAuditSample;
                last.Signals = last.Signals.Concat(item.Signals).Distinct(StringComparer.Ordinal).Take(64).ToList();
                last.CandidateNames = last.CandidateNames.Concat(item.CandidateNames).Distinct(StringComparer.Ordinal).ToList();
            }
            else output.Add(item);
        }
        return output;
    }

    private static IEnumerable<(int Start, int Length)> SourceWindows(string source)
    {
        int start = 0;
        while (start < source.Length)
        {
            int desired = Math.Min(source.Length, start + WindowSize);
            int end = desired;
            if (desired < source.Length)
            {
                int floor = start + WindowSize / 2;
                for (int i = desired; i >= floor; i--)
                    if (source[i - 1] is '\r' or '\n' or '。' or '！' or '？') { end = i; break; }
            }
            if (end <= start) end = Math.Min(source.Length, start + WindowSize);
            yield return (start, end - start);
            start = end;
        }
    }

    private static bool IsAuditSample(Guid chapterId, int ordinal, int start, double rate)
    {
        if (rate <= 0) return false;
        Span<byte> bytes = stackalloc byte[24];
        chapterId.TryWriteBytes(bytes); BitConverter.TryWriteBytes(bytes[16..20], ordinal); BitConverter.TryWriteBytes(bytes[20..24], start);
        uint value = BitConverter.ToUInt32(SHA256.HashData(bytes)[..4]);
        return value / (double)uint.MaxValue < rate;
    }

    private static bool IsUsefulCandidate(string value) => value.Length is >= 2 and <= 6 &&
        value is not "一个" and not "什么" and not "自己" and not "他们" and not "我们" and not "你们";
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
