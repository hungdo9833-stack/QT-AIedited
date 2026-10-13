using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.Projects;

public sealed class ProjectSettingsStore(ProjectSession session)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, RecoveryInfo> _recovery = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<RecoveryInfo> RecoverySources => _recovery.Values;
    public Task SaveTranslationAsync(ProjectTranslationSettings value, CancellationToken cancellationToken = default) =>
        SaveAsync(session.Manifest.Files.TranslationSettings, value, DecodeTranslation, cancellationToken);
    public Task<ProjectTranslationSettings> LoadTranslationAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(session.Manifest.Files.TranslationSettings, DecodeTranslation, () => new ProjectTranslationSettings(), cancellationToken);
    public Task SaveExportAsync(ProjectExportSettings value, CancellationToken cancellationToken = default) =>
        SaveAsync(session.Manifest.Files.ExportSettings, value, DecodeExport, cancellationToken);
    public Task<ProjectExportSettings> LoadExportAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(session.Manifest.Files.ExportSettings, DecodeExport, () => new ProjectExportSettings(), cancellationToken);
    public Task SaveGlossaryAsync(ProjectGlossary value, CancellationToken cancellationToken = default) =>
        SaveAsync(session.Manifest.Files.Glossary, value, DecodeGlossary, cancellationToken);
    public Task<ProjectGlossary> LoadGlossaryAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(session.Manifest.Files.Glossary, DecodeGlossary, () => new ProjectGlossary(), cancellationToken);

    public Task<string> LoadContextNotesAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(session.Manifest.Files.ContextNotes, DecodeContext, () => "", cancellationToken);
    public async Task SaveContextNotesAsync(string notes, CancellationToken cancellationToken = default)
    {
        byte[] bytes = new UTF8Encoding(false, true).GetBytes(notes ?? "");
        _ = DecodeContext(bytes);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureWritable();
            _recovery[session.Manifest.Files.ContextNotes] = await RecoveryFiles.WriteAsync(session.Resolve(session.Manifest.Files.ContextNotes),
                bytes, DecodeContext, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task SaveAsync<T>(string relative, T value, Func<byte[], T> decode, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true });
        _ = decode(bytes);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { EnsureWritable(); _recovery[relative] = await RecoveryFiles.WriteAsync(session.Resolve(relative), bytes, decode, token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    private async Task<T> LoadAsync<T>(string relative, Func<byte[], T> decode, Func<T> missing, CancellationToken token)
    {
        var result = await RecoveryFiles.ReadAsync(session.Resolve(relative), decode, missing, token).ConfigureAwait(false);
        _recovery[relative] = result.Info; return result.Value;
    }
    private void EnsureWritable()
    {
        if (session.RecoveryReport.RequiresDecision) throw new InvalidOperationException("Transaction project chưa đối chiếu; không ghi settings.");
    }
    public async Task RestoreRecoveredBackupsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var info in _recovery.Values.Where(x => x.Source == RecoverySource.Backup).ToArray())
        {
            string relative = Path.GetRelativePath(session.RootDirectory, info.PrimaryPath).Replace('\\', '/');
            RecoveryInfo restored;
            if (relative == session.Manifest.Files.TranslationSettings) restored = await RecoveryFiles.RestoreBackupAsync(info, DecodeTranslation, cancellationToken).ConfigureAwait(false);
            else if (relative == session.Manifest.Files.ExportSettings) restored = await RecoveryFiles.RestoreBackupAsync(info, DecodeExport, cancellationToken).ConfigureAwait(false);
            else if (relative == session.Manifest.Files.Glossary) restored = await RecoveryFiles.RestoreBackupAsync(info, DecodeGlossary, cancellationToken).ConfigureAwait(false);
            else if (relative == session.Manifest.Files.ContextNotes) restored = await RecoveryFiles.RestoreBackupAsync(info, DecodeContext, cancellationToken).ConfigureAwait(false);
            else throw new InvalidDataException("Recovery path không thuộc settings của session.");
            _recovery[relative] = restored;
        }
    }
    private static ProjectTranslationSettings DecodeTranslation(byte[] bytes) => RecoveryFiles.DecodeJson<ProjectTranslationSettings>(bytes, value =>
    {
        if (value.Model is null || value.Segmentation is null || value.TraditionalDataDirectory is null ||
            value.SelectedPromptPresetId == Guid.Empty || value.OllamaProfileId == Guid.Empty)
            throw new InvalidDataException("Thiết lập dịch thiếu dữ liệu.");
        value.Model.Validate(); value.Segmentation.Validate();
    }, "model", "segmentation");
    private static ProjectExportSettings DecodeExport(byte[] bytes) => RecoveryFiles.DecodeJson<ProjectExportSettings>(bytes, value =>
    {
        if (string.IsNullOrWhiteSpace(value.FileNameTemplate) || value.FileNameTemplate.Length > 300)
            throw new InvalidDataException("Mẫu tên file xuất không hợp lệ.");
    }, "fileNameTemplate");
    private static ProjectGlossary DecodeGlossary(byte[] bytes) => RecoveryFiles.DecodeJson<ProjectGlossary>(bytes, value =>
    {
        if (value.Terms is null || value.Terms.Count > 1000 || value.Terms.Any(x => x is null || string.IsNullOrWhiteSpace(x.Chinese) || string.IsNullOrWhiteSpace(x.Vietnamese)) ||
            value.Terms.Select(x => x.Chinese).Distinct(StringComparer.Ordinal).Count() != value.Terms.Count)
            throw new InvalidDataException("Glossary bị thiếu/trùng/không hợp lệ.");
    }, "terms");
    private static string DecodeContext(byte[] bytes)
    {
        string notes = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        if (notes.Length > 4000 || notes.Contains('\0')) throw new InvalidDataException("Context không hợp lệ hoặc quá 4.000 ký tự.");
        return notes;
    }
}
