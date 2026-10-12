namespace QuickTranslator.AiEditor.Core.Persistence;

internal static class AtomicLocalFile
{
    public static async Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken, bool preserveExistingBackup = false)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path) && !preserveExistingBackup) File.Replace(temporary, path, path + ".bak", true);
            else File.Move(temporary, path, overwrite: preserveExistingBackup);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
