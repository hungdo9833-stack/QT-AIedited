using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.Importing;

namespace QuickTranslator.AiEditor.Wpf;

public partial class ChapterTitleTranslationWindow : Window
{
    private readonly Guid _projectId;
    private readonly string _sourceKey;
    private readonly ChapterTitleTranslationSource[] _sources;
    private readonly ChapterTitleRuntimeFactory _runtimeFactory;
    private readonly ChapterTitleTranslationDraftStore _store;
    private readonly ObservableCollection<Row> _rows = [];
    private ChapterTitleTranslationDraft? _draft;
    private CancellationTokenSource? _cancellation;
    private bool _working;

    internal ChapterTitleTranslationWindow(Guid projectId, string sourceKey,
        IReadOnlyCollection<ChapterTitleTranslationSource> sources, ChapterTitleRuntimeFactory runtimeFactory,
        string draftPath)
    {
        _projectId = projectId; _sourceKey = sourceKey;
        _sources = sources?.ToArray() ?? throw new ArgumentNullException(nameof(sources));
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        _store = new(draftPath);
        InitializeComponent();
        MainWindow.ClampWindowToWorkArea(this, 1120, 760);
        TitleGrid.ItemsSource = _rows;
        ScopeText.Text = $"{_sources.Length:N0} chương · chỉ cập nhật metadata tiêu đề sau khi xác nhận";
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    internal ChapterTitleTranslationDraft? ResultDraft { get; private set; }
    internal bool OverwriteProtected => OverwriteProtectedCheck.IsChecked == true;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            ChapterTitleTranslationDraft? saved = await _store.LoadAsync();
            if (saved is not null && saved.ProjectId == _projectId && saved.SourceKey == _sourceKey &&
                saved.Items.Select(item => item.Id).SequenceEqual(_sources.Select(item => item.Id)))
            {
                _draft = saved;
                MethodInput.SelectedIndex = (int)saved.Method;
                StatusText.Text = "Đã khôi phục draft dịch tiêu đề từ lần trước.";
            }
            RefreshRows();
        }
        catch (Exception ex) { StatusText.Text = "Không khôi phục được draft: " + ex.Message; }
    }

    private async void OnTranslateAll(object sender, RoutedEventArgs e) => await TranslateAsync(false, null);
    private async void OnRetryFailed(object sender, RoutedEventArgs e) => await TranslateAsync(true, null);
    private async void OnRetrySelected(object sender, RoutedEventArgs e)
    {
        Guid[] ids = TitleGrid.SelectedItems.Cast<Row>().Select(row => row.Id).ToArray();
        if (_draft is not null)
            foreach (ChapterTitleTranslationItem item in _draft.Items.Where(item => ids.Contains(item.Id)))
                item.Status = ChapterTitleTranslationStatus.Pending;
        await TranslateAsync(false, ids);
    }
    private void OnCancelTranslation(object sender, RoutedEventArgs e) => _cancellation?.Cancel();

    private async Task TranslateAsync(bool retryFailedOnly, IReadOnlyCollection<Guid>? selected)
    {
        if (_working) return;
        try
        {
            ChapterTitleTranslationMethod method = SelectedMethod();
            ChapterTitleRuntime runtime = await _runtimeFactory(method, CancellationToken.None);
            EnsureDraft(method, runtime);
            if (OverwriteProtected && _draft is not null)
                foreach (ChapterTitleTranslationItem item in _draft.Items.Where(item => item.ExistingTitleProtected &&
                             item.Status == ChapterTitleTranslationStatus.Protected &&
                             (selected is null || selected.Contains(item.Id))))
                    item.Status = ChapterTitleTranslationStatus.Pending;
            _working = true; _cancellation = new(); UpdateCommands();
            await _store.SaveAsync(_draft!);
            var progress = new Progress<ChapterTitleTranslationProgress>(value =>
                ProgressText.Text = $"{value.Completed:N0}/{value.Total:N0} · {value.Failed:N0} lỗi");
            await new ChapterTitleTranslationService().TranslateAsync(_draft!, runtime.Backend,
                runtime.ModelOptions, retryFailedOnly, selected, OverwriteProtected, progress,
                _store.SaveAsync, _cancellation.Token);
            StatusText.Text = "Đã hoàn tất các batch hợp lệ. Kiểm tra bảng trước khi áp dụng.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Đã hủy; các batch hoàn tất vẫn được giữ trong draft."; }
        catch (Exception ex)
        {
            StatusText.Text = "Dịch tiêu đề dừng: " + ex.Message;
            ThemedDialog.Show(this, ex.Message, "Dịch tiêu đề", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cancellation?.Dispose(); _cancellation = null; _working = false; RefreshRows();
        }
    }

    private void EnsureDraft(ChapterTitleTranslationMethod method, ChapterTitleRuntime runtime)
    {
        bool compatible = _draft is not null && _draft.Method == method &&
            _draft.Items.Select(item => item.Id).SequenceEqual(_sources.Select(item => item.Id));
        if (compatible) return;
        _draft = ChapterTitleTranslationDraft.Create(_projectId, _sourceKey, method, _sources,
            runtime.BackendProfileId, runtime.Model);
    }

    private void RefreshRows()
    {
        _rows.Clear();
        IEnumerable<ChapterTitleTranslationItem> items = _draft?.Items ?? _sources.Select(source => new ChapterTitleTranslationItem
        {
            Id = source.Id, ChapterNumber = source.ChapterNumber, ChineseTitle = source.ChineseTitle,
            ExistingVietnameseTitle = source.ExistingVietnameseTitle,
            VietnameseTitle = source.ProtectExistingVietnameseTitle ? source.ExistingVietnameseTitle : "",
            ExistingTitleProtected = source.ProtectExistingVietnameseTitle,
            Status = source.ProtectExistingVietnameseTitle ? ChapterTitleTranslationStatus.Protected : ChapterTitleTranslationStatus.Pending
        });
        foreach (ChapterTitleTranslationItem item in items) _rows.Add(new(item));
        int completed = items.Count(item => item.Status is ChapterTitleTranslationStatus.Completed or ChapterTitleTranslationStatus.Protected);
        int failed = items.Count(item => item.Status == ChapterTitleTranslationStatus.Failed);
        ProgressText.Text = $"{completed:N0}/{_sources.Length:N0} · {failed:N0} lỗi";
        UpdateCommands();
    }

    private void UpdateCommands()
    {
        int failed = _draft?.Items.Count(item => item.Status == ChapterTitleTranslationStatus.Failed) ?? 0;
        TranslateAllButton.IsEnabled = !_working;
        RetryFailedButton.IsEnabled = !_working && failed > 0;
        RetrySelectedButton.IsEnabled = !_working && TitleGrid.SelectedItems.Count > 0;
        CancelButton.IsEnabled = _working;
        ApplyButton.IsEnabled = !_working && _draft is not null && _draft.Items.All(item =>
            item.Status is ChapterTitleTranslationStatus.Completed or ChapterTitleTranslationStatus.Protected &&
            !string.IsNullOrWhiteSpace(item.VietnameseTitle));
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCommands();

    private void OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (_draft is null || e.EditAction != DataGridEditAction.Commit || e.Row.Item is not Row row ||
            e.EditingElement is not TextBox input || e.Column.Header?.ToString() != "Tên Việt đề xuất") return;
        ChapterTitleTranslationItem item = _draft.Items.Single(value => value.Id == row.Id);
        item.VietnameseTitle = input.Text.Trim(); item.TranslatedName = input.Text.Trim(); item.Error = null;
        item.Status = string.IsNullOrWhiteSpace(input.Text) ? ChapterTitleTranslationStatus.Pending : ChapterTitleTranslationStatus.Completed;
        _draft.UpdatedAt = DateTimeOffset.UtcNow;
        Dispatcher.BeginInvoke(SaveManualEditAsync);
    }

    private async void SaveManualEditAsync()
    {
        try { if (_draft is not null) await _store.SaveAsync(_draft); RefreshRows(); }
        catch (Exception ex) { StatusText.Text = "Không lưu được draft tiêu đề: " + ex.Message; }
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (_draft is null || !ApplyButton.IsEnabled) return;
        ResultDraft = _draft;
        DialogResult = true;
    }

    private ChapterTitleTranslationMethod SelectedMethod() => Enum.Parse<ChapterTitleTranslationMethod>(
        (MethodInput.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Ollama", true);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_working) return;
        e.Cancel = true;
        _cancellation?.Cancel();
        StatusText.Text = "Đang hủy request và lưu draft…";
    }

    private sealed record Row(Guid Id, int? Number, string ChineseTitle, string ExistingVietnameseTitle,
        string VietnameseTitle, string StatusLabel, string Error)
    {
        public Row(ChapterTitleTranslationItem item) : this(item.Id, item.ChapterNumber, item.ChineseTitle,
            item.ExistingVietnameseTitle, item.VietnameseTitle, item.Status switch
            {
                ChapterTitleTranslationStatus.Completed => "Đã dịch", ChapterTitleTranslationStatus.Failed => "Lỗi",
                ChapterTitleTranslationStatus.Protected => "Giữ tên hiện có", ChapterTitleTranslationStatus.Translating => "Đang dịch",
                _ => "Chưa dịch"
            }, item.Error ?? "") { }
    }
}
