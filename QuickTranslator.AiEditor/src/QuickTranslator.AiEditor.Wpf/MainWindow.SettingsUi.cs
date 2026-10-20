using System.IO;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private bool _settingsDirtyValue;
    // Every assignment also updates the footer indicator, so each settings page shows unsaved changes the same way.
    private bool _settingsDirty
    {
        get => _settingsDirtyValue;
        set { _settingsDirtyValue = value; UpdateSettingsDirtyIndicator(); }
    }

    private void UpdateSettingsDirtyIndicator()
    {
        if (SettingsDirtyText is null) return;
        SettingsDirtyText.Text = _settingsDirtyValue ? "Có thay đổi chưa lưu" : "Mọi thay đổi đã được lưu";
        SettingsDirtyText.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, _settingsDirtyValue ? "WarningBrush" : "MutedBrush");
    }
    private bool _syncingGlossary;
    private readonly ObservableCollection<GlossaryRow> _glossaryRows = [];

    private void InitializeSettingsUi()
    {
        GlossaryGrid.ItemsSource = _glossaryRows;
        foreach (var input in new[] { TemperatureInput, ContextInput, OutputTokensInput, TimeoutInput,
                     MinimumInput, TargetInput, MaximumInput, CharacterProjectNameInput })
            input.TextChanged += OnSettingsInputChanged;
        InitializeOllamaBackendUi();
        NormalizeAfterTranslationCheck.Checked += OnSettingsInputChanged;
        NormalizeAfterTranslationCheck.Unchecked += OnSettingsInputChanged;
        UpdateSettingsCounters();
    }

    private void OnSettingsNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsAiPanel is null || SettingsNavigation.SelectedItem is not ListBoxItem item) return;
        string selected = item.Tag?.ToString() ?? "Ai";
        SettingsAiPanel.Visibility = selected == "Ai" ? Visibility.Visible : Visibility.Collapsed;
        SettingsTraditionalPanel.Visibility = selected == "Traditional" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPromptPanel.Visibility = selected == "Prompts" ? Visibility.Visible : Visibility.Collapsed;
        SettingsDictionaryPanel.Visibility = selected == "Dictionary" ? Visibility.Visible : Visibility.Collapsed;
        SettingsCharactersPanel.Visibility = selected == "Characters" ? Visibility.Visible : Visibility.Collapsed;
        SettingsQualityPanel.Visibility = selected == "Quality" ? Visibility.Visible : Visibility.Collapsed;
        SettingsAppearancePanel.Visibility = selected == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        if (_settingsDirty && _initialized) SetStatus("Thiết lập có thay đổi chưa lưu. Nội dung vẫn được giữ khi chuyển nhóm.");
    }

    private void OnSettingsInputChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _rendering || _syncingGlossary) return;
        _settingsDirty = true;
        UpdateSettingsCounters();
        SetStatus("Thiết lập có thay đổi chưa lưu.");
    }

    private void UpdateSettingsCounters()
    {
        if (TermsCountText is null) return;
        TermsCountText.Text = $"{_glossaryRows.Count} thuật ngữ";
        ContextCountText.Text = $"{ContextNotesInput.Text.Length}/1.000 ký tự";
    }

    private async void OnImportGlossary(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Từ điển (*.txt;*.json)|*.txt;*.json|Tất cả tệp|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        if (!BeginUiOperation()) return;
        try
        {
            string content = await File.ReadAllTextAsync(dialog.FileName, new UTF8Encoding(false, true));
            if (Path.GetExtension(dialog.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                var terms = JsonSerializer.Deserialize<List<ConfirmedTerm>>(content, DocumentJson.Options)
                    ?? throw new InvalidDataException("JSON không chứa danh sách thuật ngữ.");
                SetGlossaryTerms(terms);
            }
            else SetGlossaryTerms(ParseGlossaryText(content));
            ReadTerms();
            _settingsDirty = true;
            SetStatus("Đã nhập và kiểm tra từ điển trong bộ nhớ. Bấm Lưu để áp dụng.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private async void OnExportGlossary(object sender, RoutedEventArgs e)
    {
        try
        {
            var terms = ReadTerms();
            var dialog = new SaveFileDialog { Filter = "Từ điển UTF-8 (*.txt)|*.txt", FileName = "glossary.txt", AddExtension = true, DefaultExt = ".txt" };
            if (dialog.ShowDialog(this) != true) return;
            await File.WriteAllTextAsync(dialog.FileName, string.Join(Environment.NewLine, terms.Select(t => t.Chinese + "=" + t.Vietnamese)), new UTF8Encoding(false));
            SetStatus("Đã xuất từ điển TXT UTF-8.");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OnCharacterSearchChanged(object sender, TextChangedEventArgs e) => RefreshCharacterGrid();

    private void SetGlossaryTerms(IEnumerable<ConfirmedTerm> terms)
    {
        _syncingGlossary = true;
        try
        {
            _glossaryRows.Clear();
            foreach (var term in terms) _glossaryRows.Add(new(term.Chinese, term.Vietnamese));
            TermsInput.Text = string.Join(Environment.NewLine, _glossaryRows.Select(t => t.Chinese + "=" + t.Vietnamese));
            UpdateSettingsCounters();
        }
        finally { _syncingGlossary = false; }
    }

    private static IReadOnlyList<ConfirmedTerm> ParseGlossaryText(string content)
    {
        var terms = new List<ConfirmedTerm>();
        int lineNumber = 0;
        foreach (string line in content.ReplaceLineEndings("\n").Split('\n'))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            int separator = line.IndexOf('=');
            if (separator < 1 || separator == line.Length - 1)
                throw new ArgumentException($"Dòng {lineNumber} cần định dạng Trung=Việt.");
            terms.Add(new(line[..separator].Trim(), line[(separator + 1)..].Trim()));
        }
        return terms;
    }

    private void OnAddGlossaryTerm(object sender, RoutedEventArgs e)
    {
        var term = ShowGlossaryEditor(null);
        if (term is null) return;
        _glossaryRows.Add(term);
        GlossaryChanged();
    }

    private void OnEditGlossaryTerm(object sender, RoutedEventArgs e)
    {
        if (GlossaryGrid.SelectedItem is not GlossaryRow selected) return;
        var term = ShowGlossaryEditor(selected);
        if (term is null) return;
        int index = _glossaryRows.IndexOf(selected);
        _glossaryRows[index] = term;
        GlossaryChanged();
    }

    private void OnDeleteGlossaryTerm(object sender, RoutedEventArgs e)
    {
        if (GlossaryGrid.SelectedItem is not GlossaryRow selected) return;
        if (ThemedDialog.Show(this, $"Xóa thuật ngữ {selected.Chinese} = {selected.Vietnamese}?", "Xóa thuật ngữ",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _glossaryRows.Remove(selected);
        GlossaryChanged();
    }

    private GlossaryRow? ShowGlossaryEditor(GlossaryRow? source, string? chineseSeed = null)
    {
        var chinese = new TextBox { Text = source?.Chinese ?? chineseSeed ?? "" };
        var vietnamese = new TextBox { Text = source?.Vietnamese ?? "" };
        var validation = new TextBlock { TextWrapping = TextWrapping.Wrap };
        validation.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "DangerBrush");
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Tiếng Trung", FontWeight = FontWeights.SemiBold }); panel.Children.Add(chinese);
        panel.Children.Add(new TextBlock { Text = "Tiếng Việt đã xác nhận", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) }); panel.Children.Add(vietnamese);
        panel.Children.Add(validation);
        var save = new Button { Content = "Lưu" }; save.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        var cancel = new Button { Content = "Hủy" };
        var root = new DockPanel();
        Border footer = ThemedDialog.Footer(save, cancel);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(panel);
        var window = new Window { Owner = this, Title = source is null ? "Thêm thuật ngữ" : "Sửa thuật ngữ", Width = 540, MinWidth = 420, SizeToContent = SizeToContent.Height, Content = root };
        window.SetResourceReference(BackgroundProperty, "PanelBrush");
        window.SetResourceReference(ForegroundProperty, "TextBrush");
        ClampWindowToWorkArea(window, 540, 420);
        GlossaryRow? result = null;
        save.Click += (_, _) =>
        {
            string key = chinese.Text.Trim(), value = vietnamese.Text.Trim();
            if (key.Length == 0 || value.Length == 0) { validation.Text = "Cả hai ô đều bắt buộc."; return; }
            if (_glossaryRows.Any(row => !ReferenceEquals(row, source) && string.Equals(row.Chinese, key, StringComparison.Ordinal)))
            { validation.Text = "Từ tiếng Trung này đã có. Hãy sửa mục hiện tại thay vì tạo trùng."; return; }
            result = new(key, value); window.DialogResult = true;
        };
        cancel.Click += (_, _) => window.DialogResult = false;
        return window.ShowDialog() == true ? result : null;
    }

    private void GlossaryChanged()
    {
        if (_syncingGlossary) return;
        TermsInput.Text = string.Join(Environment.NewLine, _glossaryRows.Select(t => t.Chinese + "=" + t.Vietnamese));
        _settingsDirty = true;
        UpdateSettingsCounters();
        SetStatus("Từ điển có thay đổi chưa lưu.");
    }

    private async void OnOpenPresetManager(object sender, RoutedEventArgs e)
    {
        if (!_presetsReady || !BeginUiOperation()) return;
        try
        {
            await SavePresetEditorAsync();
            var working = new PromptPresetManager(_presetManager.Snapshot());
            var list = new ListBox { Width = 250, DisplayMemberPath = "Name", Margin = new Thickness(4) };
            var name = new TextBox();
            var description = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 64 };
            var template = new TextBox { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            var validation = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
            validation.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "DangerBrush");
            bool dirty = false, loading = false;

            void RefreshList(Guid? select = null)
            {
                loading = true;
                list.ItemsSource = null; list.ItemsSource = working.Presets;
                list.SelectedItem = working.Presets.FirstOrDefault(p => p.Id == (select ?? working.Selected.Id));
                loading = false;
                LoadSelected();
            }
            void LoadSelected()
            {
                if (list.SelectedItem is not PromptPreset preset) return;
                loading = true; working.Select(preset.Id); name.Text = preset.Name; description.Text = preset.Description; template.Text = preset.Template; validation.Text = ""; loading = false;
            }
            bool SaveEditor()
            {
                try
                {
                    if (list.SelectedItem is not PromptPreset preset) return false;
                    working.Update(preset.Id, name.Text, template.Text, description.Text);
                    dirty = false; validation.Text = "Đã kiểm tra hợp lệ trong bộ nhớ."; RefreshList(preset.Id); return true;
                }
                catch (Exception ex) { validation.Text = ex.Message; return false; }
            }

            list.SelectionChanged += (_, _) => { if (!loading) { if (dirty && !SaveEditor()) { list.SelectedItem = working.Selected; return; } LoadSelected(); } };
            name.TextChanged += (_, _) => { if (!loading) dirty = true; };
            description.TextChanged += (_, _) => { if (!loading) dirty = true; };
            template.TextChanged += (_, _) => { if (!loading) dirty = true; };

            var leftButtons = new WrapPanel();
            var create = new Button { Content = "Tạo" }; var duplicate = new Button { Content = "Nhân bản" }; var delete = new Button { Content = "Xóa" };
            delete.SetResourceReference(FrameworkElement.StyleProperty, "DangerButton");
            leftButtons.Children.Add(create); leftButtons.Children.Add(duplicate); leftButtons.Children.Add(delete);
            create.Click += (_, _) => { if (dirty && !SaveEditor()) return; var p = working.Create("Preset mới"); RefreshList(p.Id); };
            duplicate.Click += (_, _) => { if (dirty && !SaveEditor()) return; var p = working.Duplicate(working.Selected.Id, working.Selected.Name + " (bản sao)"); RefreshList(p.Id); };
            delete.Click += (_, _) => { try { working.Delete(working.Selected.Id); RefreshList(); } catch (Exception ex) { validation.Text = ex.Message; } };

            var save = new Button { Content = "Lưu thay đổi" }; save.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
            var restore = new Button { Content = "Khôi phục prompt mặc định" }; var close = new Button { Content = "Đóng" };
            var editor = new Grid(); editor.RowDefinitions.Add(new() { Height = GridLength.Auto }); editor.RowDefinitions.Add(new() { Height = GridLength.Auto }); editor.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); editor.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var namePanel = new DockPanel(); namePanel.Children.Add(new TextBlock { Text = "Tên preset", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }); namePanel.Children.Add(name); DockPanel.SetDock(namePanel.Children[0], Dock.Left); editor.Children.Add(namePanel);
            var descriptionPanel = new DockPanel(); descriptionPanel.Children.Add(new TextBlock { Text = "Mô tả", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 10, 4, 0) }); descriptionPanel.Children.Add(description); DockPanel.SetDock(descriptionPanel.Children[0], Dock.Left); Grid.SetRow(descriptionPanel, 1); editor.Children.Add(descriptionPanel);
            Grid.SetRow(template, 2); editor.Children.Add(template); Grid.SetRow(validation, 3); editor.Children.Add(validation);
            var grid = new Grid { Margin = new Thickness(24) }; grid.ColumnDefinitions.Add(new() { Width = new GridLength(270) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var left = new DockPanel(); DockPanel.SetDock(leftButtons, Dock.Bottom); left.Children.Add(leftButtons); left.Children.Add(list); grid.Children.Add(left); Grid.SetColumn(editor, 1); grid.Children.Add(editor);
            var root = new DockPanel();
            Border footer = ThemedDialog.Footer(save, close, restore);
            DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(grid);
            var window = new Window { Owner = this, Title = "Quản lý Prompt Preset", Width = 1050, Height = 720, MinWidth = 800, MinHeight = 520, Content = root };
            window.SetResourceReference(BackgroundProperty, "PanelBrush");
            window.SetResourceReference(ForegroundProperty, "TextBrush");
            ClampWindowToWorkArea(window, 1050, 720);
            save.Click += (_, _) => SaveEditor();
            restore.Click += (_, _) => { template.Text = PromptPreset.DefaultTemplate; dirty = true; validation.Text = "Đã nạp nội dung mặc định; bấm Lưu thay đổi để xác nhận."; };
            close.Click += (_, _) => { if (dirty && ThemedDialog.Show(window, "Bỏ thay đổi chưa lưu trong cửa sổ preset?", "Đóng", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; window.DialogResult = true; };
            window.Closing += (_, args) => { if (window.DialogResult != true && dirty && ThemedDialog.Show(window, "Bỏ thay đổi chưa lưu trong cửa sổ preset?", "Đóng", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) args.Cancel = true; };
            RefreshList();
            if (window.ShowDialog() == true)
            {
                _presetManager = working;
                await _presetStore.SaveAsync(_presetManager.Snapshot());
                await SaveProjectPresetSelectionAsync();
                RefreshPresetEditor();
                SetStatus("Đã lưu thư viện preset cục bộ. Snapshot của các lần dịch cũ không đổi.");
            }
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private sealed record GlossaryRow(string Chinese, string Vietnamese);
}
