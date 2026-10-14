using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Translation;

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
        ConsistencyLockCheck.IsChecked = _characterProject.ConsistencyLockEnabled;
        RefreshCharacterGrid();
        ClearPronounReport();
        if (_initialized && _service is not null && _selectedId is Guid id)
            RefreshTranslationInspector(_service.GetSnapshot().Segments.SingleOrDefault(s => s.Id == id));
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
        CharactersGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        CharactersGrid.CommitEdit(DataGridEditingUnit.Row, true);
        _characterProject.Name = CharacterProjectNameInput.Text.Trim();
        _characterProject.NormalizeAfterTranslation = NormalizeAfterTranslationCheck.IsChecked == true;
        _characterProject.ConsistencyLockEnabled = ConsistencyLockCheck.IsChecked == true;
        _characterProject.UpdatedAt = DateTimeOffset.UtcNow;
        _characterProject.Validate();
    }

    private async Task SaveCharacterProjectAsync()
    {
        if (_projectSession is not null)
        {
            await SaveProjectResourcesAsync();
            RefreshCharacterGrid();
            if (_service is not null && _selectedId is Guid selId)
                RefreshTranslationInspector(_service.GetSnapshot().Segments.SingleOrDefault(s => s.Id == selId));
            return;
        }
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
        if (_service is not null && _selectedId is Guid activeId)
            RefreshTranslationInspector(_service.GetSnapshot().Segments.SingleOrDefault(s => s.Id == activeId));
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
            if (_projectSession is not null)
            {
                _characterProject.Id = _projectSession.Manifest.ProjectId;
                await SaveProjectResourcesAsync();
            }
            else if (_service is not null) await _service.ConfigureCharacterProjectAsync(_characterProject);
            LoadCharacterProject(_characterProject);
            SetStatus(_projectSession is null ? "Đã tạo dự án nhân vật mới với Project ID riêng." : "Đã tạo bộ hồ sơ mới thuộc dự án truyện đang mở.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private async void OnAddCharacter(object sender, RoutedEventArgs e)
    {
        if (WorkActive || _uiOperation) return;
        var profile = ShowCharacterEditor(new CharacterProfile());
        if (profile is null) return;
        if (!BeginUiOperation()) return;
        try
        {
            if (_characterProject.Profiles.Any(p => p.Id != profile.Id &&
                    string.Equals(p.VietnameseName, profile.VietnameseName, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Tên nhân vật đã có trong dự án.");
            int existingIdx = _characterProject.Profiles.FindIndex(p => p.Id == profile.Id);
            if (existingIdx >= 0) _characterProject.Profiles[existingIdx] = profile;
            else _characterProject.Profiles.Add(profile);
            _characterProject.Validate();
            RefreshCharacterGrid();
            ClearPronounReport();
            await SaveCharacterProjectAsync();
            SetStatus("Đã thêm và lưu hồ sơ nhân vật vào tài nguyên dự án.");
        }
        catch (Exception ex)
        {
            // Adjustment B: retain confirmed inputs in memory on write failure so user can retry Save.
            _settingsDirty = true;
            RefreshCharacterGrid();
            ShowError(ex);
        }
        finally { EndUiOperation(); }
    }

    private async void OnEditCharacter(object sender, RoutedEventArgs e)
    {
        if (CharactersGrid.SelectedItem is not CharacterProfile selected || WorkActive || _uiOperation) return;
        var edited = ShowCharacterEditor(CloneProfile(selected));
        if (edited is null) return;
        if (!BeginUiOperation()) return;
        try
        {
            int index = _characterProject.Profiles.FindIndex(p => p.Id == selected.Id);
            if (index >= 0) _characterProject.Profiles[index] = edited;
            _characterProject.Validate();
            RefreshCharacterGrid();
            ClearPronounReport();
            await SaveCharacterProjectAsync();
            SetStatus("Đã cập nhật và lưu hồ sơ nhân vật vào tài nguyên dự án.");
        }
        catch (Exception ex)
        {
            _settingsDirty = true;
            RefreshCharacterGrid();
            ShowError(ex);
        }
        finally { EndUiOperation(); }
    }

    private async void OnDeleteCharacter(object sender, RoutedEventArgs e)
    {
        if (CharactersGrid.SelectedItem is not CharacterProfile selected || WorkActive || _uiOperation) return;
        if (MessageBox.Show(this, $"Xóa hồ sơ {selected.VietnameseName}? Lịch sử chuẩn hóa cũ trong chương vẫn được giữ.",
                "Xóa nhân vật", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!BeginUiOperation()) return;
        try
        {
            _characterProject.Profiles.RemoveAll(p => p.Id == selected.Id);
            foreach (var profile in _characterProject.Profiles)
                profile.Relationships.RemoveAll(r => r.EffectiveListenerCharacterId == selected.Id);
            RefreshCharacterGrid();
            ClearPronounReport();
            await SaveCharacterProjectAsync();
            SetStatus($"Đã xóa hồ sơ {selected.VietnameseName} và lưu tài nguyên dự án.");
        }
        catch (Exception ex)
        {
            _settingsDirty = true;
            RefreshCharacterGrid();
            ShowError(ex);
        }
        finally { EndUiOperation(); }
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
            CharacterConsistencyProtection.PreserveLockedRules(_characterProject, imported);
            if (_projectSession is not null)
            {
                imported.Id = _projectSession.Manifest.ProjectId;
                LoadCharacterProject(imported);
                await SaveProjectResourcesAsync();
            }
            else
            {
                if (_service is not null) await _service.ConfigureCharacterProjectAsync(imported);
                LoadCharacterProject(imported);
                await _characterStore.SaveAsync(imported);
            }
            SetStatus(_projectSession is null ? "Đã nhập hồ sơ JSON UTF-8, giữ Project ID gốc." : "Đã nhập hồ sơ vào tài nguyên của dự án truyện đang mở.");
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
        try
        {
            await SaveCharacterProjectAsync();
            Guid segmentId = _selectedId ?? _service.GetSnapshot().Segments.First().Id;
            CharacterConsistencySelection preview = _service.PreviewCharacterConsistency(segmentId);
            var text = new TextBox { Text = string.IsNullOrWhiteSpace(preview.PromptBlock)
                    ? "Không có quy tắc khóa phù hợp với nhân vật xuất hiện trong đoạn đang chọn."
                    : preview.PromptBlock,
                IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12) };
            var dialog = new Window { Owner = this, Title = "Xem trước Character Consistency", Width = 760, Height = 520, Content = text };
            ClampWindowToWorkArea(dialog, 760, 520);
            _ = dialog.ShowDialog();
        }
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
        var items = _pronounReport.Items.Select(i => new PronounResultItem(i,
            $"Đoạn {i.SegmentOrder} · {PronounKindLabel(i.Kind)} · “{i.Original}”" +
            (i.Replacement is null ? "" : $" → “{i.Replacement}”") + $" · {i.Reason}")).ToList();
        string summary = $"Đã kiểm tra {_pronounReport.Checked} vị trí · an toàn {_pronounReport.Safe} · " +
            $"cần xác nhận {_pronounReport.Ambiguous} · được bảo vệ {_pronounReport.Protected} · giữ nguyên {_pronounReport.Valid}.";
        PronounResultsList.ItemsSource = items;
        PronounReportSummary.Text = summary;
        if (FindName("InspectorPronounResultsList") is ListBox inspectorList) inspectorList.ItemsSource = items;
        if (FindName("InspectorPronounSummaryText") is TextBlock inspectorSummary) inspectorSummary.Text = summary;
        SetStatus("Đã quét bằng quy tắc cục bộ. Chọn kết quả ngay trên Editor/Inspector để đối chiếu hoặc bấm Áp dụng an toàn.");
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
        if (_rendering || (sender as ListBox)?.SelectedItem is not PronounResultItem selected || _service is null) return;
        _selectedId = selected.Item.SegmentId;
        _shownAttemptId = null;
        _service.SelectSegment(selected.Item.SegmentId);
        // BL-05 (CC-06): Do not force-jump between tabs when already in Editor (Two-Pane or Four-Pane);
        // only leave Settings (Tab 2) to the user's active Editor mode.
        if (EditorTabs.SelectedIndex == 2)
        {
            ReaderModeButton.IsChecked = false;
            EditorModeButton.IsChecked = true;
            EditorTabs.SelectedIndex = _workspaceLayout.FourPaneMode ? 1 : 0;
        }
        Refresh();
        if (EditorTabs.SelectedIndex == 1)
        {
            int start = Math.Min(selected.Item.StartUtf16, DraftText.Text.Length);
            int length = Math.Min(selected.Item.Length, DraftText.Text.Length - start);
            DraftText.Focus();
            DraftText.Select(start, length);
            DraftText.ScrollToLine(DraftText.GetLineIndexFromCharacterIndex(start));
        }
        else
        {
            Dispatcher.BeginInvoke(() =>
            {
                TextBox? blockBox = FindVisualChildren<TextBox>(ChapterBlocksItems)
                    .FirstOrDefault(input => input.Tag is Guid id && id == selected.Item.SegmentId);
                if (blockBox is not null)
                {
                    int start = Math.Min(selected.Item.StartUtf16, blockBox.Text.Length);
                    int length = Math.Min(selected.Item.Length, blockBox.Text.Length - start);
                    blockBox.Focus();
                    blockBox.Select(start, length);
                    blockBox.BringIntoView();
                }
            });
        }
        SetStatus("Đã mở đúng đoạn và vị trí đại từ ngay tại chỗ. Hãy đối chiếu câu gốc, câu dịch và lý do trước khi sửa hoặc áp dụng.");
    }

    private void ClearPronounReport()
    {
        _pronounReport = null;
        if (!_initialized) return;
        PronounResultsList.ItemsSource = null;
        PronounReportSummary.Text = "";
        if (FindName("InspectorPronounResultsList") is ListBox inspectorList) inspectorList.ItemsSource = null;
        if (FindName("InspectorPronounSummaryText") is TextBlock inspectorSummary) inspectorSummary.Text = "";
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
        var narrativeLocked = new CheckBox { Content = "Khóa đại từ trần thuật chính cho nhân vật này",
            IsChecked = profile.NarrativePronounLocked, Margin = new Thickness(4, 8, 4, 4) };
        panel.Children.Add(narrativeLocked);
        panel.Children.Add(new TextBlock { Text = "Khi khóa nhất quán của Project bật, đại từ phụ không thay thế đại từ chính trong prompt hoặc chuẩn hóa.",
            TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"), Margin = new Thickness(4) });
        var disallowed = Field("Cách gọi cần tránh trong lời kể — mỗi dòng một cách gọi", string.Join(Environment.NewLine, profile.DisallowedNarrativePronouns), 85);
        panel.Children.Add(new TextBlock { Text = "Quan hệ với nhân vật khác", FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 10, 4, 2) });
        panel.Children.Add(new TextBlock { Text = $"Người nói: {profile.VietnameseName}. Chọn người nghe; mỗi chiều A→B và B→A là quy tắc độc lập, cần xác nhận riêng và không ghi đè quy tắc đã Khóa.",
            TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"), Margin = new Thickness(4) });

        var createdListeners = new List<CharacterProfile>();
        List<CharacterProfile> AvailableListeners() => _characterProject.Profiles
            .Concat(createdListeners)
            .Where(p => p.Id != profile.Id)
            .DistinctBy(p => p.Id)
            .OrderBy(p => p.VietnameseName)
            .ToList();

        var relationshipRows = profile.Relationships.Select(r =>
        {
            var existingReverse = _characterProject.Profiles.SingleOrDefault(p => p.Id == r.EffectiveListenerCharacterId)?
                .Relationships.FirstOrDefault(rev => rev.EffectiveListenerCharacterId == profile.Id);
            return new RelationshipEditorRow
            {
                TargetCharacterId = r.EffectiveListenerCharacterId,
                Relationship = r.Relationship,
                AddressingNotes = r.AddressingNotes,
                SelfReference = r.SelfReference,
                AddresseeReference = r.AddresseeReference,
                Enabled = r.Enabled,
                Locked = r.Locked,
                SyncReverse = false,
                ReverseRelationship = existingReverse?.Relationship ?? "",
                ReverseSelfReference = existingReverse?.SelfReference ?? "",
                ReverseAddresseeReference = existingReverse?.AddresseeReference ?? ""
            };
        }).ToList();

        var listenerColumn = new DataGridComboBoxColumn
        {
            Header = "Người nghe",
            ItemsSource = AvailableListeners(),
            DisplayMemberPath = "VietnameseName",
            SelectedValuePath = "Id",
            SelectedValueBinding = new Binding(nameof(RelationshipEditorRow.TargetCharacterId)),
            Width = new DataGridLength(1.2, DataGridLengthUnitType.Star)
        };
        var relationshipGrid = new DataGrid
        {
            ItemsSource = relationshipRows, AutoGenerateColumns = false, CanUserAddRows = false,
            MinHeight = 140, MaxHeight = 230, SelectionMode = DataGridSelectionMode.Single
        };
        relationshipGrid.Columns.Add(listenerColumn);
        relationshipGrid.Columns.Add(new DataGridTextColumn { Header = "Quan hệ (A→B)", Binding = new Binding(nameof(RelationshipEditorRow.Relationship)), Width = new DataGridLength(0.95, DataGridLengthUnitType.Star) });
        relationshipGrid.Columns.Add(new DataGridTextColumn { Header = "Tự xưng", Binding = new Binding(nameof(RelationshipEditorRow.SelfReference)), Width = new DataGridLength(0.75, DataGridLengthUnitType.Star) });
        relationshipGrid.Columns.Add(new DataGridTextColumn { Header = "Gọi đối phương", Binding = new Binding(nameof(RelationshipEditorRow.AddresseeReference)), Width = new DataGridLength(0.85, DataGridLengthUnitType.Star) });
        relationshipGrid.Columns.Add(new DataGridCheckBoxColumn { Header = "Bật", Binding = new Binding(nameof(RelationshipEditorRow.Enabled)), Width = 48 });
        relationshipGrid.Columns.Add(new DataGridCheckBoxColumn { Header = "Khóa", Binding = new Binding(nameof(RelationshipEditorRow.Locked)), Width = 52 });
        relationshipGrid.Columns.Add(new DataGridCheckBoxColumn { Header = "Xác nhận B→A", Binding = new Binding(nameof(RelationshipEditorRow.SyncReverse)), Width = 92 });
        relationshipGrid.Columns.Add(new DataGridTextColumn { Header = "B tự xưng", Binding = new Binding(nameof(RelationshipEditorRow.ReverseSelfReference)), Width = new DataGridLength(0.75, DataGridLengthUnitType.Star) });
        relationshipGrid.Columns.Add(new DataGridTextColumn { Header = "B gọi A", Binding = new Binding(nameof(RelationshipEditorRow.ReverseAddresseeReference)), Width = new DataGridLength(0.75, DataGridLengthUnitType.Star) });
        relationshipGrid.Columns.Add(new DataGridTextColumn { Header = "Ghi chú", Binding = new Binding(nameof(RelationshipEditorRow.AddressingNotes)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        panel.Children.Add(relationshipGrid);

        var presetBar = new WrapPanel { Margin = new Thickness(0, 4, 0, 2) };
        var presetCombo = new ComboBox
        {
            Width = 260,
            ItemsSource = CharacterRelationshipPreset.Defaults,
            DisplayMemberPath = nameof(CharacterRelationshipPreset.Label),
            SelectedIndex = 0,
            ToolTip = "Chọn mẫu quan hệ & xưng hô phổ biến"
        };
        var applyPresetBtn = new Button { Content = "Áp dụng mẫu vào dòng chọn" };
        var suggestReverseBtn = new Button { Content = "Gợi ý đảo chiều (B→A)", ToolTip = "Điền gợi ý đảo chiều vào cột B tự xưng / B gọi A để bạn kiểm tra và xác nhận" };
        presetBar.Children.Add(presetCombo);
        presetBar.Children.Add(applyPresetBtn);
        presetBar.Children.Add(suggestReverseBtn);
        panel.Children.Add(presetBar);

        var relationButtons = new WrapPanel();
        var addRelation = new Button { Content = "Thêm quan hệ" };
        var removeRelation = new Button { Content = "Xóa quan hệ đã chọn" };
        var createListenerBtn = new Button { Content = "+ Tạo nhân vật người nghe mới…" };
        relationButtons.Children.Add(addRelation);
        relationButtons.Children.Add(removeRelation);
        relationButtons.Children.Add(createListenerBtn);
        panel.Children.Add(relationButtons);

        addRelation.Click += (_, _) =>
        {
            relationshipRows.Add(new() { Enabled = true });
            relationshipGrid.Items.Refresh();
            relationshipGrid.SelectedIndex = relationshipRows.Count - 1;
        };
        removeRelation.Click += (_, _) =>
        {
            if (relationshipGrid.SelectedItem is RelationshipEditorRow row)
            {
                relationshipRows.Remove(row);
                relationshipGrid.Items.Refresh();
            }
        };
        applyPresetBtn.Click += (_, _) =>
        {
            if (presetCombo.SelectedItem is not CharacterRelationshipPreset preset) return;
            RelationshipEditorRow row = relationshipGrid.SelectedItem as RelationshipEditorRow ?? new RelationshipEditorRow();
            if (!relationshipRows.Contains(row))
            {
                relationshipRows.Add(row);
                relationshipGrid.SelectedItem = row;
            }
            row.Relationship = preset.Relationship;
            row.SelfReference = preset.SelfReference;
            row.AddresseeReference = preset.AddresseeReference;
            row.Enabled = true;
            row.ReverseRelationship = preset.ReverseRelationship;
            row.ReverseSelfReference = preset.ReverseSelfReference;
            row.ReverseAddresseeReference = preset.ReverseAddresseeReference;
            relationshipGrid.Items.Refresh();
        };
        suggestReverseBtn.Click += (_, _) =>
        {
            if (relationshipGrid.SelectedItem is not RelationshipEditorRow row) return;
            if (string.IsNullOrWhiteSpace(row.ReverseSelfReference)) row.ReverseSelfReference = row.AddresseeReference;
            if (string.IsNullOrWhiteSpace(row.ReverseAddresseeReference)) row.ReverseAddresseeReference = row.SelfReference;
            if (string.IsNullOrWhiteSpace(row.ReverseRelationship)) row.ReverseRelationship = row.Relationship;
            relationshipGrid.Items.Refresh();
        };

        var notes = Field("Ghi chú đặc biệt về xưng hô", profile.AddressingNotes, 75);
        var enabled = new CheckBox { Content = "Bật chuẩn hóa cho nhân vật này", IsChecked = profile.NormalizationEnabled, Margin = new Thickness(4, 8, 4, 4) };
        panel.Children.Add(enabled);
        var validation = new TextBlock { Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
        panel.Children.Add(validation);
        var buttons = new WrapPanel(); var save = new Button { Content = "Lưu hồ sơ", Style = (Style)FindResource("PrimaryButton") }; var cancel = new Button { Content = "Hủy" };
        buttons.Children.Add(save); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        var window = new Window { Owner = this, Title = "Hồ sơ nhân vật", Width = 860, Height = Math.Min(860, SystemParameters.WorkArea.Height - 60), MinHeight = 560,
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        ClampWindowToWorkArea(window, 860, 860);

        createListenerBtn.Click += (_, _) =>
        {
            CharacterProfile? created = ShowMinimalListenerCreatorDialog(window, AvailableListeners());
            if (created is null) return;
            createdListeners.Add(created);
            listenerColumn.ItemsSource = AvailableListeners();
            if (relationshipGrid.SelectedItem is RelationshipEditorRow currentRow && currentRow.TargetCharacterId == Guid.Empty)
                currentRow.TargetCharacterId = created.Id;
            else
            {
                var added = new RelationshipEditorRow { TargetCharacterId = created.Id, Enabled = true };
                relationshipRows.Add(added);
                relationshipGrid.SelectedItem = added;
            }
            relationshipGrid.Items.Refresh();
        };

        save.Click += (_, _) =>
        {
            try
            {
                relationshipGrid.CommitEdit(DataGridEditingUnit.Cell, true); relationshipGrid.CommitEdit(DataGridEditingUnit.Row, true);
                profile.VietnameseName = vietnamese.Text.Trim(); profile.ChineseName = chinese.Text.Trim();
                profile.Aliases = Lines(aliases.Text); profile.Gender = ((GenderChoice)gender.SelectedItem).Value;
                profile.PrimaryNarrativePronoun = primary.Text.Trim(); profile.SecondaryNarrativePronoun = secondary.Text.Trim();
                profile.NarrativePronounLocked = narrativeLocked.IsChecked == true;
                profile.DisallowedNarrativePronouns = Lines(disallowed.Text); profile.AddressingNotes = notes.Text.Trim();
                profile.NormalizationEnabled = enabled.IsChecked == true;
                profile.ApplyGenderDefaults();
                var allCandidates = AvailableListeners();
                profile.Relationships = relationshipRows.Select(row =>
                {
                    if (row.TargetCharacterId == Guid.Empty || !allCandidates.Any(p => p.Id == row.TargetCharacterId))
                        throw new ArgumentException("Mỗi quan hệ phải chọn một nhân vật đang có trong dự án.");
                    if (string.IsNullOrWhiteSpace(row.Relationship) &&
                        (string.IsNullOrWhiteSpace(row.SelfReference) || string.IsNullOrWhiteSpace(row.AddresseeReference)))
                        throw new ArgumentException("Quan hệ cần tên quan hệ hoặc đủ cách tự xưng và gọi đối phương.");
                    bool directional = !string.IsNullOrWhiteSpace(row.SelfReference) ||
                        !string.IsNullOrWhiteSpace(row.AddresseeReference) || row.Enabled || row.Locked;
                    if (directional && (string.IsNullOrWhiteSpace(row.SelfReference) ||
                                        string.IsNullOrWhiteSpace(row.AddresseeReference)))
                        throw new ArgumentException("Quy tắc hai chiều cần đủ cách tự xưng và gọi đối phương.");
                    if (row.SyncReverse && (string.IsNullOrWhiteSpace(row.ReverseSelfReference) ||
                                            string.IsNullOrWhiteSpace(row.ReverseAddresseeReference)))
                        throw new ArgumentException("Khi tích chọn Xác nhận B→A, hãy nhập đủ B tự xưng và B gọi A.");
                    return new CharacterRelationship { TargetCharacterId = row.TargetCharacterId,
                        SpeakerCharacterId = directional ? profile.Id : Guid.Empty,
                        ListenerCharacterId = directional ? row.TargetCharacterId : Guid.Empty,
                        Relationship = row.Relationship.Trim(), AddressingNotes = row.AddressingNotes.Trim(),
                        SelfReference = row.SelfReference.Trim(), AddresseeReference = row.AddresseeReference.Trim(),
                        Enabled = row.Enabled, Locked = row.Locked };
                }).ToList();
                profile.Validate();

                // Commit any newly created valid listener profiles first (Adjustment C).
                foreach (CharacterProfile created in createdListeners)
                {
                    created.Validate();
                    if (!_characterProject.Profiles.Any(p => p.Id == created.Id))
                        _characterProject.Profiles.Add(created);
                }
                // Ensure speaker is present in a temporary validation clone before applying confirmed reverse rules (Adjustment A).
                if (!_characterProject.Profiles.Any(p => p.Id == profile.Id))
                    _characterProject.Profiles.Add(profile);
                else
                {
                    int idx = _characterProject.Profiles.FindIndex(p => p.Id == profile.Id);
                    _characterProject.Profiles[idx] = profile;
                }

                var warnings = new List<string>();
                foreach (RelationshipEditorRow row in relationshipRows.Where(r => r.SyncReverse))
                {
                    if (!CharacterRelationshipSync.TryApplyConfirmedReverseRule(
                            _characterProject,
                            profile.Id,
                            row.TargetCharacterId,
                            string.IsNullOrWhiteSpace(row.ReverseRelationship) ? row.Relationship : row.ReverseRelationship,
                            row.ReverseSelfReference,
                            row.ReverseAddresseeReference,
                            row.Enabled,
                            locked: false,
                            row.AddressingNotes,
                            out string? warn) && warn is not null)
                    {
                        warnings.Add(warn);
                    }
                }
                _characterProject.Validate();
                if (warnings.Count > 0)
                    SetStatus(string.Join(" ", warnings));
                validation.Text = "";
                window.DialogResult = true;
            }
            catch (Exception ex) { validation.Text = ex.Message; }
        };
        cancel.Click += (_, _) => window.DialogResult = false;
        return window.ShowDialog() == true ? profile : null;
    }

    private CharacterProfile? ShowMinimalListenerCreatorDialog(Window owner, IReadOnlyList<CharacterProfile> existingProfiles)
    {
        var vnBox = new TextBox { MinWidth = 340 };
        var zhBox = new TextBox { MinWidth = 340 };
        var genderChoices = new[]
        {
            new GenderChoice(CharacterGender.Unknown, "Chưa xác định"),
            new GenderChoice(CharacterGender.Female, "Nữ"),
            new GenderChoice(CharacterGender.Male, "Nam"),
            new GenderChoice(CharacterGender.Other, "Khác")
        };
        var genderBox = new ComboBox { ItemsSource = genderChoices, DisplayMemberPath = "Label", SelectedIndex = 0 };
        var primaryBox = new ComboBox { IsEditable = true, ItemsSource = new[] { "nàng", "hắn", "y", "người ấy" } };
        var normCheck = new CheckBox { Content = "Bật chuẩn hóa lời kể cho nhân vật này", IsChecked = false };
        var errorText = new TextBlock { Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };

        genderBox.SelectionChanged += (_, _) =>
        {
            var g = ((GenderChoice)genderBox.SelectedItem).Value;
            if (g == CharacterGender.Female && string.IsNullOrWhiteSpace(primaryBox.Text)) primaryBox.Text = "nàng";
            if (g == CharacterGender.Male && string.IsNullOrWhiteSpace(primaryBox.Text)) primaryBox.Text = "hắn";
        };
        zhBox.LostFocus += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(vnBox.Text) && !string.IsNullOrWhiteSpace(zhBox.Text))
            {
                string suggested = SuggestTraditionalVietnamese(zhBox.Text.Trim(), titleCase: true);
                if (!string.IsNullOrWhiteSpace(suggested)) vnBox.Text = suggested;
            }
        };

        var panel = FormPanel(
            ("Tên Hán Việt / tiếng Việt (bắt buộc)", vnBox),
            ("Tên tiếng Trung đã xác nhận (tùy chọn)", zhBox),
            ("Giới tính", genderBox),
            ("Đại từ trần thuật chính", primaryBox));
        panel.Children.Add(normCheck);
        panel.Children.Add(errorText);
        var save = new Button { Content = "Tạo nhân vật hợp lệ", Style = (Style)FindResource("PrimaryButton") };
        var cancel = new Button { Content = "Hủy" };
        var buttons = new WrapPanel(); buttons.Children.Add(save); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        var dialog = new Window { Owner = owner, Title = "Tạo nhanh nhân vật người nghe", Width = 520, SizeToContent = SizeToContent.Height, Content = panel };
        ClampWindowToWorkArea(dialog, 520, 480);
        CharacterProfile? created = null;
        save.Click += (_, _) =>
        {
            try
            {
                string vn = vnBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(vn))
                    throw new ArgumentException("Hãy nhập tên Hán Việt / tiếng Việt cho nhân vật đích.");
                if (existingProfiles.Any(p => string.Equals(p.VietnameseName, vn, StringComparison.OrdinalIgnoreCase)) ||
                    _characterProject.Profiles.Any(p => string.Equals(p.VietnameseName, vn, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Tên nhân vật đã tồn tại trong dự án.");
                var candidate = new CharacterProfile
                {
                    VietnameseName = vn,
                    ChineseName = zhBox.Text.Trim(),
                    Gender = ((GenderChoice)genderBox.SelectedItem).Value,
                    PrimaryNarrativePronoun = primaryBox.Text.Trim(),
                    NormalizationEnabled = normCheck.IsChecked == true
                };
                candidate.ApplyGenderDefaults();
                if (candidate.NormalizationEnabled && candidate.DisallowedNarrativePronouns.Count == 0)
                    candidate.DisallowedNarrativePronouns = ["anh ấy", "cô ấy"];
                candidate.Validate();
                created = candidate;
                dialog.DialogResult = true;
            }
            catch (Exception ex) { errorText.Text = ex.Message; }
        };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        return dialog.ShowDialog() == true ? created : null;
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
        NormalizationEnabled = source.NormalizationEnabled,
        NarrativePronounLocked = source.NarrativePronounLocked
    };

    private static string SafeFileName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private static string PronounKindLabel(PronounSuggestionKind kind) => kind switch
    {
        PronounSuggestionKind.SafeReplacement => "có thể áp dụng", PronounSuggestionKind.Ambiguous => "cần xác nhận",
        PronounSuggestionKind.Protected => "đoạn đã bảo vệ", _ => "hợp lệ / giữ nguyên"
    };
    private sealed record PronounResultItem(PronounSuggestion Item, string Label) { public override string ToString() => Label; }
    private sealed record GenderChoice(CharacterGender Value, string Label) { public override string ToString() => Label; }
    private sealed class RelationshipEditorRow
    {
        public Guid TargetCharacterId { get; set; }
        public string Relationship { get; set; } = "";
        public string AddressingNotes { get; set; } = "";
        public string SelfReference { get; set; } = "";
        public string AddresseeReference { get; set; } = "";
        public bool Enabled { get; set; }
        public bool Locked { get; set; }
        public bool SyncReverse { get; set; }
        public string ReverseRelationship { get; set; } = "";
        public string ReverseSelfReference { get; set; } = "";
        public string ReverseAddresseeReference { get; set; } = "";
    }
}
