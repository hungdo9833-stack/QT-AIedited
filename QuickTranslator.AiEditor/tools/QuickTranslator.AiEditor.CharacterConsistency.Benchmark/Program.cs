using System.Diagnostics;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

const int chapters = 300;
const int segmentsPerChapter = 10;
var promptBuilder = new TranslationPromptBuilder();
PromptPreset preset = PromptPreset.Default;

Console.WriteLine("Character Consistency Lock V1 - CPU/fake-provider benchmark");
Console.WriteLine($"Synthetic workload: {chapters} chapters x {segmentsPerChapter} segments; no Ollama/GPU request.");
foreach (int characterCount in new[] { 50, 200, 1_000 })
{
    CharacterProject project = CreateProject(characterCount);
    var selector = new CharacterConsistencyRuleSelector();
    string[] sources = Enumerable.Range(0, chapters * segmentsPerChapter)
        .Select(index => $"角色{index % characterCount:D4}推门而入。角色{(index + 1) % characterCount:D4}问：“你准备好了吗？”")
        .ToArray();
    _ = selector.Select(project, sources[0]);
    _ = promptBuilder.Build(sources[0], [], "", preset);

    long[] selectionTicks = new long[sources.Length];
    long[] enhancedBuildTicks = new long[sources.Length];
    long[] baselineBuildTicks = new long[sources.Length];
    long extraCharacters = 0;
    int baselineRequests = 0, lockRequests = 0;
    for (int index = 0; index < sources.Length; index++)
    {
        string source = sources[index];
        long start = Stopwatch.GetTimestamp();
        string baseline = promptBuilder.Build(source, [], "", preset);
        baselineBuildTicks[index] = Stopwatch.GetTimestamp() - start;
        baselineRequests++;

        start = Stopwatch.GetTimestamp();
        CharacterConsistencySelection selected = selector.Select(project, source);
        selectionTicks[index] = Stopwatch.GetTimestamp() - start;
        start = Stopwatch.GetTimestamp();
        string enhanced = promptBuilder.Build(source, [], "", preset, selected.PromptBlock);
        enhancedBuildTicks[index] = Stopwatch.GetTimestamp() - start;
        lockRequests++;
        extraCharacters += enhanced.Length - baseline.Length;
    }

    Console.WriteLine($"characters={characterCount}; selection p50={Micros(Percentile(selectionTicks, .50)):F2}us p95={Micros(Percentile(selectionTicks, .95)):F2}us; " +
        $"build baseline p50={Micros(Percentile(baselineBuildTicks, .50)):F2}us; enhanced p50={Micros(Percentile(enhancedBuildTicks, .50)):F2}us; " +
        $"avg prompt +chars={(double)extraCharacters / sources.Length:F1}; approx +tokens={(double)extraCharacters / sources.Length / 4:F1}; " +
        $"fake requests {baselineRequests}/{lockRequests}");
}

static CharacterProject CreateProject(int count)
{
    var profiles = Enumerable.Range(0, count).Select(index => new CharacterProfile
    {
        ChineseName = $"角色{index:D4}", VietnameseName = $"Nhân vật {index:D4}",
        PrimaryNarrativePronoun = index % 2 == 0 ? "hắn" : "nàng",
        SecondaryNarrativePronoun = index % 2 == 0 ? "hắn ta" : "nàng ấy",
        NarrativePronounLocked = true, DisallowedNarrativePronouns = ["người ấy"]
    }).ToArray();
    for (int index = 0; index + 1 < profiles.Length; index += 2)
        profiles[index].Relationships.Add(new CharacterRelationship
        {
            TargetCharacterId = profiles[index + 1].Id, SpeakerCharacterId = profiles[index].Id,
            ListenerCharacterId = profiles[index + 1].Id, Relationship = "synthetic",
            SelfReference = "ta", AddresseeReference = "ngươi", Enabled = true, Locked = true
        });
    return new CharacterProject { Name = "Synthetic benchmark", ConsistencyLockEnabled = true,
        UpdatedAt = DateTimeOffset.UnixEpoch, Profiles = [.. profiles] };
}

static long Percentile(long[] values, double percentile)
{
    long[] copy = [.. values]; Array.Sort(copy);
    return copy[(int)Math.Clamp(Math.Ceiling(percentile * copy.Length) - 1, 0, copy.Length - 1)];
}

static double Micros(long ticks) => ticks * 1_000_000d / Stopwatch.Frequency;
