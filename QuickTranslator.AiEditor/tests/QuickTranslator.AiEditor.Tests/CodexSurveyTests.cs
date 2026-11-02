using QuickTranslator.AiEditor.Core.AutoTranslate;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Tests;

public sealed class CodexSurveyTests
{
    private const string Characters = """
        {
          "setting": "Historical",
          "characters": [
            { "chineseName": "林凡", "vietnameseName": "Lâm Phàm", "aliases": ["凡哥", "林凡", "凡哥"], "gender": "Male" },
            { "chineseName": "苏雪", "vietnameseName": "Tô Tuyết", "gender": "female", "narrativePronoun": "nàng" },
            { "chineseName": "路人", "vietnameseName": "", "gender": "Unknown" },
            { "chineseName": "林凡2", "vietnameseName": "lâm phàm" },
            { "chineseName": "老者", "vietnameseName": "Lão Giả", "gender": "Unknown" }
          ],
          "relationships": [
            { "speaker": "林凡", "listener": "苏雪", "relationship": "Sư huynh - Sư muội", "selfReference": "ta", "addresseeReference": "muội" },
            { "speaker": "苏雪", "listener": "Lâm Phàm", "relationship": "Sư muội - Sư huynh" },
            { "speaker": "林凡", "listener": "不存在", "selfReference": "ta", "addresseeReference": "ngươi" }
          ]
        }
        """;

    [Fact]
    public void Convert_BuildsValidProjectAndDropsUnusableEntries()
    {
        Guid projectId = Guid.NewGuid();
        CodexSurveyImportResult result = CodexSurveyImport.Convert(Characters,
            """[{"chinese":"青云宗","vietnamese":"Thanh Vân Tông"},{"chinese":"青云宗","vietnamese":"Trùng"},{"chinese":"","vietnamese":"x"}]""",
            projectId, "Truyện thử");

        CharacterProject project = result.Characters;
        Assert.Equal(projectId, project.Id);
        Assert.Equal(StorySetting.Historical, project.Setting);
        Assert.Equal(["Lâm Phàm", "Tô Tuyết", "Lão Giả"], project.Profiles.Select(p => p.VietnameseName));
        CharacterProfile lam = project.Profiles[0];
        Assert.Equal(["凡哥"], lam.Aliases);
        Assert.Equal("hắn", lam.PrimaryNarrativePronoun);
        Assert.True(lam.NormalizationEnabled);
        Assert.False(project.Profiles[2].NormalizationEnabled);

        CharacterRelationship directional = Assert.Single(lam.Relationships);
        Assert.Equal(project.Profiles[1].Id, directional.ListenerCharacterId);
        Assert.True(directional.Enabled);
        CharacterRelationship plain = Assert.Single(project.Profiles[1].Relationships);
        Assert.Equal(lam.Id, plain.TargetCharacterId);
        Assert.False(plain.Enabled);

        Assert.Equal([new ConfirmedTerm("青云宗", "Thanh Vân Tông")], result.Terms);
        Assert.Equal(3, result.Warnings.Count);
        project.Validate();
    }

