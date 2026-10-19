using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Tests;

public sealed class EntityMemoryPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-memory-a-" + Guid.NewGuid().ToString("N"));
    private async Task<(ProjectSession Session, EntityMemoryStore Store)> Create()
    {
        var session = await new ProjectRepository().CreateAsync(_root, "Memory synthetic");
        return (session, new(session));
    }
    private static void AddEntity(EntityMemoryState state, string name) => state.Entities.Add(new(
        EntityMemoryIdentity.StableId(state.ProjectId, name), state.ProjectId, MemoryEntityKind.Character, name));

    [Fact]
    public async Task Optional_sidecar_round_trips_without_migrating_or_writing_old_project_documents()
    {
        var (session, store) = await Create();
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllBytes);
        Assert.Equal(0, (await store.LoadAsync()).Revision); Assert.False(File.Exists(store.PathName));
        var saved = await store.UpdateAsync(0, s => AddEntity(s, "甲"));
        Assert.Equal(1, saved.Revision);
        Assert.Equal(saved.Entities, (await new EntityMemoryStore(session).LoadAsync()).Entities);
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        Assert.False(File.Exists(session.Resolve("Settings/ai-book-intelligence.json")));
        Assert.False(File.Exists(session.Resolve("Settings/abi3-missing-identity-review.v1.json")));
    }

    [Fact]
    public async Task Competing_writers_with_the_same_revision_cannot_lose_updates()
    {
        var (session, store) = await Create();
        async Task<bool> Write(string label)
        {
            try { await new EntityMemoryStore(session).UpdateAsync(0, s => AddEntity(s, label)); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var results = await Task.WhenAll(Task.Run(() => Write("甲")), Task.Run(() => Write("乙")));
        Assert.Single(results, x => x); Assert.Single((await store.LoadAsync()).Entities);
        Assert.Equal(1, (await store.LoadAsync()).Revision);
    }

    [Fact]
    public async Task Failure_after_flush_rolls_back_primary_file_and_checkpoint_atomically()
    {
        var (_, store) = await Create(); await store.UpdateAsync(0, s => AddEntity(s, "甲"));
        byte[] before = await File.ReadAllBytesAsync(store.PathName);
        SimpleFileStorage.FaultPoint.Value = _ => throw new IOException("synthetic interrupted save");
        try
        {
            await Assert.ThrowsAsync<IOException>(() => store.UpdateAsync(1, s =>
            {
                AddEntity(s, "乙"); s.Checkpoints.Add(new(Guid.NewGuid(), 1, new string('A', 64), "test"));
            }));
        }
        finally { SimpleFileStorage.FaultPoint.Value = null; }
        Assert.Equal(before, await File.ReadAllBytesAsync(store.PathName));
        Assert.Empty((await store.LoadAsync()).Checkpoints);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.PathName)!, "*.tmp-*"));
    }

    [Fact]
    public async Task Cancellation_and_cross_process_lease_do_not_modify_memory()
    {
        var (_, store) = await Create(); await store.UpdateAsync(0, s => AddEntity(s, "甲"));
        byte[] before = await File.ReadAllBytesAsync(store.PathName);
        using (var lease = new FileStream(store.PathName + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => store.UpdateAsync(1, s => AddEntity(s, "乙")));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpdateAsync(1, s => AddEntity(s, "乙"), cancel.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.PathName));
    }

    [Fact]
    public async Task Existing_definitions_and_old_review_history_cannot_be_rewritten_or_removed()
    {
        var (_, store) = await Create();
        var saved = await store.UpdateAsync(0, s =>
        {
            AddEntity(s, "甲");
            s.Facts.Add(new() { Id = Guid.NewGuid(), ProjectId = s.ProjectId, SubjectId = s.Entities[0].Id,
                Kind = MemoryFactKind.PrimaryName, Value = "甲", Provenance = new(MemoryOrigin.User, "fact:one", "manual draft") });
            s.Reviews.Add(new(Guid.NewGuid(), s.Facts[0].Id, MemoryReviewStatus.Accepted, DateTimeOffset.UtcNow,
                "accepted but not translation-confirmed", new(), EntityMemoryIdentity.DefinitionFingerprint(s, s.Facts[0].Id)));
        });
        byte[] before = await File.ReadAllBytesAsync(store.PathName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(saved.Revision, s => s.Reviews.Clear()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(saved.Revision, s => s.Entities[0] = s.Entities[0] with { Label = "乙" }));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.PathName));
    }

    [Fact]
    public async Task Duplicate_ids_invalid_timeline_schema_and_cross_project_memory_fail_closed()
    {
        var (_, store) = await Create();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateAsync(0, s => { AddEntity(s, "甲"); AddEntity(s, "甲"); }));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateAsync(0, s => s.SchemaVersion = 2));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateAsync(0, s => s.Entities.Add(new(Guid.NewGuid(), Guid.NewGuid(), MemoryEntityKind.Body, "body"))));
        Assert.Throws<InvalidDataException>(() => new MemoryTimeline(10, 1).Validate());
        Assert.False(File.Exists(store.PathName));
    }

    [Fact]
    public void Stable_identity_is_project_scoped_and_does_not_normalize_similar_names_or_facets()
    {
        Guid project = Guid.NewGuid();
        Assert.Equal(EntityMemoryIdentity.StableId(project, "character:小锋"), EntityMemoryIdentity.StableId(project, "character:小锋"));
        Assert.NotEqual(EntityMemoryIdentity.StableId(project, "character:小锋"), EntityMemoryIdentity.StableId(project, "character:小峰"));
        Assert.NotEqual(EntityMemoryIdentity.StableId(project, "body:甲"), EntityMemoryIdentity.StableId(project, "consciousness:甲"));
        Assert.NotEqual(EntityMemoryIdentity.StableId(project, "甲"), EntityMemoryIdentity.StableId(Guid.NewGuid(), "甲"));
    }

    [Fact]
    public async Task Partial_or_malformed_sidecar_is_not_treated_as_an_empty_project()
    {
        var (session, store) = await Create();
        Directory.CreateDirectory(Path.GetDirectoryName(store.PathName)!);
        string partial = "{\"projectId\":\"" + session.Manifest.ProjectId + "\"}";
        await File.WriteAllTextAsync(store.PathName, partial);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateAsync(0, s => AddEntity(s, "甲")));
        Assert.Equal(partial, await File.ReadAllTextAsync(store.PathName));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
