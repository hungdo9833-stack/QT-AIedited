using System.Diagnostics;
using System.Text;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit.Abstractions;

namespace QuickTranslator.AiEditor.Tests;

public sealed class NarrativePronounEquivalenceTests(ITestOutputHelper output)
{
    [Fact]
    public void OptimizedTracker_MatchesFrozenAlgorithmOnVariedAndRandomChineseTexts()
    {
        CharacterProject project = Project();
        var old = new LegacyNarrativePronounTracker();
        var tracker = new NarrativePronounTracker();
        string[] varied =
        [
            "", "他她他们她们其他他人他乡他日他年他国吉他",
            "林风看着陈石。他回头。苏婉停下。她笑了。",
            "林风说：\"他看着‘她’。\"他走了。",
            "林风说：「他看着『她』。」他走了。",
            "林风说：“他看着「她」。她来了。”他走了。",
            "林风说：“他看着「她，\n陈石回头。他走了。",
            "林风说：“他。”\n苏婉说：‘她。’\n陈石说：\"他\"他走了。",
            "林风\n\n\n他。苏婉\r\n\r\n\r\n她。",
            "林风😀𠀀。他看见苏婉😀。她回头。",
            "阿风看着风。林风看着林。共同说道。他她。",
            "他风风风她。她他。",
            "林风" + new string('字', 399) + "他她",
            "林风" + new string('字', 397) + "\n他她",
            string.Concat(Enumerable.Repeat("林风她他陈石苏婉😀", 2000)),
            string.Concat(Enumerable.Repeat(string.Concat(project.Profiles.Select(p => p.ChineseName + "他她。")), 10))
        ];
        foreach (string text in varied) Assert.Equal(old.Scan(text, project), tracker.Scan(text, project));

        var random = new Random(185731);
        string[] tokens = ["他", "她", "他们", "她人", "其他", "吉他", "。", "！", "？", "!", "?", "…", "，", "\n", "\r\n",
            "\n\n\n", "「", "」", "“", "”", "『", "』", "\"", "‘", "’", "😀", "𠀀", "林风", "陈石", "苏婉", "阿风", "共同", "人物甲"];
        for (int sample = 0; sample < 300; sample++)
        {
            var text = new StringBuilder();
            for (int token = 0, count = random.Next(20, 700); token < count; token++)
                text.Append(random.Next(10) == 0 ? new string('字', random.Next(1, 450)) : tokens[random.Next(tokens.Length)]);
            Assert.Equal(old.Scan(text.ToString(), project), tracker.Scan(text.ToString(), project));
        }
        output.WriteLine($"Frozen vs optimized: {varied.Length + 300} varied/random texts; all reference records identical.");
    }

    [Fact]
    public void WindowAndOverlapBoundaries_MatchFrozenAlgorithm()
    {
        var project = Project();
        project.Profiles[0].Aliases.AddRange(["林他", "风石", "林风石"]);
        project.Profiles[2].Aliases.Add("苏她婉");
        var old = new LegacyNarrativePronounTracker();
        var tracker = new NarrativePronounTracker();
        for (int distance = 395; distance <= 405; distance++)
            for (int breaks = 0; breaks <= 4; breaks++)
            {
                string text = "林风。" + new string('字', distance) + new string('\n', breaks) + "他她。林他看着苏她婉。林风石，他回头。";
                Assert.Equal(old.Scan(text, project), tracker.Scan(text, project));
                text = "林风。" + new string('\n', breaks) + new string('字', distance) + "他她。";
                Assert.Equal(old.Scan(text, project), tracker.Scan(text, project));
            }
        Assert.Equal(old.Scan("他她", new CharacterProject()), tracker.Scan("他她", new CharacterProject()));
    }

    [Fact]
    public void SingleParagraphOf20000Characters_ScansWithinGenerousBound()
    {
        CharacterProject project = Project();
        string text = string.Concat(Enumerable.Repeat("林风他她陈石他苏婉她", 2000));
        Assert.Equal(20_000, text.Length);
        var old = new LegacyNarrativePronounTracker();
        var tracker = new NarrativePronounTracker();
        old.Scan("林风。他", project);
        tracker.Scan("林风。他", project);
        var watch = Stopwatch.StartNew();
        var expected = old.Scan(text, project);
        TimeSpan before = watch.Elapsed;
        watch.Restart();
        var actual = tracker.Scan(text, project);
        TimeSpan after = watch.Elapsed;
        Assert.Equal(expected, actual);
        // Deliberately generous for shared CI machines; the actual measured time is emitted for inspection.
        Assert.True(after < TimeSpan.FromSeconds(3), $"20,000 UTF-16 characters took {after.TotalMilliseconds:F1} ms.");
        output.WriteLine($"20,000 UTF-16 chars, {actual.Count} references: old {before.TotalMilliseconds:F2} ms; new {after.TotalMilliseconds:F2} ms.");
    }

    private static CharacterProject Project()
    {
        var project = new CharacterProject
        {
            Profiles =
            [
                new() { ChineseName = "林风", Gender = CharacterGender.Male, Aliases = ["阿风", " 林风 ", "风", "共同", "他风"] },
                new() { ChineseName = "陈石", Gender = CharacterGender.Male, Aliases = ["石", "共同"] },
                new() { ChineseName = "苏婉", Gender = CharacterGender.Female, Aliases = ["婉", "林"] }
            ]
        };
        for (int i = 0; i < 40; i++)
            project.Profiles.Add(new() { ChineseName = "人物" + (char)('甲' + i), Gender = i % 2 == 0 ? CharacterGender.Male : CharacterGender.Female });
        return project;
    }
}
