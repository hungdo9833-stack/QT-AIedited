using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed class PresetLibrary
{
    public int SchemaVersion { get; set; } = 1;
    public Guid SelectedId { get; set; } = PromptPreset.Default.Id;
    public List<PromptPreset> Presets { get; set; } = [PromptPreset.Default];

    public void Validate()
    {
        if (SchemaVersion != 1 || Presets is null || Presets.Count == 0 || Presets.Count > 200 ||
            Presets.Any(p => p is null) || Presets.Select(p => p.Id).Distinct().Count() != Presets.Count || !Presets.Any(p => p.Id == SelectedId))
            throw new InvalidDataException("Danh sách preset sai định dạng, trùng ID hoặc thiếu preset đang chọn.");
        foreach (var preset in Presets) preset.Validate();
    }
}

public sealed class PromptPresetManager
{
    private readonly PresetLibrary _library;
    public PromptPresetManager(PresetLibrary? library = null)
    {
        _library = library ?? new();
        _library.Validate();
    }
    public IReadOnlyList<PromptPreset> Presets => _library.Presets.ToArray();
    public PromptPreset Selected => _library.Presets.Single(p => p.Id == _library.SelectedId) with { };
    public PresetLibrary Snapshot() => new() { SelectedId = _library.SelectedId, Presets = _library.Presets.Select(p => p with { }).ToList() };
    public void Select(Guid id)
    {
        if (!_library.Presets.Any(p => p.Id == id)) throw new ArgumentException("Không tìm thấy preset.");
        _library.SelectedId = id;
    }
    public PromptPreset Create(string name, string? template = null, string description = "")
    {
        if (_library.Presets.Count >= 200) throw new InvalidOperationException("Tối đa 200 preset.");
        var preset = new PromptPreset { Name = name.Trim(), Description = description.Trim(), Template = template ?? PromptPreset.DefaultTemplate };
        preset.Validate();
        _library.Presets.Add(preset);
        Select(preset.Id);
        return preset;
    }
    public PromptPreset Duplicate(Guid id, string name)
    {
        var source = _library.Presets.Single(p => p.Id == id);
        return Create(name, source.Template, source.Description);
    }
    public void Delete(Guid id)
    {
        if (id == PromptPreset.Default.Id) throw new InvalidOperationException("Preset mặc định không thể xóa; có thể khôi phục nội dung mặc định.");
        int index = _library.Presets.FindIndex(p => p.Id == id);
        if (index < 0) throw new ArgumentException("Không tìm thấy preset.");
        _library.Presets.RemoveAt(index);
        if (_library.SelectedId == id) _library.SelectedId = _library.Presets[Math.Clamp(index - 1, 0, _library.Presets.Count - 1)].Id;
    }
    public PromptPreset Update(Guid id, string name, string template, string? description = null)
    {
        int index = _library.Presets.FindIndex(p => p.Id == id);
        if (index < 0) throw new ArgumentException("Không tìm thấy preset.");
        var updated = _library.Presets[index] with { Name = name.Trim(), Template = template,
            Description = description?.Trim() ?? _library.Presets[index].Description,
            UpdatedAt = DateTimeOffset.UtcNow, Revision = _library.Presets[index].Revision + 1 };
        updated.Validate();
        _library.Presets[index] = updated;
        return updated;
    }
    public void ImportSnapshot(PromptPreset preset)
    {
        preset.Validate();
        if (_library.Presets.Any(p => p.Id == preset.Id && p.Name == preset.Name && p.Template == preset.Template && p.Revision == preset.Revision))
        { Select(preset.Id); return; }
        if (_library.Presets.Any(p => p.Id == preset.Id))
        {
            var imported = Create(preset.Name + " (snapshot chương)", preset.Template);
            Select(imported.Id);
        }
        else
        {
            if (_library.Presets.Count >= 200) throw new InvalidOperationException("Tối đa 200 preset.");
            _library.Presets.Add(preset with { });
            Select(preset.Id);
        }
    }
}
