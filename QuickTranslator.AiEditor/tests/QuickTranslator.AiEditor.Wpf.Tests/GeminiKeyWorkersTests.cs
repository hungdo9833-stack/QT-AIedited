using System.IO;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

[Collection("GeminiKeyWorkers")]
public sealed class GeminiKeyWorkersTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "qt-gemini-names-" + Guid.NewGuid().ToString("N"));
    private readonly Func<IReadOnlyList<string>> _reader = GeminiKeyWorkers.KeyReader;
    private readonly string _path = GeminiKeyWorkers.NamesPath;

    public GeminiKeyWorkersTests()
    {
        GeminiKeyWorkers.NamesPath = Path.Combine(_folder, "names.json");
        GeminiKeyWorkers.KeyReader = () => ["AIzaKEY-one-000000000", "AIzaKEY-two-111111111", "AIzaKEY-three-22222222"];
    }

    public void Dispose()
    {
        GeminiKeyWorkers.KeyReader = _reader;
        GeminiKeyWorkers.NamesPath = _path;
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    [Fact]
    public void Each_key_is_its_own_worker_with_a_stable_id()
    {
        IReadOnlyList<GeminiKeyWorker> workers = GeminiKeyWorkers.All();
        Assert.Equal(3, workers.Select(worker => worker.ProfileId).Distinct().Count());
        Assert.Equal(["Key 1", "Key 2", "Key 3"], workers.Select(worker => worker.Name));
        Assert.Equal(workers[1].ProfileId, GeminiKeyWorkers.ProfileId("AIzaKEY-two-111111111"));
        Assert.Equal(workers[2].Key, GeminiKeyWorkers.ByProfile(workers[2].ProfileId)!.Key);
        Assert.True(GeminiKeyWorkers.IsKeyProfile(workers[0].ProfileId));
        Assert.False(GeminiKeyWorkers.IsKeyProfile(Guid.NewGuid()));
    }

    [Fact]
    public void Names_are_saved_by_fingerprint_without_the_key()
    {
        GeminiKeyWorker second = GeminiKeyWorkers.All()[1];
        GeminiKeyWorkers.SetName(second.Fingerprint, "  Tài khoản B  ");
        Assert.Equal("Tài khoản B", GeminiKeyWorkers.All()[1].Name);
        Assert.Equal("Tài khoản B", GeminiKeyWorkers.NameOf(second.Fingerprint));
        Assert.DoesNotContain("AIzaKEY", File.ReadAllText(GeminiKeyWorkers.NamesPath));

        GeminiKeyWorkers.SetName(second.Fingerprint, " ");
        Assert.Equal("Key 2", GeminiKeyWorkers.All()[1].Name);
    }

    [Fact]
    public void A_key_lane_id_round_trips_its_fingerprint()
    {
        string fingerprint = GeminiKeyWorkers.All()[0].Fingerprint;
        string lane = TranslationLanes.GeminiKey(fingerprint);
        Assert.Equal(fingerprint, TranslationLanes.GeminiKeyFingerprint(lane));
        Assert.Null(TranslationLanes.GeminiKeyFingerprint(TranslationLanes.GeminiId));
        Assert.Equal(fingerprint, GeminiKeyWorkers.ByFingerprint(fingerprint)!.Fingerprint);
    }
}
