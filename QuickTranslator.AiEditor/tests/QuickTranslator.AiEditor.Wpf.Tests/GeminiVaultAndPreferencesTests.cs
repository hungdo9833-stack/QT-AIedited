using System.IO;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class GeminiVaultAndPreferencesTests
{
    [Fact]
    public void Vault_keeps_order_and_removes_only_the_selected_key()
    {
        var store = new MemoryCredentialStore();
        GeminiApiKeyVault.SaveAll(store, ["AIza-one-1111", "AIza-two-2222", "AIza-three-3333"]);

        Assert.Equal(["AIza-one-1111", "AIza-two-2222", "AIza-three-3333"],
            GeminiApiKeyVault.ReadAll(store));
        GeminiApiKeyVault.RemoveAt(store, 1);
        Assert.Equal(["AIza-one-1111", "AIza-three-3333"], GeminiApiKeyVault.ReadAll(store));
        Assert.DoesNotContain("AIza-two-2222", store.Values[GeminiApiKeyVault.ListTarget], StringComparison.Ordinal);
    }

    [Fact]
    public void Vault_migrates_the_legacy_single_key_as_the_first_entry()
    {
        var store = new MemoryCredentialStore();
        store.Write(GeminiApiKeyVault.LegacyTarget, "AIza-legacy-1111");

        Assert.Equal(["AIza-legacy-1111"], GeminiApiKeyVault.ReadAll(store));
        Assert.False(store.Values.ContainsKey(GeminiApiKeyVault.LegacyTarget));
        Assert.True(store.Values.ContainsKey(GeminiApiKeyVault.ListTarget));
    }

    [Fact]
    public void Legacy_preferences_without_source_load_as_gpu_with_the_same_mode()
    {
        string path = TemporaryFile();
        try
        {
            File.WriteAllText(path, "{\"mode\":2,\"modeExplicitlySelected\":true}");
            GpuTranslationPreferences loaded = GpuTranslationPreferences.Load(path);
            Assert.Equal(TranslationSource.Gpu, loaded.Source);
            Assert.Equal(GpuTranslationMode.ParallelLocalRemote, loaded.Mode);
            Assert.False(loaded.ModeExplicitlySelected);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Api_source_preferences_round_trip()
    {
        string path = TemporaryFile();
        try
        {
            new GpuTranslationPreferences
            {
                Source = TranslationSource.Api,
                Mode = GpuTranslationMode.RemoteOnly,
                ModeExplicitlySelected = true
            }.Save(path);
            GpuTranslationPreferences loaded = GpuTranslationPreferences.Load(path);
            Assert.Equal(TranslationSource.Api, loaded.Source);
            Assert.Equal(GpuTranslationMode.RemoteOnly, loaded.Mode);
            Assert.False(loaded.ModeExplicitlySelected);
        }
        finally { File.Delete(path); }
    }

    private static string TemporaryFile() =>
        Path.Combine(Path.GetTempPath(), "qt-gemini-preferences-" + Guid.NewGuid().ToString("N") + ".json");

    private sealed class MemoryCredentialStore : IGeminiCredentialStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public string? Read(string target) => Values.GetValueOrDefault(target);
        public void Write(string target, string value) => Values[target] = value;
        public void Delete(string target) => Values.Remove(target);
    }
}
