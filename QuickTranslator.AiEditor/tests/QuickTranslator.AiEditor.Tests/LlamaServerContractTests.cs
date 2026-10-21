using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class LlamaServerContractTests
{
    [Theory]
    [InlineData("hf.co/IndexTeam/Index-Translate-9B-GGUF:Q4_K_M")]
    [InlineData("IndexTeam/Index-Translate-9B-GGUF:Q4_K_M")]
    public void Hugging_face_forms_parse_to_the_same_reference(string text)
    {
        var model = GgufModelReference.Parse(text);
        Assert.Equal("IndexTeam/Index-Translate-9B-GGUF:Q4_K_M", model.ToString());
        Assert.Equal(["-hf", "IndexTeam/Index-Translate-9B-GGUF:Q4_K_M"], model.ServerArguments());
        Assert.Equal("Index-Translate-9B-Q4_K_M", model.DisplayName);
    }

    [Fact]
    public void Gguf_file_path_loads_with_m()
    {
        var model = GgufModelReference.Parse(@"D:\models\index.gguf");
        Assert.Equal(["-m", @"D:\models\index.gguf"], model.ServerArguments());
        Assert.False(model.IsHuggingFace);
    }

    [Theory]
    [InlineData("translategemma:12b")]
    [InlineData("index.gguf")]
    [InlineData("owner/repo")]
    public void Ollama_tags_relative_files_and_missing_quant_are_rejected(string text) =>
        Assert.Throws<ArgumentException>(() => GgufModelReference.Parse(text));

    [Fact]
    public void Launch_splits_total_context_across_slots_and_binds_loopback()
    {
        var launch = new LlamaServerLaunch(GgufModelReference.Parse("IndexTeam/Index-Translate-9B-GGUF:Q4_K_M"), 11436, 4, 8192);
        IReadOnlyList<string> args = launch.Arguments();
        Assert.Equal("32768", args[args.ToList().IndexOf("-c") + 1]);
        Assert.Equal("4", args[args.ToList().IndexOf("-np") + 1]);
        Assert.Equal("127.0.0.1", args[args.ToList().IndexOf("--host") + 1]);
        Assert.Contains("--no-mmproj", args);
        Assert.Throws<ArgumentOutOfRangeException>(() => (launch with { Slots = 9 }).Arguments());
    }
}
