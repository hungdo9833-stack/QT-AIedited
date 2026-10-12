using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace QuickTranslator.AiEditor.Core.Persistence;

public enum RecoverySource { Primary, Backup, Missing }
public enum RecoveryFileCondition { Valid, Missing, Invalid, NotChecked }
public sealed record RecoveryInfo(string PrimaryPath, string? SourcePath, RecoverySource Source,
    RecoveryFileCondition PrimaryCondition, RecoveryFileCondition BackupCondition, string? PrimaryError,
    string? PrimarySha256, string? BackupSha256, string? RescuePath = null);
public sealed record RecoveryRead<T>(T Value, RecoveryInfo Info);

// One read/write policy: missing is optional, corruption requires a verified backup,
// and access/sharing errors are retryable failures, never proof that a file is absent/corrupt.
public static class RecoveryFiles
{
    internal static readonly AsyncLocal<Action<string>?> FaultPoint = new();
    public static async Task<RecoveryRead<T>> ReadAsync<T>(string path, Func<byte[], T> decode,
        Func<T>? missingDefault = null, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        byte[]? primary = await ReadOptionalAsync(path, cancellationToken).ConfigureAwait(false);
        Exception? primaryError = null;
        if (primary is not null)
        {
            try { return new(decode(primary), new(path, path, RecoverySource.Primary, RecoveryFileCondition.Valid,
                RecoveryFileCondition.NotChecked, null, Hash(primary), null)); }
            catch (Exception ex) when (IsInvalidContent(ex)) { primaryError = ex; }
        }
        byte[]? backup = await ReadOptionalAsync(path + ".bak", cancellationToken).ConfigureAwait(false);
        Exception? backupError = null;
        if (backup is not null)
        {
            try { return new(decode(backup), new(path, path + ".bak", RecoverySource.Backup,
                primary is null ? RecoveryFileCondition.Missing : RecoveryFileCondition.Invalid,
                RecoveryFileCondition.Valid, primaryError?.Message, Hash(primary), Hash(backup))); }
            catch (Exception ex) when (IsInvalidContent(ex)) { backupError = ex; }
        }
        if (primary is null && backup is null)
        {
            if (missingDefault is null) throw new FileNotFoundException("Không có primary hoặc backup; không dùng defaults thay dữ liệu bắt buộc.", path);
            return new(missingDefault(), new(path, null, RecoverySource.Missing, RecoveryFileCondition.Missing, RecoveryFileCondition.Missing, null, null, null));
        }
        throw new InvalidDataException($"Không đọc được primary/backup của {Path.GetFileName(path)}. Giữ nguyên các file để xuất cứu hộ và đối chiếu; không ghi defaults.",
            new AggregateException(new[] { primaryError, backupError }.OfType<Exception>()));
    }

    public static async Task<RecoveryInfo> WriteAsync<T>(string path, byte[] bytes, Func<byte[], T> decode,
        CancellationToken cancellationToken = default)
    {
        _ = decode(bytes); // Proposed schema/identity/content must be valid before any write.
        var current = await ReadAsync(path, decode, () => default!, cancellationToken).ConfigureAwait(false);
        string? rescue = null;
        bool recovered = current.Info.Source == RecoverySource.Backup;
        bool damagedBackup = false;
        if (current.Info.Source == RecoverySource.Primary)
        {
            byte[]? backup = await ReadOptionalAsync(path + ".bak", cancellationToken).ConfigureAwait(false);
            if (backup is not null)
            {
                try { _ = decode(backup); }
                catch (Exception ex) when (IsInvalidContent(ex)) { damagedBackup = true; }
            }
            current = current with { Info = current.Info with { BackupSha256 = Hash(backup),
                BackupCondition = backup is null ? RecoveryFileCondition.Missing : damagedBackup ? RecoveryFileCondition.Invalid : RecoveryFileCondition.Valid } };
        }
        FaultPoint.Value?.Invoke("BeforeRescue");
        if (recovered || damagedBackup)
        {
            rescue = path + ".rescue-" + Guid.NewGuid().ToString("N") + ".zip";
            await ExportRescueAsync([path], rescue, cancellationToken).ConfigureAwait(false);
            FaultPoint.Value?.Invoke("AfterRescue");
            if (Hash(await ReadOptionalAsync(path, cancellationToken).ConfigureAwait(false)) != current.Info.PrimarySha256 ||
                Hash(await ReadOptionalAsync(path + ".bak", cancellationToken).ConfigureAwait(false)) != current.Info.BackupSha256)
                throw new IOException("File đã thay đổi sau preview phục hồi; hãy đọc và xác nhận lại, không ghi đè.");
        }
        FaultPoint.Value?.Invoke("BeforeReplacement");
        await AtomicLocalFile.WriteAsync(path, bytes, cancellationToken, preserveExistingBackup: recovered).ConfigureAwait(false);
        return current.Info with { RescuePath = rescue };
    }

