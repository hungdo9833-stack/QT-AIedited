using System.Collections.Concurrent;

namespace QuickTranslator.AiEditor.Core.Persistence;

/// <summary>
/// Direct primary-file storage. It writes a temporary sibling file and replaces the
/// target without creating backups, journals, revisions, or recovery metadata.
/// Written bytes are not re-decoded, but the temporary file is forced to disk before the replace:
/// otherwise a power loss can commit the rename while the data is still cached, leaving a target
/// of the right length filled with zero bytes.
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
        string fullPath = Path.GetFullPath(path);
        byte[] bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        StorageIoMetricsCapture.Read(bytes.Length);
        try { return decode(bytes); }
        catch (InvalidDataException) { throw; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new InvalidDataException($"Không đọc được JSON trong {Path.GetFileName(fullPath)}.", error);
        }
    }

    public static async Task<T> ReadOptionalAsync<T>(string path, Func<byte[], T> decode, Func<T> missingDefault,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(missingDefault);
        try { return await ReadRequiredAsync(path, decode, cancellationToken).ConfigureAwait(false); }
        catch (FileNotFoundException) { return missingDefault(); }
        catch (DirectoryNotFoundException) { return missingDefault(); }
    }

    public static async Task WriteAsync<T>(string path, byte[] bytes, Func<byte[], T> decode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(decode);
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
            // Unbuffered, and disposal never resumes on the caller's context: a UI thread blocked on a save
            // (sync-over-async on close) would otherwise deadlock waiting for the final flush.
            var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                0, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                StorageIoMetricsCapture.Write(bytes.Length);
            }

            FaultPoint.Value?.Invoke("AfterTemporaryFlush");
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporary, path, overwrite: true);
                StorageIoMetricsCapture.Replaced();
            }
            catch (UnauthorizedAccessException error)
            {
                throw new IOException($"Không thể thay thế file {Path.GetFileName(path)} vì bị khóa hoặc không có quyền ghi.", error);
            }
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
