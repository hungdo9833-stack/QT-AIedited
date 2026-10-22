using System.Windows;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Wpf;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class GeminiCredentialSettingsTests
{
    [Fact]
    public Task No_saved_key_starts_in_entry_mode() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        var store = new MemoryKeyStore();
        Configure(fixture, store);

        Assert.Equal(Visibility.Visible, Element(fixture, "GeminiApiKeyInput").Visibility);
        Assert.Equal(Visibility.Collapsed, Element(fixture, "GeminiSavedKeyPlaceholder").Visibility);
        Assert.True(GetButton(fixture, "GeminiSaveApiKeyButton").IsEnabled);
        Assert.False(GetButton(fixture, "GeminiEditApiKeyButton").IsEnabled);
        Assert.Equal(Visibility.Collapsed, GetButton(fixture, "GeminiCancelApiKeyEditButton").Visibility);
        Assert.Equal(Visibility.Collapsed, GetButton(fixture, "GeminiRemoveApiKeyButton").Visibility);
        Assert.Contains("Chưa lưu Gemini API key", Status(fixture).Text, StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Saved_key_is_masked_and_edit_can_be_cancelled_without_exposing_it() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        var store = new MemoryKeyStore { Value = "saved-secret-must-not-appear" };
        Configure(fixture, store);

        var input = (PasswordBox)Element(fixture, "GeminiApiKeyInput");
        var placeholder = (TextBox)Element(fixture, "GeminiSavedKeyPlaceholder");
        Assert.Equal(Visibility.Collapsed, input.Visibility);
        Assert.Equal(string.Empty, input.Password);
        Assert.Equal(Visibility.Visible, placeholder.Visibility);
        Assert.True(placeholder.IsReadOnly);
        Assert.DoesNotContain(store.Value, placeholder.Text, StringComparison.Ordinal);
        Assert.False(GetButton(fixture, "GeminiSaveApiKeyButton").IsEnabled);
        Assert.True(GetButton(fixture, "GeminiEditApiKeyButton").IsEnabled);

        Click(fixture, "GeminiEditApiKeyButton");

        Assert.Equal(Visibility.Visible, input.Visibility);
        Assert.Equal(string.Empty, input.Password);
        Assert.True(GetButton(fixture, "GeminiSaveApiKeyButton").IsEnabled);
        Assert.False(GetButton(fixture, "GeminiEditApiKeyButton").IsEnabled);
        Assert.Equal(Visibility.Visible, GetButton(fixture, "GeminiCancelApiKeyEditButton").Visibility);
        Assert.Equal(Visibility.Visible, GetButton(fixture, "GeminiRemoveApiKeyButton").Visibility);
        Assert.Contains("Đang sửa API key", Status(fixture).Text, StringComparison.Ordinal);

        input.Password = "unsaved-replacement";
        Click(fixture, "GeminiCancelApiKeyEditButton");

        Assert.Equal("saved-secret-must-not-appear", store.Value);
        Assert.Equal(string.Empty, input.Password);
        Assert.Equal(Visibility.Collapsed, input.Visibility);
        Assert.Equal(Visibility.Visible, placeholder.Visibility);
        Assert.False(GetButton(fixture, "GeminiSaveApiKeyButton").IsEnabled);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Empty_edit_keeps_the_saved_key_and_a_nonempty_edit_replaces_it() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        var store = new MemoryKeyStore { Value = "old-secret" };
        Configure(fixture, store);
        Click(fixture, "GeminiEditApiKeyButton");

        Click(fixture, "GeminiSaveApiKeyButton");

        Assert.Equal("old-secret", store.Value);
        Assert.Equal(0, store.SaveCalls);
        Assert.Equal(Visibility.Visible, Element(fixture, "GeminiApiKeyInput").Visibility);
        Assert.Contains("vẫn được giữ nguyên", Status(fixture).Text, StringComparison.Ordinal);

        ((PasswordBox)Element(fixture, "GeminiApiKeyInput")).Password = "new-secret";
        Click(fixture, "GeminiSaveApiKeyButton");

        Assert.Equal("new-secret", store.Value);
        Assert.Equal(1, store.SaveCalls);
        Assert.Equal(string.Empty, ((PasswordBox)Element(fixture, "GeminiApiKeyInput")).Password);
        Assert.Equal(Visibility.Visible, Element(fixture, "GeminiSavedKeyPlaceholder").Visibility);
        Assert.DoesNotContain("new-secret", ((TextBox)Element(fixture, "GeminiSavedKeyPlaceholder")).Text,
            StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Delete_is_only_available_while_editing_and_still_requires_confirmation() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        var store = new MemoryKeyStore { Value = "saved-secret" };
        Configure(fixture, store);
        Click(fixture, "GeminiEditApiKeyButton");
        ThemedDialog.Responder = _ => MessageBoxResult.No;
        try
        {
            Click(fixture, "GeminiRemoveApiKeyButton");
            Assert.Equal("saved-secret", store.Value);
            Assert.Equal(0, store.DeleteCalls);

            ThemedDialog.Responder = _ => MessageBoxResult.Yes;
            Click(fixture, "GeminiRemoveApiKeyButton");

            Assert.Null(store.Value);
            Assert.Equal(1, store.DeleteCalls);
            Assert.Equal(Visibility.Visible, Element(fixture, "GeminiApiKeyInput").Visibility);
            Assert.True(GetButton(fixture, "GeminiSaveApiKeyButton").IsEnabled);
            Assert.False(GetButton(fixture, "GeminiEditApiKeyButton").IsEnabled);
            Assert.Equal(Visibility.Collapsed, GetButton(fixture, "GeminiRemoveApiKeyButton").Visibility);
        }
        finally
        {
            ThemedDialog.Responder = null;
        }

        return Task.CompletedTask;
    });

    private static void Configure(WorkspaceFixture fixture, MemoryKeyStore store)
    {
        fixture.Window.GeminiApiKeyReader = store.Read;
        fixture.Window.GeminiApiKeySaver = store.Save;
        fixture.Window.GeminiApiKeyDeleter = store.Delete;
        fixture.Call("RefreshGeminiCredentialStatus");
    }

    private static FrameworkElement Element(WorkspaceFixture fixture, string name) =>
        (FrameworkElement)fixture.Window.FindName(name);

    private static Button GetButton(WorkspaceFixture fixture, string name) =>
        (Button)fixture.Window.FindName(name);

    private static TextBlock Status(WorkspaceFixture fixture) =>
        (TextBlock)fixture.Window.FindName("GeminiCredentialStatus");

    private static void Click(WorkspaceFixture fixture, string name) =>
        GetButton(fixture, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private sealed class MemoryKeyStore
    {
        public string? Value { get; set; }
        public int SaveCalls { get; private set; }
        public int DeleteCalls { get; private set; }

        public string? Read() => Value;

        public void Save(string apiKey)
        {
            SaveCalls++;
            Value = apiKey;
        }

        public void Delete()
        {
            DeleteCalls++;
            Value = null;
        }
    }
}
