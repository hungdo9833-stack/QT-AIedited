using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuickTranslator.AiEditor.Core.Editing;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Traditional;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private readonly Dictionary<TextBox, TextSelectionSnapshot> _sourceSelections = [];

    private void InitializeSourceContextMenus()
    {
        AttachSourceContextMenu(ChapterInput);
        AttachSourceContextMenu(SourceText);
        AttachSourceContextMenu(HanVietText);
        AttachSourceContextMenu(VietPhraseText);
    }

    private void AttachSourceContextMenu(TextBox textBox)
    {
        var menu = new ContextMenu();
        var addCharacter = WorkspaceMenuItem("Thêm vào hồ sơ _nhân vật (Ctrl+Shift+C)", () => AddQuickCharacterAsync(textBox));
        var addGlossary = WorkspaceMenuItem("Thêm vào _Glossary Trung–Việt (Ctrl+Shift+G)", () => AddSelectedGlossaryAsync(textBox));
        var find = MenuItem("_Tìm các vị trí xuất hiện trong chương", () => ShowSourceOccurrences(textBox));
        var translate = MenuItem("_Dịch riêng phần đã chọn", async () => await TranslateSourceSelectionAsync(textBox));
        var copy = MenuItem("_Sao chép", () => CopySourceSelection(textBox));
        menu.Items.Add(addCharacter); menu.Items.Add(addGlossary); menu.Items.Add(find); menu.Items.Add(translate);
        menu.Items.Add(new Separator()); menu.Items.Add(copy);

        textBox.PreviewMouseRightButtonDown += (_, _) => CaptureSourceSelection(textBox);
        menu.Opened += (_, _) =>
        {
            RestoreSourceSelection(textBox);
            string selected = GetSourceSelection(textBox);
            var capabilities = SourceContextMenuPolicy.Evaluate(selected, WorkActive || _uiOperation, textBox.IsReadOnly);
            addCharacter.IsEnabled = capabilities.AddCharacter;
            addGlossary.IsEnabled = capabilities.AddGlossary;
            find.IsEnabled = capabilities.FindOccurrences;
            translate.IsEnabled = capabilities.TranslateSelection;
            copy.IsEnabled = !string.IsNullOrEmpty(textBox.SelectedText) || capabilities.Copy;
        };
        menu.Closed += (_, _) => Dispatcher.BeginInvoke(() => RestoreSourceSelection(textBox));
        textBox.ContextMenu = menu;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private MenuItem WorkspaceMenuItem(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) =>
        {
            if (!BeginUiOperation()) return;
            try { await action(); }
            catch (Exception ex) { ShowError(ex); }
            finally { EndUiOperation(); }
        };
        return item;
    }

    private void CaptureSourceSelection(TextBox textBox) =>
        _sourceSelections[textBox] = TextSelectionSnapshot.Capture(textBox.Text, textBox.SelectionStart, textBox.SelectionLength);

    private void RestoreSourceSelection(TextBox textBox)
    {
        if (!_sourceSelections.TryGetValue(textBox, out var selected)) return;
        int start = Math.Min(selected.Start, textBox.Text.Length);
        int length = Math.Min(selected.Length, textBox.Text.Length - start);
        textBox.Select(start, length);
    }

    private string GetSourceSelection(TextBox textBox)
    {
        RestoreSourceSelection(textBox);
        // Hán Việt / VP shown in the bilingual left cell is not Chinese: glossary, character and search actions stay off.
        if (ShowsTranslatedSource(textBox)) return "";
        if ((ReferenceEquals(textBox, HanVietText) || ReferenceEquals(textBox, VietPhraseText)) &&
            textBox.SelectionLength > 0 && _traditionalResult is not null &&
            _traditionalDisplayedSegmentId == _selectedId &&
            string.Equals(_traditionalResult.SourceText, SourceText.Text, StringComparison.Ordinal))
        {
            TraditionalPane origin = ReferenceEquals(textBox, HanVietText) ? TraditionalPane.HanViet : TraditionalPane.VietPhrase;
            SelectionProjection projected = SelectionAlignment.Project(_traditionalResult, origin,
                new(textBox.SelectionStart, textBox.SelectionLength));
            if (projected.Chinese.Length > 0 &&
                projected.Chinese.Start >= 0 &&
                projected.Chinese.Start + projected.Chinese.Length <= SourceText.Text.Length)
            {
                return SourceText.Text.Substring(projected.Chinese.Start, projected.Chinese.Length);
            }
        }
        return textBox.SelectedText;
    }

    internal string SuggestTraditionalVietnamese(string chinese, bool titleCase)
    {
        if (string.IsNullOrWhiteSpace(chinese) || _traditionalService.CurrentSnapshot is not { } snapshot)
            return "";
        string trimmed = chinese.Trim();
        string? raw = null;
        if (snapshot.Names.TryGetValue(trimmed, out string? nameVal) && !string.IsNullOrWhiteSpace(nameVal))
            raw = FirstTraditionalMeaning(nameVal);
        else if (!titleCase && snapshot.CombinedOneMeaning.TryGetValue(trimmed, out string? vpVal) && !string.IsNullOrWhiteSpace(vpVal))
            raw = FirstTraditionalMeaning(vpVal);
        else
        {
            var syllables = new List<string>();
            foreach (Rune rune in trimmed.EnumerateRunes())
            {
                string ch = rune.ToString();
                if (snapshot.HanViet.TryGetValue(ch, out string? hv) && !string.IsNullOrWhiteSpace(hv))
                    syllables.Add(FirstTraditionalMeaning(hv));
            }
            if (syllables.Count > 0) raw = string.Join(" ", syllables);
        }
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return titleCase ? CultureInfo.CurrentCulture.TextInfo.ToTitleCase(raw.ToLower(CultureInfo.CurrentCulture)) : raw;
    }

    private void CopySourceSelection(TextBox textBox)
    {
        try
        {
            RestoreSourceSelection(textBox);
            string text = textBox.SelectedText.Length > 0 ? textBox.SelectedText : GetSourceSelection(textBox);
            if (text.Length > 0) Clipboard.SetText(text);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task AddQuickCharacterAsync(TextBox textBox)
    {
        string chinese;
        try { chinese = SourceContextActions.ValidateCharacterName(GetSourceSelection(textBox)); }
        catch (Exception ex) { ShowError(ex); return; }

        var existing = SourceContextActions.FindCharacter(_characterProject, chinese);
        if (existing is not null)
        {
            if (ThemedDialog.Show(this, $"“{chinese}” đã thuộc hồ sơ {existing.VietnameseName}. Mở hồ sơ để sửa?",
                "Nhân vật đã có", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
            var edited = ShowCharacterEditor(CloneProfile(existing));
            if (edited is null) return;
            try
            {
                int index = _characterProject.Profiles.FindIndex(p => p.Id == existing.Id);
                _characterProject.Profiles[index] = edited;
                await SaveCharacterProjectAsync();
                RefreshCharacterGrid();
                SetStatus("Đã cập nhật hồ sơ nhân vật hiện có; không tạo bản trùng.");
            }
            catch (Exception ex) { _resourcesDirty = true; RefreshCharacterGrid(); ShowError(ex); }
            return;
        }

        var quick = ShowQuickCharacterEditor(chinese);
        if (quick is null) return;
        try
        {
            var conflict = _glossaryRows.FirstOrDefault(t => string.Equals(t.Chinese, quick.Profile.ChineseName, StringComparison.Ordinal));
            if (conflict is not null && !string.Equals(conflict.Vietnamese, quick.Profile.VietnameseName, StringComparison.Ordinal) &&
                ThemedDialog.Show(this, $"Glossary đang có “{conflict.Chinese} = {conflict.Vietnamese}”. Thay bằng “{quick.Profile.VietnameseName}”?",
                    "Xác nhận ánh xạ", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _characterProject.Profiles.Add(quick.Profile);
            var merged = ReadTerms().Where(t => !string.Equals(t.Chinese, quick.Profile.ChineseName, StringComparison.Ordinal)).ToList();
            merged.Add(new(quick.Profile.ChineseName, quick.Profile.VietnameseName));
            SetGlossaryTerms(merged);
            await SaveCharacterProjectAsync();
            RefreshCharacterGrid();
            SetStatus("Đã thêm nhân vật và đồng bộ ánh xạ Trung–Việt vào glossary.");
            if (quick.ScanExisting && _service is not null)
            {
                ScanPronounsAndShow();
            }
        }
        catch (Exception ex)
        {
            // Keep the confirmed editor inputs available for an explicit save retry.
            // Some earlier writes may already have succeeded; rolling back only the UI would diverge.
            _resourcesDirty = true;
            RefreshCharacterGrid();
            ShowError(ex);
        }
    }

    private QuickCharacterResult? ShowQuickCharacterEditor(string chinese)
    {
        string suggestedVietnamese = SuggestTraditionalVietnamese(chinese, titleCase: true);
        var vietnamese = new TextBox { Text = suggestedVietnamese };
        var genders = new[] { new GenderChoice(CharacterGender.Unknown, "Chưa xác định"), new GenderChoice(CharacterGender.Female, "Nữ"),
            new GenderChoice(CharacterGender.Male, "Nam"), new GenderChoice(CharacterGender.Other, "Khác") };
        var gender = new ComboBox { ItemsSource = genders, DisplayMemberPath = "Label", SelectedIndex = 0 };
        var primary = new ComboBox { IsEditable = true, ItemsSource = new[] { "nàng", "nàng ấy", "hắn", "hắn ta", "y", "người ấy" } };
        var secondary = new ComboBox { IsEditable = true, ItemsSource = new[] { "nàng ấy", "hắn ta", "người ấy" } };
        var lockPronoun = new CheckBox { Content = "Khóa đại từ trần thuật chính cho nhân vật này", IsChecked = _characterProject.ConsistencyLockEnabled };

        // Optional quick relationship with existing character in the project/segment (BL-06 / CC-04)
        var listenerChoices = _characterProject.Profiles.OrderBy(p => p.VietnameseName).ToList();
        var quickListenerCombo = new ComboBox
        {
            ItemsSource = listenerChoices,
            DisplayMemberPath = nameof(CharacterProfile.VietnameseName),
            ToolTip = "Tùy chọn: thiết lập nhanh quan hệ hội thoại với một nhân vật đã có"
        };
        var quickPresetCombo = new ComboBox
        {
            ItemsSource = CharacterRelationshipPreset.Defaults,
            DisplayMemberPath = nameof(CharacterRelationshipPreset.Label),
            ToolTip = "Chọn mẫu xưng hô nhanh (tự điền Tự xưng / Gọi đối phương)"
        };
        var scan = new CheckBox { Content = "Quét lại bản dịch hiện có và chỉ xem trước đề xuất", IsChecked = true };
        var validation = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
        validation.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "DangerBrush");
        var panel = new StackPanel { Margin = new Thickness(24) };
        void Add(string label, Control field) { panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 8, 4, 2) }); panel.Children.Add(field); }
        // IMPORTANT: Keep Chinese TextBox first and Vietnamese TextBox second so AsyncSafetyTests Descendants<TextBox>(dialog).Skip(1).First() always targets `vietnamese`.
        Add("Tên tiếng Trung đã chọn", new TextBox { Text = chinese, IsReadOnly = true });
        Add("Tên Hán Việt / tiếng Việt đã xác nhận", vietnamese);
        Add("Giới tính", gender);
        Add("Đại từ trần thuật chính", primary);
        Add("Đại từ trần thuật phụ", secondary);
        panel.Children.Add(lockPronoun);
        if (listenerChoices.Count > 0)
        {
            Add("Quan hệ nhanh với nhân vật đã có (tùy chọn)", quickListenerCombo);
            Add("Mẫu xưng hô hội thoại (tùy chọn)", quickPresetCombo);
        }
        panel.Children.Add(scan); panel.Children.Add(validation);
        var save = new Button { Content = "Thêm nhân vật" }; save.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        var advanced = new Button { Content = "Hồ sơ nâng cao…" }; var cancel = new Button { Content = "Hủy" };
        var root = new DockPanel();
        Border footer = ThemedDialog.Footer(save, cancel, advanced);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(panel);
        var window = new Window { Owner = this, Title = "Thêm nhanh nhân vật", Width = 620, MinWidth = 420, SizeToContent = SizeToContent.Height, Content = root };
        window.SetResourceReference(BackgroundProperty, "PanelBrush");
        window.SetResourceReference(ForegroundProperty, "TextBrush");
        ClampWindowToWorkArea(window, 620, 680);
        CharacterProfile? result = null;
        CharacterProfile Build()
        {
            var built = SourceContextActions.CreateQuickCharacter(chinese, vietnamese.Text,
                ((GenderChoice)gender.SelectedItem).Value, primary.Text, secondary.Text);
            if (lockPronoun.IsChecked == true && !string.IsNullOrWhiteSpace(built.PrimaryNarrativePronoun))
                built.NarrativePronounLocked = true;
            if (quickListenerCombo.SelectedItem is CharacterProfile listener &&
                quickPresetCombo.SelectedItem is CharacterRelationshipPreset preset)
            {
                built.Relationships.Add(new CharacterRelationship
                {
                    TargetCharacterId = listener.Id,
                    SpeakerCharacterId = built.Id,
                    ListenerCharacterId = listener.Id,
                    Relationship = preset.Relationship,
                    SelfReference = preset.SelfReference,
                    AddresseeReference = preset.AddresseeReference,
                    Enabled = true
                });
            }
            built.Validate();
            return built;
        }
        gender.SelectionChanged += (_, _) =>
        {
            var value = ((GenderChoice)gender.SelectedItem).Value;
            if (value == CharacterGender.Female) { if (string.IsNullOrWhiteSpace(primary.Text)) primary.Text = "nàng"; if (string.IsNullOrWhiteSpace(secondary.Text)) secondary.Text = "nàng ấy"; }
            if (value == CharacterGender.Male) { if (string.IsNullOrWhiteSpace(primary.Text)) primary.Text = "hắn"; if (string.IsNullOrWhiteSpace(secondary.Text)) secondary.Text = "hắn ta"; }
        };
        save.Click += (_, _) => { try { result = Build(); window.DialogResult = true; } catch (Exception ex) { validation.Text = ex.Message; } };
        advanced.Click += (_, _) =>
        {
            try
            {
                var edited = ShowCharacterEditor(Build());
                if (edited is not null) { result = edited; window.DialogResult = true; }
            }
            catch (Exception ex) { validation.Text = ex.Message; }
        };
        cancel.Click += (_, _) => window.DialogResult = false;
        return window.ShowDialog() == true && result is not null ? new(result, scan.IsChecked == true) : null;
    }

    private async Task AddSelectedGlossaryAsync(TextBox textBox)
    {
        string chinese;
        try { chinese = SourceContextActions.ValidateGlossaryTerm(GetSourceSelection(textBox)); }
        catch (Exception ex) { ShowError(ex); return; }
        var existing = _glossaryRows.FirstOrDefault(t => string.Equals(t.Chinese, chinese, StringComparison.Ordinal));
        string suggestedVn = existing?.Vietnamese ?? SuggestTraditionalVietnamese(chinese, titleCase: false);
        var edited = ShowGlossaryEditor(existing is null && !string.IsNullOrWhiteSpace(suggestedVn)
            ? new GlossaryRow(chinese, suggestedVn) : existing, chinese);
        if (edited is null) return;
        var character = _characterProject.Profiles.FirstOrDefault(p => string.Equals(p.ChineseName, chinese, StringComparison.Ordinal));
        if (character is not null && !string.Equals(character.VietnameseName, edited.Vietnamese, StringComparison.Ordinal))
        {
            if (ThemedDialog.Show(this, $"“{chinese}” đang gắn với hồ sơ {character.VietnameseName}. Mở hồ sơ nhân vật để đổi đồng bộ?",
                "Thuật ngữ thuộc hồ sơ nhân vật", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                var profile = ShowCharacterEditor(CloneProfile(character));
                if (profile is not null)
                {
                    int index = _characterProject.Profiles.FindIndex(p => p.Id == character.Id);
                    _characterProject.Profiles[index] = profile;
                    try { await SaveCharacterProjectAsync(); RefreshCharacterGrid(); }
                    catch (Exception ex) { _resourcesDirty = true; RefreshCharacterGrid(); ShowError(ex); }
                }
            }
            return;
        }
        if (existing is not null && existing.Vietnamese != edited.Vietnamese &&
            ThemedDialog.Show(this, $"Đổi nghĩa “{existing.Vietnamese}” thành “{edited.Vietnamese}”?", "Cập nhật glossary",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            if (existing is null) _glossaryRows.Add(edited);
            else _glossaryRows[_glossaryRows.IndexOf(existing)] = edited;
            GlossaryChanged();
            if (!await PersistCurrentGlossaryAsync())
            {
                SetStatus("Glossary còn trong bộ nhớ, chưa lưu. Hãy lưu chương rồi thử Lưu thiết lập.");
                return;
            }
            SetStatus("Đã lưu thuật ngữ vào glossary của chương hiện tại.");
        }
        catch (Exception ex) { _resourcesDirty = true; ShowError(ex); }
    }

    private async Task<bool> PersistCurrentGlossaryAsync()
    {
        if (_service is null)
        {
            if (!ConfirmWorkspaceChange("Chương chưa có tệp tiến độ. Tạo phân đoạn và lưu chương ngay để glossary không bị mất khi đóng ứng dụng?",
                "Lưu glossary")) return false;
            var chapter = TranslationService.CreateChapter(TitleInput.Text, _importedOriginalText ?? ChapterInput.Text,
                ReadSegmenterOptions(), ReadModelOptions());
            chapter.CharacterProject = CharacterProjectStore.Clone(_characterProject);
            chapter.SelectedPreset = _presetManager.Selected;
            chapter.ConfirmedTerms = ReadTerms().ToList();
            chapter.ConfirmedContextNotes = ContextNotesInput.Text;
            chapter = await RegisterChapterIfNeededAsync(chapter);
            AttachChapter(chapter); _selectedId = chapter.SelectedSegmentId; _shownAttemptId = null;
        }
        var service = _service!;
        var snapshot = service.GetSnapshot();
        if (_projectSession is not null) await SaveProjectResourcesAsync();
        await service.ConfigureAsync(snapshot.DefaultModelOptions, _presetManager.Selected, ReadTerms(), snapshot.ConfirmedContextNotes);
        Refresh();
        return true;
    }

    private Window? _sourceOccurrencesWindow;

    private void ShowSourceOccurrences(TextBox textBox)
    {
        string selected;
        try { selected = SourceContextActions.ValidateGlossaryTerm(GetSourceSelection(textBox)); }
        catch (Exception ex) { ShowError(ex); return; }
        _sourceOccurrencesWindow?.Close();
        var items = new List<SourceOccurrence>();
        if (_service?.GetSnapshot() is { } chapter)
        {
            foreach (var segment in chapter.Segments.OrderBy(s => s.Order))
                foreach (int start in SourceContextActions.FindOccurrences(segment.SourceText, selected))
                    items.Add(new(segment.Id, start, $"Đoạn {segment.Order} · vị trí {start + 1} · {OccurrencePreview(segment.SourceText, start, selected.Length)}"));
        }
        else
            foreach (int start in SourceContextActions.FindOccurrences(ChapterInput.Text, selected))
                items.Add(new(null, start, $"Vị trí {start + 1} · {OccurrencePreview(ChapterInput.Text, start, selected.Length)}"));
        var list = new ListBox { ItemsSource = items, DisplayMemberPath = "Label", Margin = new Thickness(24) };
        System.Windows.Automation.AutomationProperties.SetName(list, "Danh sách vị trí xuất hiện");
        var open = new Button { Content = "Mở vị trí" }; open.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        var close = new Button { Content = "Đóng" };
        var panel = new DockPanel();
        Border footer = ThemedDialog.Footer(open, close);
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer); panel.Children.Add(list);
        var window = new Window { Title = $"{items.Count} vị trí có “{selected}”", Width = 760, Height = 520, Content = panel };
        window.SetResourceReference(BackgroundProperty, "PanelBrush");
        window.SetResourceReference(ForegroundProperty, "TextBrush");
        if (IsVisible) window.Owner = this;
        ClampWindowToWorkArea(window, 760, 520);
        void Open()
        {
            if (list.SelectedItem is not SourceOccurrence item) return;
            if (item.SegmentId is Guid id)
            {
                _selectedId = id; _shownAttemptId = null; NavigateTo(WorkspaceId.Editor, EditorMode.Review); Refresh();
                SourceText.Focus(); SourceText.Select(item.Start, selected.Length);
            }
            else { NavigateTo(WorkspaceId.Editor, EditorMode.Translate); ChapterInput.Focus(); ChapterInput.Select(item.Start, selected.Length); }
        }
        open.Click += (_, _) => Open(); list.MouseDoubleClick += (_, _) => Open(); close.Click += (_, _) => window.Close();
        window.Closed += (_, _) => { if (ReferenceEquals(_sourceOccurrencesWindow, window)) _sourceOccurrencesWindow = null; };
        if (items.Count > 0) list.SelectedIndex = 0;
        _sourceOccurrencesWindow = window;
        window.Show();
    }

    private async Task TranslateSourceSelectionAsync(TextBox textBox)
    {
        if (WorkActive || _uiOperation) return;
        string selected;
        try
        {
            selected = SourceContextActions.ValidateTranslationSelection(GetSourceSelection(textBox));
            await EnsureGpuBackendReadyAsync();
        }
        catch (Exception ex) { ShowError(ex); return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeWorkCompletion = completion.Task;
        var cancellation = new CancellationTokenSource();
        Task? request = null;
        try
        {
            _busy = true; _requestCancellation = cancellation; UpdateButtons();
            var options = ReadModelOptions();
            var glossary = ReadTerms().Where(t => selected.Contains(t.Chinese, StringComparison.Ordinal))
                .Select(t => new GlossaryEntry(t.Chinese, t.Vietnamese)).ToArray();
            string prompt = new TranslationPromptBuilder().Build(selected, glossary, ContextNotesInput.Text, _presetManager.Selected);
            var translation = _translationClient.TranslateAsync(prompt, options, cancellation.Token);
            request = translation;
            _activeRequest = request;
            SetStatus("Đang dịch riêng vùng chọn; kết quả chỉ để xem và không ghi đè bản dịch chương.");
            var result = await translation;
            ShowTextWindow("Dịch riêng vùng chọn — chưa lưu vào bản dịch chương", result.Text);
            SetStatus("Đã dịch riêng vùng chọn. Kết quả không làm thay đổi segment hoặc bản người dùng đã sửa.");
        }
        catch (OperationCanceledException) { SetStatus("Đã hủy dịch riêng vùng chọn."); }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(cancellation, _requestCancellation)) _requestCancellation = null;
            if (ReferenceEquals(request, _activeRequest)) { _activeRequest = null; _busy = false; }
            if (ReferenceEquals(_activeWorkCompletion, completion.Task)) _activeWorkCompletion = null;
            UpdateButtons(); completion.TrySetResult();
            RestoreSourceSelection(textBox);
        }
    }

    private static string OccurrencePreview(string text, int start, int length)
    {
        int from = Math.Max(0, start - 24), to = Math.Min(text.Length, start + length + 24);
        return text[from..to].ReplaceLineEndings(" ");
    }

    private sealed record QuickCharacterResult(CharacterProfile Profile, bool ScanExisting);
    private sealed record SourceOccurrence(Guid? SegmentId, int Start, string Label) { public override string ToString() => Label; }
}
