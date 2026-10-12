using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private CharacterProjectStore _characterStore = new(CharacterProjectStore.DefaultDirectory);
    private CharacterProject _characterProject = new();
    private PronounScanReport? _pronounReport;

    private void LoadCharacterProject(CharacterProject project)
    {
        _characterProject = CharacterProjectStore.Clone(project);
        CharacterProjectNameInput.Text = _characterProject.Name;
        NormalizeAfterTranslationCheck.IsChecked = _characterProject.NormalizeAfterTranslation;
        RefreshCharacterGrid();
        ClearPronounReport();
    }

    private void RefreshCharacterGrid()
    {
        string query = _initialized && CharacterSearchInput is not null ? CharacterSearchInput.Text.Trim() : "";
        CharactersGrid.ItemsSource = null;
        CharactersGrid.ItemsSource = _characterProject.Profiles
            .Where(p => query.Length == 0 || p.VietnameseName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                        p.ChineseName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        p.Aliases.Any(a => a.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            .OrderBy(p => p.VietnameseName).ToList();
    }

    private void OnCharacterSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized && !_rendering) UpdateButtons();
    }

    private void UpdateCharacterProjectFromInputs()
    {
        _characterProject.Name = CharacterProjectNameInput.Text.Trim();
        _characterProject.NormalizeAfterTranslation = NormalizeAfterTranslationCheck.IsChecked == true;
        _characterProject.UpdatedAt = DateTimeOffset.UtcNow;
        _characterProject.Validate();
    }

    private async Task SaveCharacterProjectAsync()
    {
        UpdateCharacterProjectFromInputs();
        if (_service is not null)
        {
            await _service.ConfigureCharacterProjectAsync(_characterProject);
            var saved = _service.GetSnapshot();
            _characterProject = CharacterProjectStore.Clone(saved.CharacterProject);
            SetGlossaryTerms(saved.ConfirmedTerms);
        }
        await _characterStore.SaveAsync(_characterProject);
        RefreshCharacterGrid();
    }

    private async void OnNewCharacterProject(object sender, RoutedEventArgs e)
    {
        if (!BeginUiOperation()) return;
        try
        {
            if (_characterProject.Profiles.Count > 0 && MessageBox.Show(this,
                    "Tạo Project ID mới? Hồ sơ hiện tại vẫn còn trong tệp đã lưu nhưng sẽ không áp dụng cho chương này.",
                    "Dự án nhân vật mới", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _characterProject = new CharacterProject { Name = "Bộ truyện mới" };
            if (_service is not null) await _service.ConfigureCharacterProjectAsync(_characterProject);
            LoadCharacterProject(_characterProject);
            SetStatus("Đã tạo dự án nhân vật mới với Project ID riêng.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private void OnAddCharacter(object sender, RoutedEventArgs e)
    {
        if (WorkActive || _uiOperation) return;
        var profile = ShowCharacterEditor(new CharacterProfile());
        if (profile is null) return;
        try
        {
            if (_characterProject.Profiles.Any(p => string.Equals(p.VietnameseName, profile.VietnameseName, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Tên nhân vật đã có trong dự án.");
            _characterProject.Profiles.Add(profile); _characterProject.Validate();
            RefreshCharacterGrid(); ClearPronounReport();
            SetStatus("Đã thêm hồ sơ trong bộ nhớ. Bấm Lưu hồ sơ để ghi xuống ổ đĩa và chương.");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OnEditCharacter(object sender, RoutedEventArgs e)
    {
        if (CharactersGrid.SelectedItem is not CharacterProfile selected || WorkActive || _uiOperation) return;
        var edited = ShowCharacterEditor(CloneProfile(selected));
        if (edited is null) return;
        try
        {
            int index = _characterProject.Profiles.FindIndex(p => p.Id == selected.Id);
            _characterProject.Profiles[index] = edited; _characterProject.Validate();
            RefreshCharacterGrid(); ClearPronounReport();
            SetStatus("Đã sửa hồ sơ trong bộ nhớ. Bấm Lưu hồ sơ để ghi thay đổi.");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OnDeleteCharacter(object sender, RoutedEventArgs e)
    {
        if (CharactersGrid.SelectedItem is not CharacterProfile selected || WorkActive || _uiOperation) return;
        if (MessageBox.Show(this, $"Xóa hồ sơ {selected.VietnameseName}? Lịch sử chuẩn hóa cũ trong chương vẫn được giữ.",
                "Xóa nhân vật", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _characterProject.Profiles.RemoveAll(p => p.Id == selected.Id);
        foreach (var profile in _characterProject.Profiles) profile.Relationships.RemoveAll(r => r.TargetCharacterId == selected.Id);
        RefreshCharacterGrid(); ClearPronounReport();
    }

    private async void OnSaveCharacters(object sender, RoutedEventArgs e)
    {
        if (!BeginUiOperation()) return;
        try { await SaveCharacterProjectAsync(); SetStatus("Đã lưu hồ sơ nhân vật cục bộ và snapshot của chương."); }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private async void OnImportCharacters(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Hồ sơ nhân vật (*.characters.json)|*.characters.json|JSON (*.json)|*.json",
            InitialDirectory = CharacterProjectStore.DefaultDirectory };
        if (dialog.ShowDialog(this) != true || !BeginUiOperation()) return;
        try
        {
            var imported = await _characterStore.LoadAsync(dialog.FileName);
            if (_characterProject.Profiles.Count > 0 && imported.Id != _characterProject.Id && MessageBox.Show(this,
                    $"Thay dự án “{_characterProject.Name}” bằng “{imported.Name}” cho chương hiện tại?",
                    "Nhập hồ sơ", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            if (_service is not null) await _service.ConfigureCharacterProjectAsync(imported);
            LoadCharacterProject(imported);
            await _characterStore.SaveAsync(imported);
            SetStatus("Đã nhập hồ sơ JSON UTF-8. Project ID được giữ nguyên để tránh trộn dự án.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private async void OnExportCharacters(object sender, RoutedEventArgs e)
    {
        if (!BeginUiOperation()) return;
        try
        {
            UpdateCharacterProjectFromInputs();
            var dialog = new SaveFileDialog { Filter = "Hồ sơ nhân vật (*.characters.json)|*.characters.json", DefaultExt = ".characters.json",
                AddExtension = true, FileName = SafeFileName(_characterProject.Name) + ".characters.json" };
            if (dialog.ShowDialog(this) != true) return;
            await _characterStore.SaveToAsync(_characterProject, dialog.FileName);
            SetStatus("Đã xuất hồ sơ nhân vật JSON UTF-8 có checksum.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private async void OnPreviewCharacters(object sender, RoutedEventArgs e)
    {
        if (_service is null) { ShowError(new InvalidOperationException("Hãy chia hoặc mở một chương trước khi xem trước.")); return; }
        if (!BeginUiOperation()) return;
        try { await SaveCharacterProjectAsync(); ScanPronounsAndShow(); EditorTabs.SelectedIndex = 0; }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private void OnScanPronouns(object sender, RoutedEventArgs e)
    {
        if (_service is null || WorkActive) return;
        try { ScanPronounsAndShow(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ScanPronounsAndShow()
    {
        _pronounReport = _service!.ScanPronouns();
        PronounResultsList.ItemsSource = _pronounReport.Items.Select(i => new PronounResultItem(i,
            $"Đoạn {i.SegmentOrder} · {PronounKindLabel(i.Kind)} · “{i.Original}”" +
            (i.Replacement is null ? "" : $" → “{i.Replacement}”") + $" · {i.Reason}")).ToList();
        PronounReportSummary.Text = $"Đã kiểm tra {_pronounReport.Checked} vị trí · an toàn {_pronounReport.Safe} · " +
            $"cần xác nhận {_pronounReport.Ambiguous} · được bảo vệ {_pronounReport.Protected} · giữ nguyên {_pronounReport.Valid}.";
        SetStatus("Đã quét bằng quy tắc cục bộ. Danh sách không dùng phần trăm tin cậy và chưa tự duyệt bản dịch.");
        UpdateButtons();
    }

    private async void OnApplyPronouns(object sender, RoutedEventArgs e)
    {
        if (_service is null || _pronounReport is null || _pronounReport.Safe == 0 || !BeginUiOperation()) return;
        try
        {
            int count = _pronounReport.Safe;
            await _service.ApplySafePronounChangesAsync(_pronounReport);
            Refresh(); ScanPronounsAndShow();
            SetStatus($"Đã áp dụng {count} thay đổi có tiền ngữ rõ ràng. Bản AI gốc và lịch sử hoàn tác vẫn được giữ; trạng thái chưa được duyệt.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private async void OnUndoPronouns(object sender, RoutedEventArgs e)
    {
        if (_service is null || !BeginUiOperation()) return;
        try
        {
            var batch = await _service.UndoLatestPronounNormalizationAsync();
            if (batch is null) SetStatus("Không có lần chuẩn hóa chưa hoàn tác.");
            else { Refresh(); ScanPronounsAndShow(); SetStatus("Đã hoàn tác toàn bộ thay đổi của lần chuẩn hóa gần nhất."); }
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private void OnPronounResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering || PronounResultsList.SelectedItem is not PronounResultItem selected || _service is null) return;
        _selectedId = selected.Item.SegmentId; _shownAttemptId = null;
        EditorTabs.SelectedIndex = 1; Refresh();
        int start = Math.Min(selected.Item.StartUtf16, DraftText.Text.Length);
        int length = Math.Min(selected.Item.Length, DraftText.Text.Length - start);
        DraftText.Focus(); DraftText.Select(start, length); DraftText.ScrollToLine(DraftText.GetLineIndexFromCharacterIndex(start));
        SetStatus("Đã mở đúng đoạn và vị trí đại từ. Hãy đối chiếu câu gốc, câu dịch và lý do trước khi sửa thủ công.");
    }

    private void ClearPronounReport()
    {
        _pronounReport = null;
        if (!_initialized) return;
        PronounResultsList.ItemsSource = null;
        PronounReportSummary.Text = "";
    }

    private CharacterProfile? ShowCharacterEditor(CharacterProfile profile)
    {
        var panel = new StackPanel { Margin = new Thickness(12) };
        TextBox Field(string label, string value, int height = 30)
        {
            panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 6, 4, 2) });
            var box = new TextBox { Text = value, MinHeight = height, AcceptsReturn = height > 40, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = height > 40 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden };
            panel.Children.Add(box); return box;
        }
        ComboBox PronounField(string label, string value)
        {
            panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 6, 4, 2) });
            var box = new ComboBox { IsEditable = true, Text = value,
                ItemsSource = new[] { "nàng", "nàng ấy", "hắn", "hắn ta", "y", "người ấy" } };
            panel.Children.Add(box); return box;
        }
        var vietnamese = Field("Tên Hán Việt / tiếng Việt (bắt buộc)", profile.VietnameseName);
        var chinese = Field("Tên tiếng Trung đã xác nhận (có thể để trống; không tự suy đoán)", profile.ChineseName);
        var aliases = Field("Bí danh / tên thay thế đã xác nhận — mỗi dòng một tên", string.Join(Environment.NewLine, profile.Aliases), 70);
        panel.Children.Add(new TextBlock { Text = "Giới tính đã xác định", FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 6, 4, 2) });
        var genderChoices = new[] { new GenderChoice(CharacterGender.Unknown, "Chưa xác định"),
            new GenderChoice(CharacterGender.Female, "Nữ"), new GenderChoice(CharacterGender.Male, "Nam"),
            new GenderChoice(CharacterGender.Other, "Khác") };
        var gender = new ComboBox { ItemsSource = genderChoices, DisplayMemberPath = "Label",
            SelectedItem = genderChoices.Single(g => g.Value == profile.Gender) };
        panel.Children.Add(gender);
        var primary = PronounField("Đại từ trần thuật chính — chọn hoặc nhập thủ công", profile.PrimaryNarrativePronoun);
        var secondary = PronounField("Đại từ trần thuật phụ — chọn hoặc nhập thủ công", profile.SecondaryNarrativePronoun);
        var disallowed = Field("Cách gọi cần tránh trong lời kể — mỗi dòng một cách gọi", string.Join(Environment.NewLine, profile.DisallowedNarrativePronouns), 85);
        panel.Children.Add(new TextBlock { Text = "Quan hệ với nhân vật khác", FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 10, 4, 2) });
        panel.Children.Add(new TextBlock { Text = "Chọn nhân vật đích rồi nhập quan hệ và ghi chú xưng hô. Dữ liệu sai được giữ trong cửa sổ để sửa.",
            TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(4) });
        var relationshipRows = profile.Relationships.Select(r => new RelationshipEditorRow
        {
            TargetCharacterId = r.TargetCharacterId, Relationship = r.Relationship, AddressingNotes = r.AddressingNotes
        }).ToList();
        var relationshipGrid = new DataGrid { ItemsSource = relationshipRows, AutoGenerateColumns = false, CanUserAddRows = false,
            MinHeight = 130, MaxHeight = 230, SelectionMode = DataGridSelectionMode.Single };
        relationshipGrid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "Nhân vật", ItemsSource = _characterProject.Profiles.Where(p => p.Id != profile.Id).OrderBy(p => p.VietnameseName).ToList(),
            DisplayMemberPath = "VietnameseName", SelectedValuePath = "Id", SelectedValueBinding = new Binding(nameof(RelationshipEditorRow.TargetCharacterId)), Width = new DataGridLength(1.3, DataGridLengthUnitType.Star)
        });
        relationshipGrid.Columns.Add(new DataGridTextColumn { Header = "Quan hệ", Binding = new Binding(nameof(RelationshipEditorRow.Relationship)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        relationshipGrid.Columns.Add(new DataGridTextColumn { Header = "Ghi chú xưng hô", Binding = new Binding(nameof(RelationshipEditorRow.AddressingNotes)), Width = new DataGridLength(1.7, DataGridLengthUnitType.Star) });
        panel.Children.Add(relationshipGrid);
        var relationButtons = new WrapPanel();
        var addRelation = new Button { Content = "Thêm quan hệ" }; var removeRelation = new Button { Content = "Xóa quan hệ đã chọn" };
        relationButtons.Children.Add(addRelation); relationButtons.Children.Add(removeRelation); panel.Children.Add(relationButtons);
        addRelation.Click += (_, _) => { relationshipRows.Add(new()); relationshipGrid.Items.Refresh(); relationshipGrid.SelectedIndex = relationshipRows.Count - 1; };
        removeRelation.Click += (_, _) => { if (relationshipGrid.SelectedItem is RelationshipEditorRow row) { relationshipRows.Remove(row); relationshipGrid.Items.Refresh(); } };
        var notes = Field("Ghi chú đặc biệt về xưng hô", profile.AddressingNotes, 75);
        var enabled = new CheckBox { Content = "Bật chuẩn hóa cho nhân vật này", IsChecked = profile.NormalizationEnabled, Margin = new Thickness(4, 8, 4, 4) };
        panel.Children.Add(enabled);
        var validation = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
        panel.Children.Add(validation);
        var buttons = new WrapPanel(); var save = new Button { Content = "Lưu hồ sơ" }; var cancel = new Button { Content = "Hủy" };
        buttons.Children.Add(save); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        var window = new Window { Owner = this, Title = "Hồ sơ nhân vật", Width = 760, Height = Math.Min(860, SystemParameters.WorkArea.Height - 60), MinHeight = 600,
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        save.Click += (_, _) =>
        {
            try
            {
                relationshipGrid.CommitEdit(DataGridEditingUnit.Cell, true); relationshipGrid.CommitEdit(DataGridEditingUnit.Row, true);
                profile.VietnameseName = vietnamese.Text.Trim(); profile.ChineseName = chinese.Text.Trim();
                profile.Aliases = Lines(aliases.Text); profile.Gender = ((GenderChoice)gender.SelectedItem).Value;
                profile.PrimaryNarrativePronoun = primary.Text.Trim(); profile.SecondaryNarrativePronoun = secondary.Text.Trim();
                profile.DisallowedNarrativePronouns = Lines(disallowed.Text); profile.AddressingNotes = notes.Text.Trim();
                profile.NormalizationEnabled = enabled.IsChecked == true;
                profile.ApplyGenderDefaults();
                profile.Relationships = relationshipRows.Select(row =>
                {
                    if (row.TargetCharacterId == Guid.Empty || !_characterProject.Profiles.Any(p => p.Id == row.TargetCharacterId))
                        throw new ArgumentException("Mỗi quan hệ phải chọn một nhân vật đang có trong dự án.");
                    if (string.IsNullOrWhiteSpace(row.Relationship)) throw new ArgumentException("Quan hệ không được để trống.");
                    return new CharacterRelationship { TargetCharacterId = row.TargetCharacterId,
                        Relationship = row.Relationship.Trim(), AddressingNotes = row.AddressingNotes.Trim() };
                }).ToList();
                profile.Validate();
                validation.Text = "";
                window.DialogResult = true;
            }
            catch (Exception ex) { validation.Text = ex.Message; }
        };
        cancel.Click += (_, _) => window.DialogResult = false;
        return window.ShowDialog() == true ? profile : null;
    }

    private List<CharacterRelationship> ParseRelationships(string text, Guid self)
    {
        var result = new List<CharacterRelationship>();
        foreach (string line in Lines(text))
        {
            string[] parts = line.Split('|', 3, StringSplitOptions.TrimEntries);
            var target = _characterProject.Profiles.SingleOrDefault(p => p.Id != self &&
                string.Equals(p.VietnameseName, parts[0], StringComparison.OrdinalIgnoreCase));
            if (parts.Length < 2 || target is null) throw new ArgumentException($"Quan hệ “{line}” cần tên nhân vật đã có và dạng Tên|quan hệ|ghi chú.");
            result.Add(new() { TargetCharacterId = target.Id, Relationship = parts[1], AddressingNotes = parts.Length == 3 ? parts[2] : "" });
        }
        return result;
    }

    private static List<string> Lines(string text) => text.ReplaceLineEndings("\n").Split('\n',
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static CharacterProfile CloneProfile(CharacterProfile source) => new()
    {
        Id = source.Id, ChineseName = source.ChineseName, VietnameseName = source.VietnameseName,
        Aliases = [.. source.Aliases], Gender = source.Gender, Relationships = [.. source.Relationships],
        PrimaryNarrativePronoun = source.PrimaryNarrativePronoun, SecondaryNarrativePronoun = source.SecondaryNarrativePronoun,
        DisallowedNarrativePronouns = [.. source.DisallowedNarrativePronouns], AddressingNotes = source.AddressingNotes,
        NormalizationEnabled = source.NormalizationEnabled
    };

    private static string SafeFileName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private static string PronounKindLabel(PronounSuggestionKind kind) => kind switch
    {
        PronounSuggestionKind.SafeReplacement => "có thể áp dụng", PronounSuggestionKind.Ambiguous => "cần xác nhận",
        PronounSuggestionKind.Protected => "đoạn đã bảo vệ", _ => "hợp lệ / giữ nguyên"
    };
    private sealed record PronounResultItem(PronounSuggestion Item, string Label);
    private sealed record GenderChoice(CharacterGender Value, string Label);
    private sealed class RelationshipEditorRow
    {
        public Guid TargetCharacterId { get; set; }
        public string Relationship { get; set; } = "";
        public string AddressingNotes { get; set; } = "";
    }
}
