using System.Text.Json;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.Projects;

public sealed class ProjectSettingsStore(ProjectSession session)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task SaveTranslationAsync(ProjectTranslationSettings value, CancellationToken cancellationToken = default) =>
        SaveAsync(session.Manifest.Files.TranslationSettings, value, DecodeTranslation, cancellationToken);
    public Task<ProjectTranslationSettings> LoadTranslationAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(session.Manifest.Files.TranslationSettings, DecodeTranslation, () => new ProjectTranslationSettings(), cancellationToken);
    public Task SaveExportAsync(ProjectExportSettings value, CancellationToken cancellationToken = default) =>
        SaveAsync(session.Manifest.Files.ExportSettings, value, DecodeExport, cancellationToken);
    public Task<ProjectExportSettings> LoadExportAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(session.Manifest.Files.ExportSettings, DecodeExport, () => new ProjectExportSettings(), cancellationToken);
    private async Task SaveAsync<T>(string relative, T value, Func<byte[], T> decode, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value,
            new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true });
        _ = decode(bytes);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { await SimpleFileStorage.WriteAsync(session.Resolve(relative), bytes, decode, token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private Task<T> LoadAsync<T>(string relative, Func<byte[], T> decode, Func<T> missing, CancellationToken token) =>
        SimpleFileStorage.ReadOptionalAsync(session.Resolve(relative), decode, missing, token);

    private static ProjectTranslationSettings DecodeTranslation(byte[] bytes) =>
        JsonFileCodec.DecodeDirect<ProjectTranslationSettings>(bytes, value =>
        {
            if (value.Model is null || value.Segmentation is null || value.TraditionalDataDirectory is null ||
                value.SelectedPromptPresetId == Guid.Empty || value.OllamaProfileId == Guid.Empty)
                throw new InvalidDataException("Thiết lập dịch thiếu dữ liệu.");
            value.Model.Validate(); value.Segmentation.Validate();
        }, "model", "segmentation", "ollamaProfileId");

    private static ProjectExportSettings DecodeExport(byte[] bytes) =>
        JsonFileCodec.DecodeDirect<ProjectExportSettings>(bytes, value =>
        {
            if (string.IsNullOrWhiteSpace(value.FileNameTemplate) || value.FileNameTemplate.Length > 300)
                throw new InvalidDataException("Mẫu tên file xuất không hợp lệ.");
        }, "fileNameTemplate");

}
