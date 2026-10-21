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
    Func<IReadOnlyList<Guid>, Task> TranslateTitles,
    Func<Task>? ImportTxt = null);

public partial class ChapterManagerView : UserControl
{
    private readonly ProjectSession _session;
    private readonly ProjectCatalog _catalog;
    private readonly ChapterManagerCommands _commands;
    private bool _working;
    private bool _refreshing;
    private bool _attached;
    private int _refreshCount;
    private IReadOnlyList<ChapterManagerWindow.ChapterManagerRow> _rows = [];

    internal ChapterManagerView(ProjectSession session, ProjectCatalog catalog, ChapterManagerCommands commands)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        InitializeComponent();
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))
            .AddValueChanged(StatusText, (_, _) => Accessibility.Announce(StatusText));
        ProjectTitleText.Text = session.Manifest.VietnameseTitle;
        ImportTxtButton.Visibility = commands.ImportTxt is null ? Visibility.Collapsed : Visibility.Visible;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event EventHandler? CloseRequested;

    internal void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    internal IReadOnlyList<ChapterManagerWindow.ChapterManagerRow> CurrentRows => _rows;
    internal int RefreshCount => _refreshCount;
    internal int CatalogSubscriptionCount => _attached ? 1 : 0;
    internal bool UsesRecyclingVirtualization => VirtualizingPanel.GetIsVirtualizing(ChapterGrid) &&
        VirtualizingPanel.GetVirtualizationMode(ChapterGrid) == VirtualizationMode.Recycling;

    internal void Attach()
    {
        if (_attached) return;
        _attached = true;
        _catalog.Changed += OnCatalogChanged;
        RefreshRows();
    }

    internal void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _catalog.Changed -= OnCatalogChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Attach();
    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void RefreshRows()
    {
        if (!_attached || _refreshing) return;
        _refreshing = true;
        try
        {
            _refreshCount++;
            Guid[] selection = ChapterGrid.SelectedItems.Cast<ChapterManagerWindow.ChapterManagerRow>().Select(row => row.ChapterId).ToArray();
            ProjectCatalogSnapshot snapshot = _catalog.GetSnapshot(_session);
            RefreshVolumeFilter(snapshot);
            var query = new ProjectCatalogQuery(
                SearchInput.Text,
                ParseOptional<ProjectChapterState>(StateFilter),
                Parse<ProjectCatalogApprovalFilter>(ApprovalFilter, ProjectCatalogApprovalFilter.All),
                Parse<ProjectCatalogDeletionFilter>(DeletionFilter, ProjectCatalogDeletionFilter.Active),
                SelectedTag(VolumeFilter) is { Length: > 0 } volume ? volume : null,
                Parse<ProjectCatalogSort>(SortInput, ProjectCatalogSort.ReadingOrder));
            _rows = _catalog.Query(_session, query).Select(chapter => new ChapterManagerWindow.ChapterManagerRow(chapter)).ToArray();
            ChapterGrid.ItemsSource = _rows;
            foreach (ChapterManagerWindow.ChapterManagerRow row in _rows.Where(row => selection.Contains(row.ChapterId)))
                ChapterGrid.SelectedItems.Add(row);
            CatalogCountText.Text = $"{snapshot.ActiveCount:N0} hoạt động · {snapshot.DeletedCount:N0} trong History";
            StatusText.Text = _rows.Count == 0
                ? "Không có chương phù hợp bộ lọc."
                : $"Đang hiển thị {_rows.Count:N0} chương.";
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
        if (change.ProjectId != _session.Manifest.ProjectId || !_attached) return;
        Dispatcher.BeginInvoke(RefreshRows);
    }

    private void OnFilterChanged(object sender, EventArgs e)
    {
        if (IsInitialized && !_working && !_refreshing) RefreshRows();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCommands();
    private void OnClearSelection(object sender, RoutedEventArgs e) => ChapterGrid.UnselectAll();

    private void OnSelectChapterRange(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RangeStartInput.Text.Trim(), out int from) ||
            !int.TryParse(RangeEndInput.Text.Trim(), out int to))
        {
            StatusText.Text = "Hãy nhập số chương bắt đầu và kết thúc hợp lệ (ví dụ: Từ chương 1 đến 100).";
            return;
        }
        SelectChapterRange(from, to);
    }

    internal int SelectChapterRange(int fromNumber, int toNumber)
    {
        int min = Math.Min(fromNumber, toNumber);
        int max = Math.Max(fromNumber, toNumber);
        ChapterGrid.SelectedItems.Clear();
        foreach (ChapterManagerWindow.ChapterManagerRow row in _rows.Where(r => r.Number >= min && r.Number <= max))
            ChapterGrid.SelectedItems.Add(row);
        UpdateCommands();
        StatusText.Text = $"Đã chọn {ChapterGrid.SelectedItems.Count:N0} chương trong dải từ số {min} đến {max}.";
        return ChapterGrid.SelectedItems.Count;
    }

    private async void OnGridDoubleClick(object sender, MouseButtonEventArgs e) => await OpenSelectedAsync();
    private async void OnOpen(object sender, RoutedEventArgs e) => await OpenSelectedAsync();

    private async Task OpenSelectedAsync()
    {
        ChapterManagerWindow.ChapterManagerRow? row = SelectedRows().SingleOrDefault();
        if (row is null || row.IsDeleted) return;
        await RunAsync(() => _commands.Open(row.ChapterId), "Đã mở chương trong Editor.");
    }

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        ChapterManagerWindow.ChapterManagerRow? row = SelectedRows().SingleOrDefault();
        if (row is null || row.IsDeleted) return;
        var title = new TextBox { Text = row.VietnameseTitle, MinWidth = 430 };
        var chinese = new TextBox { Text = row.ChineseTitle, MinWidth = 430 };
        var save = new Button { Content = "Lưu tên", IsDefault = true };
        save.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        var cancel = new Button { Content = "Hủy", IsCancel = true };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Tên tiếng Việt", Margin = new Thickness(4) }); panel.Children.Add(title);
        panel.Children.Add(new TextBlock { Text = "Tên tiếng Trung", Margin = new Thickness(4) }); panel.Children.Add(chinese);
        var root = new DockPanel();
        Border footer = ThemedDialog.Footer(save, cancel);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(panel);
        var dialog = new Window { Owner = Window.GetWindow(this), Title = "Đổi tên chương", Width = 560, MinWidth = 420, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root };
        dialog.SetResourceReference(BackgroundProperty, "PanelBrush");
        dialog.SetResourceReference(ForegroundProperty, "TextBrush");
        MainWindow.ClampWindowToWorkArea(dialog, 560, 320);
        save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(title.Text)) dialog.DialogResult = true; };
        if (dialog.ShowDialog() != true) return;
        await RunAsync(() => _commands.Rename(row.ChapterId, title.Text.Trim(), chinese.Text.Trim()), "Đã đổi tên và cập nhật mục lục.");
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        ChapterManagerWindow.ChapterManagerRow[] rows = SelectedRows().Where(row => !row.IsDeleted).ToArray();
        if (rows.Length == 0) return;
        if (ThemedDialog.Show(Window.GetWindow(this), $"Chuyển {rows.Length:N0} chương đã chọn vào History? File không bị xóa vĩnh viễn.",
                "Xóa mềm chương", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync(() => _commands.SoftDelete(rows.Select(row => row.ChapterId).ToArray()),
            "Đã chuyển các chương đã chọn vào History.");
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        ChapterManagerWindow.ChapterManagerRow[] rows = SelectedRows().Where(row => row.IsDeleted).ToArray();
        if (rows.Length == 0) return;
        await RunAsync(() => _commands.Restore(rows.Select(row => row.ChapterId).ToArray()),
            "Đã phục hồi các chương đã chọn.");
    }

    private async void OnQueue(object sender, RoutedEventArgs e)
    {
        ChapterManagerWindow.ChapterManagerRow[] rows = SelectedRows().Where(row => !row.IsDeleted).OrderBy(row => row.DisplayOrder).ToArray();
        if (rows.Length == 0) return;
        await RunAsync(() => _commands.Queue(rows.Select(row => row.ChapterId).ToArray()),
            $"Đã bắt đầu dịch {rows.Length:N0} chương. Theo dõi tiến độ trong cửa sổ Hàng đợi.");
    }

    private async void OnResegment(object sender, RoutedEventArgs e)
    {
        ChapterManagerWindow.ChapterManagerRow[] rows = SelectedRows().Where(row => !row.IsDeleted)
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

    private async void OnImportTxt(object sender, RoutedEventArgs e)
    {
        if (_commands.ImportTxt is null) return;
        await RunAsync(_commands.ImportTxt, "Đã nhập các chương từ tệp TXT.");
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
            ThemedDialog.Show(Window.GetWindow(this), ex.Message, "Chapter Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _working = false; UpdateCommands(); }
    }

    private ChapterManagerWindow.ChapterManagerRow[] SelectedRows() => ChapterGrid.SelectedItems.Cast<ChapterManagerWindow.ChapterManagerRow>().ToArray();

    private void UpdateCommands()
    {
        ChapterManagerWindow.ChapterManagerRow[] selected = SelectedRows();
        int active = selected.Count(row => !row.IsDeleted), deleted = selected.Length - active;
        string busy = "Đang hoàn tất thao tác hiện tại.";
        string oneChapter = selected.Length == 0 ? "Chọn một chương." :
            selected.Length > 1 ? "Chỉ chọn một chương." : "Chương này đang nằm trong History.";
        string activeChapter = selected.Length == 0 ? "Chọn ít nhất một chương." :
            deleted > 0 ? "Chỉ chọn các chương không nằm trong History." : busy;
        string historyChapter = selected.Length == 0 ? "Chọn ít nhất một chương." :
            active > 0 ? "Chương này không nằm trong History." : busy;

        SetButtonState(OpenButton, !_working && selected.Length == 1 && active == 1,
            _working ? busy : oneChapter, "Mở chương đã chọn trong Editor.");
        SetButtonState(RenameButton, OpenButton.IsEnabled, _working ? busy : oneChapter, "Đổi tên chương đã chọn.");
        SetButtonState(DeleteButton, !_working && active > 0 && deleted == 0,
            _working ? busy : activeChapter, "Chuyển các chương đã chọn vào History.");
        SetButtonState(RestoreButton, !_working && deleted > 0 && active == 0,
            _working ? busy : historyChapter, "Phục hồi các chương đã chọn.");
        SetButtonState(ResegmentButton, !_working && active > 0 && deleted == 0,
            _working ? busy : activeChapter, "Xem trước ranh giới mới; giữ nguyên các đoạn đã có dữ liệu dịch/duyệt.");
        SetButtonState(QueueButton, !_working && active > 0 && deleted == 0,
            _working ? busy : activeChapter, "Tạo hàng đợi an toàn và bắt đầu dịch ngay.");
        SetButtonState(TranslateSelectedTitlesButton, !_working && active > 0 && deleted == 0,
            _working ? busy : activeChapter, "Dịch tiêu đề các chương đã chọn.");
        SetButtonState(TranslateAllTitlesButton,
            !_working && _session.Manifest.Chapters.Any(entry => entry.DeletedAt is null),
            _working ? busy : "Không có chương hoạt động để dịch tiêu đề.", "Dịch tiêu đề tất cả chương hoạt động.");
        SetButtonState(ClearSelectionButton, !_working && selected.Length > 0,
            _working ? busy : "Chọn ít nhất một chương.", "Bỏ chọn tất cả chương.");
        SetButtonState(ImportTxtButton, !_working, busy, "Nhập nhiều chương từ tệp TXT");

        SetPrimary(OpenButton, selected.Length == 1);
        SetPrimary(QueueButton, selected.Length >= 2);
        SelectionText.Text = $"Đã chọn {selected.Length:N0}";
    }

    private static void SetButtonState(Button button, bool enabled, string disabledToolTip, string enabledToolTip)
    {
        button.IsEnabled = enabled;
        ToolTipService.SetShowOnDisabled(button, true);
        button.ToolTip = enabled ? enabledToolTip : disabledToolTip;
    }

    private static void SetPrimary(Button button, bool primary)
    {
        button.ClearValue(StyleProperty);
        if (primary) button.SetResourceReference(StyleProperty, "PrimaryButton");
    }

    private static T Parse<T>(ComboBox combo, T fallback) where T : struct, Enum =>
        Enum.TryParse(SelectedTag(combo), out T parsed) ? parsed : fallback;
    private static T? ParseOptional<T>(ComboBox combo) where T : struct, Enum =>
        Enum.TryParse(SelectedTag(combo), out T parsed) ? parsed : null;
    private static string SelectedTag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

}
