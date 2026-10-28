using System.IO;
using System.Windows;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class VanCacPublishUiTests
{
    private const string Token = "vcp_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private sealed class MemoryCredentialStore : IGeminiCredentialStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public string? Read(string target) => Values.GetValueOrDefault(target);
        public void Write(string target, string value) => Values[target] = value;
        public void Delete(string target) => Values.Remove(target);
    }

    [Fact]
    public void Token_vault_accepts_only_publisher_tokens_and_masks_them()
    {
        var store = new MemoryCredentialStore();
        Assert.Null(VanCacTokenVault.Read(store));
        VanCacTokenVault.Save(store, "  " + Token + "\n");
        Assert.Equal(Token, store.Values[VanCacTokenVault.Target]);
        Assert.Equal(Token, VanCacTokenVault.Read(store));
        Assert.Throws<ArgumentException>(() => VanCacTokenVault.Save(store, "AIza-not-a-publisher-token"));
        Assert.Equal("vcp_…cdef", VanCacTokenVault.Mask(Token));
        store.Values[VanCacTokenVault.Target] = "garbage";
        Assert.Null(VanCacTokenVault.Read(store));
    }

    [Fact]
    public void Preferences_round_trip_and_never_contain_the_token()
    {
        string path = Path.Combine(Path.GetTempPath(), "qt-vancac-" + Guid.NewGuid().ToString("N"), "prefs.json");
        try
        {
            Guid project = Guid.NewGuid();
            var preferences = new VanCacPublisherPreferences { BaseUrl = "https://vancac.example", PublishImmediately = true };
            preferences.NovelByProject[project] = "novel-1";
            preferences.Save(path);
            VanCacPublisherPreferences loaded = VanCacPublisherPreferences.Load(path);
            Assert.Equal("https://vancac.example", loaded.BaseUrl);
            Assert.True(loaded.PublishImmediately);
            Assert.Equal("novel-1", loaded.NovelByProject[project]);
            Assert.DoesNotContain("vcp_", File.ReadAllText(path), StringComparison.Ordinal);
            File.WriteAllText(path, "{not json");
            Assert.Equal("", VanCacPublisherPreferences.Load(path).BaseUrl);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    [Fact]
    public Task Publication_offers_van_cac_upload_for_the_vietnamese_translation_only() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        await fixture.AddChapterAsync("Chương một", 1);
        var service = new ProjectPublicationService(fixture.Repository, fixture.Get<ProjectCatalog>("_projectCatalog"));
        var window = new ProjectPublicationWindow(fixture.Project, service);
        try
        {
            await window.LoadInspectionAsync();
            ((CheckBox)window.FindName("AllowIncompleteInput")).IsChecked = true;
            var format = (ComboBox)window.FindName("FormatInput");
            format.SelectedItem = format.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == "VanCac");
            var export = (Button)window.FindName("ExportButton");
            Assert.Equal("Đăng lên Sắc Viện…", export.Content);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("FileContentOptionsPanel")).Visibility);
            Assert.Contains("Sắc Viện", ((TextBlock)window.FindName("FormatDescriptionText")).Text);
            Assert.True(export.IsEnabled);

            var language = (ComboBox)window.FindName("LanguageInput");
            language.SelectedItem = language.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == "Chinese");
            await window.LoadInspectionAsync();
            Assert.False(export.IsEnabled);
            Assert.Contains("tiếng Việt", (string)export.ToolTip);

            format.SelectedItem = format.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == "Txt");
            Assert.Equal("Chọn nơi lưu và xuất", export.Content);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("FileContentOptionsPanel")).Visibility);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Upload_window_opens_disconnected_without_touching_real_settings() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        fixture.Project.Manifest.Description = "Giới thiệu truyện.";
        await fixture.AddChapterAsync("Chương một", 1);
        await fixture.AddChapterAsync("Chương hai", 2);
        var service = new ProjectPublicationService(fixture.Repository, fixture.Get<ProjectCatalog>("_projectCatalog"));
        ProjectPublicationInspection inspection = await service.InspectAsync(fixture.Project);
        string preferences = Path.Combine(fixture.Root, "vancac-prefs.json");
        int tokenReads = 0;
        var window = new VanCacPublishWindow(fixture.Project, service, inspection, allowIncomplete: true, preferences,
            () => { tokenReads++; return null; });
        try
        {
            window.Show();
            await Task.Yield();
            Assert.Contains("2 chương", ((TextBlock)window.FindName("SubtitleText")).Text);
            Assert.False(((Button)window.FindName("StartButton")).IsEnabled);
            Assert.False(((FrameworkElement)window.FindName("NovelSection")).IsEnabled);
            Assert.Equal("Giới thiệu truyện.", ((TextBox)window.FindName("NewDescriptionInput")).Text);
            Assert.True(tokenReads > 0);
            Assert.False(File.Exists(preferences), "opening the window must not write settings");

            ((TextBox)window.FindName("BaseUrlInput")).Text = "http://vancac.example";
            ((Button)window.FindName("ConnectButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Task.Yield();
            Assert.Contains("HTTPS", ((TextBlock)window.FindName("ConnectionText")).Text);
            Assert.False(File.Exists(preferences));
        }
        finally { window.Close(); }
    });
}
