using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

internal sealed record ChapterManagerCommands(
    Func<Guid, Task> Open,
    Func<Guid, string, string, Task> Rename,
    Func<IReadOnlyList<Guid>, Task> SoftDelete,
    Func<IReadOnlyList<Guid>, Task> Restore,
    Func<IReadOnlyList<Guid>, Task> Resegment,
    Func<IReadOnlyList<Guid>, Task> Queue,
    Func<IReadOnlyList<Guid>, Task> TranslateTitles);

public partial class ChapterManagerWindow : Window
{
    private readonly ProjectSession _session;
    private readonly ProjectCatalog _catalog;
    private readonly ChapterManagerCommands _commands;
    private bool _working;
    private bool _refreshing;
    private bool _closed;
    private IReadOnlyList<ChapterManagerRow> _rows = [];

    internal ChapterManagerWindow(ProjectSession session, ProjectCatalog catalog, ChapterManagerCommands commands)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        InitializeComponent();
        ProjectTitleText.Text = session.Manifest.VietnameseTitle;
        _catalog.Changed += OnCatalogChanged;
        Closed += OnClosed;
        RefreshRows();
    }

    internal IReadOnlyList<ChapterManagerRow> CurrentRows => _rows;
    internal bool UsesRecyclingVirtualization => VirtualizingPanel.GetIsVirtualizing(ChapterGrid) &&
        VirtualizingPanel.GetVirtualizationMode(ChapterGrid) == VirtualizationMode.Recycling;

    private void RefreshRows()
    {
        if (_closed || _refreshing) return;
        _refreshing = true;
        try
        {
            Guid[] selection = ChapterGrid.SelectedItems.Cast<ChapterManagerRow>().Select(row => row.ChapterId).ToArray();
            ProjectCatalogSnapshot snapshot = _catalog.GetSnapshot(_session);
            RefreshVolumeFilter(snapshot);
            var query = new ProjectCatalogQuery(
                SearchInput.Text,
                ParseOptional<ProjectChapterState>(StateFilter),
                Parse<ProjectCatalogApprovalFilter>(ApprovalFilter, ProjectCatalogApprovalFilter.All),
                Parse<ProjectCatalogDeletionFilter>(DeletionFilter, ProjectCatalogDeletionFilter.Active),
                SelectedTag(VolumeFilter) is { Length: > 0 } volume ? volume : null,
                Parse<ProjectCatalogSort>(SortInput, ProjectCatalogSort.ReadingOrder));
            _rows = _catalog.Query(_session, query).Select(chapter => new ChapterManagerRow(chapter)).ToArray();
            ChapterGrid.ItemsSource = _rows;
            foreach (ChapterManagerRow row in _rows.Where(row => selection.Contains(row.ChapterId)))
                ChapterGrid.SelectedItems.Add(row);
            CatalogCountText.Text = $"{snapshot.ActiveCount:N0} hoạt động · {snapshot.DeletedCount:N0} trong History";
            StatusText.Text = _rows.Count == 0
                ? "Không có chương phù hợp bộ lọc."
                : $"Đang hiển thị {_rows.Count:N0} chương · revision {snapshot.Revision[..12]}…";
            UpdateCommands();
        }
        finally { _refreshing = false; }
    }

    private void RefreshVolumeFilter(ProjectCatalogSnapshot snapshot)
    {
        string selected = SelectedTag(VolumeFilter);
        string[] volumes = snapshot.Chapters.Select(chapter => chapter.VolumeTitle).Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!).Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (VolumeFilter.Items.Count == volumes.Length + 1) return;
        VolumeFilter.Items.Clear();
        VolumeFilter.Items.Add(new ComboBoxItem { Content = "Tất cả quyển", Tag = "" });
        foreach (string volume in volumes) VolumeFilter.Items.Add(new ComboBoxItem { Content = volume, Tag = volume });
        VolumeFilter.SelectedItem = VolumeFilter.Items.Cast<ComboBoxItem>().FirstOrDefault(item =>
            string.Equals(item.Tag?.ToString(), selected, StringComparison.CurrentCultureIgnoreCase)) ?? VolumeFilter.Items[0];
    }

    private void OnCatalogChanged(object? sender, ProjectCatalogChangedEventArgs change)
    {
        if (change.ProjectId != _session.Manifest.ProjectId || _closed) return;
        Dispatcher.BeginInvoke(RefreshRows);
    }

    private void OnFilterChanged(object sender, EventArgs e)
    {
        if (IsInitialized && !_working && !_refreshing) RefreshRows();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCommands();
    private void OnClearSelection(object sender, RoutedEventArgs e) => ChapterGrid.UnselectAll();
    private async void OnGridDoubleClick(object sender, MouseButtonEventArgs e) => await OpenSelectedAsync();
    private async void OnOpen(object sender, RoutedEventArgs e) => await OpenSelectedAsync();

    private async Task OpenSelectedAsync()
    {
        ChapterManagerRow? row = SelectedRows().SingleOrDefault();
        if (row is null || row.IsDeleted) return;
        await RunAsync(() => _commands.Open(row.ChapterId), "Đã mở chương trong Editor.");
    }

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        ChapterManagerRow? row = SelectedRows().SingleOrDefault();
        if (row is null || row.IsDeleted) return;
        var title = new TextBox { Text = row.VietnameseTitle, MinWidth = 430 };
        var chinese = new TextBox { Text = row.ChineseTitle, MinWidth = 430 };
        var save = new Button { Content = "Lưu tên", IsDefault = true };
        var cancel = new Button { Content = "Hủy", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Tên tiếng Việt", Margin = new Thickness(4) }); panel.Children.Add(title);
        panel.Children.Add(new TextBlock { Text = "Tên tiếng Trung", Margin = new Thickness(4) }); panel.Children.Add(chinese); panel.Children.Add(buttons);
        var dialog = new Window { Owner = this, Title = "Đổi tên chương", Width = 560, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Foreground, Content = panel };
        save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(title.Text)) dialog.DialogResult = true; };
        if (dialog.ShowDialog() != true) return;
        await RunAsync(() => _commands.Rename(row.ChapterId, title.Text.Trim(), chinese.Text.Trim()), "Đã đổi tên và cập nhật mục lục.");
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        ChapterManagerRow[] rows = SelectedRows().Where(row => !row.IsDeleted).ToArray();
        if (rows.Length == 0) return;
        if (MessageBox.Show(this, $"Chuyển {rows.Length:N0} chương đã chọn vào History? File không bị xóa vĩnh viễn.",
                "Xóa mềm chương", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync(() => _commands.SoftDelete(rows.Select(row => row.ChapterId).ToArray()),
            "Đã chuyển các chương đã chọn vào History.");
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        ChapterManagerRow[] rows = SelectedRows().Where(row => row.IsDeleted).ToArray();
        if (rows.Length == 0) return;
        await RunAsync(() => _commands.Restore(rows.Select(row => row.ChapterId).ToArray()),
            "Đã phục hồi các chương đã chọn.");
    }

    private async void OnQueue(object sender, RoutedEventArgs e)
    {
        ChapterManagerRow[] rows = SelectedRows().Where(row => !row.IsDeleted).OrderBy(row => row.DisplayOrder).ToArray();
        if (rows.Length == 0) return;
        await RunAsync(() => _commands.Queue(rows.Select(row => row.ChapterId).ToArray()),
            "Đã mở Queue Plan. Hàng đợi chỉ chạy sau khi bạn xác nhận Start.");
    }

    private async void OnResegment(object sender, RoutedEventArgs e)
    {
        ChapterManagerRow[] rows = SelectedRows().Where(row => !row.IsDeleted)
            .OrderBy(row => row.DisplayOrder).ToArray();
        if (rows.Length == 0) return;
        await RunAsync(() => _commands.Resegment(rows.Select(row => row.ChapterId).ToArray()),
            "Đã chia lại đoạn cho các chương đã chọn.");
    }

    private async void OnTranslateSelectedTitles(object sender, RoutedEventArgs e)
    {
        Guid[] ids = SelectedRows().Where(row => !row.IsDeleted).OrderBy(row => row.DisplayOrder)
            .Select(row => row.ChapterId).ToArray();
        if (ids.Length == 0) return;
        await RunAsync(() => _commands.TranslateTitles(ids), "Đã cập nhật tiêu đề các chương được chọn.");
    }

    private async void OnTranslateAllTitles(object sender, RoutedEventArgs e)
    {
        Guid[] ids = _catalog.GetSnapshot(_session).Chapters.Where(chapter => !chapter.IsDeleted)
            .OrderBy(chapter => chapter.DisplayOrder).Select(chapter => chapter.ChapterId).ToArray();
        if (ids.Length == 0) return;
        await RunAsync(() => _commands.TranslateTitles(ids), "Đã cập nhật tiêu đề toàn bộ chương hoạt động.");
    }

    private async Task RunAsync(Func<Task> operation, string success)
    {
        if (_working) return;
        _working = true; UpdateCommands(); StatusText.Text = "Đang ghi an toàn…";
        try { await operation(); StatusText.Text = success; RefreshRows(); }
        catch (OperationCanceledException ex)
        {
            StatusText.Text = ex.Message;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Thao tác chưa hoàn tất: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Chapter Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _working = false; UpdateCommands(); }
    }

    private ChapterManagerRow[] SelectedRows() => ChapterGrid.SelectedItems.Cast<ChapterManagerRow>().ToArray();

    private void UpdateCommands()
    {
        ChapterManagerRow[] selected = SelectedRows();
        int active = selected.Count(row => !row.IsDeleted), deleted = selected.Length - active;
        OpenButton.IsEnabled = !_working && selected.Length == 1 && active == 1;
        RenameButton.IsEnabled = OpenButton.IsEnabled;
        DeleteButton.IsEnabled = !_working && active > 0 && deleted == 0;
        RestoreButton.IsEnabled = !_working && deleted > 0 && active == 0;
        ResegmentButton.IsEnabled = !_working && active > 0 && deleted == 0;
        QueueButton.IsEnabled = !_working && active > 0 && deleted == 0;
        TranslateSelectedTitlesButton.IsEnabled = !_working && active > 0 && deleted == 0;
        TranslateAllTitlesButton.IsEnabled = !_working && _session.Manifest.Chapters.Any(entry => entry.DeletedAt is null);
        ClearSelectionButton.IsEnabled = !_working && selected.Length > 0;
        SelectionText.Text = $"Đã chọn {selected.Length:N0} · {active:N0} hoạt động · {deleted:N0} History";
    }

    private static T Parse<T>(ComboBox combo, T fallback) where T : struct, Enum =>
        Enum.TryParse(SelectedTag(combo), out T parsed) ? parsed : fallback;
    private static T? ParseOptional<T>(ComboBox combo) where T : struct, Enum =>
        Enum.TryParse(SelectedTag(combo), out T parsed) ? parsed : null;
    private static string SelectedTag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _catalog.Changed -= OnCatalogChanged;
    }

    internal sealed record ChapterManagerRow(
        Guid ChapterId, int Number, long DisplayOrder, string VietnameseTitle, string ChineseTitle,
        string VolumeLabel, ProjectChapterState TranslationState, string TranslationLabel,
        ProjectCatalogApprovalState ApprovalState, string ApprovalLabel, string ProgressLabel,
        DateTimeOffset UpdatedAt, string UpdatedLabel, bool IsDeleted, string DeletedLabel)
    {
        public ChapterManagerRow(ProjectCatalogChapter chapter) : this(
            chapter.ChapterId, chapter.Number, chapter.DisplayOrder, chapter.VietnameseTitle, chapter.ChineseTitle,
            chapter.VolumeTitle ?? "—", chapter.TranslationState, StateLabel(chapter.TranslationState), chapter.ApprovalState,
            chapter.ApprovalState == ProjectCatalogApprovalState.Approved ? "Đã duyệt" : "Chưa duyệt xong",
            chapter.SegmentCount == 0 ? "0/0" : $"{chapter.CompletedSegmentCount:N0}/{chapter.SegmentCount:N0}",
            chapter.UpdatedAt, chapter.UpdatedAt.LocalDateTime.ToString("g"), chapter.IsDeleted,
            chapter.IsDeleted ? "History" : "Hoạt động") { }

        private static string StateLabel(ProjectChapterState state) => state switch
        {
            ProjectChapterState.Untranslated => "Chưa dịch", ProjectChapterState.Translating => "Đang dịch",
            ProjectChapterState.AwaitingReview => "Chờ duyệt", ProjectChapterState.NeedsInspection => "Cần kiểm tra",
            ProjectChapterState.Approved => "Đã duyệt", _ => "Lỗi"
        };
    }
}
