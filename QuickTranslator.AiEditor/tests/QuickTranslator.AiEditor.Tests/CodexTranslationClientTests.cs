using QuickTranslator.AiEditor.Core.AutoTranslate;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class CodexTranslationClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qt-codex-translate-" + Guid.NewGuid().ToString("N"));
    private const string Prompt = "请将以下中文小说翻译成越南语。\n\n【源文】\n她笑了。\n\n【约束要求】\n1. 【硬性要求】逐句完整翻译\n\n只输出译文";

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public async Task The_last_message_is_the_translation_and_the_request_folder_is_removed()
    {
        var runner = new FakeRunner(0, "```\nNàng cười.\n```");
        var client = new CodexTranslationClient(new CodexSurveyOptions { Model = "gpt-6-luna" }, runner, _root);
        LlmCompletion result = await client.TranslateAsync(Prompt, new ModelOptions { Model = "codex:gpt-6-luna" });
        Assert.Equal("Nàng cười.", result.Text);
        CodexRunRequest request = Assert.Single(runner.Requests);
        Assert.StartsWith(CodexTranslationClient.Instruction, request.Prompt);
        Assert.EndsWith(Prompt, request.Prompt);
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task A_used_up_usage_window_is_a_quota_error_not_a_chapter_error()
    {
        var runner = new FakeRunner(1, null, "ERROR: You've hit your usage limit. Try again at 18:42.");
        var client = new CodexTranslationClient(new CodexSurveyOptions(), runner, _root);
        await Assert.ThrowsAsync<CodexTranslationQuotaException>(() => client.TranslateAsync(Prompt, new ModelOptions()));
    }

    [Fact]
    public async Task An_empty_answer_is_an_invalid_response()
    {
        var client = new CodexTranslationClient(new CodexSurveyOptions(), new FakeRunner(0, "  "), _root);
        LlmRequestException error = await Assert.ThrowsAsync<LlmRequestException>(() => client.TranslateAsync(Prompt, new ModelOptions()));
        Assert.Equal(LlmErrorKind.InvalidResponse, error.Kind);
    }

    private sealed class FakeRunner(int exit, string? lastMessage, string log = "") : ICodexProcessRunner
    {
        public List<CodexRunRequest> Requests { get; } = [];

        public Task<int> RunAsync(CodexRunRequest request, IProgress<string>? output, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Assert.True(Directory.Exists(request.WorkDirectory));
            if (lastMessage is not null) File.WriteAllText(request.LastMessagePath, lastMessage);
            if (log.Length > 0) output?.Report(log);
            return Task.FromResult(exit);
        }
    }
}
