namespace QuickTranslator.AiEditor.Core.Persistence;

internal static class AtomicLocalFile
{
    internal static readonly AsyncLocal<Action<string>?> FaultPoint = new();

    public static async Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken, bool preserveExistingBackup = false)
    {
        path = Path.GetFullPath(path);
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
                stream.Flush(true);
            }
            FaultPoint.Value?.Invoke("AfterTemporaryFlush");
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path) && !preserveExistingBackup)
            {
                File.Replace(temporary, path, path + ".bak", true);
                StorageIoMetricsCapture.BackupReplaced();
            }
            else File.Move(temporary, path, overwrite: preserveExistingBackup);
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
            { System.Diagnostics.Trace.TraceError($"Không dọn được file tạm {temporary}: {ex}"); }
        }
    }
}
