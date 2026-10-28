using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class GeminiCredentialSettingsTests
{
    [Fact]
    public Task No_saved_key_starts_in_multiline_entry_mode() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        var store = new MemoryKeyStore();
        Configure(fixture, store);

        var input = (TextBox)fixture.Window.FindName("GeminiApiKeyInput");
        Assert.True(input.AcceptsReturn);
        Assert.Empty(((ItemsControl)fixture.Window.FindName("GeminiSavedKeyList")).Items);
        Assert.Contains("Chưa lưu Gemini API key", Status(fixture).Text, StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Multiple_keys_are_saved_in_order_and_displayed_only_masked() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        var store = new MemoryKeyStore { Values = ["AIza-first-1111"] };
        Configure(fixture, store);
        var input = (TextBox)fixture.Window.FindName("GeminiApiKeyInput");
        input.Text = "AIza-second-2222\r\nAIza-third-3333";

        Click(fixture, "GeminiSaveApiKeyButton");

        Assert.Equal(["AIza-first-1111", "AIza-second-2222", "AIza-third-3333"], store.Values);
        Assert.Equal(string.Empty, input.Text);
        var list = (ItemsControl)fixture.Window.FindName("GeminiSavedKeyList");
        Assert.Equal(3, list.Items.Count);
        string shown = string.Join(" ", list.Items.Cast<object>());
        Assert.DoesNotContain("AIza-first-1111", shown, StringComparison.Ordinal);
        Assert.Contains("3 key", Status(fixture).Text, StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Removing_one_key_requires_confirmation_and_keeps_the_other_order() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        var store = new MemoryKeyStore { Values = ["AIza-a-1111", "AIza-b-2222", "AIza-c-3333"] };
        Configure(fixture, store);
        var button = new Button { Tag = 1 };
        ThemedDialog.Responder = _ => MessageBoxResult.No;
        try
        {
            fixture.Call("OnRemoveGeminiApiKey", button, new RoutedEventArgs());
            Assert.Equal(3, store.Values.Count);
            ThemedDialog.Responder = _ => MessageBoxResult.Yes;
            fixture.Call("OnRemoveGeminiApiKey", button, new RoutedEventArgs());
            Assert.Equal(["AIza-a-1111", "AIza-c-3333"], store.Values);
        }
        finally { ThemedDialog.Responder = null; }
        return Task.CompletedTask;
    });

    private static void Configure(WorkspaceFixture fixture, MemoryKeyStore store)
    {
        fixture.Window.GeminiApiKeysReader = store.Read;
        fixture.Window.GeminiApiKeysSaver = store.Save;
        fixture.Call("RefreshGeminiCredentialStatus");
    }

    private static TextBlock Status(WorkspaceFixture fixture) =>
        (TextBlock)fixture.Window.FindName("GeminiCredentialStatus");

    private static void Click(WorkspaceFixture fixture, string name) =>
        ((Button)fixture.Window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private sealed class MemoryKeyStore
    {
        public IReadOnlyList<string> Values { get; set; } = [];
        public IReadOnlyList<string> Read() => Values;
        public void Save(IReadOnlyList<string> values) => Values = values.ToArray();
    }
}
