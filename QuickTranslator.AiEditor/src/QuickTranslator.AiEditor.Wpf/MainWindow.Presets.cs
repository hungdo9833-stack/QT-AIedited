using System.IO;
using System.Windows;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private PromptPresetManager _presetManager = new();
    private PromptPresetStore _presetStore = new(PromptPresetStore.DefaultPath);
    private bool _presetsReady;

    private async void OnPresetsLoaded(object sender, RoutedEventArgs e)
    {
        var store = _presetStore;
        try
        {
            var presets = await store.LoadAsync();
            if (!ReferenceEquals(store, _presetStore)) return;
            _presetManager = new(presets);
            _presetsReady = true;
            if (_projectSession is null && _service is not null)
                _presetManager.ImportSnapshot(_service.GetSnapshot().SelectedPreset);
            RefreshPresetEditor(); UpdateButtons();
        }
        catch (Exception ex) { if (ReferenceEquals(store, _presetStore)) { ShowError(ex); UpdateButtons(); } }
    }
    private void RefreshPresetEditor()
    {
        bool prior = _rendering; _rendering = true;
        try
        {
            PresetCombo.ItemsSource = _presetManager.Presets;
            PresetCombo.SelectedItem = _presetManager.Presets.Single(p => p.Id == _presetManager.Selected.Id);
            PresetNameInput.Text = _presetManager.Selected.Name;
            PresetTemplateInput.Text = _presetManager.Selected.Template;
            var selected = _presetManager.Selected;
            PresetSummaryText.Text = $"{selected.Name} · phiên bản {selected.Revision} · sửa {selected.UpdatedAt.ToLocalTime():g} · đã lưu cục bộ" +
                (string.IsNullOrWhiteSpace(selected.Description) ? "" : "\n" + selected.Description);
        }
        finally { _rendering = prior; }
    }
    private void SelectChapterPreset(PromptPreset preset)
    {
        // A Project owns its current preset selection. Chapter snapshots are
        // historical request data and must not replace that Project preference.
        if (!_presetsReady || _projectSession is not null) return;
        _presetManager.ImportSnapshot(preset);
        RefreshPresetEditor();
    }
    private async Task SavePresetEditorAsync()
    {
        if (!_presetsReady) throw new InvalidOperationException("Thư viện preset chưa đọc được; kiểm tra tệp preset và bản dự phòng trước khi dịch.");
        var selected = _presetManager.Selected;
        if (selected.Name != PresetNameInput.Text.Trim() || selected.Template != PresetTemplateInput.Text)
        {
            _presetManager.Update(selected.Id, PresetNameInput.Text, PresetTemplateInput.Text);
            RefreshPresetEditor();
        }
        await _presetStore.SaveAsync(_presetManager.Snapshot());
    }
    private async Task SaveProjectPresetSelectionAsync()
    {
        if (_projectSession is not { } session || !_presetsReady) return;
        var store = new ProjectSettingsStore(session);
        ProjectTranslationSettings settings = await store.LoadTranslationAsync();
        if (settings.SelectedPromptPresetId == _presetManager.Selected.Id) return;
        settings.SelectedPromptPresetId = _presetManager.Selected.Id;
        await store.SaveTranslationAsync(settings);
    }

    private async Task SaveSelectedSettingsAsync()
    {
        await SavePresetEditorAsync();
        if (_projectSession is not null) await SaveProjectResourcesAsync();
        if (_service is not null)
        {
            await _service.ConfigureAsync(ReadModelOptions(), _presetManager.Selected, ReadTerms(), ContextNotesInput.Text);
            await SaveCharacterProjectAsync();
            await _service.ConfigureTextCleanerAsync(AutoCleanConfirmedCheck.IsChecked == true);
        }
    }
    private IReadOnlyList<ConfirmedTerm> ReadTerms()
    {
        var terms = _glossaryRows.Select(row => new ConfirmedTerm(row.Chinese.Trim(), row.Vietnamese.Trim())).ToList();
        if (terms.Any(t => string.IsNullOrWhiteSpace(t.Chinese) || string.IsNullOrWhiteSpace(t.Vietnamese)))
            throw new ArgumentException("Mỗi thuật ngữ phải có cả tiếng Trung và tiếng Việt.");
        if (terms.Select(t => t.Chinese).Distinct(StringComparer.Ordinal).Count() != terms.Count)
            throw new ArgumentException("Một thuật ngữ Trung đang có nhiều dòng. Hãy chọn một bản Việt đã xác nhận.");
        return terms;
    }
    private async void OnPresetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering || !_presetsReady || PresetCombo.SelectedItem is not PromptPreset incoming) return;
        if (!BeginUiOperation()) { RefreshPresetEditor(); return; }
        try
        {
            await SavePresetEditorAsync();
            _presetManager.Select(incoming.Id);
            RefreshPresetEditor();
            await _presetStore.SaveAsync(_presetManager.Snapshot());
            await SaveProjectPresetSelectionAsync();
        }
        catch (Exception ex) { ShowError(ex); RefreshPresetEditor(); }
        finally { EndUiOperation(); }
    }
    private async void OnSavePreset(object sender, RoutedEventArgs e)
    {
        if (!BeginUiOperation()) return;
        try { await SavePresetEditorAsync(); SetStatus("Đã lưu preset cục bộ. Các lần dịch cũ vẫn giữ snapshot cũ."); }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }
    private async void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        if (!BeginUiOperation()) return;
        try
        {
            await SaveRuntimeProfileEditorAsync();
            if (_service is null)
            {
                await SavePresetEditorAsync(); ReadModelOptions().Validate(); ReadSegmenterOptions().Validate(); ReadTerms();
                await SaveCharacterProjectAsync();
            }
            else await SaveSelectedSettingsAsync();
            if (_projectSession is not null) await SaveProjectAssetsAsync();
            MarkSettingsSaved();
            SetStatus("Đã lưu AI Runtime Profile của GPU đang chọn và tài nguyên Project. Chuyển chapter không đổi cấu hình runtime.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }
    private void OnPreviewPreset(object sender, RoutedEventArgs e)
    {
        try
        {
            var preset = _presetManager.Selected with { Name = PresetNameInput.Text, Template = PresetTemplateInput.Text };
            preset.Validate();
            var service = _service;
            if (service is null)
            {
                var chapter = TranslationService.CreateChapter(TitleInput.Text, _importedOriginalText ?? ChapterInput.Text, ReadSegmenterOptions(), ReadModelOptions());
                chapter.ConfirmedTerms = ReadTerms().ToList(); chapter.ConfirmedContextNotes = ContextNotesInput.Text;
                service = new(chapter, _translationClient, _store);
            }
            string preview = service.PreviewPrompt(_selectedId ?? service.GetSnapshot().Segments[0].Id, preset, ReadTerms(), ContextNotesInput.Text);
            ShowTextWindow("Prompt thực tế cho đoạn đang chọn (hoặc đoạn đầu) — chưa gửi tới AI", preview);
        }
        catch (Exception ex) { ShowError(ex); }
    }
}
