using System.Collections.Concurrent;

namespace QuickTranslator.AiEditor.Core.Persistence;

/// <summary>
/// Direct primary-file storage. It validates proposed content, writes a temporary
/// sibling file, flushes it, and replaces the target without creating backups,
/// journals, revisions, or recovery metadata.
/// </summary>
public static class SimpleFileStorage
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteGates =
        new(StringComparer.OrdinalIgnoreCase);

    internal static readonly AsyncLocal<Action<string>?> FaultPoint = new();

    public static async Task<T> ReadRequiredAsync<T>(string path, Func<byte[], T> decode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(decode);
        byte[] bytes = await File.ReadAllBytesAsync(Path.GetFullPath(path), cancellationToken).ConfigureAwait(false);
        StorageIoMetricsCapture.Read(bytes.Length);
        return decode(bytes);
    }

    public static async Task<T> ReadOptionalAsync<T>(string path, Func<byte[], T> decode, Func<T> missingDefault,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(missingDefault);
        try { return await ReadRequiredAsync(path, decode, cancellationToken).ConfigureAwait(false); }
        catch (FileNotFoundException) { return missingDefault(); }
        catch (DirectoryNotFoundException) { return missingDefault(); }
    }

    public static async Task<byte[]?> ReadOptionalBytesAsync(string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(Path.GetFullPath(path), cancellationToken).ConfigureAwait(false);
            StorageIoMetricsCapture.Read(bytes.Length);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public static async Task WriteAsync<T>(string path, byte[] bytes, Func<byte[], T> decode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(decode);
        _ = decode(bytes);

        path = Path.GetFullPath(path);
        SemaphoreSlim gate = WriteGates.GetOrAdd(path, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteAtomicAsync(path, bytes, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public static async Task WriteBytesAsync(string path, byte[] bytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);
        path = Path.GetFullPath(path);
        SemaphoreSlim gate = WriteGates.GetOrAdd(path, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteAtomicAsync(path, bytes, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            StorageIoMetricsCapture.TemporaryCreated();
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                StorageIoMetricsCapture.Write(bytes.Length);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            FaultPoint.Value?.Invoke("AfterTemporaryFlush");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                    StorageIoMetricsCapture.Deleted();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceError($"Không dọn được file tạm {temporary}: {ex}");
            }
        }
    }
}
