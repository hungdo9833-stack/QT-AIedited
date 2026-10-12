using System.Text;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Traditional;

namespace QuickTranslator.AiEditor.Tests;

public sealed class TraditionalTranslationTests
{
    [Fact]
    public async Task VerifiedQt2020CasesUseAdditionAndSecondaryNamePriority()
    {
        using var data = TraditionalTestData.Create();
        var snapshot = await new TraditionalDataLoader().LoadAsync(data.Directory);
        var engine = new TraditionalTranslator();

        Assert.Equal("Ending song", engine.Translate("片尾曲", snapshot).VietPhraseText);
        Assert.Equal("Không biết có phải là", engine.Translate("不知道是不是", snapshot).VietPhraseText);
        Assert.Equal("Một bóng người", engine.Translate("一个人形", snapshot).VietPhraseText);
        Assert.Equal("Phòng Tốt", engine.Translate("房卒", snapshot).VietPhraseText);
    }

    [Fact]
    public async Task LoaderPreservesValuesWarnsAboutBadLinesAndNeverWritesData()
    {
        using var data = TraditionalTestData.Create();
        string file = Path.Combine(data.Directory, "VietPhrase2.txt");
        await File.AppendAllTextAsync(file, "\n坏=one=two", new UTF8Encoding(false));
        byte[] before = await File.ReadAllBytesAsync(file);

        var snapshot = await new TraditionalDataLoader().LoadAsync(data.Directory);

        Assert.Equal(before, await File.ReadAllBytesAsync(file));
        Assert.Contains(snapshot.Manifest.Diagnostics, item => item.File == "VietPhrase2.txt" && item.Line is not null);
        Assert.Equal("khoảng trắng  ", snapshot.VietPhrase["留白"]);
    }

    [Fact]
    public async Task MissingOptionalFilesDoNotPreventCoreTranslation()
    {
        using var data = TraditionalTestData.Create(includeOptional: false);
        var snapshot = await new TraditionalDataLoader().LoadAsync(data.Directory);
        var result = new TraditionalTranslator().Translate("房卒", snapshot);

        Assert.Equal("Phòng Tốt", result.VietPhraseText);
        Assert.Contains(snapshot.Manifest.Diagnostics, item => item.Severity == DictionaryDiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task MissingRequiredFileIsReportedPrecisely()
    {
        using var data = TraditionalTestData.Create();
        File.Delete(Path.Combine(data.Directory, "VietPhrase.txt"));

        var error = await Assert.ThrowsAsync<TraditionalDataException>(() => new TraditionalDataLoader().LoadAsync(data.Directory));

        Assert.Contains("VietPhrase.txt", error.Message);
        Assert.Contains(error.Diagnostics, item => item.Severity == DictionaryDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Utf16DictionaryIsRejectedInsteadOfSilentlyCorruptingChinese()
    {
        using var data = TraditionalTestData.Create();
        await File.WriteAllTextAsync(Path.Combine(data.Directory, "Names.txt"), "房卒=Phòng Tuất", Encoding.Unicode);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new TraditionalDataLoader().LoadAsync(data.Directory));

        Assert.Contains("UTF-16", error.Message);
    }

    [Fact]
    public async Task TraditionalPreferencesRoundTripOutsideChapterData()
    {
        string root = Path.Combine(Path.GetTempPath(), "qtaie-traditional-preferences", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "settings.json");
        try
        {
            var store = new TraditionalEditorPreferencesStore(path);
            var expected = new TraditionalEditorPreferences
            {
                DataDirectory = @"C:\QT\Data", PrioritizeNames = false, TranslationAlgorithm = 2,
                EnableAlignmentDiagnostics = true
            };
            await store.SaveAsync(expected);

            Assert.Equal(expected, await store.LoadAsync());
        }
        finally { if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LongChapterTranslationKeepsEverySourcePositionAndOrder()
    {
        using var data = TraditionalTestData.Create();
        var snapshot = await new TraditionalDataLoader().LoadAsync(data.Directory);
        string source = string.Concat(Enumerable.Repeat("片尾曲。房卒\r\n", 1_000));

        var result = new TraditionalTranslator().Translate(source, snapshot);

        Assert.True(source.Length > 5_000);
        Assert.Equal(source.Length, result.HanVietAlignment.Sum(item => item.Source.Length));
        Assert.Equal(source.Length, result.VietPhraseAlignment.Sum(item => item.Source.Length));
        Assert.Equal(1_000, Count(result.VietPhraseText, "Ending song"));
        Assert.Equal(1_000, Count(result.VietPhraseText, "Phòng Tốt"));
    }

    private static int Count(string text, string value)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0) { count++; index += value.Length; }
        return count;
    }
}

internal sealed class TraditionalTestData : IDisposable
{
    private TraditionalTestData(string directory) => Directory = directory;
    public string Directory { get; }

    public static TraditionalTestData Create(bool includeOptional = true)
    {
        string root = Path.Combine(Path.GetTempPath(), "qtaie-traditional-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        Write("Dictionaries.ini", """
            Names=Names.txt
            NamesPhu=Names2.txt
            VietPhrase=VietPhrase.txt
            VietPhraseAddition=VietPhrase2.txt
            ChinesePhienAmWords=ChinesePhienAmWords.txt
            Pronouns=Pronouns.txt
            LuatNhan=LuatNhan.txt
            Mark=Mark.txt
            ThuatToanNhan=3
            CumTuToiDa=20
            """);
        Write("ChinesePhienAmWords.txt", "片=phiến\n尾=vĩ\n曲=khúc\n不=bất\n知=tri\n道=đạo\n是=thị\n一=nhất\n个=cá\n人=nhân\n形=hình\n房=phòng\n卒=tốt\n林=lâm\n悦=duyệt\n王=vương\n秀=tú\n兰=lan\n甲=giáp");
        Write("VietPhrase.txt", "片尾曲=ca khúc cuối\n不知道是不是=không biết có phải hay không là\n一个人形=một chình người\n林悦=rừng duyệt\n王秀兰=Vương Tú Lan\n留白=khoảng trắng  ");
        Write("VietPhrase2.txt", "片尾曲=ending song\n不知道是不是=không biết có phải là\n一个人形=một bóng người");
        Write("Names.txt", "房卒=Phòng Tuất");
        Write("Names2.txt", "房卒=Phòng Tốt");
        if (includeOptional)
        {
            Write("Pronouns.txt", "他=hắn");
            Write("LuatNhan.txt", "{0}们=các {0}");
            Write("Mark.txt", "，=, \n。=. \n：=: \n“=\"\n”=\" ");
        }
        return new(root);

        void Write(string relative, string content) => File.WriteAllText(Path.Combine(root, relative), content.ReplaceLineEndings("\n"), new UTF8Encoding(false));
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (IOException) { }
    }
}
