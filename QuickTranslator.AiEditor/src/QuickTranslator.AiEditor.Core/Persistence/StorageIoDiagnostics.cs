namespace QuickTranslator.AiEditor.Core.Persistence;

public sealed record StorageIoMetricsSnapshot(long Serializations, long Hashes, long FileReads,
    long BytesRead, long FileWrites, long BytesWritten, long TemporaryFilesCreated,
    long BackupReplacements, long FileDeletes);

public sealed class StorageIoMetricsCapture : IDisposable
{
    private static readonly AsyncLocal<StorageIoMetricsCapture?> CurrentCapture = new();
    private readonly StorageIoMetricsCapture? _previous;
    private long _serializations;
    private long _hashes;
    private long _fileReads;
    private long _bytesRead;
    private long _fileWrites;
    private long _bytesWritten;
    private long _temporaryFilesCreated;
    private long _backupReplacements;
    private long _fileDeletes;
    private bool _disposed;

    private StorageIoMetricsCapture()
    {
        _previous = CurrentCapture.Value;
        CurrentCapture.Value = this;
    }

    public static StorageIoMetricsCapture Begin() => new();

    public StorageIoMetricsSnapshot Snapshot() => new(
        Interlocked.Read(ref _serializations), Interlocked.Read(ref _hashes),
        Interlocked.Read(ref _fileReads), Interlocked.Read(ref _bytesRead),
        Interlocked.Read(ref _fileWrites), Interlocked.Read(ref _bytesWritten),
        Interlocked.Read(ref _temporaryFilesCreated), Interlocked.Read(ref _backupReplacements),
        Interlocked.Read(ref _fileDeletes));

    internal static void Serialization()
    {
        StorageIoMetricsCapture? value = CurrentCapture.Value;
        if (value is not null) Interlocked.Increment(ref value._serializations);
    }

    internal static void Hash()
    {
        StorageIoMetricsCapture? value = CurrentCapture.Value;
        if (value is not null) Interlocked.Increment(ref value._hashes);
    }

    internal static void Read(long bytes)
    {
        StorageIoMetricsCapture? value = CurrentCapture.Value;
        if (value is null) return;
        Interlocked.Increment(ref value._fileReads);
        Interlocked.Add(ref value._bytesRead, bytes);
    }

    internal static void Write(long bytes)
    {
        StorageIoMetricsCapture? value = CurrentCapture.Value;
        if (value is null) return;
        Interlocked.Increment(ref value._fileWrites);
        Interlocked.Add(ref value._bytesWritten, bytes);
    }

    internal static void TemporaryCreated()
    {
        StorageIoMetricsCapture? value = CurrentCapture.Value;
        if (value is not null) Interlocked.Increment(ref value._temporaryFilesCreated);
    }

    internal static void BackupReplaced()
    {
        StorageIoMetricsCapture? value = CurrentCapture.Value;
        if (value is not null) Interlocked.Increment(ref value._backupReplacements);
    }

    internal static void Deleted()
    {
        StorageIoMetricsCapture? value = CurrentCapture.Value;
        if (value is not null) Interlocked.Increment(ref value._fileDeletes);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CurrentCapture.Value = _previous;
    }
}
