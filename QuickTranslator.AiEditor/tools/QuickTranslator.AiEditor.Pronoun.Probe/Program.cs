using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

string? projectPath = null;
string? characterName = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--project" when i + 1 < args.Length:
            projectPath = args[++i];
            break;
        case "--character" when i + 1 < args.Length:
            characterName = args[++i];
            break;
        default:
            return BadArguments();
    }
}

if (projectPath is null || !projectPath.EndsWith(".qtproj", StringComparison.OrdinalIgnoreCase) || !File.Exists(projectPath))
    return BadArguments();

projectPath = Path.GetFullPath(projectPath);
string sourceDirectory = Path.GetDirectoryName(projectPath)!;
string copiedDirectory = Path.Combine(Path.GetTempPath(), "qtstudio-pronoun-probe-" + Guid.NewGuid().ToString("N"));
try
{
    CopyDirectory(sourceDirectory, copiedDirectory);
    string copiedManifest = Path.Combine(copiedDirectory, Path.GetFileName(projectPath));
    var repository = new ProjectRepository();
    ProjectSession session = (await repository.OpenAsync(copiedManifest)).Session;
    ProjectResources resources = await new ProjectResourceStore(session).LoadAsync();
    CharacterProfile? requested = characterName is null ? null : resources.Characters.Profiles.SingleOrDefault(profile =>
        string.Equals(profile.ChineseName, characterName, StringComparison.Ordinal) ||
        profile.Aliases.Contains(characterName, StringComparer.Ordinal));
    if (characterName is not null && requested is null) return BadArguments();

    var engine = new PronounResolutionEngine();
    var tracker = new NarrativePronounTracker();
    var selector = new CharacterConsistencyRuleSelector();
    var beforeAll = new List<PronounSuggestion>();
    var all = new List<PronounSuggestion>();
    var agreementCandidates = new List<(PronounSuggestion Suggestion, string Previous)>();
    int newPromptRuleSegments = 0;
    int chapters = 0, translatedChapters = 0, segments = 0;
    foreach (ProjectChapterEntry entry in session.Manifest.Chapters.Where(x => x.DeletedAt is null).OrderBy(x => x.Number))
    {
        chapters++;
        ChapterDocument loaded = (await repository.LoadChapterAsync(session, entry.ChapterId)).Chapter;
        ChapterDocument chapter = resources.CreateWorkingChapter(loaded);
        IReadOnlyList<NarrativePronounReference> chapterReferences = tracker.Scan(chapter.OriginalText, chapter.CharacterProject);
        string previous = "";
        bool translated = false;
        foreach (TranslationSegment segment in chapter.Segments.OrderBy(x => x.Order))
        {
            IReadOnlyList<NarrativePronounReference> segmentReferences = tracker.ForSegment(
                chapter.OriginalText, segment, chapterReferences);
            Guid[] referencedIds = segmentReferences.Where(reference => reference.Confidence == NarrativePronounConfidence.High &&
                    reference.CharacterId is not null).Select(reference => reference.CharacterId!.Value).Distinct().ToArray();
            string sceneText = SceneText(chapter, segment);
            CharacterConsistencySelection withoutReferences = selector.Select(chapter.CharacterProject, segment.SourceText,
                chapter.ChapterNumber, sceneText);
            CharacterConsistencySelection withReferences = selector.Select(chapter.CharacterProject, segment.SourceText,
                chapter.ChapterNumber, sceneText, referencedIds);
            if (withReferences.CharacterIds.Except(withoutReferences.CharacterIds).Any()) newPromptRuleSegments++;

            string text = segment.EditableText;
            if (!string.IsNullOrWhiteSpace(text))
            {
                translated = true;
                segments++;
                PronounSuggestion[] before = engine.ScanSegment(segment.Id, segment.Order, segment.SourceText, text, previous,
                    chapter.CharacterProject, segment.IsProtectedFromAutomaticTranslation, chapter.ConfirmedTerms).ToArray();
                NarrativePronounHint? hint = tracker.HintForSegment(chapter.OriginalText, segment, chapterReferences);
                PronounSuggestion[] after = engine.ScanSegment(segment.Id, segment.Order, segment.SourceText, text, previous,
                    chapter.CharacterProject, segment.IsProtectedFromAutomaticTranslation, chapter.ConfirmedTerms, hint).ToArray();
                beforeAll.AddRange(before);
                all.AddRange(after);
                foreach (PronounSuggestion resolved in after.Where(item => item.CharacterId is not null &&
                             item.Kind is PronounSuggestionKind.SafeReplacement or PronounSuggestionKind.Valid))
                {
                    PronounSuggestion? old = before.FirstOrDefault(item => item.StartUtf16 == resolved.StartUtf16 &&
                        item.Length == resolved.Length && item.Kind == PronounSuggestionKind.Ambiguous);
                    if (old is not null) agreementCandidates.Add((resolved, previous));
                }
            }
            previous = text;
        }
        if (translated) translatedChapters++;
    }

    Console.WriteLine($"0 {chapters} {translatedChapters} {segments} {resources.Characters.Profiles.Count}");
    PrintCounts(10, beforeAll);
    PrintCounts(1, all);
    foreach ((CharacterProfile profile, int index) in resources.Characters.Profiles.OrderBy(x => x.Id).Select((value, index) => (value, index)))
        PrintCounts(1000 + index, ForProfile(all, profile));
    if (requested is not null)
    {
        PronounSuggestion[] beforeTarget = ForProfile(beforeAll, requested);
        PronounSuggestion[] target = ForProfile(all, requested);
        PrintCounts(11, beforeTarget);
        PrintCounts(2, target);
        int beforeDisallowedAmbiguous = beforeTarget.Count(item => item.Kind == PronounSuggestionKind.Ambiguous &&
            requested.DisallowedNarrativePronouns.Contains(item.Original, StringComparer.OrdinalIgnoreCase));
        int disallowedAmbiguous = target.Count(item => item.Kind == PronounSuggestionKind.Ambiguous &&
            requested.DisallowedNarrativePronouns.Contains(item.Original, StringComparer.OrdinalIgnoreCase));
        Console.WriteLine($"12 {beforeDisallowedAmbiguous}");
        Console.WriteLine($"3 {disallowedAmbiguous}");
    }
    var sample = agreementCandidates.Select(candidate => (candidate.Suggestion,
            Nearest: NearestVietnameseName(candidate.Suggestion, candidate.Previous, resources.Characters)))
        .Where(candidate => candidate.Nearest is not null).Take(30).ToArray();
    int agreements = sample.Count(candidate => candidate.Nearest == candidate.Suggestion.CharacterId);
    decimal percentage = sample.Length == 0 ? 0 : Math.Round(agreements * 100m / sample.Length, 2);
    Console.WriteLine($"4 {newPromptRuleSegments}");
    Console.WriteLine($"5 {sample.Length} {agreements} {percentage.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"1 {ex.HResult}");
    return 1;
}
finally
{
    if (IsProbeDirectory(copiedDirectory) && Directory.Exists(copiedDirectory))
        Directory.Delete(copiedDirectory, recursive: true);
}

static PronounSuggestion[] ForProfile(IEnumerable<PronounSuggestion> items, CharacterProfile profile) => items.Where(item =>
    item.CharacterId == profile.Id || item.CharacterId is null &&
    (profile.DisallowedNarrativePronouns.Contains(item.Original, StringComparer.OrdinalIgnoreCase) ||
     string.Equals(profile.PrimaryNarrativePronoun, item.Original, StringComparison.OrdinalIgnoreCase) ||
     string.Equals(profile.SecondaryNarrativePronoun, item.Original, StringComparison.OrdinalIgnoreCase))).ToArray();

static Guid? NearestVietnameseName(PronounSuggestion suggestion, string previous, CharacterProject project)
{
    string prefix = previous + "\n" + suggestion.AnalysedText[..suggestion.StartUtf16];
    int floor = ThreeSentenceFloor(prefix);
    CharacterGender resolvedGender = project.Profiles.Single(profile => profile.Id == suggestion.CharacterId).Gender;
    var owners = project.Profiles.Where(profile => profile.Gender == resolvedGender)
        .SelectMany(profile => new[] { profile.VietnameseName }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => (Name: name.Trim(), Profile: profile)))
        .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        .Where(group => group.Select(item => item.Profile.Id).Distinct().Count() == 1)
        .Select(group => group.First()).ToArray();
    (Guid Id, int Start)? nearest = null;
    foreach (var owner in owners)
    {
        int start = prefix.LastIndexOf(owner.Name, StringComparison.OrdinalIgnoreCase);
        if (start < floor || !Whole(prefix, start, owner.Name.Length)) continue;
        if (nearest is null || start > nearest.Value.Start) nearest = (owner.Profile.Id, start);
    }
    return nearest?.Id;
}

