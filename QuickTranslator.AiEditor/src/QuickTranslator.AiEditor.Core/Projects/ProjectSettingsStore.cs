using System.Text.Json;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.Projects;

public sealed class ProjectSettingsStore(ProjectSession session)
{
    public Task SaveTranslationAsync(ProjectTranslationSettings settings, CancellationToken cancellationToken = default) =>
        SaveAsync(session.Manifest.Files.TranslationSettings, settings, cancellationToken);
    public Task<ProjectTranslationSettings> LoadTranslationAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(session.Manifest.Files.TranslationSettings, new ProjectTranslationSettings(), cancellationToken);
    public Task SaveExportAsync(ProjectExportSettings settings, CancellationToken cancellationToken = default) =>
        SaveAsync(session.Manifest.Files.ExportSettings, settings, cancellationToken);
    public Task<ProjectExportSettings> LoadExportAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(session.Manifest.Files.ExportSettings, new ProjectExportSettings(), cancellationToken);
    public Task SaveGlossaryAsync(ProjectGlossary glossary, CancellationToken cancellationToken = default) =>
        SaveAsync(session.Manifest.Files.Glossary, glossary, cancellationToken);
    public Task<ProjectGlossary> LoadGlossaryAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(session.Manifest.Files.Glossary, new ProjectGlossary(), cancellationToken);

    public async Task SaveContextNotesAsync(string notes, CancellationToken cancellationToken = default) =>
        await AtomicLocalFile.WriteAsync(session.Resolve(session.Manifest.Files.ContextNotes),
            System.Text.Encoding.UTF8.GetBytes(notes ?? ""), cancellationToken).ConfigureAwait(false);

    public async Task<string> LoadContextNotesAsync(CancellationToken cancellationToken = default)
    {
        string path = session.Resolve(session.Manifest.Files.ContextNotes);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : "";
    }

    private async Task SaveAsync<T>(string relative, T value, CancellationToken cancellationToken) =>
        await AtomicLocalFile.WriteAsync(session.Resolve(relative),
            JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true }), cancellationToken).ConfigureAwait(false);

    private async Task<T> LoadAsync<T>(string relative, T fallback, CancellationToken cancellationToken)
    {
        string path = session.Resolve(relative);
        if (!File.Exists(path)) return fallback;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<T>(stream, DocumentJson.Options, cancellationToken).ConfigureAwait(false) ?? fallback;
    }
}
