using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuickTranslator.AiEditor.Core.Editing;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private readonly Dictionary<TextBox, TextSelectionSnapshot> _sourceSelections = [];

    private void InitializeSourceContextMenus()
    {
        AttachSourceContextMenu(ChapterInput);
        AttachSourceContextMenu(SourceText);
    }

    private void AttachSourceContextMenu(TextBox textBox)
    {
        var menu = new ContextMenu();
        var addCharacter = WorkspaceMenuItem("Thêm vào hồ sơ nhân vật", () => AddQuickCharacterAsync(textBox));
        var addGlossary = WorkspaceMenuItem("Thêm vào Glossary Trung–Việt", () => AddSelectedGlossaryAsync(textBox));
        var find = MenuItem("Tìm các vị trí xuất hiện trong chương", () => ShowSourceOccurrences(textBox));
        var translate = MenuItem("Dịch riêng phần đã chọn", async () => await TranslateSourceSelectionAsync(textBox));
        var copy = MenuItem("Sao chép", () => CopySourceSelection(textBox));
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
            copy.IsEnabled = capabilities.Copy;
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
        return textBox.SelectedText;
    }

    private void CopySourceSelection(TextBox textBox)
    {
        try
        {
            string text = GetSourceSelection(textBox);
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
            if (MessageBox.Show(this, $"“{chinese}” đã thuộc hồ sơ {existing.VietnameseName}. Mở hồ sơ để sửa?",
                "Nhân vật đã có", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
            var edited = ShowCharacterEditor(CloneProfile(existing));
            if (edited is null) return;
            var backup = CharacterProjectStore.Clone(_characterProject);
            try
            {
                int index = _characterProject.Profiles.FindIndex(p => p.Id == existing.Id);
                _characterProject.Profiles[index] = edited;
                await SaveCharacterProjectAsync();
                RefreshCharacterGrid();
                SetStatus("Đã cập nhật hồ sơ nhân vật hiện có; không tạo bản trùng.");
            }
            catch (Exception ex) { _characterProject = backup; RefreshCharacterGrid(); ShowError(ex); }
            return;
        }

        var quick = ShowQuickCharacterEditor(chinese);
        if (quick is null) return;
        var projectBackup = CharacterProjectStore.Clone(_characterProject);
        var termsBackup = ReadTerms().ToList();
        try
        {
            var conflict = _glossaryRows.FirstOrDefault(t => string.Equals(t.Chinese, quick.Profile.ChineseName, StringComparison.Ordinal));
            if (conflict is not null && !string.Equals(conflict.Vietnamese, quick.Profile.VietnameseName, StringComparison.Ordinal) &&
                MessageBox.Show(this, $"Glossary đang có “{conflict.Chinese} = {conflict.Vietnamese}”. Thay bằng “{quick.Profile.VietnameseName}”?",
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
                EditorTabs.SelectedIndex = 0;
                EditorModeButton.IsChecked = true;
            }
        }
        catch (Exception ex)
        {
            _characterProject = projectBackup;
            SetGlossaryTerms(termsBackup);
            RefreshCharacterGrid();
            ShowError(ex);
        }
    }

    private QuickCharacterResult? ShowQuickCharacterEditor(string chinese)
    {
        var vietnamese = new TextBox();
        var genders = new[] { new GenderChoice(CharacterGender.Unknown, "Chưa xác định"), new GenderChoice(CharacterGender.Female, "Nữ"),
            new GenderChoice(CharacterGender.Male, "Nam"), new GenderChoice(CharacterGender.Other, "Khác") };
        var gender = new ComboBox { ItemsSource = genders, DisplayMemberPath = "Label", SelectedIndex = 0 };
        var primary = new ComboBox { IsEditable = true, ItemsSource = new[] { "nàng", "nàng ấy", "hắn", "hắn ta", "y", "người ấy" } };
        var secondary = new ComboBox { IsEditable = true, ItemsSource = new[] { "nàng ấy", "hắn ta", "người ấy" } };
        var scan = new CheckBox { Content = "Quét lại bản dịch hiện có và chỉ xem trước đề xuất", IsChecked = true };
        var validation = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
        var panel = new StackPanel { Margin = new Thickness(14) };
        void Add(string label, Control field) { panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 8, 4, 2) }); panel.Children.Add(field); }
        Add("Tên tiếng Trung đã chọn", new TextBox { Text = chinese, IsReadOnly = true });
        Add("Tên Hán Việt / tiếng Việt đã xác nhận", vietnamese); Add("Giới tính", gender);
        Add("Đại từ trần thuật chính", primary); Add("Đại từ trần thuật phụ", secondary);
        panel.Children.Add(scan); panel.Children.Add(validation);
        var buttons = new WrapPanel(); var save = new Button { Content = "Thêm nhân vật", Style = (Style)FindResource("PrimaryButton") };
        var advanced = new Button { Content = "Hồ sơ nâng cao…" }; var cancel = new Button { Content = "Hủy" };
        buttons.Children.Add(save); buttons.Children.Add(advanced); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        var window = new Window { Owner = this, Title = "Thêm nhanh nhân vật", Width = 620, SizeToContent = SizeToContent.Height, Content = panel };
        CharacterProfile? result = null;
        CharacterProfile Build() => SourceContextActions.CreateQuickCharacter(chinese, vietnamese.Text,
            ((GenderChoice)gender.SelectedItem).Value, primary.Text, secondary.Text);
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
        var edited = ShowGlossaryEditor(existing, chinese);
        if (edited is null) return;
        var character = _characterProject.Profiles.FirstOrDefault(p => string.Equals(p.ChineseName, chinese, StringComparison.Ordinal));
        if (character is not null && !string.Equals(character.VietnameseName, edited.Vietnamese, StringComparison.Ordinal))
        {
            if (MessageBox.Show(this, $"“{chinese}” đang gắn với hồ sơ {character.VietnameseName}. Mở hồ sơ nhân vật để đổi đồng bộ?",
                "Thuật ngữ thuộc hồ sơ nhân vật", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                var profile = ShowCharacterEditor(CloneProfile(character));
                if (profile is not null)
                {
                    int index = _characterProject.Profiles.FindIndex(p => p.Id == character.Id);
                    _characterProject.Profiles[index] = profile;
                    try { await SaveCharacterProjectAsync(); RefreshCharacterGrid(); }
                    catch (Exception ex) { _characterProject.Profiles[index] = character; ShowError(ex); }
                }
            }
            return;
        }
        if (existing is not null && existing.Vietnamese != edited.Vietnamese &&
            MessageBox.Show(this, $"Đổi nghĩa “{existing.Vietnamese}” thành “{edited.Vietnamese}”?", "Cập nhật glossary",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var backup = ReadTerms().ToList();
        try
        {
            if (existing is null) _glossaryRows.Add(edited);
            else _glossaryRows[_glossaryRows.IndexOf(existing)] = edited;
            GlossaryChanged();
            if (!await PersistCurrentGlossaryAsync()) { SetGlossaryTerms(backup); return; }
            SetStatus("Đã lưu thuật ngữ vào glossary của chương hiện tại.");
        }
        catch (Exception ex) { SetGlossaryTerms(backup); ShowError(ex); }
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

    private void ShowSourceOccurrences(TextBox textBox)
    {
        string selected;
        try { selected = SourceContextActions.ValidateGlossaryTerm(GetSourceSelection(textBox)); }
        catch (Exception ex) { ShowError(ex); return; }
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
        var list = new ListBox { ItemsSource = items, DisplayMemberPath = "Label", Margin = new Thickness(8) };
        var open = new Button { Content = "Mở vị trí", Style = (Style)FindResource("PrimaryButton") };
        var close = new Button { Content = "Đóng" }; var buttons = new WrapPanel(); buttons.Children.Add(open); buttons.Children.Add(close);
        var panel = new DockPanel(); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(list);
        var window = new Window { Owner = this, Title = $"{items.Count} vị trí có “{selected}”", Width = 760, Height = 520, Content = panel };
        void Open()
        {
            if (list.SelectedItem is not SourceOccurrence item) return;
            window.Close();
            if (item.SegmentId is Guid id)
            {
                _selectedId = id; _shownAttemptId = null; EditorTabs.SelectedIndex = 1; Refresh();
                SourceText.Focus(); SourceText.Select(item.Start, selected.Length);
            }
            else { EditorTabs.SelectedIndex = 0; EditorModeButton.IsChecked = true; ChapterInput.Focus(); ChapterInput.Select(item.Start, selected.Length); }
        }
        open.Click += (_, _) => Open(); list.MouseDoubleClick += (_, _) => Open(); close.Click += (_, _) => window.Close();
        if (items.Count > 0) list.SelectedIndex = 0;
        window.ShowDialog();
    }

    private async Task TranslateSourceSelectionAsync(TextBox textBox)
    {
        if (WorkActive || _uiOperation) return;
        string selected;
        try { selected = SourceContextActions.ValidateTranslationSelection(GetSourceSelection(textBox)); }
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
            var translation = _client.TranslateAsync(prompt, options, cancellation.Token);
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
    private sealed record SourceOccurrence(Guid? SegmentId, int Start, string Label);
}
