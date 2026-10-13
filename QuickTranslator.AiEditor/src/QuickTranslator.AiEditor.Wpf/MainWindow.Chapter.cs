using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Segmentation;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private TranslationQueue? _queue;
    private readonly ObservableCollection<ChapterBlock> _chapterBlocks = [];
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool WorkActive => _busy || _queue?.IsRunning == true;

    private void InitializeChapterView()
    {
        ChapterBlocksItems.ItemsSource = _chapterBlocks;
        Loaded += OnPresetsLoaded;
        _progressTimer.Tick += (_, _) => RefreshQueueProgress();
        _progressTimer.Start();
    }

    private void AttachChapter(ChapterDocument chapter)
    {
        if (_projectSession is not null && (chapter.ProjectId != _projectSession.Manifest.ProjectId ||
            !_projectSession.Manifest.Chapters.Any(x => x.ChapterId == chapter.Id && x.DeletedAt is null)))
            throw new InvalidOperationException("Đăng ký chương trong manifest trước khi mở editor.");
        if (_queue is not null) _queue.ProgressChanged -= OnQueueProgress;
        _service = new(chapter, _translationClient, _projectSession is null ? _store : new RegisteredChapterStore(_projectSession, _projectRepository));
        _queue = new(_service);
        _queue.ProgressChanged += OnQueueProgress;
        if (_projectSession is null) LoadCharacterProject(_service.GetSnapshot().CharacterProject);
        else ApplyCurrentProjectResources();
    }
    private void DetachChapter()
    {
        if (_queue is not null) _queue.ProgressChanged -= OnQueueProgress;
        _queue = null; _service = null;
    }
    private void OnQueueProgress(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (!ReferenceEquals(sender, _queue) || _closing || _transitioning) return;
        if (FollowQueueCheck.IsChecked == true && !DraftText.IsKeyboardFocusWithin && _service?.GetSnapshot().Queue.CurrentSegmentId is Guid current)
        { _selectedId = current; _shownAttemptId = null; }
        Refresh();
        _ = SyncProjectSummaryAsync();
    });

    private SegmenterOptions ReadSegmenterOptions() => new()
    {
        MinimumHanCharacters = ReadInteger(MinimumInput, "Tối thiểu"), TargetHanCharacters = ReadInteger(TargetInput, "Mục tiêu"),
        MaximumHanCharacters = ReadInteger(MaximumInput, "Tối đa"), MaximumTextElements = Math.Max(2000, ReadInteger(MaximumInput, "Tối đa") * 4)
    };

    private async Task PrepareChapterAsync()
    {
        if (_service is null)
        {
            var chapter = TranslationService.CreateChapter(TitleInput.Text, _importedOriginalText ?? ChapterInput.Text, ReadSegmenterOptions(), ReadModelOptions());
            chapter.CharacterProject = CharacterProjectStore.Clone(_characterProject);
            chapter.SelectedPreset = _presetManager.Selected;
            chapter.ConfirmedTerms = ReadTerms().ToList(); chapter.ConfirmedContextNotes = ContextNotesInput.Text;
            chapter = await RegisterChapterIfNeededAsync(chapter);
            AttachChapter(chapter); _selectedId = chapter.SelectedSegmentId; _shownAttemptId = null;
        }
        await SaveSelectedSettingsAsync();
    }

    private async void OnTranslateAll(object sender, RoutedEventArgs e) => await RunQueueUiAsync(false, QueueMode.MissingSegments);
    private async void OnRetryErrors(object sender, RoutedEventArgs e) => await RunQueueUiAsync(false, QueueMode.FailedSegments);
    private async void OnResumeQueue(object sender, RoutedEventArgs e) => await RunQueueUiAsync(true, QueueMode.MissingSegments);

    private async Task RunQueueUiAsync(bool resume, QueueMode mode)
    {
        if (WorkActive || !BeginUiOperation()) return;
        Task? request = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeWorkCompletion = completion.Task;
        TranslationService? service = null;
        bool preparing = true;
        try
        {
            if (!resume) await PrepareChapterAsync();
            else await SaveCurrentAsync();
            EndUiOperation();
            preparing = false;
            service = _service;
            FollowQueueCheck.IsChecked = true;
            request = resume ? _queue!.ResumeAsync() : _queue!.StartAsync(mode, ReadModelOptions(), _presetManager.Selected);
            _activeRequest = request;
            Refresh();
            SetStatus(resume ? "Đang tiếp tục phần chưa hoàn tất với snapshot cấu hình của hàng đợi." : "Đang dịch tuần tự toàn chương; tự lưu từng đoạn, không chờ duyệt.");
            await request;
            if (!ReferenceEquals(service, _service) || _transitioning) return;
            Refresh();
            var state = _service!.GetSnapshot().Queue;
            SetStatus(state.LastError ?? QueueLabel(state.Status));
        }
        catch (Exception ex) { if (!_transitioning) ShowError(ex); }
        finally
        {
            if (ReferenceEquals(_activeRequest, request)) _activeRequest = null;
            if (preparing) EndUiOperation();
            if (ReferenceEquals(_activeWorkCompletion, completion.Task)) _activeWorkCompletion = null;
            if (!_transitioning) Refresh();
            completion.TrySetResult();
        }
    }

    private async void OnPauseQueue(object sender, RoutedEventArgs e)
    {
        try
        {
            var pause = _queue?.PauseAsync();
            SetStatus("Đang tạm dừng: chờ request hiện tại kết thúc và lưu. Chỉ đổi ranh giới khi trạng thái là Đã tạm dừng.");
            Refresh();
            if (pause is not null) await pause;
            Refresh();
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void OnCancelQueue(object sender, RoutedEventArgs e)
    {
        try { if (_queue is not null) await _queue.CancelAsync(); Refresh(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RefreshQueueProgress()
    {
        var progress = _queue?.GetProgress();
        ChapterProgress.Value = progress is null || progress.Total == 0 ? 0 : 100d * progress.Translated / progress.Total;
        QueueInfo.Text = progress is null ? "Chưa có chương. Một lần bấm sẽ tự chia, dịch và ghép." :
            $"{QueueLabel(progress.Status)} · đã có bản dịch {progress.Translated}/{progress.Total} · lỗi {progress.Errors} · cần kiểm tra {progress.NeedsInspection}" +
            (progress.CurrentOrder is int order ? $" · đang dịch đoạn {order}" : "") +
            $"\nĐã chạy {progress.Elapsed:hh\\:mm\\:ss} · còn khoảng " +
            (progress.EstimatedRemaining is TimeSpan eta ? eta.ToString(@"hh\:mm\:ss") : "chưa đủ số liệu") +
            (progress.Error is null ? "" : "\n" + progress.Error);
    }
    private static string QueueLabel(QueueStatus status) => status switch
    {
        QueueStatus.Idle => "Sẵn sàng", QueueStatus.Running => "Đang dịch", QueueStatus.PauseRequested => "Đang chờ tạm dừng",
        QueueStatus.Paused => "Đã tạm dừng", QueueStatus.Cancelling => "Đang hủy", QueueStatus.Cancelled => "Đã hủy — có thể tiếp tục",
        QueueStatus.Interrupted => "Đã phục hồi — bấm Tiếp tục", QueueStatus.Completed => "Đã dịch hết — chờ người dùng duyệt",
        QueueStatus.CompletedWithWarnings => "Đã dịch hết — có cảnh báo cần kiểm tra",
        QueueStatus.CompletedWithErrors => "Kết thúc hàng đợi — còn lỗi hoặc đoạn chưa hoàn tất", _ => "Hàng đợi dừng vì lỗi"
    };

    private void RefreshChapterBlocks(ChapterDocument? chapter)
    {
        var live = chapter?.Segments ?? [];
        for (int i = _chapterBlocks.Count - 1; i >= 0; i--)
            if (!live.Any(s => s.Id == _chapterBlocks[i].Id)) _chapterBlocks.RemoveAt(i);
        for (int i = 0; i < live.Count; i++)
        {
            var segment = live[i];
            var block = _chapterBlocks.SingleOrDefault(b => b.Id == segment.Id);
            if (block is null) { block = new(segment.Id, OnChapterDraftChanged); _chapterBlocks.Insert(i, block); }
            else if (_chapterBlocks.IndexOf(block) != i) _chapterBlocks.Move(_chapterBlocks.IndexOf(block), i);
            block.Label = $"Đoạn {segment.Order} · {StatusLabel(segment.Status)}" + (segment.RequiresRetranslation ? " · ranh giới đã đổi" : "");
            block.Text = segment.EditableText;
        }
    }
    private void OnChapterDraftChanged(Guid id, string text)
    {
        if (_rendering || _closing || _service is null || !_service.GetSnapshot().Segments.Any(s => s.Id == id)) return;
        _service.UpdateDraft(id, text); _autosave.Start();
        SetStatus("Đã sửa khối bản dịch chương; thay đổi gắn với đúng đoạn và đang tự lưu.");
        Dispatcher.BeginInvoke(Refresh);
    }

    private async void OnExportChapter(object sender, RoutedEventArgs e)
    {
        if (_service is null || !BeginUiOperation(allowDuringTranslation: true)) return;
        try
        {
            await SaveCurrentAsync();
            var snapshot = _service.GetSnapshot();
            var composed = ChapterTranslationComposer.Compose(snapshot);
            bool incomplete = !composed.IsComplete;
            if ((incomplete || composed.InspectionIds.Count > 0) && MessageBox.Show(this,
                incomplete ? "Còn đoạn lỗi/chưa hoàn tất. Xuất bản nháp hiện tại? File có thể thiếu phần dịch." : "Có đoạn cần kiểm tra. Xuất bản đang chờ duyệt?",
                "Xuất bản nháp", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            string title = string.Concat(snapshot.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var dialog = new SaveFileDialog { Filter = "Văn bản UTF-8 (*.txt)|*.txt", FileName = title + ".txt", DefaultExt = ".txt", AddExtension = true };
            if (dialog.ShowDialog(this) != true) return;
            await ChapterTranslationComposer.ExportAsync(snapshot, dialog.FileName, allowIncomplete: incomplete);
            SetStatus(incomplete ? "Đã xuất bản nháp chưa hoàn tất, không coi là toàn chương thành công." : "Đã xuất TXT UTF-8 theo thứ tự đoạn; trạng thái duyệt giữ nguyên.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private async void OnSplitSegment(object sender, RoutedEventArgs e)
    {
        if (_service is null || _selectedId is not Guid id) return;
        string text = _service.GetSnapshot().Segments.Single(s => s.Id == id).SourceText;
        int? boundary = PickBoundary(text, text.Length / 2);
        if (boundary is int cut) await ChangeStructureUiAsync(confirmed => _service.SplitSegmentAsync(id, cut, confirmed));
    }
    private async void OnMergeSegments(object sender, RoutedEventArgs e)
    {
        if (_service is not null && _selectedId is Guid id) await ChangeStructureUiAsync(confirmed => _service.MergeWithNextAsync(id, confirmed));
    }
    private async void OnMoveBoundary(object sender, RoutedEventArgs e)
    {
        if (_service is null || _selectedId is not Guid id) return;
        var chapter = _service.GetSnapshot(); int index = chapter.Segments.FindIndex(s => s.Id == id);
        if (index + 1 >= chapter.Segments.Count) { ShowError(new InvalidOperationException("Không có đoạn liền sau.")); return; }
        string text = chapter.Segments[index].SourceText + chapter.Segments[index + 1].SourceText;
        int? boundary = PickBoundary(text, chapter.Segments[index].SourceText.Length);
        if (boundary is int cut) await ChangeStructureUiAsync(confirmed => _service.MoveBoundaryAsync(id, cut, confirmed));
    }
    private async void OnRestoreStructure(object sender, RoutedEventArgs e)
    {
        if (_service is not null) await ChangeStructureUiAsync(confirmed => _service.RestoreLastSegmentationAsync(confirmed));
    }
    private async Task ChangeStructureUiAsync(Func<bool, Task> change)
    {
        if (!BeginUiOperation()) return;
        try
        {
            try { await change(false); }
            catch (SegmentationConfirmationRequiredException)
            {
                if (MessageBox.Show(this, "Lưu toàn bộ bản cũ vào lịch sử và thay ranh giới của phần này?\n" +
                    "Chỉ các đoạn bị ảnh hưởng cần xử lý lại. Bản sửa/duyệt được giữ và bảo vệ khỏi dịch tự động. Khi tách, hãy lấy bản cũ trong lịch sử để chia bản Việt thủ công hoặc dịch lại riêng.",
                    "Xác nhận đổi ranh giới", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                await change(true);
            }
            _selectedId = _service!.GetSnapshot().SelectedSegmentId; _shownAttemptId = null;
            Refresh(); SetStatus("Đã đổi ranh giới và lưu. Nguyên văn giữ nguyên; bản trước đó có trong lịch sử đổi đoạn.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }
    private int? PickBoundary(string text, int preferred)
    {
        var boundaries = TextSegmenter.GetSentenceBoundaries(text).Select(offset => new BoundaryChoice(offset,
            $"Vị trí {offset} · {Preview(text[Math.Max(0, offset - 18)..Math.Min(text.Length, offset + 18)])}")).ToArray();
        if (boundaries.Length == 0) { ShowError(new InvalidOperationException("Không có cuối câu/lời thoại hoàn chỉnh để tách an toàn. Không sửa nguyên văn tự động.")); return null; }
        var selector = new ComboBox { ItemsSource = boundaries, DisplayMemberPath = "Label", Margin = new Thickness(8) };
        selector.SelectedItem = boundaries.MinBy(b => Math.Abs(b.Offset - preferred));
        var left = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var right = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        void ShowParts() { if (selector.SelectedItem is not BoundaryChoice choice) return; int offset = choice.Offset; left.Text = text[..offset]; right.Text = text[offset..]; }
        selector.SelectionChanged += (_, _) => ShowParts(); ShowParts();
        var content = new DockPanel(); content.Children.Add(selector); DockPanel.SetDock(selector, Dock.Top);
        var buttons = new WrapPanel(); var accept = new Button { Content = "Áp dụng ranh giới" }; var cancel = new Button { Content = "Hủy" };
        buttons.Children.Add(accept); buttons.Children.Add(cancel); content.Children.Add(buttons); DockPanel.SetDock(buttons, Dock.Bottom);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new()); grid.Children.Add(left); grid.Children.Add(right); Grid.SetColumn(right, 1); content.Children.Add(grid);
        var window = new Window { Owner = this, Title = "Chọn cuối câu — xem trước hai đoạn", Width = 900, Height = 600, Content = content };
        accept.Click += (_, _) => window.DialogResult = true; cancel.Click += (_, _) => window.DialogResult = false;
        return window.ShowDialog() == true && selector.SelectedItem is BoundaryChoice choice ? choice.Offset : null;
    }
    private void OnShowSegmentationArchive(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        var history = _service.GetSnapshot().SegmentationHistory;
        string text = string.Join("\n\n", history.Select(change => $"{change.ChangedAt.ToLocalTime():g} — {change.Description}\n" +
            string.Join("\n\n", change.PreviousSegments.Select(s => $"Đoạn cũ {s.Order} [{s.Id}]\nTRUNG:\n{s.SourceText}\nBẢN ĐANG SỬA:\n{s.EditableText}\n" +
                string.Join("\n", s.Attempts.Select((a, i) => $"AI lần {i + 1}:\n{a.AiText}")) + "\n" + string.Join("\n", s.Approvals.Select(a => "BẢN ĐÃ DUYỆT:\n" + a.Text)) +
                "\n" + string.Join("\n", s.DraftHistory.Select(d => "NHÁP LƯU:\n" + d.Text))))));
        ShowTextWindow("Bản trước đổi đoạn — có thể chọn và sao chép", text.Length == 0 ? "Chưa có thay đổi phân đoạn." : text);
    }

    private static string Preview(string text) => text.Replace('\r', ' ').Replace('\n', ' ') is var normalized && normalized.Length > 60 ? normalized[..60] + "…" : normalized;
    private static List<AttemptItem> HistoryItems(TranslationSegment? segment)
    {
        if (segment is null) return [];
        return segment.Attempts.Select((a, i) => new AttemptItem(a.Id, $"AI lần {i + 1} · {AttemptLabel(a.Outcome)}", 0, a.StartedAt))
            .Concat(segment.Approvals.Select((a, i) => new AttemptItem(a.Id, $"Bản duyệt {i + 1}", 1, a.ApprovedAt)))
            .Concat(segment.DraftHistory.Select((a, i) => new AttemptItem(a.Id, $"Nháp lưu {i + 1} · {a.Reason}", 2, a.RecordedAt)))
            .Concat(segment.NormalizationHistory.Select((a, i) => new AttemptItem(a.Id,
                $"Chuẩn hóa xưng hô {i + 1}" + (a.Reverted ? " · đã hoàn tác" : a.Automatic ? " · tự động" : " · thủ công"), 3, a.CreatedAt)))
            .OrderByDescending(a => a.Date).ToList();
    }
    private static string? HistoryText(TranslationSegment segment, AttemptItem? item) => item?.Kind switch
    {
        0 => segment.Attempts.Single(a => a.Id == item.Id).AiText,
        1 => segment.Approvals.Single(a => a.Id == item.Id).Text,
        2 => segment.DraftHistory.Single(a => a.Id == item.Id).Text,
        3 => segment.NormalizationHistory.Single(a => a.Id == item.Id).AfterText, _ => ""
    };
    private void ShowTextWindow(string title, string text) => new Window
    {
        Owner = this, Title = title, Width = 900, Height = 650,
        Content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(10) }
    }.ShowDialog();
    private sealed record BoundaryChoice(int Offset, string Label);
    private sealed class ChapterBlock(Guid id, Action<Guid, string> changed) : INotifyPropertyChanged
    {
        public Guid Id { get; } = id;
        private string _text = "", _label = "";
        public string Text { get => _text; set { if (_text == value) return; _text = value; PropertyChanged?.Invoke(this, new(nameof(Text))); changed(Id, value); } }
        public string Label { get => _label; set { if (_label == value) return; _label = value; PropertyChanged?.Invoke(this, new(nameof(Label))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