    public static async Task<RecoveryInfo> RestoreBackupAsync<T>(RecoveryInfo preview, Func<byte[], T> decode,
        CancellationToken cancellationToken = default)
    {
        if (preview.Source != RecoverySource.Backup) throw new InvalidOperationException("Preview không phải backup đã kiểm tra.");
        var current = await ReadAsync(preview.PrimaryPath, decode, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (current.Info.Source != RecoverySource.Backup || current.Info.PrimarySha256 != preview.PrimarySha256 || current.Info.BackupSha256 != preview.BackupSha256)
            throw new IOException("Dữ liệu thay đổi sau preview; hãy kiểm tra và xác nhận lại.");
        byte[] bytes = await File.ReadAllBytesAsync(preview.SourcePath!, cancellationToken).ConfigureAwait(false);
        if (Hash(bytes) != preview.BackupSha256) throw new IOException("Backup đổi sau preview; không ghi đè.");
        var written = await WriteAsync(preview.PrimaryPath, bytes, decode, cancellationToken).ConfigureAwait(false);
        var after = await ReadAsync(preview.PrimaryPath, decode, cancellationToken: cancellationToken).ConfigureAwait(false);
        return after.Info with { RescuePath = written.RescuePath };
    }

    public static async Task ExportRescueAsync(IEnumerable<string> primaryPaths, string destination,
        CancellationToken cancellationToken = default)
    {
        destination = Path.GetFullPath(destination);
        var paths = primaryPaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(x => new[] { x, x + ".bak" }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Any(x => string.Equals(x, destination, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Bản cứu hộ không được ghi đè source/backup.");
        var files = new List<(string Path, byte[] Bytes)>();
        foreach (string path in paths)
        {
            var bytes = await ReadOptionalAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes is not null) files.Add((path, bytes));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                for (int i = 0; i < files.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry($"{i:D4}-{Path.GetFileName(files[i].Path)}", CompressionLevel.Optimal);
                    await using var stream = entry.Open();
                    await stream.WriteAsync(files[i].Bytes, cancellationToken).ConfigureAwait(false);
                }
                var metadata = archive.CreateEntry("rescue-manifest.json");
                await using var metadataStream = metadata.Open();
                await JsonSerializer.SerializeAsync(metadataStream, new { SchemaVersion = 1, CreatedAt = DateTimeOffset.UtcNow,
                    Files = files.Select((file, i) => new { SourcePath = file.Path, Entry = $"{i:D4}-{Path.GetFileName(file.Path)}",
                        Length = file.Bytes.Length, Sha256 = Hash(file.Bytes) }).ToArray() }, DocumentJson.Options, cancellationToken).ConfigureAwait(false);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false); output.Flush(true);
        }
        using var verified = ZipFile.OpenRead(destination);
        for (int i = 0; i < files.Count; i++)
        {
            await using var input = verified.GetEntry($"{i:D4}-{Path.GetFileName(files[i].Path)}")!.Open();
            using var bytes = new MemoryStream(); await input.CopyToAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (Hash(bytes.ToArray()) != Hash(files[i].Bytes)) throw new InvalidDataException("Checksum bản cứu hộ không khớp; chưa thay thế dữ liệu nguồn.");
        }
    }

    internal static async Task<byte[]?> ReadOptionalAsync(string path, CancellationToken token)
    {
        try { return await File.ReadAllBytesAsync(path, token).ConfigureAwait(false); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    internal static string? Hash(byte[]? bytes) => bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes));
    private static bool IsInvalidContent(Exception ex) => ex is JsonException or InvalidDataException or ArgumentException;

    internal static T DecodeJson<T>(byte[] bytes, Action<T> validate, params string[] required) where T : class
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object || required.Any(x => !document.RootElement.TryGetProperty(x, out _)))
            throw new InvalidDataException("Thiếu trường dữ liệu bắt buộc; không thay bằng defaults.");
        if (document.RootElement.TryGetProperty("schemaVersion", out var schema) && (schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) || version != 1))
            throw new InvalidDataException("Schema dữ liệu chưa được hỗ trợ; giữ file gốc để dùng phiên bản phù hợp.");
        T value = JsonSerializer.Deserialize<T>(bytes, DocumentJson.Options) ?? throw new InvalidDataException("Dữ liệu JSON trống.");
        validate(value); return value;
    }
}
