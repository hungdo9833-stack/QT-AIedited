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
        Assert.Equal(4, result.Warnings.Count); // 3 dropped entries + no book.json
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
        Assert.Equal(3, result.Warnings.Count); // 2 modernized + no book.json
        result.Characters.Validate();
    }

    [Fact]
    public void Convert_BookFileRenamesTheProjectAndKeepsAtMostFiveGenres()
    {
        const string book = """
            { "vietnameseTitle": " Vợ Tôi Là Đóa Hoa Công Sở ", "synopsis": "Một câu móc câu.\n\nĐoạn hai.",
              "genres": ["Đô thị", "Sắc", "đô thị", "", "Hiện đại", "NTR", "Hài hước", "Ngôn tình"] }
            """;
        CodexSurveyImportResult result = CodexSurveyImport.Convert(Characters, "[]", Guid.NewGuid(), "人妻", book);

        CodexBookInfo info = Assert.IsType<CodexBookInfo>(result.Book);
        Assert.Equal("Vợ Tôi Là Đóa Hoa Công Sở", info.VietnameseTitle);
        Assert.Equal("Một câu móc câu.\n\nĐoạn hai.", info.Synopsis);
        Assert.Equal(["Đô thị", "Sắc", "Hiện đại", "NTR", "Hài hước"], info.Genres);
        Assert.Equal("Vợ Tôi Là Đóa Hoa Công Sở", result.Characters.Name);

        var manifest = new QuickTranslator.AiEditor.Core.Projects.ProjectManifest { VietnameseTitle = "人妻", ChineseTitle = "" };
        AutoBookPreparer.ApplyBookInfo(manifest, info);
        Assert.Equal(("Vợ Tôi Là Đóa Hoa Công Sở", "人妻"), (manifest.VietnameseTitle, manifest.ChineseTitle));
        Assert.Equal(info.Synopsis, manifest.Description);
        Assert.Equal(info.Genres, manifest.Genres);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{ broken")]
    [InlineData("""{ "vietnameseTitle": "", "genres": ["Đô thị"] }""")]
    public void Convert_UnusableBookFileKeepsCharactersAndOnlyWarns(string? book)
    {
        CodexSurveyImportResult result = CodexSurveyImport.Convert(Characters, "[]", Guid.NewGuid(), "人妻", book);
        Assert.Null(result.Book);
        Assert.Equal(3, result.Characters.Profiles.Count);
        Assert.Equal("人妻", result.Characters.Name);
        Assert.Contains(result.Warnings, warning => warning.Contains("book.json"));
    }

    [Fact]
    public void Convert_KeepsTitleFormsAndSurnamesButDropsTermsThatWouldLeaveChinese()
    {
        CodexSurveyImportResult result = CodexSurveyImport.Convert(Characters,
            """[{"chinese":"覃太太","vietnamese":"Đàm phu nhân"},{"chinese":"覃","vietnamese":"Đàm"},""" +
            """{"chinese":"覃先生","vietnamese":"Đàm 先生"},{"chinese":"Mr Tan","vietnamese":"Đàm tiên sinh"}]""",
            Guid.NewGuid(), "x");

        Assert.Equal([new ConfirmedTerm("覃太太", "Đàm phu nhân"), new ConfirmedTerm("覃", "Đàm")], result.Terms);
        Assert.Equal(2, result.Warnings.Count(warning => warning.StartsWith("Bỏ mục từ điển", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Convert_TitleNamedCharacterMatchesTheGlossaryAndSavesIntoAProject()
    {
        // Live case: the husband has no given name, so Codex named him by his title in both files with different casing.
        const string characters = """
            { "setting": "Modern", "characters": [
              { "chineseName": "覃先生", "vietnameseName": "Đàm Tiên Sinh", "gender": "Male", "narrativePronoun": "anh" },
              { "chineseName": "蔓露", "vietnameseName": "Mạn Lộ", "gender": "Female", "narrativePronoun": "cô" },
              { "chineseName": "王经理", "vietnameseName": "Mạn Lộ", "gender": "Male" } ] }
            """;
        const string glossary = """
            [ {"chinese":"覃先生","vietnamese":"Đàm tiên sinh"}, {"chinese":"覃","vietnamese":"Đàm"},
              {"chinese":"蔓露","vietnamese":"Tiểu Mạn"} ]
            """;
        string root = Path.Combine(Path.GetTempPath(), "qt-codex-save-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new QuickTranslator.AiEditor.Core.Projects.ProjectRepository();
            var session = await repository.CreateAsync(root, "Thử");
            CodexSurveyImportResult result = CodexSurveyImport.Convert(characters, glossary, session.Manifest.ProjectId, "Thử");

            Assert.Equal("Đàm tiên sinh", result.Characters.Profiles[0].VietnameseName);
            // "Tiểu Mạn" for 蔓露 would clash with her name: the term goes, the name stays.
            Assert.Equal("Mạn Lộ", result.Characters.Profiles[1].VietnameseName);
            Assert.DoesNotContain(result.Terms, t => t.Chinese == "蔓露");

            var store = new QuickTranslator.AiEditor.Core.Projects.ProjectResourceStore(session);
            var resources = await store.LoadAsync();
            resources.Characters = result.Characters;
            resources.Terms = result.Terms.ToList();
            var saved = await store.SaveAsync(resources);
            Assert.Contains(new ConfirmedTerm("覃先生", "Đàm tiên sinh"), saved.Terms);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
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
