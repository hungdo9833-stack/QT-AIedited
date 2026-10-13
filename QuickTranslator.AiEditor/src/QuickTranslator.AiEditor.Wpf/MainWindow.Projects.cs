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
    private bool _projectLibraryReady = true;
    private ProjectSession? _projectSession;
    private ProjectResources? _projectResources;
    private readonly ProjectCatalog _projectCatalog;
    private ChapterManagerWindow? _chapterManagerWindow;
    private readonly ObservableCollection<ProjectChapterItem> _projectChapters = [];
    private bool _syncingProjectUi;
    private bool _syncingProjectSummary;

    private void InitializeProjectWorkspace()
    {
        ProjectChapterList.ItemsSource = _projectChapters;
        Loaded += async (_, _) =>
        {
            try { await Dispatcher.InvokeAsync(InitializeProjectsAsync, System.Windows.Threading.DispatcherPriority.ApplicationIdle).Task.Unwrap(); }
            catch (Exception ex) { ShowError(ex); }
        };
    }

    private async Task InitializeProjectsAsync()
    {
        await EnsureOllamaBackendsLoadedAsync();
        _projectLibraryReady = false;
        try
        {
            var library = await _projectLibraryStore.LoadAsync();
            await ConfirmRecoveredSourcesAsync(_projectLibraryStore.LastRecovery is null ? [] : [_projectLibraryStore.LastRecovery],
                _projectLibraryStore.LastRecovery?.Source == RecoverySource.Backup ? [() => _projectLibraryStore.RestoreBackupAsync()] : [],
                Path.Combine(Path.GetDirectoryName(_projectLibraryStore.PathName)!, "Recovery"));
            _projectLibrary = library; _projectLibraryReady = true;
        }
        catch (Exception ex) { ShowError(ex); }
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
        try
        {
            await RunWorkspaceTransitionAsync(() => OpenProjectCoreAsync(path));
        }
        catch (Exception ex) { ShowError(ex); }
        finally { RefreshProjectSwitcher(); }
    }

    private async Task OpenProjectCoreAsync(string path)
    {
        var opened = await _projectRepository.OpenAsync(path);
        var candidate = opened.Session;
        if (candidate.ManifestRecovery?.Source == RecoverySource.Backup)
            await ConfirmRecoveredSourcesAsync([candidate.ManifestRecovery],
                [() => _projectRepository.RestoreManifestBackupAsync(candidate)], Path.Combine(candidate.RootDirectory, "Backups"));
        await _projectRepository.InspectRecoveryAsync(candidate);
        await ReviewProjectRecoveryAsync(candidate);
        var assets = await ReadProjectAssetsAsync(candidate);
        await PrepareOllamaProfileForProjectAsync(assets.Translation.OllamaProfileId);
        ChapterDocument? chapter = candidate.Manifest.CurrentChapterId is Guid current
            ? (await _projectRepository.LoadChapterAsync(candidate, current)).Chapter : null;
        // Validate the service before touching the live context.
        if (chapter is not null) _ = new TranslationService(chapter, _translationClient, new RegisteredChapterStore(candidate, _projectRepository));
        var chapterRecovery = candidate.ChapterStore.LastRecovery;
        var recoverySources = assets.RecoverySources.Concat(chapterRecovery is null ? [] : new[] { chapterRecovery }).ToArray();
        var repairs = assets.Repairs.Concat(chapterRecovery?.Source == RecoverySource.Backup
            ? new Func<Task>[] { () => candidate.ChapterStore.RestoreBackupAsync() } : []).ToArray();
        await ConfirmRecoveredSourcesAsync(recoverySources, repairs, Path.Combine(candidate.RootDirectory, "Backups"));
        candidate.Manifest.LastOpenedAt = DateTimeOffset.UtcNow;
        await _projectRepository.SaveManifestAsync(candidate);
        var library = System.Text.Json.JsonSerializer.Deserialize<ProjectLibrary>(
            System.Text.Json.JsonSerializer.Serialize(_projectLibrary, DocumentJson.Options), DocumentJson.Options)!;
        if (_projectLibraryReady)
        {
            ProjectLibraryStore.Remember(library, candidate);
            await _projectLibraryStore.SaveAsync(library);
        }

        // No await after the context commit: all target stores/chapter have been checked.
        _chapterManagerWindow?.Close();
        ClearProjectTranslationCoordinator();
        DetachChapter();
        _projectSession = candidate; _projectLibrary = library; _store = candidate.ChapterStore;
        _presetStore = new(candidate.Resolve(candidate.Manifest.Files.PromptPresets));
        _readerStore = new(candidate.Resolve(candidate.Manifest.Files.ReaderSettings));
        _characterStore = new(Path.Combine(candidate.RootDirectory, "Characters"));
        InitializeProjectTranslationCoordinator(candidate);
        ApplyProjectAssets(assets);
        if (chapter is not null)
        {
            AttachChapter(chapter); _selectedId = chapter.SelectedSegmentId ?? chapter.Segments[0].Id; _shownAttemptId = null;
            ApplyInputs(chapter); SelectChapterPreset(chapter.SelectedPreset);
        }
        else ClearChapterEditor();
        RefreshProjectSwitcher(); RefreshProjectChapterList(); Refresh();
        SetStatus((opened.RecoveredFromBackup
            ? "Đã mở dự án từ bản dự phòng manifest; hãy kiểm tra tiến độ. Không tự dịch."
            : $"Đã mở dự án “{candidate.Manifest.VietnameseTitle}”.") +
            (_projectLibraryReady ? "" : " Danh sách gần đây chưa đọc/xác nhận được; chưa ghi shortcut, file lỗi và backup được giữ."));
    }

    private async Task SafelyLeaveCurrentProjectAsync()
    {
        await WaitForCurrentWorkAsync();
        await SaveCurrentAsync();
        if (_projectSession is not null) await SaveProjectAssetsAsync();
        else
        {
            if (_presetsReady) await SavePresetEditorAsync();
            if (_service is not null || _characterProject.Profiles.Count > 0 || !string.IsNullOrWhiteSpace(CharacterProjectNameInput.Text))
                await SaveCharacterProjectAsync();
        }
    }

    private async Task LoadProjectAssetsAsync()
    {
        if (_projectSession is null) return;
        ApplyProjectAssets(await ReadProjectAssetsAsync(_projectSession));
    }

    private static async Task<ProjectAssetState> ReadProjectAssetsAsync(ProjectSession session)
    {
        var settings = new ProjectSettingsStore(session);
        var translation = await settings.LoadTranslationAsync();
        translation.Model.Validate(); translation.Segmentation.Validate();
        var presets = new PromptPresetStore(session.Resolve(session.Manifest.Files.PromptPresets));
        var reader = new ReaderPreferencesStore(session.Resolve(session.Manifest.Files.ReaderSettings));
        var resources = new ProjectResourceStore(session);
        var loadedPresets = await presets.LoadAsync(); var loadedReader = await reader.LoadAsync(); var loadedResources = await resources.LoadAsync();
        var sources = settings.RecoverySources.Concat(resources.RecoverySources)
            .Concat(new[] { presets.LastRecovery!, reader.LastRecovery! }).ToArray();
        var repairs = new List<Func<Task>>();
        if (settings.RecoverySources.Any(x => x.Source == RecoverySource.Backup)) repairs.Add(() => settings.RestoreRecoveredBackupsAsync());
        if (resources.RecoverySources.Any(x => x.Source == RecoverySource.Backup)) repairs.Add(() => resources.RestoreRecoveredBackupsAsync());
        if (presets.RecoveredFromBackup) repairs.Add(() => presets.RestoreBackupAsync());
        if (reader.RecoveredFromBackup) repairs.Add(() => reader.RestoreBackupAsync());
        return new(new PromptPresetManager(loadedPresets), loadedReader, loadedResources, translation, sources, repairs);
    }

    private void ApplyProjectAssets(ProjectAssetState assets)
    {
        bool prior = _rendering; _rendering = true;
        try
        {
            _presetManager = assets.Presets; _presetsReady = true; RefreshPresetEditor();
            _readerPreferences = assets.Reader; _readerLoadFailure = null; _readerReady = true; LoadReaderAppearanceControls(); ApplyReaderAppearance();
            _projectResources = assets.Resources; LoadCharacterProject(assets.Resources.Characters);
            ApplyProjectDefaults(assets.Translation); SetGlossaryTerms(assets.Resources.Terms); ContextNotesInput.Text = assets.Resources.Context;
            ApplyCurrentProjectResources(); _settingsDirty = false;
        }
        finally { _rendering = prior; }
    }

    private sealed record ProjectAssetState(PromptPresetManager Presets, ReaderPreferences Reader,
        ProjectResources Resources, ProjectTranslationSettings Translation, IReadOnlyList<RecoveryInfo> RecoverySources, IReadOnlyList<Func<Task>> Repairs);

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
            OllamaProfileId = SelectedOllamaProfileId,
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
        SelectOllamaProfileInUi(settings.OllamaProfileId);
        if (settings.SelectedPromptPresetId is Guid preset && _presetManager.Presets.Any(x => x.Id == preset))
        { _presetManager.Select(preset); RefreshPresetEditor(); }
        UpdateOllamaBackendStatus();
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
        if (_projectTranslationCoordinator?.IsRunning == true)
            throw new InvalidOperationException("Hãy Pause Translation Queue trước khi mở chapter trong Editor.");
        if (_service?.GetSnapshot().Id == chapterId) return;
        await RunWorkspaceTransitionAsync(() => LoadProjectChapterCoreAsync(chapterId));
    }

    private async Task LoadProjectChapterCoreAsync(Guid chapterId)
    {
        if (_projectSession is null) return;
        var loaded = await _projectRepository.LoadChapterAsync(_projectSession, chapterId);
        var oldCurrent = _projectSession.Manifest.CurrentChapterId;
        _projectSession.Manifest.CurrentChapterId = chapterId;
        try { await _projectRepository.SaveManifestAsync(_projectSession); }
        catch { _projectSession.Manifest.CurrentChapterId = oldCurrent; throw; }
        AttachChapter(loaded.Chapter); _selectedId = loaded.Chapter.SelectedSegmentId ?? loaded.Chapter.Segments[0].Id; _shownAttemptId = null;
        ApplyInputs(loaded.Chapter); SelectChapterPreset(loaded.Chapter.SelectedPreset);
        RefreshProjectChapterList(); Refresh();
        SetStatus(loaded.RecoveredFromBackup ? "Chương được phục hồi từ bản dự phòng; không tự tiếp tục hàng đợi."
            : $"Đã mở chương “{loaded.Chapter.Title}” sau khi lưu trạng thái trước đó.");
    }

    private void RefreshProjectChapterList()
    {
        _syncingProjectUi = true;
        try
        {
            _projectChapters.Clear();
            if (_projectSession is null)
            {
                ApplyProjectSidebarVisibility(); ActiveProjectTitle.Text = "Dự án"; return;
            }
            ApplyProjectSidebarVisibility(); ActiveProjectTitle.Text = _projectSession.Manifest.VietnameseTitle;
            string query = ChapterSearchInput.Text.Trim();
            string filter = (ChapterStateFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
            ProjectChapterState? state = Enum.TryParse(filter, out ProjectChapterState parsed) ? parsed : null;
            ProjectCatalogSort sort = (ProjectChapterSort.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
            {
                "Title" => ProjectCatalogSort.Title,
                "Updated" => ProjectCatalogSort.Updated,
                _ => ProjectCatalogSort.ReadingOrder
            };
            foreach (ProjectCatalogChapter entry in _projectCatalog.Query(_projectSession,
                         new(Search: query, State: state, Sort: sort)))
                _projectChapters.Add(new(entry.ChapterId,
                    $"{entry.Number:0000} · {entry.VietnameseTitle}\n{ProjectStateLabel(entry.TranslationState)}"));
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
        finally { RefreshProjectChapterList(); }
    }
    private void OnChapterSearchChanged(object sender, RoutedEventArgs e) { if (_initialized) RefreshProjectChapterList(); }

    private void OnProjectCatalogChanged(object? sender, ProjectCatalogChangedEventArgs change)
    {
        if (_projectSession?.Manifest.ProjectId != change.ProjectId || _closing) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_projectSession?.Manifest.ProjectId == change.ProjectId && !_closing)
            {
                RefreshProjectChapterList();
                RefreshReaderCatalog();
            }
        });
    }

    private async void OnNewProject(object sender, RoutedEventArgs e)
    {
        var title = AskText("Dự án mới", "Tên truyện tiếng Việt", "Truyện mới"); if (title is null) return;
        var folder = new OpenFolderDialog { Title = "Chọn thư mục cha để tạo dự án", Multiselect = false,
            InitialDirectory = Directory.Exists(_projectLibrary.DefaultProjectsDirectory) ? _projectLibrary.DefaultProjectsDirectory : null };
        if (folder.ShowDialog(this) != true) return;
        string destination = Path.Combine(folder.FolderName, SafeFolderName(title));
        try
        {
            await RunWorkspaceTransitionAsync(async () =>
            {
                var created = await _projectRepository.CreateAsync(destination, title);
                await OpenProjectCoreAsync(created.ManifestPath);
                _projectLibrary.DefaultProjectsDirectory = folder.FolderName;
                await _projectLibraryStore.SaveAsync(_projectLibrary);
            });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void OnOpenProject(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Dự án QuickTranslator (*.qtproj)|*.qtproj" };
        if (dialog.ShowDialog(this) == true) await OpenProjectPathAsync(dialog.FileName);
    }

    private async void OnCloseProject(object sender, RoutedEventArgs e)
    {
        if (_projectSession is null) return;
        try
        {
            await RunWorkspaceTransitionAsync(async () =>
            {
                var presets = new PromptPresetManager(await new PromptPresetStore(PromptPresetStore.DefaultPath).LoadAsync());
                var reader = await new ReaderPreferencesStore(ReaderPreferencesStore.DefaultPath).LoadAsync();
                _chapterManagerWindow?.Close();
                ClearProjectTranslationCoordinator();
                DetachChapter(); _projectSession = null; _projectResources = null;
                _store = new(ChapterFileStore.DefaultDirectory); _presetStore = new(PromptPresetStore.DefaultPath);
                _readerStore = new(ReaderPreferencesStore.DefaultPath); _characterStore = new(CharacterProjectStore.DefaultDirectory);
                _presetManager = presets; _presetsReady = true; RefreshPresetEditor();
                _readerPreferences = reader; _readerReady = true; LoadReaderAppearanceControls(); ApplyReaderAppearance();
                LoadCharacterProject(new CharacterProject { Name = "Bộ truyện mới" }); SetGlossaryTerms([]); ContextNotesInput.Clear();
                ClearChapterEditor(); RefreshProjectChapterList(); RefreshProjectSwitcher(); Refresh();
                SetStatus("Đã đóng dự án sau khi lưu.");
            });
        }
        catch (Exception ex) { ShowError(ex); }
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
    private async void ShowProjectLibraryWindow()
    {
        while (!_closing)
        {
            IReadOnlyList<ProjectLibraryCard> cards = await BuildProjectLibraryCardsAsync();
            var window = new ProjectLibraryWindow(cards, _projectSession?.Manifest.ProjectId) { Owner = this };
            if (window.ShowDialog() != true || window.RequestedAction is not ProjectLibraryAction action) return;
            ProjectLibraryCard? selected = window.RequestedProject;
            try
            {
                if (action == ProjectLibraryAction.Continue && selected is not null)
                { await OpenProjectPathAsync(selected.ManifestPath); return; }
                if (action == ProjectLibraryAction.Create)
                { OnNewProject(this, new RoutedEventArgs()); return; }
                if (selected is null) continue;
                if (action == ProjectLibraryAction.Locate)
                {
                    var dialog = new OpenFileDialog { Filter = "Dự án QuickTranslator (*.qtproj)|*.qtproj" };
                    if (dialog.ShowDialog(this) != true) continue;
                    ProjectLibrarySummary summary = await ProjectRepository.ReadLibrarySummaryAsync(dialog.FileName);
                    if (summary.ProjectId != selected.ProjectId) throw new InvalidDataException("Tệp được chọn có Project ID khác.");
                    ProjectLibraryStore.Relocate(_projectLibrary, selected.ProjectId, dialog.FileName);
                    await _projectLibraryStore.SaveAsync(_projectLibrary); RefreshProjectSwitcher(); continue;
                }
                if (action == ProjectLibraryAction.Remove)
                {
                    if (!BeginUiOperation(allowDuringTranslation: true)) return;
                    try { ProjectLibraryStore.RemoveShortcut(_projectLibrary, selected.ProjectId);
                        await _projectLibraryStore.SaveAsync(_projectLibrary); RefreshProjectSwitcher(); }
                    finally { EndUiOperation(); }
                    continue;
                }
                if (action == ProjectLibraryAction.Delete)
                {
                    if (selected.ProjectId == _projectSession?.Manifest.ProjectId)
                    { MessageBox.Show(this, "Hãy đóng dự án đang mở trước."); continue; }
                    if (MessageBox.Show(this, $"Chuyển toàn bộ thư mục “{selected.Title}” vào Thùng rác của Windows?\nBạn có thể phục hồi từ Thùng rác.",
                        "Xóa dự án", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) continue;
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(Path.GetDirectoryName(selected.ManifestPath)!,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    ProjectLibraryStore.RemoveShortcut(_projectLibrary, selected.ProjectId);
                    await _projectLibraryStore.SaveAsync(_projectLibrary); RefreshProjectSwitcher();
                }
            }
            catch (Exception ex) { ShowError(ex); }
        }
    }

    private async Task<IReadOnlyList<ProjectLibraryCard>> BuildProjectLibraryCardsAsync()
    {
        var cards = new List<ProjectLibraryCard>();
        foreach (RecentProject recent in _projectLibrary.RecentProjects.OrderByDescending(project => project.LastOpenedAt))
        {
            if (recent.Missing || !File.Exists(recent.ManifestPath))
            {
                cards.Add(new(recent.ProjectId, recent.Title, "", null, recent.ManifestPath, null,
                    0, 0, 0, null, recent.LastOpenedAt, true, null));
                continue;
            }
            try
            {
                ProjectLibrarySummary summary = await ProjectRepository.ReadLibrarySummaryAsync(recent.ManifestPath);
                cards.Add(new(summary.ProjectId, summary.VietnameseTitle, summary.Author, summary.Status,
                    recent.ManifestPath, summary.CoverImagePath, summary.ChapterCount, summary.CompletedChapterCount,
                    summary.Progress, summary.CurrentChapterTitle, recent.LastOpenedAt, false,
                    summary.RecoveredFromBackup ? "Đang hiển thị metadata từ backup đã kiểm tra." : null));
            }
            catch (Exception ex)
            {
                cards.Add(new(recent.ProjectId, recent.Title, "", null, recent.ManifestPath, null,
                    0, 0, 0, null, recent.LastOpenedAt, false, "Không đọc được metadata: " + ex.Message));
            }
        }
        return cards;
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
        if (!BeginUiOperation(allowDuringTranslation: true)) return;
        try
        {
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
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
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
        try
        {
            await RunWorkspaceTransitionAsync(async () =>
            {
                var restored = await _projectBackup.RestoreAsync(file.FileName, target);
                await OpenProjectCoreAsync(restored.ManifestPath);
            });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void OnAddChapterFromText(object sender, RoutedEventArgs e)
    {
        await OpenDetectedBulkImportAsync();
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
        if (_projectSession is null) throw new InvalidOperationException("Project đã đóng.");
        if (!BeginUiOperation(allowDuringTranslation: true))
            throw new InvalidOperationException("Editor đang xử lý một thao tác khác. Hãy thử lại sau khi thao tác đó hoàn tất.");
        try
        {
            var session = _projectSession;
            var service = _service;
            if (service?.GetSnapshot().Id == chapterId)
            {
                await service.RenameAsync(title, chineseTitle);
                await _projectRepository.UpdateChapterSummaryAsync(session, service.GetSnapshot());
                if (ReferenceEquals(service, _service)) TitleInput.Text = service.GetSnapshot().Title;
            }
            else
            {
                await SaveCurrentAsync();
                await _projectRepository.RenameChapterAsync(session, chapterId, title, chineseTitle);
            }
            RefreshProjectChapterList(); Refresh();
        }
        finally { EndUiOperation(); }
    }

    private async void OnDuplicateProjectChapter(object sender, RoutedEventArgs e)
    {
        if (_projectSession is null || ProjectChapterList.SelectedItem is not ProjectChapterItem item) return;
        try
        {
            await RunWorkspaceTransitionAsync(async () =>
            {
                var entry = await _projectRepository.DuplicateChapterAsync(_projectSession, item.Id, NextChapterNumber(),
                    "Bản sao - " + _projectSession.Manifest.Chapters.Single(x => x.ChapterId == item.Id).VietnameseTitle);
                await LoadProjectChapterCoreAsync(entry.ChapterId);
            });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void OnDeleteProjectChapter(object sender, RoutedEventArgs e)
    {
        if (_projectSession is null || ProjectChapterList.SelectedItem is not ProjectChapterItem item) return;
        if (MessageBox.Show(this, "Chuyển chương vào History của dự án? Có thể phục hồi bằng dữ liệu manifest; tệp không bị xóa vĩnh viễn.", "Xóa mềm chương", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await RunWorkspaceTransitionAsync(async () =>
            {
                await _projectRepository.SoftDeleteChapterAsync(_projectSession, item.Id);
                if (_projectSession.Manifest.CurrentChapterId is Guid id) await LoadProjectChapterCoreAsync(id);
                else { DetachChapter(); ClearChapterEditor(); RefreshProjectChapterList(); Refresh(); }
            });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void OnPreviousProjectChapter(object sender, RoutedEventArgs e)
    { try { await MoveProjectChapterAsync(-1); } catch (Exception ex) { ShowError(ex); } }
    private async void OnNextProjectChapter(object sender, RoutedEventArgs e)
    { try { await MoveProjectChapterAsync(1); } catch (Exception ex) { ShowError(ex); } }
    private async Task MoveProjectChapterAsync(int delta)
    {
        if (_projectSession?.Manifest.CurrentChapterId is not Guid current) return;
        var ordered = _projectCatalog.Query(_projectSession, new()).ToList(); int index = ordered.FindIndex(x => x.ChapterId == current);
        int next = index + delta; if (next >= 0 && next < ordered.Count) await LoadProjectChapterAsync(ordered[next].ChapterId);
    }

    private async void OnImportLegacyProject(object sender, RoutedEventArgs e)
    {
        if (_projectSession is null) { ShowError(new InvalidOperationException("Hãy mở hoặc tạo dự án đích trước.")); return; }
        var folder = new OpenFolderDialog { Title = "Chọn thư mục Chapters cũ", Multiselect = false, InitialDirectory = Directory.Exists(ChapterFileStore.DefaultDirectory) ? ChapterFileStore.DefaultDirectory : null };
        if (folder.ShowDialog(this) != true) return;
        try
        {
            await RunWorkspaceTransitionAsync(async () =>
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
                var result = await importer.ImportAsync(_projectSession, selections);
                RefreshProjectChapterList();
                try { if (_projectSession.Manifest.CurrentChapterId is Guid current) await LoadProjectChapterCoreAsync(current); }
                catch (Exception ex)
                {
                    if (result.Report is not null) ShowLegacyImportReport(result.Report);
                    ShowError(new IOException($"Đã commit {result.ImportedCount} chương nhưng chưa nạp được editor. Chọn lại chương sau khi xử lý lỗi đọc; dữ liệu import và ZIP được giữ.", ex));
                    return;
                }
                SetStatus($"Đã nhập {result.ImportedCount} chương. Nguồn cũ không đổi; bản sao trước nhập: {result.SourceBackupPath}");
            });
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
