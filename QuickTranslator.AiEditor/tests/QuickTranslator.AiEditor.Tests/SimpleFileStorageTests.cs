using System.Text.Json;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Tests;

public sealed class SimpleFileStorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "qt-simple-storage-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Save_ReplacesTargetWithoutBackupOrHistory()
    {
        string path = Path.Combine(_directory, "settings.json");
        await SimpleFileStorage.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(new Sample("one")), Decode);
        await SimpleFileStorage.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(new Sample("two")), Decode);

        Assert.Equal("two", (await SimpleFileStorage.ReadRequiredAsync(path, Decode)).Value);
        Assert.False(File.Exists(path + ".bak"));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Save_WaitsOutAReaderBriefly_HoldingTheTarget()
    {
        string path = Path.Combine(_directory, "project.qtproj");
        await SimpleFileStorage.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(new Sample("one")), Decode);
        // A reader that does not share delete blocks the replace on Windows until it closes.
        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task release = Task.Delay(300).ContinueWith(_ => reader.Dispose());

        await SimpleFileStorage.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(new Sample("two")), Decode);
        await release;

        Assert.Equal("two", (await SimpleFileStorage.ReadRequiredAsync(path, Decode)).Value);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
    }

    [Fact]
    public async Task FailedReplacement_PreservesPreviousTarget()
    {
        string path = Path.Combine(_directory, "settings.json");
        await SimpleFileStorage.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(new Sample("stable")), Decode);
        byte[] before = await File.ReadAllBytesAsync(path);
        SimpleFileStorage.FaultPoint.Value = point =>
        {
            if (point == "AfterTemporaryFlush") throw new IOException("synthetic write failure");
        };
        try
        {
            await Assert.ThrowsAsync<IOException>(() => SimpleFileStorage.WriteAsync(
                path, JsonSerializer.SerializeToUtf8Bytes(new Sample("incomplete")), Decode));
        }
        finally { SimpleFileStorage.FaultPoint.Value = null; }

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
    }

    [Fact]
    public async Task Load_DoesNotFallBackToAnExistingBackup()
    {
        string path = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(path, "{broken");
        await File.WriteAllTextAsync(path + ".bak", JsonSerializer.Serialize(new Sample("legacy backup")));

        await Assert.ThrowsAsync<InvalidDataException>(() => SimpleFileStorage.ReadRequiredAsync(path, Decode));
        Assert.Equal("{broken", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task OptionalLoad_ReturnsDefaultOnlyWhenPrimaryIsMissing()
    {
        string path = Path.Combine(_directory, "optional.json");
        Sample value = await SimpleFileStorage.ReadOptionalAsync(path, Decode, () => new Sample("default"));
        Assert.Equal("default", value.Value);
        Assert.False(File.Exists(path));
    }

    private static Sample Decode(byte[] bytes) =>
        JsonSerializer.Deserialize<Sample>(bytes) ?? throw new InvalidDataException("empty");

    public void Dispose()
    {
        SimpleFileStorage.FaultPoint.Value = null;
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed record Sample(string Value);
}
