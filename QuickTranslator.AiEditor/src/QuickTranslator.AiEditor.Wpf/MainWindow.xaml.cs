using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Translation;
using QuickTranslator.AiEditor.Core.TextCleaning;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow : Window
{
    private readonly OllamaBackendManager _ollamaBackends;
    private readonly GpuConnectionManager _gpuConnections = new();
    private readonly IOllamaClient _translationClient;
    private ChapterFileStore _store = new(ChapterFileStore.DefaultDirectory);
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private TranslationService? _service;
    private Guid? _selectedId;
    private Guid? _shownAttemptId;
    private CancellationTokenSource? _requestCancellation;
    private Task? _activeRequest;
    private string? _importedOriginalText;
    private bool _assigningOriginalText;
    private bool _rendering, _busy, _uiOperation, _closing, _allowClose, _initialized;

    private readonly string? _startupProjectPath;

    public MainWindow(string? startupProjectPath = null) : this(startupProjectPath, initializeApplication: true, null) { }

    // Integration tests construct controls on an STA dispatcher without loading any real user stores.
    internal MainWindow(string? startupProjectPath, bool initializeApplication, OllamaBackendManager? ollamaBackends = null)
    {
        _startupProjectPath = startupProjectPath;
        _ollamaBackends = ollamaBackends ?? new OllamaBackendManager();
        _translationClient = _ollamaBackends;
        InitializeComponent();
        _projectCatalog = new(_projectRepository);
        _projectCatalog.Changed += OnProjectCatalogChanged;
        _initialized = true;
        if (!initializeApplication) return;
        _autosave.Tick += OnAutosave;
        InitializeChapterView();
        InitializeReader();
        InitializeSettingsUi();
        InitializeSourceContextMenus();
        InitializeTraditionalEditor();
        InitializeProjectWorkspace();
        InitializeProductionShell();
        SavePathText.Text = "Dữ liệu riêng tư chỉ lưu tại: " + ChapterFileStore.DefaultDirectory;
        Refresh();
    }

    private async void OnSplitChapter(object sender, RoutedEventArgs e)
    {
        if (!BeginUiOperation()) return;
        try
        {
            var options = new SegmenterOptions
            {
                MinimumHanCharacters = ReadInteger(MinimumInput, "Tối thiểu"),
                TargetHanCharacters = ReadInteger(TargetInput, "Mục tiêu"),
                MaximumHanCharacters = ReadInteger(MaximumInput, "Tối đa"),
                MaximumTextElements = Math.Max(2000, ReadInteger(MaximumInput, "Tối đa") * 4)
            };
            var chapter = TranslationService.CreateChapter(TitleInput.Text, _importedOriginalText ?? ChapterInput.Text, options, ReadModelOptions());
            chapter.CharacterProject = CharacterProjectStore.Clone(_characterProject);
            chapter.SelectedPreset = _presetManager.Selected;
            chapter.ConfirmedTerms = ReadTerms().ToList();
            chapter.ConfirmedContextNotes = ContextNotesInput.Text;
            chapter = await RegisterChapterIfNeededAsync(chapter);
            AttachChapter(chapter);
            _selectedId = chapter.Segments[0].Id;
            _shownAttemptId = null;
            await SaveSelectedSettingsAsync();
            Refresh();
            SetStatus($"Đã chia {chapter.Segments.Count} đoạn. Bấm Dịch toàn chương hoặc dịch riêng ở tab Phân đoạn.");
        }
        catch (Exception ex) { ShowError(ex); Refresh(); }
        finally { EndUiOperation(); }
    }

    private async void OnNewChapter(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunWorkspaceTransitionAsync(() =>
            {
                DetachChapter(); ClearChapterEditor(); Refresh();
                SetStatus("Nhập chương mới. Chương trước đã được lưu.");
                return Task.CompletedTask;
            });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OnPasteChapter(object sender, RoutedEventArgs e)
    {
        try { AssignOriginalText(Clipboard.GetText()); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void OnImportText(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Văn bản UTF-8/Unicode (*.txt)|*.txt|Tất cả tệp|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        if (!BeginUiOperation()) return;
        try
        {
            // Reject legacy bytes that are not UTF-8 instead of silently replacing Chinese characters.
            AssignOriginalText(await File.ReadAllTextAsync(dialog.FileName, new UTF8Encoding(false, true)));
            TitleInput.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        }
        catch (DecoderFallbackException)
        {
            ShowError(new InvalidDataException("Tệp không phải UTF-8/Unicode. Hãy mở bằng trình soạn thảo, lưu lại UTF-8 hoặc dán văn bản từ clipboard."));
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private async void OnOpenProgress(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Tiến độ AI Editor (*.qtaie.json)|*.qtaie.json",
            InitialDirectory = ChapterFileStore.DefaultDirectory
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await RunWorkspaceTransitionAsync(async () =>
            {
                var loaded = await _store.LoadAsync(dialog.FileName);
                var openedChapter = await RegisterChapterIfNeededAsync(loaded.Chapter);
                // Persist crash recovery before replacing the previous live service.
                var prepared = new TranslationService(openedChapter, _translationClient,
                    _projectSession is null ? _store : new RegisteredChapterStore(_projectSession, _projectRepository));
                await prepared.SaveAsync();
                AttachChapter(prepared.GetSnapshot());
                _selectedId = openedChapter.SelectedSegmentId ?? openedChapter.Segments[0].Id; _shownAttemptId = null;
                ApplyInputs(openedChapter); SelectChapterPreset(openedChapter.SelectedPreset); Refresh();
                SetStatus(loaded.RecoveredFromBackup ? "Đã phục hồi từ bản dự phòng; hãy kiểm tra tiến độ." : "Đã mở tiến độ; không tự gửi lại request.");
            });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void OnTranslate(object sender, RoutedEventArgs e)
    {
        if (_service is null || _selectedId is not Guid selected || WorkActive || _uiOperation) return;
        var service = _service;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeWorkCompletion = completion.Task;
        CancellationTokenSource? cancellation = null;
        Task? request = null;
        try
        {
            EnsureOllamaBackendReady();
            if (!BeginUiOperation()) return;
            try { await SaveSelectedSettingsAsync(); } finally { EndUiOperation(); }
            var options = ReadModelOptions();
            _busy = true;
            cancellation = new(); _requestCancellation = cancellation;
            request = service.TranslateAsync(selected, options, cancellation.Token, _presetManager.Selected);
            _activeRequest = request;
            Refresh();
            SetStatus("Đang dịch riêng đúng một đoạn. Bạn vẫn có thể sửa nháp; bản sửa không bị ghi đè.");
            await request;
            if (!ReferenceEquals(service, _service) || _transitioning) return;
            if (_selectedId == selected) _shownAttemptId = null;
            Refresh();
            var translated = service.GetSnapshot().Segments.Single(s => s.Id == selected);
            if (translated.Status == SegmentStatus.Error)
                ShowError(new InvalidDataException(translated.LastError ?? "Kết quả AI không hợp lệ; hãy kiểm tra hoặc dịch lại."));
            else
                SetStatus("Đã nhận kết quả. Hãy đọc, sửa và bấm Duyệt khi bạn đồng ý. Không chuyển đoạn tự động.");
        }
        catch (OperationCanceledException) { if (!_transitioning) SetStatus("Đã hủy lần dịch. Bản nháp và lịch sử cũ được giữ."); }
        catch (Exception ex) { if (!_transitioning) ShowError(ex); }
        finally
        {
            cancellation?.Dispose();
            if (ReferenceEquals(cancellation, _requestCancellation)) _requestCancellation = null;
            if (ReferenceEquals(request, _activeRequest)) { _activeRequest = null; _busy = false; }
            if (ReferenceEquals(_activeWorkCompletion, completion.Task)) _activeWorkCompletion = null;
            if (!_transitioning) Refresh();
            completion.TrySetResult();
        }
    }

    private void OnCancelTranslation(object sender, RoutedEventArgs e) => _requestCancellation?.Cancel();

    private async void OnUseAiDraft(object sender, RoutedEventArgs e)
    {
        if (_service is null || _selectedId is not Guid id || _shownAttemptId is not Guid attemptId) return;
        if (!BeginUiOperation(allowDuringTranslation: true)) return;
        try
        {
            var selected = _service.GetSnapshot().Segments.Single(s => s.Id == id);
            var history = HistoryList.SelectedItem as AttemptItem;
            string? ai = HistoryText(selected, history);
            if (selected.UserDraft is not null && selected.UserDraft != ai &&
                MessageBox.Show(this, "Thao tác này thay bản nháp đang sửa bằng bản AI đang xem. Tiếp tục?", "Chọn bản AI",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (history?.Kind == 0) await _service.UseAiAsDraftAsync(id, attemptId);
            else await _service.RestoreDraftAsync(id, attemptId, history?.Kind == 1);
            Refresh();
            SetStatus("Đã chọn bản AI làm nháp theo yêu cầu của bạn. Bản này vẫn chờ duyệt.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private async void OnApprove(object sender, RoutedEventArgs e)
    {
        if (_service is null || _selectedId is not Guid id) return;
        if (!BeginUiOperation(allowDuringTranslation: true)) return;
        try
        {
            await _service.ApproveAsync(id);
            Refresh();
            SetStatus("Đã duyệt và lưu đoạn đang chọn. Chọn đoạn khác khi bạn muốn tiếp tục.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private void OnDraftChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized || _rendering || _service is null || _selectedId is not Guid id) return;
        _service.UpdateDraft(id, DraftText.Text);
        _pronounReport = null;
        // Keep the running interval: continuous typing must still get periodic checkpoints.
        _autosave.Start();
        SetStatus("Có thay đổi bản nháp; đang chờ tự lưu. Sửa bản đã duyệt sẽ đưa đoạn về chờ duyệt.");
        UpdateButtons();
    }

    private async void OnAutosave(object? sender, EventArgs e)
    {
        _autosave.Stop();
        var service = _service;
        if (service is null) return;
        try
        {
            await service.SaveAsync();
            if (_projectSession is not null && ReferenceEquals(service, _service))
                await _projectRepository.UpdateChapterSummaryAsync(_projectSession, service.GetSnapshot());
            if (service.HasUnsavedChanges && ReferenceEquals(service, _service) && !_closing) _autosave.Start();
            if (ReferenceEquals(service, _service) && !WorkActive && !_uiOperation && !_closing)
            {
                Refresh();
                RefreshProjectChapterList();
                SetStatus(service.HasUnsavedChanges ? "Còn thay đổi đang chờ lưu." : "Đã tự lưu bản nháp trên máy này.");
            }
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void OnSaveProgress(object sender, RoutedEventArgs e)
    {
        if (!BeginUiOperation(allowDuringTranslation: true)) return;
        try
        {
            if (!WorkActive) await SaveCharacterProjectAsync();
            await SaveCurrentAsync(); Refresh(); SetStatus("Đã lưu tiến độ, bản nháp và hồ sơ nhân vật trên máy này.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private async Task SaveCurrentAsync()
    {
        _autosave.Stop();
        var service = _service;
        var session = _projectSession;
        if (service is not null)
        {
            await service.SaveAsync();
            if (session is not null) await _projectRepository.UpdateChapterSummaryAsync(session, service.GetSnapshot());
        }
    }

    private void OnSegmentSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering) return;
        FollowQueueCheck.IsChecked = false;
        _selectedId = (SegmentList.SelectedItem as SegmentItem)?.Id;
        if (_service is not null && _selectedId is Guid id)
        {
            _service.SelectSegment(id);
            _autosave.Stop();
            _autosave.Start();
        }
        _shownAttemptId = null;
        Refresh();
    }

    private void OnHistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering) return;
        _shownAttemptId = (HistoryList.SelectedItem as AttemptItem)?.Id;
        Refresh();
    }

    private void OnShowPrompt(object sender, RoutedEventArgs e)
    {
        if (_service is null || _selectedId is not Guid id || _shownAttemptId is not Guid attemptId) return;
        var attempt = _service.GetSnapshot().Segments.Single(s => s.Id == id).Attempts.Single(a => a.Id == attemptId);
        var options = attempt.ModelOptions;
        string details = $"Model: {options.Model}\nTemperature: {options.Temperature}\nContext: {options.ContextSize}\n" +
            $"Đầu ra: {options.MaximumOutputTokens} token; timeout: {options.RequestTimeout.TotalSeconds:0} giây\n" +
            $"Preset: {attempt.PresetSnapshot?.Name ?? "Legacy"} (v{attempt.PresetSnapshot?.Revision})\n" +
            $"Tài nguyên project: revision {attempt.ProjectResourceRevision?.ToString() ?? "legacy/standalone"}\nPrompt: {attempt.PromptVersion}\n\n{attempt.Prompt}";
        new Window
        {
            Owner = this, Title = "Prompt đã lưu — chỉ hiển thị trên máy này", Width = 850, Height = 600,
            Content = new TextBox { Text = details, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(10) }
        }.ShowDialog();
    }

    private void Refresh()
    {
        if (!_initialized) return;
        _rendering = true;
        try
        {
            var chapter = _service?.GetSnapshot();
            var items = chapter?.Segments.Select(s => new SegmentItem(s.Id,
                $"Đoạn {s.Order} · {s.HanCharacterCount} chữ Hán\n{StatusLabel(s.Status)}\nTrung: {Preview(s.SourceText)}\nViệt: {Preview(s.EditableText)}")).ToList() ?? [];
            SegmentList.ItemsSource = items;
            SegmentList.SelectedItem = items.SingleOrDefault(i => i.Id == _selectedId);
            var segment = chapter?.Segments.SingleOrDefault(s => s.Id == _selectedId);
            string source = segment?.SourceText ?? "";
            if (SourceText.Text != source) SourceText.Text = source;
            RefreshTraditionalSegment(segment);
            var attempts = HistoryItems(segment);
            HistoryList.ItemsSource = attempts;
            var shown = attempts.SingleOrDefault(a => a.Id == _shownAttemptId) ?? attempts.FirstOrDefault();
            _shownAttemptId = shown?.Id;
            HistoryList.SelectedItem = shown;
            var attempt = segment?.Attempts.SingleOrDefault(a => a.Id == _shownAttemptId);
            AiText.Text = segment is null ? "" : HistoryText(segment, shown) ?? "";
            string draft = segment?.EditableText ?? "";
            if (DraftText.Text != draft)
            {
                int caret = DraftText.SelectionStart;
                DraftText.Text = draft;
                DraftText.Select(Math.Min(caret, draft.Length), 0);
            }
            var info = new StringBuilder();
            if (segment is not null) info.AppendLine("Trạng thái: " + StatusLabel(segment.Status));
            if (segment?.LastError is string error) info.AppendLine(error);
            if (segment?.RequiresRetranslation == true) info.AppendLine("Ranh giới đã đổi: cần dịch/kiểm tra lại riêng phần này.");
            if (segment?.ProtectedByPreviousEdit == true) info.AppendLine("Có bản sửa/duyệt trước đổi đoạn: bảo vệ khỏi dịch tự động. Xem lịch sử đổi đoạn để lấy bản cũ.");
            if (attempt?.Metrics is GenerationMetrics metrics)
                info.AppendLine($"Token vào/ra: {metrics.InputTokens?.ToString() ?? "không có"}/{metrics.OutputTokens?.ToString() ?? "không có"}; " +
                    $"thời gian client: {metrics.ClientElapsed.TotalSeconds:0.0}s" +
                    (metrics.TotalDurationNanoseconds is long ns ? $"; Ollama: {ns / 1_000_000_000d:0.0}s" : ""));
            if (attempt is not null)
            {
                foreach (var issue in attempt.Issues) info.AppendLine("Cảnh báo: " + issue.Message);
                if (attempt.NormalizationRunId is Guid runId && segment!.NormalizationHistory.SingleOrDefault(r => r.Id == runId) is { } run)
                    info.AppendLine($"Chuẩn hóa xưng hô: {run.Changes.Count} thay đổi; bản AI gốc vẫn nằm trong lịch sử.");
            }
            info.Append("Kiểm tra tự động chỉ gợi ý lỗi; không xác nhận bản dịch đúng. Bạn quyết định duyệt.");
            ReviewInfo.Text = info.ToString();
            ChapterSummary.Text = chapter is null ? "" : $"{chapter.Segments.Count} đoạn · đã duyệt {chapter.Segments.Count(s => s.Status == SegmentStatus.Approved)} · " +
                $"đã chuẩn hóa {chapter.Segments.SelectMany(s => s.NormalizationHistory).Where(r => !r.Reverted).Sum(r => r.Changes.Count)} vị trí";
            RefreshChapterBlocks(chapter);
            RefreshReader(chapter);
            RefreshQueueProgress();
            SavePathText.Text = _service is not null ? "Lưu tại: " + _service.SavePath
                : _projectSession is not null ? "Dự án: " + _projectSession.RootDirectory
                : "Dữ liệu riêng tư chỉ lưu tại: " + ChapterFileStore.DefaultDirectory;
            UpdateShellChrome();
        }
        finally { _rendering = false; }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool document = _service is not null, selected = document && _selectedId is not null;
        bool available = !_uiOperation && !_closing;
        bool active = WorkActive;
        NewButton.IsEnabled = OpenButton.IsEnabled = !active && available;
        SaveButton.IsEnabled = document && available;
        SplitButton.IsEnabled = !document && !active && available && _presetsReady;
        PasteButton.IsEnabled = ImportTextButton.IsEnabled = !document && !active && available;
        ChapterInput.IsReadOnly = TitleInput.IsReadOnly = document || !available;
        MinimumInput.IsEnabled = TargetInput.IsEnabled = MaximumInput.IsEnabled = !document && available;
        SegmentList.IsEnabled = available;
        TranslateButton.IsEnabled = selected && !active && available && _presetsReady;
        CancelButton.IsEnabled = _busy && !_closing;
        var selectedSegment = _service?.GetSnapshot().Segments.SingleOrDefault(s => s.Id == _selectedId);
        bool processingSelected = selectedSegment?.Status == SegmentStatus.Translating;
        UseAiButton.IsEnabled = selected && !processingSelected && !string.IsNullOrWhiteSpace(AiText.Text) && available;
        ApproveButton.IsEnabled = selected && !processingSelected && !string.IsNullOrWhiteSpace(DraftText.Text) && available;
        ApproveCurrentButton.IsEnabled = ApproveButton.IsEnabled;
        PromptButton.IsEnabled = selected && (HistoryList.SelectedItem as AttemptItem)?.Kind == 0;
        if ((HistoryList.SelectedItem as AttemptItem)?.Kind == 3) UseAiButton.IsEnabled = false;
        DraftText.IsReadOnly = !selected || !available;
        ChapterBlocksItems.IsEnabled = document && available;
        TranslateAllButton.IsEnabled = !active && available && _presetsReady;
        RetryErrorsButton.IsEnabled = document && !active && available && _presetsReady;
        PauseQueueButton.IsEnabled = _queue?.IsRunning == true && _service?.GetSnapshot().Queue.Status == QueueStatus.Running && available;
        CancelQueueButton.IsEnabled = _queue?.IsRunning == true && !_closing;
        var queueStatus = _service?.GetSnapshot().Queue.Status;
        ResumeQueueButton.IsEnabled = document && !active && available && queueStatus is QueueStatus.Paused or QueueStatus.Interrupted or QueueStatus.Cancelled or QueueStatus.Faulted;
        ExportButton.IsEnabled = document && available;
        BatchReplaceButton.IsEnabled = document && !active && available;
        NormalizePronounsButton.IsEnabled = document && !active && available;
        ApplyPronounsButton.IsEnabled = document && !active && available && _pronounReport?.Safe > 0;
        UndoPronounsButton.IsEnabled = document && !active && available &&
            _service!.GetSnapshot().Segments.SelectMany(s => s.NormalizationHistory).Any(r => !r.Reverted);
        ScanChineseButton.IsEnabled = document && !active && available;
        UndoBatchReplaceButton.IsEnabled = document && !active && available &&
            _service!.GetSnapshot().BatchReplacementHistory.Any(r => !r.Reverted && !r.Automatic);
        SettingsPanel.IsEnabled = !active && available;
        PresetPanel.IsEnabled = _presetsReady;
        NewCharacterProjectButton.IsEnabled = AddCharacterButton.IsEnabled = SaveCharactersButton.IsEnabled =
            ImportCharactersButton.IsEnabled = ExportCharactersButton.IsEnabled = !active && available;
        EditCharacterButton.IsEnabled = DeleteCharacterButton.IsEnabled = CharactersGrid.SelectedItem is CharacterProfile && !active && available;
        PreviewCharactersButton.IsEnabled = document && !active && available;
        SplitSegmentButton.IsEnabled = MergeSegmentButton.IsEnabled = MoveBoundaryButton.IsEnabled = selected && !active && available;
        ArchiveButton.IsEnabled = document && available;
        RestoreStructureButton.IsEnabled = document && !active && available && _service!.GetSnapshot().SegmentationHistory.Count > 0;
        if (selected)
        {
            var segment = _service!.GetSnapshot().Segments.Single(s => s.Id == _selectedId);
            TranslateButton.Content = segment.Attempts.Count == 0 ? "Dịch đoạn đang chọn" : "Dịch lại đoạn đang chọn";
        }
    }

    private ModelOptions ReadModelOptions()
    {
        if (!double.TryParse(TemperatureInput.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double temperature))
            throw new ArgumentException("Temperature phải là số, ví dụ 0.1.");
        var options = new ModelOptions
        {
            Model = ModelInput.Text.Trim(), Temperature = temperature,
            ContextSize = ReadInteger(ContextInput, "Context"),
            MaximumOutputTokens = ReadInteger(OutputTokensInput, "Đầu ra tối đa"),
            RequestTimeout = TimeSpan.FromSeconds(ReadInteger(TimeoutInput, "Timeout"))
        };
        options.Validate();
        return options;
    }

    private static int ReadInteger(TextBox input, string name) =>
        int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value : throw new ArgumentException($"{name} phải là số nguyên.");

    private void ApplyInputs(ChapterDocument chapter)
    {
        TitleInput.Text = chapter.Title;
        AssignOriginalText(chapter.OriginalText);
        MinimumInput.Text = chapter.SegmentationOptions.MinimumHanCharacters.ToString(CultureInfo.InvariantCulture);
        TargetInput.Text = chapter.SegmentationOptions.TargetHanCharacters.ToString(CultureInfo.InvariantCulture);
        MaximumInput.Text = chapter.SegmentationOptions.MaximumHanCharacters.ToString(CultureInfo.InvariantCulture);
        ModelInput.Text = chapter.DefaultModelOptions.Model;
        TemperatureInput.Text = chapter.DefaultModelOptions.Temperature.ToString(CultureInfo.InvariantCulture);
        ContextInput.Text = chapter.DefaultModelOptions.ContextSize.ToString(CultureInfo.InvariantCulture);
        OutputTokensInput.Text = chapter.DefaultModelOptions.MaximumOutputTokens.ToString(CultureInfo.InvariantCulture);
        TimeoutInput.Text = chapter.DefaultModelOptions.RequestTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
        if (_projectSession is null)
        {
            SetGlossaryTerms(chapter.ConfirmedTerms);
            ContextNotesInput.Text = chapter.ConfirmedContextNotes;
        }
        AutoCleanConfirmedCheck.IsChecked = chapter.TextCleaner.AutoApplyConfirmedMappings;
    }

    private static string StatusLabel(SegmentStatus status) => status switch
    {
        SegmentStatus.Untranslated => "Chưa dịch", SegmentStatus.Translating => "Đang dịch",
        SegmentStatus.AwaitingReview => "Đã dịch / nháp — chờ duyệt", SegmentStatus.Approved => "Đã duyệt",
        SegmentStatus.InspectionRequired => "Cần kiểm tra — chờ duyệt", _ => "Lỗi"
    };

    private static string AttemptLabel(AttemptOutcome outcome) => outcome switch
    {
        AttemptOutcome.InProgress => "đang dịch", AttemptOutcome.Completed => "chờ người duyệt",
        AttemptOutcome.InvalidOutput => "kết quả rỗng", AttemptOutcome.Cancelled => "đã hủy",
        AttemptOutcome.Interrupted => "bị gián đoạn", _ => "có lỗi"
    };

    private void SetStatus(string text) { StatusText.Text = text; StatusText.Foreground = Brushes.DarkSlateGray; }

    private bool BeginUiOperation(bool allowDuringTranslation = false)
    {
        if (_uiOperation || _closing || (WorkActive && !allowDuringTranslation)) return false;
        _uiOperation = true;
        UpdateButtons();
        return true;
    }

    private void EndUiOperation() { _uiOperation = false; UpdateButtons(); }

    private void AssignOriginalText(string text)
    {
        _assigningOriginalText = true;
        try { _importedOriginalText = text; ChapterInput.Text = text; }
        finally { _assigningOriginalText = false; }
    }

    private void OnChapterInputChanged(object sender, TextChangedEventArgs e)
    {
        if (!_assigningOriginalText && _initialized) _importedOriginalText = null;
    }
    private void ShowError(Exception error)
    {
        if (error is LegacyImportException import) ShowLegacyImportReport(import.Report);
        else if (error is LegacyImportCanceledException cancelledImport) ShowLegacyImportReport(cancelledImport.Report);
        StatusText.Text = "Lỗi: " + error.Message +
            (error is IOException or UnauthorizedAccessException
                ? " Tác vụ chưa hoàn tất; hãy giữ ứng dụng mở, kiểm tra dung lượng/quyền ghi và thử lại thao tác lưu."
                : _service?.HasUnsavedChanges == true ? " Có thay đổi chưa lưu; hãy giữ ứng dụng mở và thử Lưu tiến độ." : "");
        StatusText.Foreground = Brushes.Firebrick;
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        if (_uiOperation) { SetStatus("Đang đọc hoặc lưu tệp. Hãy chờ thao tác hoàn tất rồi đóng ứng dụng."); return; }
        if (_service is null && !string.IsNullOrWhiteSpace(ChapterInput.Text) &&
            MessageBox.Show(this, "Chương này chưa được chia và lưu. Đóng sẽ mất phần văn bản đang nhập. Chọn Không để chia/lưu trước.",
                "Nguyên văn chưa lưu", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _closing = true;
        _autosave.Stop();
        _readerSaveTimer.Stop();
        _requestCancellation?.Cancel();
        UpdateButtons();
        try
        {
            await PauseProjectTranslationForContextChangeAsync();
            if (_activeRequest is not null)
            {
                if (_queue?.IsRunning == true) await _queue.CancelAsync();
                try { await _activeRequest; }
                catch (OperationCanceledException) { SetStatus("Request đã hủy; đang lưu bản nháp trước khi đóng."); }
                catch (OllamaRequestException ex) { ShowError(ex); }
            }
            if (_projectSession is not null)
            {
                await SaveCurrentAsync();
                await SaveProjectAssetsAsync();
            }
            else
            {
                if (_presetsReady)
                {
                    await SavePresetEditorAsync();
                    await SaveCharacterProjectAsync();
                }
                await SaveCurrentAsync();
                await SaveReaderPreferencesAsync();
            }
            _allowClose = true;
            Close();
        }
        catch (Exception ex)
        {
            _closing = false;
            ShowError(ex);
            UpdateButtons();
            MessageBox.Show(this, "Chưa lưu được tiến độ. Ứng dụng vẫn mở để giữ bản nháp. Hãy kiểm tra dung lượng/quyền ghi rồi bấm Lưu tiến độ.",
                "Không đóng khi chưa lưu", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _progressTimer.Stop(); _readerSaveTimer.Stop();
        _chapterManagerWindow?.Close();
        ClearProjectTranslationCoordinator();
        _projectCatalog.Changed -= OnProjectCatalogChanged;
        _projectCatalog.Dispose();
        _gpuConnections.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _ollamaBackends.Dispose();
    }
    private sealed record SegmentItem(Guid Id, string Label);
    private sealed record AttemptItem(Guid Id, string Label, int Kind = 0, DateTimeOffset Date = default);
}