static int ThreeSentenceFloor(string text)
{
    var starts = new List<int>();
    int start = 0;
    for (int i = 0; i < text.Length; i++)
    {
        if (text[i] is not ('.' or '!' or '?' or '…' or '\n' or '\r')) continue;
        int end = i + 1;
        while (end < text.Length && text[end] is '.' or '!' or '?' or '…' or '\n' or '\r' or '”' or '\"' or '\'' or '’') end++;
        if (!string.IsNullOrWhiteSpace(text[start..end])) starts.Add(start);
        start = end;
        i = end - 1;
    }
    if (start < text.Length && !string.IsNullOrWhiteSpace(text[start..])) starts.Add(start);
    return starts.Count <= 3 ? 0 : starts[^3];
}

static bool Whole(string text, int start, int length) =>
    (start == 0 || !char.IsLetterOrDigit(text[start - 1])) &&
    (start + length == text.Length || !char.IsLetterOrDigit(text[start + length]));

static string SceneText(ChapterDocument chapter, TranslationSegment segment) => string.Join("\n",
    chapter.Segments.Where(item => item.Order < segment.Order).OrderByDescending(item => item.Order).Take(2)
        .OrderBy(item => item.Order).Select(item => item.SourceText));

static void PrintCounts(int key, IEnumerable<PronounSuggestion> source)
{
    PronounSuggestion[] items = source.ToArray();
    int[] kinds = Enum.GetValues<PronounSuggestionKind>().Select(kind => items.Count(item => item.Kind == kind)).ToArray();
    int[] reasons = Enumerable.Range(0, 8).Select(reason => items.Count(item => ReasonCategory(item.Reason) == reason)).ToArray();
    Console.WriteLine($"{key} {items.Length} {string.Join(' ', kinds)} {string.Join(' ', reasons)}");
}

static int ReasonCategory(string reason) => reason switch
{
    _ when reason.StartsWith("Không có tên nhân vật", StringComparison.Ordinal) => 0,
    _ when reason.StartsWith("Có nhiều nhân vật", StringComparison.Ordinal) => 1,
    _ when reason.StartsWith("Ngữ cảnh gần có tên riêng", StringComparison.Ordinal) => 2,
    _ when reason.StartsWith("Giữ nguyên đại từ", StringComparison.Ordinal) => 3,
    _ when reason.StartsWith("Giữ nguyên danh xưng", StringComparison.Ordinal) => 4,
    _ when reason.StartsWith("Đại từ đã phù hợp", StringComparison.Ordinal) => 5,
    _ when reason.StartsWith("Đoạn đã được", StringComparison.Ordinal) => 6,
    _ => 7
};

static int BadArguments()
{
    Console.Error.WriteLine("2");
    return 2;
}

static bool IsProbeDirectory(string path)
{
    string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
    string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    return string.Equals(Path.GetDirectoryName(candidate), parent, StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(candidate).StartsWith("qtstudio-pronoun-probe-", StringComparison.Ordinal);
}

static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        if (file.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) continue;
        string target = Path.Combine(destination, Path.GetRelativePath(source, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target);
    }
}