    [Fact]
    public void Convert_ModernBookNeverNarratesWithArchaicPronouns()
    {
        const string modern = """
            {
              "setting": "Modern",
              "characters": [
                { "chineseName": "张忠", "vietnameseName": "Trương Trung", "gender": "Male", "narrativePronoun": "hắn", "secondaryPronoun": "ông ta", "avoidPronouns": ["anh"] },
                { "chineseName": "蔓蔓", "vietnameseName": "Mạn Lộ", "gender": "Female", "narrativePronoun": "nàng", "secondaryPronoun": "cô ta" },
                { "chineseName": "小浩", "vietnameseName": "Tiểu Hạo", "gender": "Male", "narrativePronoun": "cậu", "secondaryPronoun": "cậu ta" },
                { "chineseName": "王部长", "vietnameseName": "Vương Bộ Trưởng", "gender": "Male" }
              ]
            }
            """;
        CodexSurveyImportResult result = CodexSurveyImport.Convert(modern, "[]", Guid.NewGuid(), "Truyện hiện đại");

        CharacterProfile[] profiles = result.Characters.Profiles.ToArray();
        Assert.Equal(StorySetting.Modern, result.Characters.Setting);
        Assert.Equal(("ông", "ông ta"), (profiles[0].PrimaryNarrativePronoun, profiles[0].SecondaryNarrativePronoun));
        Assert.Contains("anh", profiles[0].DisallowedNarrativePronouns);
        Assert.Equal(("cô", "cô ta"), (profiles[1].PrimaryNarrativePronoun, profiles[1].SecondaryNarrativePronoun));
        Assert.Equal(("cậu", "cậu ta"), (profiles[2].PrimaryNarrativePronoun, profiles[2].SecondaryNarrativePronoun));
        Assert.Equal(("anh", "anh ta"), (profiles[3].PrimaryNarrativePronoun, profiles[3].SecondaryNarrativePronoun));
        Assert.All(profiles, profile =>
        {
            Assert.Contains("hắn", profile.DisallowedNarrativePronouns);
            Assert.Contains("nàng", profile.DisallowedNarrativePronouns);
            Assert.True(profile.NormalizationEnabled);
        });
        Assert.Equal(2, result.Warnings.Count);
        result.Characters.Validate();
    }

    [Fact]
    public void Convert_RejectsFilesThatAreNotJson()
    {
        Assert.Throws<InvalidDataException>(() => CodexSurveyImport.Convert("{ not json", "[]", Guid.NewGuid(), "x"));
        Assert.Throws<InvalidDataException>(() => CodexSurveyImport.Convert("{}", "{}", Guid.NewGuid(), "x"));
    }

    [Fact]
    public async Task Runner_AsksCodexToRepairUnreadableFilesOnce()
    {
        string work = Path.Combine(Path.GetTempPath(), "qt-codex-" + Guid.NewGuid().ToString("N"));
        var fake = new FakeCodex(work, ["{ broken", Characters]);
        try
        {
            CodexSurveyImportResult result = await new CodexSurveyRunner(new CodexSurveyOptions(), fake)
                .RunAsync("第1章 开始\n林凡说话。", work, Guid.NewGuid(), "Truyện thử");
            Assert.Equal(3, result.Characters.Profiles.Count);
            Assert.Equal(2, fake.Prompts.Count);
            Assert.Contains("raw.txt", fake.Prompts[0]);
            Assert.Contains("characters.json", fake.Prompts[1]);
            Assert.Equal("第1章 开始\n林凡说话。", File.ReadAllText(Path.Combine(work, "raw.txt")));
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    [Fact]
    public async Task Runner_DoesNotRetryWhenCodexItselfFails()
    {
        string work = Path.Combine(Path.GetTempPath(), "qt-codex-" + Guid.NewGuid().ToString("N"));
        var fake = new FakeCodex(work, [Characters]) { ExitCode = 1 };
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new CodexSurveyRunner(new CodexSurveyOptions(), fake).RunAsync("raw", work, Guid.NewGuid(), "x"));
            Assert.Contains("mã lỗi 1", error.Message);
            Assert.Single(fake.Prompts);
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    private sealed class FakeCodex(string work, IReadOnlyList<string> characterOutputs) : ICodexProcessRunner
    {
        public List<string> Prompts { get; } = [];
        public int ExitCode { get; init; }

        public Task<int> RunAsync(CodexRunRequest request, IProgress<string>? output, CancellationToken cancellationToken)
        {
            Assert.Equal(work, request.WorkDirectory);
            Prompts.Add(request.Prompt);
            File.WriteAllText(Path.Combine(work, CodexSurveyRunner.CharactersFile), characterOutputs[Prompts.Count - 1]);
            File.WriteAllText(Path.Combine(work, CodexSurveyRunner.GlossaryFile), "[]");
            File.WriteAllText(request.LastMessagePath, "xong");
            return Task.FromResult(ExitCode);
        }
    }
}
