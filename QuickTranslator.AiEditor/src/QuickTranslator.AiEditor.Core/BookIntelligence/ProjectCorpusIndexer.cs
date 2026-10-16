using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed class ProjectCorpusIndexer(ProjectRepository repository)
{
    private static readonly Regex NameBeforeAction = new(
        @"(?<name>[\p{IsCJKUnifiedIdeographs}]{2,4})(?:说(?:道)?|问(?:道)?|答(?:道)?|喊(?:道)?|叫(?:道)?|看(?:向|着)|笑(?:道)?|皱眉|点头|摇头)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AddressSignal = new(
        @"(?:父亲|母亲|哥哥|姐姐|弟弟|妹妹|师父|师尊|徒儿|夫君|娘子|陛下|殿下|主人|少爷|小姐|前辈|长老|宗主|掌门)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TermSignal = new(
        @"[\p{IsCJKUnifiedIdeographs}]{2,10}(?:宗|派|门|宫|阁|城|国|山|谷|功|诀|法|术|剑|刀|丹|药|阵|符|兽|族|殿|会|盟)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
                List<CorpusEvidence> current = ScanChapter(entry.ChapterId, source, hash, knownNames);
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
            progress?.Report(new(processed, active.Length, rescanned,
                state.Evidence.Count(x => x.IsCurrent)));
        }
        state.Validate();
        return state;
    }

    internal static List<CorpusEvidence> ScanChapter(Guid chapterId, string source, string sourceHash,
        IReadOnlyCollection<string> knownNames)
    {
        var result = new List<CorpusEvidence>();
        foreach ((int start, int length) in Lines(source))
        {
            string line = source.Substring(start, length);
            MatchCollection nameMatches = NameBeforeAction.Matches(line);
            bool hasKnown = knownNames.Any(name => line.Contains(name, StringComparison.Ordinal));
            bool relation = AddressSignal.IsMatch(line);
            MatchCollection terms = TermSignal.Matches(line);
            if (nameMatches.Count == 0 && !hasKnown && !relation && terms.Count == 0) continue;

            int contextStart = Math.Max(0, start - 240);
            int contextEnd = Math.Min(source.Length, start + length + 240);
            if (contextEnd - contextStart > 1200) contextEnd = contextStart + 1200;
            var signals = nameMatches.Select(m => m.Groups["name"].Value)
                .Concat(knownNames.Where(name => line.Contains(name, StringComparison.Ordinal)))
                .Concat(terms.Select(m => m.Value))
                .Distinct(StringComparer.Ordinal).Take(24).ToList();
            if (relation) signals.Add("relationship-signal");
            result.Add(new CorpusEvidence
            {
                ChapterId = chapterId,
                StartUtf16 = contextStart,
                LengthUtf16 = contextEnd - contextStart,
                SourceSha256 = sourceHash,
                Excerpt = source.Substring(contextStart, contextEnd - contextStart),
                Kind = relation ? IntelligenceEvidenceKind.Relationship :
                    terms.Count > 0 && nameMatches.Count == 0 && !hasKnown ? IntelligenceEvidenceKind.Term :
                    terms.Count > 0 ? IntelligenceEvidenceKind.Mixed : IntelligenceEvidenceKind.Character,
                Signals = signals
            });
        }
        return Coalesce(result);
    }

    private static List<CorpusEvidence> Coalesce(List<CorpusEvidence> input)
    {
        var output = new List<CorpusEvidence>();
        foreach (CorpusEvidence item in input.OrderBy(x => x.StartUtf16))
        {
            CorpusEvidence? last = output.LastOrDefault();
            if (last is not null && item.StartUtf16 <= last.StartUtf16 + last.LengthUtf16 &&
                Math.Max(last.StartUtf16 + last.LengthUtf16, item.StartUtf16 + item.LengthUtf16) - last.StartUtf16 <= 1600)
            {
                int end = Math.Max(last.StartUtf16 + last.LengthUtf16, item.StartUtf16 + item.LengthUtf16);
                string combined = last.Excerpt + item.Excerpt[Math.Max(0, last.StartUtf16 + last.LengthUtf16 - item.StartUtf16)..];
                last.LengthUtf16 = end - last.StartUtf16;
                last.Excerpt = combined;
                last.Kind = last.Kind == item.Kind ? last.Kind : IntelligenceEvidenceKind.Mixed;
                last.Signals = last.Signals.Concat(item.Signals).Distinct(StringComparer.Ordinal).Take(32).ToList();
            }
            else output.Add(item);
        }
        return output;
    }

    private static IEnumerable<(int Start, int Length)> Lines(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\r' && text[i] != '\n') continue;
            yield return (start, i - start);
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        if (start <= text.Length) yield return (start, text.Length - start);
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
