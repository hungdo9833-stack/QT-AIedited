using QuickTranslator.AiEditor.Core.Importing;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Wpf;

internal sealed record ChapterTitleRuntime(
    IChapterTitleTranslationBackend Backend,
    ModelOptions ModelOptions,
    Guid? BackendProfileId,
    string Model);

internal delegate Task<ChapterTitleRuntime> ChapterTitleRuntimeFactory(
    ChapterTitleTranslationMethod method, CancellationToken cancellationToken);
