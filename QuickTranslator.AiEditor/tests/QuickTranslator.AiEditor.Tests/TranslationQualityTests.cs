using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class TranslationQualityTests
{
    private readonly TranslationQualityChecker _checker = new();

    [Fact]
    public void EmptyOutput_IsAnError()
    {
        var issue = Assert.Single(_checker.Check("你好。", FakeClient.Completion(" \n"), new()));
        Assert.Equal(IssueCode.EmptyOutput, issue.Code);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public void LengthStop_OrOutputTokenLimit_IsFlaggedAsPossiblyTruncated()
    {
        var result = new LlmCompletion("Văn bản chưa kết thúc", true, "length", new() { OutputTokens = 1536 });
        Assert.Contains(_checker.Check("你好。", result, new()), i => i.Code == IssueCode.TruncatedOutput);
        var notDone = result with { Done = false, DoneReason = "stop", Metrics = new() { OutputTokens = 10 } };
        Assert.Contains(_checker.Check("你好。", notDone, new()), i => i.Code == IssueCode.TruncatedOutput);
    }

    [Fact]
    public void MissingContent_ChineseOutput_AndRepetition_AreWarnings()
    {
        string source = new('汉', 100);
        Assert.Contains(_checker.Check(source, FakeClient.Completion("Rất ngắn."), new()), i => i.Code == IssueCode.PossibleMissingContent);
        Assert.Contains(_checker.Check(source, FakeClient.Completion(source), new()), i => i.Code == IssueCode.UntranslatedChinese);
        Assert.Contains(_checker.Check("你好。", FakeClient.Completion("Anh bước đi anh bước đi anh bước đi anh bước đi."), new()),
            i => i.Code == IssueCode.SuspiciousRepetition);
        Assert.All(_checker.Check(source, FakeClient.Completion("Rất ngắn."), new()), i => Assert.Equal(IssueSeverity.Warning, i.Severity));
    }

    [Fact]
    public void Prompt_UsesOneTranslationTask_AndOptionalReferenceProviders()
    {
        string source = "  「你好！」\r\n再见。 ";
        var prompt = new TranslationPromptBuilder().Build(source,
            [new GlossaryEntry("李明", "Lý Minh")], "Xưng hô: ta — ngươi.");
        Assert.Contains("Chinese (zh)", prompt);
        Assert.Contains("Vietnamese (vi)", prompt);
        Assert.Contains("<source_text>" + Environment.NewLine + source + Environment.NewLine + "</source_text>", prompt);
        Assert.Contains("李明", prompt);
        Assert.Contains("Lý Minh", prompt);
        Assert.Contains("ta — ngươi", prompt);
    }
}
