using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Reading;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private readonly ProjectRepository _projectRepository = new();
    private readonly ProjectBackupService _projectBackup = new();
    private readonly ProjectLibraryStore _projectLibraryStore = new();
    private ProjectLibrary _projectLibrary = new();
    private ProjectSession? _projectSession;
    private ProjectResources? _projectResources;
    private readonly ObservableCollection<ProjectChapterItem> _projectChapters = [];
    private bool _syncingProjectUi;
    private bool _syncingProjectSummary;

    private void InitializeProjectWorkspace()
    {
        ProjectChapterList.ItemsSource = _projectChapters;
        Loaded += async (_, _) =>
        {
            await Dispatcher.InvokeAsync(async () => await InitializeProjectsAsync(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
    }

    private async Task InitializeProjectsAsync()
    {
        try { _projectLibrary = await _projectLibraryStore.LoadAsync(); }
        catch (Exception ex) { ShowError(ex); _projectLibrary = new(); }
        RefreshProjectSwitcher();
        if (!string.IsNullOrWhiteSpace(_startupProjectPath))
        {
            if (!string.Equals(Path.GetExtension(_startupProjectPath), ".qtproj", StringComparison.OrdinalIgnoreCase))
                ShowError(new InvalidDataException("Tham số khởi động không phải tệp .qtproj."));
            else await OpenProjectPathAsync(_startupProjectPath);
        }
        else if (_projectLibrary.RecentProjects.FirstOrDefault(x => !x.Missing) is { } recent)
            await OpenProjectPathAsync(recent.ManifestPath);
        else ShowProjectLibraryWindow();
    }

    private async Task OpenProjectPathAsync(string path)
    {
        if (!BeginUiOperation()) return;
        try
        {
            await SafelyLeaveCurrentProjectAsync();
            DetachChapter();
            var opened = await _projectRepository.OpenAsync(path);
            _projectSession = opened.Session;
            _projectSession.Manifest.LastOpenedAt = DateTimeOffset.UtcNow;
            _store = _projectSession.ChapterStore;
            _presetStore = new(_projectSession.Resolve(_projectSession.Manifest.Files.PromptPresets));
            _readerStore = new(_projectSession.Resolve(_projectSession.Manifest.Files.ReaderSettings));
            _characterStore = new(Path.Combine(_projectSession.RootDirectory, "Characters"));
            await LoadProjectAssetsAsync();
            await _projectRepository.SaveManifestAsync(_projectSession);
            ProjectLibraryStore.Remember(_projectLibrary, _projectSession);
            await _projectLibraryStore.SaveAsync(_projectLibrary);
            RefreshProjectSwitcher(); RefreshProjectChapterList();
            if (_projectSession.Manifest.CurrentChapterId is Guid current)
                await LoadProjectChapterAsync(current);
            else
            {
                DetachChapter(); ClearChapterEditor(); Refresh();
            }
            SetStatus(opened.RecoveredFromBackup
                ? "Đã mở dự án từ bản dự phòng manifest. Hãy kiểm tra rồi bấm Lưu; không có chương nào tự dịch."
                : $"Đã mở dự án “{_projectSession.Manifest.VietnameseTitle}”.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private async Task SafelyLeaveCurrentProjectAsync()
    {
        if (_queue?.IsRunning == true)
        {
            SetStatus("Đang chờ request hiện tại hoàn tất và lưu checkpoint trước khi chuyển dự án…");
            await _queue.PauseAsync();
        }
        await SaveCurrentAsync();
        if (_projectSession is not null) await SaveProjectAssetsAsync();
    }

    private async Task LoadProjectAssetsAsync()
    {
        if (_projectSession is null) return;
        _presetsReady = false;
        _presetManager = new(await _presetStore.LoadAsync());
        _presetsReady = true; RefreshPresetEditor();
        _readerPreferences = await _readerStore.LoadAsync();
        _readerReady = true; LoadReaderAppearanceControls(); ApplyReaderAppearance();
        _projectResources = await new ProjectResourceStore(_projectSession).LoadAsync();
        LoadCharacterProject(_projectResources.Characters);
        var settings = new ProjectSettingsStore(_projectSession);
        var translation = await settings.LoadTranslationAsync();
        ApplyProjectDefaults(translation);
        SetGlossaryTerms(_projectResources.Terms);
        ContextNotesInput.Text = _projectResources.Context;
        ApplyCurrentProjectResources();
    }

    private async Task SaveProjectAssetsAsync()
    {
        if (_projectSession is null) return;
        if (_presetsReady) await SavePresetEditorAsync();
        await SaveReaderPreferencesAsync();
        await SaveCharacterProjectAsync();
        var settings = new ProjectSettingsStore(_projectSession);
        await settings.SaveTranslationAsync(new()
        {
            Model = ReadModelOptions(), Segmentation = ReadSegmenterOptions(),
            SelectedPromptPresetId = _presetsReady ? _presetManager.Selected.Id : null,
            TraditionalDataDirectory = TraditionalDataDirectoryInput.Text
        });
        await _projectRepository.SaveManifestAsync(_projectSession);
    }

    private void ApplyCurrentProjectResources()
    {
        if (_projectResources is not null && _service?.GetSnapshot().ProjectId == _projectResources.ProjectId)
            _service.UseProjectResources(_projectResources);
    }

    private async Task SaveProjectResourcesAsync()
    {
        if (_projectSession is null || _projectResources is null) return;
        UpdateCharacterProjectFromInputs();
        var proposed = new ProjectResources { ProjectId = _projectSession.Manifest.ProjectId,
            Revision = _projectResources.Revision, Characters = CharacterProjectStore.Clone(_characterProject),
            Terms = ReadTerms().ToList(), Context = ContextNotesInput.Text };
        _projectResources = await new ProjectResourceStore(_projectSession).SaveAsync(proposed);
        LoadCharacterProject(_projectResources.Characters);
        SetGlossaryTerms(_projectResources.Terms);
        ApplyCurrentProjectResources();
    }

    private void ApplyProjectDefaults(ProjectTranslationSettings settings)
    {
        ModelInput.Text = settings.Model.Model;
        TemperatureInput.Text = settings.Model.Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ContextInput.Text = settings.Model.ContextSize.ToString(); OutputTokensInput.Text = settings.Model.MaximumOutputTokens.ToString();
        TimeoutInput.Text = settings.Model.RequestTimeout.TotalSeconds.ToString("0");
        MinimumInput.Text = settings.Segmentation.MinimumHanCharacters.ToString();
        TargetInput.Text = settings.Segmentation.TargetHanCharacters.ToString(); MaximumInput.Text = settings.Segmentation.MaximumHanCharacters.ToString();
        if (settings.SelectedPromptPresetId is Guid preset && _presetManager.Presets.Any(x => x.Id == preset))
        { _presetManager.Select(preset); RefreshPresetEditor(); }
    }

    private async Task<ChapterDocument> RegisterChapterIfNeededAsync(ChapterDocument chapter)
    {
        if (_projectSession is null) return chapter;
        if (_projectSession.Manifest.Chapters.Any(x => x.ChapterId == chapter.Id && x.DeletedAt is null))
            return (await _projectRepository.LoadChapterAsync(_projectSession, chapter.Id)).Chapter;
        int number = NextChapterNumber();
        chapter.ProjectId = _projectSession.Manifest.ProjectId; chapter.ChapterNumber = number;
        chapter.CharacterProject.Id = _projectSession.Manifest.ProjectId;
        await _projectRepository.AddChapterAsync(_projectSession, chapter, number, chapter.ChineseTitle);
        RefreshProjectChapterList();
        return (await _projectRepository.LoadChapterAsync(_projectSession, chapter.Id)).Chapter;
    }

    private async Task SyncProjectSummaryAsync()
    {
        if (_syncingProjectSummary || _projectSession is null || _service is null) return;
        _syncingProjectSummary = true;
        try
        {
            await _projectRepository.UpdateChapterSummaryAsync(_projectSession, _service.GetSnapshot());
            RefreshProjectChapterList();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _syncingProjectSummary = false; }
    }

    private int NextChapterNumber() => (_projectSession?.Manifest.Chapters.Where(x => x.DeletedAt is null).Select(x => x.Number).DefaultIfEmpty(0).Max() ?? 0) + 1;

    private async Task LoadProjectChapterAsync(Guid chapterId)
    {
        if (_projectSession is null) return;
        if (_service?.GetSnapshot().Id == chapterId) return;
        if (_queue?.IsRunning == true) await _queue.PauseAsync();
        await SaveCurrentAsync();
        await SaveProjectResourcesAsync();
        var loaded = await _projectRepository.LoadChapterAsync(_projectSession, chapterId);
        AttachChapter(loaded.Chapter); _selectedId = loaded.Chapter.SelectedSegmentId ?? loaded.Chapter.Segments[0].Id; _shownAttemptId = null;
        ApplyInputs(loaded.Chapter); SelectChapterPreset(loaded.Chapter.SelectedPreset);
        _projectSession.Manifest.CurrentChapterId = chapterId; await _projectRepository.SaveManifestAsync(_projectSession);
        RefreshProjectChapterList(); Refresh();
        if (loaded.RecoveredFromBackup) SetStatus("Chương được phục hồi từ bản dự phòng; không tự tiếp tục hàng đợi.");
    }

    private void RefreshProjectChapterList()
    {
        _syncingProjectUi = true;
        try
        {
            _projectChapters.Clear();
            if (_projectSession is null)
            {
                ProjectSidebar.Visibility = Visibility.Collapsed; ActiveProjectTitle.Text = "Dự án"; return;
            }
            ProjectSidebar.Visibility = Visibility.Visible; ActiveProjectTitle.Text = _projectSession.Manifest.VietnameseTitle;
            string query = ChapterSearchInput.Text.Trim();
            string filter = (ChapterStateFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
            IEnumerable<ProjectChapterEntry> entries = _projectSession.Manifest.Chapters.Where(x => x.DeletedAt is null)
                .Where(x => query.Length == 0 || x.Number.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    x.VietnameseTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase) || x.ChineseTitle.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Where(x => filter == "All" || string.Equals(x.State.ToString(), filter, StringComparison.Ordinal));
            entries = (ProjectChapterSort.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
            {
                "Title" => entries.OrderBy(x => x.VietnameseTitle, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Number),
                "Updated" => entries.OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Number),
                _ => entries.OrderBy(x => x.Number)
            };
            foreach (var entry in entries)
                _projectChapters.Add(new(entry.ChapterId, $"{entry.Number:0000} · {entry.VietnameseTitle}\n{ProjectStateLabel(entry.State)}"));
            ProjectChapterList.SelectedItem = _projectChapters.FirstOrDefault(x => x.Id == _projectSession.Manifest.CurrentChapterId);
            ProjectAutosaveStatus.Text = $"{_projectSession.Manifest.Chapters.Count(x => x.DeletedAt is null)} chương · lưu tại {Path.GetFileName(_projectSession.ManifestPath)}";
        }
        finally { _syncingProjectUi = false; }
    }

    private static string ProjectStateLabel(ProjectChapterState state) => state switch
    {
        ProjectChapterState.Untranslated => "Chưa dịch", ProjectChapterState.Translating => "Đang dịch",
        ProjectChapterState.AwaitingReview => "Chờ duyệt", ProjectChapterState.NeedsInspection => "Cần kiểm tra",
        ProjectChapterState.Approved => "Đã duyệt", _ => "Lỗi"
    };

    private void RefreshProjectSwitcher()
    {
        _syncingProjectUi = true;
        try
        {
            var items = _projectLibrary.RecentProjects.OrderByDescending(x => x.LastOpenedAt).Select(x => new ProjectSwitchItem(x.ProjectId,
                x.Missing ? $"⚠ {x.Title}" : x.Title, x.ManifestPath, x.Missing)).ToList();
            ProjectSwitcher.ItemsSource = items;
            ProjectSwitcher.SelectedItem = items.FirstOrDefault(x => x.Id == _projectSession?.Manifest.ProjectId);
        }
        finally { _syncingProjectUi = false; }
    }

    private async void OnProjectSwitcherChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingProjectUi || ProjectSwitcher.SelectedItem is not ProjectSwitchItem item || item.Missing || item.Id == _projectSession?.Manifest.ProjectId) return;
        await OpenProjectPathAsync(item.Path);
    }
    private async void OnProjectChapterSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingProjectUi || ProjectChapterList.SelectedItem is not ProjectChapterItem item) return;
        try { await LoadProjectChapterAsync(item.Id); } catch (Exception ex) { ShowError(ex); }
    }
    private void OnChapterSearchChanged(object sender, RoutedEventArgs e) { if (_initialized) RefreshProjectChapterList(); }

    private async void OnNewProject(object sender, RoutedEventArgs e)
    {
        var title = AskText("Dự án mới", "Tên truyện tiếng Việt", "Truyện mới"); if (title is null) return;
        var folder = new OpenFolderDialog { Title = "Chọn thư mục cha để tạo dự án", Multiselect = false,
            InitialDirectory = Directory.Exists(_projectLibrary.DefaultProjectsDirectory) ? _projectLibrary.DefaultProjectsDirectory : null };
        if (folder.ShowDialog(this) != true) return;
        _projectLibrary.DefaultProjectsDirectory = folder.FolderName;
        await _projectLibraryStore.SaveAsync(_projectLibrary);
        string destination = Path.Combine(folder.FolderName, SafeFolderName(title));
        try { var created = await _projectRepository.CreateAsync(destination, title); await OpenProjectPathAsync(created.ManifestPath); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void OnOpenProject(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Dự án QuickTranslator (*.qtproj)|*.qtproj" };
        if (dialog.ShowDialog(this) == true) await OpenProjectPathAsync(dialog.FileName);
    }

    private async void OnCloseProject(object sender, RoutedEventArgs e)
    {
        if (_projectSession is null || !BeginUiOperation()) return;
        try
        {
            await SafelyLeaveCurrentProjectAsync(); DetachChapter(); _projectSession = null; _projectResources = null;
            _store = new(ChapterFileStore.DefaultDirectory); _presetStore = new(PromptPresetStore.DefaultPath);
            _readerStore = new(ReaderPreferencesStore.DefaultPath); _characterStore = new(CharacterProjectStore.DefaultDirectory);
            await LoadGlobalAssetsAsync();
            ClearChapterEditor(); RefreshProjectChapterList(); RefreshProjectSwitcher(); Refresh();
            SetStatus("Đã đóng dự án sau khi lưu. Nội dung dự án vẫn nằm nguyên trong thư mục của nó.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private async Task LoadGlobalAssetsAsync()
    {
        _presetsReady = false;
        _presetManager = new(await _presetStore.LoadAsync());
        _presetsReady = true; RefreshPresetEditor();
        _readerPreferences = await _readerStore.LoadAsync();
        _readerReady = true; LoadReaderAppearanceControls(); ApplyReaderAppearance();
        LoadCharacterProject(new CharacterProject { Name = "Bộ truyện mới" });
        SetGlossaryTerms([]); ContextNotesInput.Clear();
    }

    private void ClearChapterEditor()
    {
        _selectedId = null; _shownAttemptId = null; TitleInput.Text = "Chương mới"; ChapterInput.Clear(); _importedOriginalText = null;
    }

    private void OnRecentProjectsOpened(object sender, RoutedEventArgs e)
    {
        RecentProjectsMenu.Items.Clear();
        foreach (var recent in _projectLibrary.RecentProjects.OrderByDescending(x => x.LastOpenedAt).Take(12))
        {
            var item = new MenuItem { Header = (recent.Missing ? "⚠ " : "") + recent.Title, Tag = recent.ManifestPath, IsEnabled = !recent.Missing };
            item.Click += async (_, _) => await OpenProjectPathAsync((string)item.Tag); RecentProjectsMenu.Items.Add(item);
        }
        if (RecentProjectsMenu.Items.Count == 0) RecentProjectsMenu.Items.Add(new MenuItem { Header = "Chưa có dự án", IsEnabled = false });
    }

    private void OnShowProjectLibrary(object sender, RoutedEventArgs e) => ShowProjectLibraryWindow();
    private void ShowProjectLibraryWindow()
    {
        var list = new ListBox { DisplayMemberPath = "Title", Margin = new Thickness(4) };
        var search = new TextBox { ToolTip = "Tìm dự án", Margin = new Thickness(4) };
        void Reload() { list.ItemsSource = ProjectLibraryStore.Search(_projectLibrary, search.Text); }
        search.TextChanged += (_, _) => Reload(); Reload();
        var open = new Button { Content = "Mở", Style = (Style)FindResource("PrimaryButton") };
        var create = new Button { Content = "Tạo mới" }; var locate = new Button { Content = "Tìm lại vị trí…" };
        var remove = new Button { Content = "Bỏ khỏi danh sách" }; var delete = new Button { Content = "Chuyển dự án vào Thùng rác", Style = (Style)FindResource("DangerButton") };
        var buttons = new WrapPanel(); foreach (var button in new[] { open, create, locate, remove, delete }) buttons.Children.Add(button);
        var panel = new DockPanel { Margin = new Thickness(12) }; DockPanel.SetDock(search, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(search); panel.Children.Add(buttons); panel.Children.Add(list);
        var window = new Window { Owner = this, Title = "Thư viện dự án", Width = 820, Height = 520, Content = panel };
        open.Click += async (_, _) => { if (list.SelectedItem is RecentProject selected && !selected.Missing) { window.Close(); await OpenProjectPathAsync(selected.ManifestPath); } };
        list.MouseDoubleClick += (_, _) => open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        create.Click += (_, _) => { window.Close(); OnNewProject(create, new RoutedEventArgs()); };
        locate.Click += async (_, _) =>
        {
            if (list.SelectedItem is not RecentProject selected) return;
            var dialog = new OpenFileDialog { Filter = "Dự án QuickTranslator (*.qtproj)|*.qtproj" };
            if (dialog.ShowDialog(window) != true) return;
            try
            {
                var opened = await _projectRepository.OpenAsync(dialog.FileName);
                if (opened.Session.Manifest.ProjectId != selected.ProjectId) throw new InvalidDataException("Tệp được chọn có Project ID khác.");
                ProjectLibraryStore.Relocate(_projectLibrary, selected.ProjectId, dialog.FileName); await _projectLibraryStore.SaveAsync(_projectLibrary); Reload(); RefreshProjectSwitcher();
            }
            catch (Exception ex) { MessageBox.Show(window, ex.Message, "Không thể định vị", MessageBoxButton.OK, MessageBoxImage.Error); }
        };
        remove.Click += async (_, _) => { if (list.SelectedItem is RecentProject selected) { ProjectLibraryStore.RemoveShortcut(_projectLibrary, selected.ProjectId); await _projectLibraryStore.SaveAsync(_projectLibrary); Reload(); RefreshProjectSwitcher(); } };
        delete.Click += async (_, _) =>
        {
            if (list.SelectedItem is not RecentProject selected || selected.Missing) return;
            if (selected.ProjectId == _projectSession?.Manifest.ProjectId) { MessageBox.Show(window, "Hãy đóng dự án đang mở trước."); return; }
            if (MessageBox.Show(window, $"Chuyển toàn bộ thư mục “{selected.Title}” vào Thùng rác của Windows?\nBạn có thể phục hồi từ Thùng rác.", "Xóa dự án", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(Path.GetDirectoryName(selected.ManifestPath)!,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                ProjectLibraryStore.RemoveShortcut(_projectLibrary, selected.ProjectId); await _projectLibraryStore.SaveAsync(_projectLibrary); Reload(); RefreshProjectSwitcher();
            }
            catch (Exception ex) { MessageBox.Show(window, ex.Message, "Không thể xóa", MessageBoxButton.OK, MessageBoxImage.Error); }
        };
        window.ShowDialog();
    }

    private async void OnEditProjectProperties(object sender, RoutedEventArgs e)
    {
        if (_projectSession is null) return; var manifest = _projectSession.Manifest;
        var title = new TextBox { Text = manifest.VietnameseTitle }; var chinese = new TextBox { Text = manifest.ChineseTitle };
        var author = new TextBox { Text = manifest.Author }; var genres = new TextBox { Text = string.Join(", ", manifest.Genres) };
        var source = new TextBox { Text = manifest.SourceUrl }; var description = new TextBox { Text = manifest.Description, AcceptsReturn = true, Height = 100, TextWrapping = TextWrapping.Wrap };
        var cover = new TextBox { Text = manifest.CoverImagePath ?? "", IsReadOnly = true }; var chooseCover = new Button { Content = "Chọn ảnh…" };
        var coverRow = new DockPanel(); DockPanel.SetDock(chooseCover, Dock.Right); coverRow.Children.Add(chooseCover); coverRow.Children.Add(cover);
        string? selectedCover = null;
        chooseCover.Click += (_, _) => { var dialog = new OpenFileDialog { Filter = "Ảnh (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp" }; if (dialog.ShowDialog(this) == true) { selectedCover = dialog.FileName; cover.Text = Path.GetFileName(dialog.FileName); } };
        var status = new ComboBox { ItemsSource = Enum.GetValues<ProjectStatus>(), SelectedItem = manifest.Status };
        var panel = FormPanel(("Tên tiếng Việt", title), ("Tên tiếng Trung", chinese), ("Tác giả", author), ("Thể loại, cách nhau bằng dấu phẩy", genres), ("Nguồn", source), ("Ảnh bìa trong thư mục dự án", coverRow), ("Trạng thái", status), ("Mô tả", description));
        if (!ShowForm("Thuộc tính dự án", panel)) return;
        if (string.IsNullOrWhiteSpace(title.Text)) { ShowError(new ArgumentException("Tên dự án không được trống.")); return; }
        manifest.VietnameseTitle = title.Text.Trim(); manifest.ChineseTitle = chinese.Text.Trim(); manifest.Author = author.Text.Trim();
        manifest.Genres = genres.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        manifest.SourceUrl = source.Text.Trim(); manifest.Description = description.Text; manifest.Status = (ProjectStatus)status.SelectedItem;
        if (selectedCover is not null)
        {
            string relative = "Resources/cover-" + Guid.NewGuid().ToString("N") + Path.GetExtension(selectedCover).ToLowerInvariant();
            string destination = _projectSession.Resolve(relative);
            File.Copy(selectedCover, destination, overwrite: false); manifest.CoverImagePath = relative;
        }
        await _projectRepository.SaveManifestAsync(_projectSession); ProjectLibraryStore.Remember(_projectLibrary, _projectSession); await _projectLibraryStore.SaveAsync(_projectLibrary);
        RefreshProjectSwitcher(); RefreshProjectChapterList(); Title = manifest.VietnameseTitle + " — QuickTranslator AI Editor";
    }

    private void OnOpenProjectFolder(object sender, RoutedEventArgs e)
    {
        if (_projectSession is null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", _projectSession.RootDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void OnBackupProject(object sender, RoutedEventArgs e)
    {
        if (_projectSession is null || !BeginUiOperation()) return;
        try
        {
            await SafelyLeaveCurrentProjectAsync();
            var dialog = new SaveFileDialog { Filter = "Bản sao dự án (*.zip)|*.zip", DefaultExt = ".zip", AddExtension = true,
                FileName = SafeFolderName(_projectSession.Manifest.VietnameseTitle) + "-backup.zip" };
            if (dialog.ShowDialog(this) != true) return;
            var result = await _projectBackup.CreateAsync(_projectSession, dialog.FileName);
            SetStatus($"Đã tạo và kiểm chứng bản sao {result.FileCount} tệp: {result.ArchivePath}");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private async void OnRestoreProject(object sender, RoutedEventArgs e)
    {
        var file = new OpenFileDialog { Filter = "Bản sao dự án (*.zip)|*.zip" }; if (file.ShowDialog(this) != true) return;
        var folder = new OpenFolderDialog { Title = "Chọn thư mục cha để khôi phục thành dự án mới", Multiselect = false }; if (folder.ShowDialog(this) != true) return;
        string target = Path.Combine(folder.FolderName, "Restored-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
        try { var restored = await _projectBackup.RestoreAsync(file.FileName, target); await OpenProjectPathAsync(restored.ManifestPath); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void OnAddChapterFromText(object sender, RoutedEventArgs e)
    {
        if (_projectSession is null) return;
        var dialog = new OpenFileDialog { Filter = "Văn bản UTF-8 (*.txt)|*.txt", Multiselect = true }; if (dialog.ShowDialog(this) != true) return;
        try
        {
            foreach (string file in dialog.FileNames)
            {
                string text = await File.ReadAllTextAsync(file, new UTF8Encoding(false, true));
                var chapter = TranslationService.CreateChapter(Path.GetFileNameWithoutExtension(file), text, ReadSegmenterOptions(), ReadModelOptions());
                chapter.SelectedPreset = _presetManager.Selected; chapter.ConfirmedTerms = ReadTerms().ToList(); chapter.ConfirmedContextNotes = ContextNotesInput.Text;
                chapter.CharacterProject = CharacterProjectStore.Clone(_characterProject);
                await _projectRepository.AddChapterAsync(_projectSession, chapter, NextChapterNumber());
            }
            RefreshProjectChapterList();
            if (_projectSession.Manifest.CurrentChapterId is Guid id) await LoadProjectChapterAsync(id);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void OnRenameProjectChapter(object sender, RoutedEventArgs e)
    {
        if (_projectSession is null || ProjectChapterList.SelectedItem is not ProjectChapterItem item) return;
        var entry = _projectSession.Manifest.Chapters.Single(x => x.ChapterId == item.Id);
        string? title = AskText("Đổi tên chương", "Tên tiếng Việt", entry.VietnameseTitle); if (title is null) return;
        string? chinese = AskText("Đổi tên chương", "Tên tiếng Trung (có thể để trống)", entry.ChineseTitle); if (chinese is null) return;
        try { await RenameProjectChapterAsync(item.Id, title, chinese); }
        catch (Exception ex) { ShowError(ex); }
    }

    internal async Task RenameProjectChapterAsync(Guid chapterId, string title, string chineseTitle)
    {
        if (_projectSession is null) return;
        await _projectRepository.RenameChapterAsync(_projectSession, chapterId, title, chineseTitle);
        await LoadProjectChapterAsync(chapterId);
        RefreshProjectChapterList();
    }

    private async void OnDuplicateProjectChapter(object sender, RoutedEventArgs e)
    {
        if (_projectSession is null || ProjectChapterList.SelectedItem is not ProjectChapterItem item) return;
        try { var entry = await _projectRepository.DuplicateChapterAsync(_projectSession, item.Id, NextChapterNumber(), "Bản sao - " + _projectSession.Manifest.Chapters.Single(x => x.ChapterId == item.Id).VietnameseTitle); RefreshProjectChapterList(); await LoadProjectChapterAsync(entry.ChapterId); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void OnDeleteProjectChapter(object sender, RoutedEventArgs e)
    {
        if (_projectSession is null || ProjectChapterList.SelectedItem is not ProjectChapterItem item) return;
        if (MessageBox.Show(this, "Chuyển chương vào History của dự án? Có thể phục hồi bằng dữ liệu manifest; tệp không bị xóa vĩnh viễn.", "Xóa mềm chương", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { if (_queue?.IsRunning == true) await _queue.PauseAsync(); await SaveCurrentAsync(); await _projectRepository.SoftDeleteChapterAsync(_projectSession, item.Id); DetachChapter(); RefreshProjectChapterList(); if (_projectSession.Manifest.CurrentChapterId is Guid id) await LoadProjectChapterAsync(id); else { ClearChapterEditor(); Refresh(); } }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void OnPreviousProjectChapter(object sender, RoutedEventArgs e) => await MoveProjectChapterAsync(-1);
    private async void OnNextProjectChapter(object sender, RoutedEventArgs e) => await MoveProjectChapterAsync(1);
    private async Task MoveProjectChapterAsync(int delta)
    {
        if (_projectSession?.Manifest.CurrentChapterId is not Guid current) return;
        var ordered = _projectSession.Manifest.Chapters.Where(x => x.DeletedAt is null).OrderBy(x => x.Number).ToList(); int index = ordered.FindIndex(x => x.ChapterId == current);
        int next = index + delta; if (next >= 0 && next < ordered.Count) await LoadProjectChapterAsync(ordered[next].ChapterId);
    }

    private async void OnImportLegacyProject(object sender, RoutedEventArgs e)
    {
        if (_projectSession is null) { ShowError(new InvalidOperationException("Hãy mở hoặc tạo dự án đích trước.")); return; }
        var folder = new OpenFolderDialog { Title = "Chọn thư mục Chapters cũ", Multiselect = false, InitialDirectory = Directory.Exists(ChapterFileStore.DefaultDirectory) ? ChapterFileStore.DefaultDirectory : null };
        if (folder.ShowDialog(this) != true) return;
        try
        {
            var importer = new LegacyProjectImporter(_projectRepository); var candidates = await importer.ScanAsync(folder.FolderName);
            var valid = candidates.Where(x => x.Error is null).ToList();
            var list = new ListBox { ItemsSource = valid, SelectionMode = SelectionMode.Extended, DisplayMemberPath = "Title", Margin = new Thickness(10) };
            var window = new Window { Owner = this, Title = "Chọn chương cũ để nhập (Ctrl/Shift để chọn nhiều)", Width = 760, Height = 520 };
            var import = new Button { Content = "Sao lưu nguồn và nhập", Style = (Style)FindResource("PrimaryButton") }; var cancel = new Button { Content = "Hủy" };
            var buttons = new WrapPanel(); buttons.Children.Add(import); buttons.Children.Add(cancel); var panel = new DockPanel(); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(list); window.Content = panel;
            import.Click += (_, _) => { if (list.SelectedItems.Count > 0) window.DialogResult = true; }; cancel.Click += (_, _) => window.DialogResult = false;
            if (window.ShowDialog() != true) return;
            int number = NextChapterNumber(); var selections = list.SelectedItems.Cast<LegacyChapterCandidate>().Select(x => new LegacyChapterSelection(x, number++)).ToArray();
            var result = await importer.ImportAsync(_projectSession, selections); RefreshProjectChapterList();
            SetStatus($"Đã nhập {result.ImportedCount} chương. Nguồn cũ không đổi; bản sao trước nhập: {result.SourceBackupPath}");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private static string SafeFolderName(string value)
    {
        string name = string.Concat(value.Trim().Select(x => Path.GetInvalidFileNameChars().Contains(x) ? '_' : x));
        return string.IsNullOrWhiteSpace(name) ? "QuickTranslator-Project" : name;
    }

    private string? AskText(string title, string label, string value)
    {
        var input = new TextBox { Text = value, MinWidth = 380 }; var panel = FormPanel((label, input));
        return ShowForm(title, panel) ? input.Text.Trim() : null;
    }

    private static StackPanel FormPanel(params (string Label, FrameworkElement Input)[] rows)
    {
        var panel = new StackPanel { Margin = new Thickness(14) };
        foreach (var row in rows) { panel.Children.Add(new TextBlock { Text = row.Label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 8, 4, 0) }); panel.Children.Add(row.Input); }
        return panel;
    }

    private bool ShowForm(string title, StackPanel panel)
    {
        var save = new Button { Content = "Lưu", Style = (Style)FindResource("PrimaryButton") }; var cancel = new Button { Content = "Hủy" };
        var buttons = new WrapPanel(); buttons.Children.Add(save); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        var window = new Window { Owner = this, Title = title, Width = 600, SizeToContent = SizeToContent.Height, MaxHeight = 800, Content = panel };
        save.Click += (_, _) => window.DialogResult = true; cancel.Click += (_, _) => window.DialogResult = false;
        return window.ShowDialog() == true;
    }

    private sealed record ProjectChapterItem(Guid Id, string Label);
    private sealed record ProjectSwitchItem(Guid Id, string Title, string Path, bool Missing);
}
