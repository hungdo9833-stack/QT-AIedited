using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

/// <summary>
/// Imports old per-project presets into the application-wide library without overwriting
/// a shared preset that has been edited elsewhere. Legacy project files are left intact.
/// </summary>
public static class SharedPromptPresetLibrary
{
    public sealed record MergeResult(PresetLibrary Library, Guid SelectedProjectPresetId, bool Changed);

    public static MergeResult Merge(PresetLibrary shared, PresetLibrary legacy, Guid? projectSelectedId)
    {
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(legacy);
        shared.Validate();
        legacy.Validate();

        List<PromptPreset> result = shared.Presets.Select(p => p with { }).ToList();
        Dictionary<Guid, Guid> ids = [];
        bool changed = false;

        foreach (PromptPreset incoming in legacy.Presets)
        {
            PromptPreset? sameId = result.FirstOrDefault(p => p.Id == incoming.Id);
            if (sameId is not null && SameContent(sameId, incoming))
            {
                ids[incoming.Id] = sameId.Id;
                continue;
            }

            // An earlier import may already exist under a different ID. Never
            // duplicate it merely because the user reopened the same project.
            PromptPreset? identical = result.FirstOrDefault(p =>
                SameContent(p, incoming) ||
                (p.Name == incoming.Name + " (từ dự án)" &&
                 p.Template == incoming.Template && p.Description == incoming.Description));
            if (identical is not null)
            {
                ids[incoming.Id] = identical.Id;
                continue;
            }

            if (result.Count >= 200)
                throw new InvalidOperationException(
                    "Thư viện chung đã có 200 preset. Hãy dọn bớt preset trước khi nhập preset từ Project.");

            PromptPreset imported = sameId is null
                ? incoming with { }
                : incoming with { Id = Guid.NewGuid(), Name = incoming.Name + " (từ dự án)" };
            imported.Validate();
            result.Add(imported);
            ids[incoming.Id] = imported.Id;
            changed = true;
        }

        Guid wanted = projectSelectedId ?? legacy.SelectedId;
        Guid selectedId = ids.TryGetValue(wanted, out Guid mapped) ? mapped :
            result.Any(p => p.Id == wanted) ? wanted : shared.SelectedId;
        PresetLibrary merged = new()
        {
            SchemaVersion = shared.SchemaVersion,
            SelectedId = shared.SelectedId,
            Presets = result
        };
        merged.Validate();
        return new(merged, selectedId, changed);
    }

    private static bool SameContent(PromptPreset left, PromptPreset right) =>
        left.Name == right.Name && left.Template == right.Template &&
        left.Description == right.Description;
}
