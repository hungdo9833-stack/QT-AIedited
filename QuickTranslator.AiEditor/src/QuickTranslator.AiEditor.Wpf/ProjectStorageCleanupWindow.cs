using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

internal sealed class ProjectStorageCleanupWindow : Window
{
    private readonly ProjectRepository _repository;
    private readonly ProjectSession _session;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 16 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _details = new();
    private readonly Button _cleanup = new() { Content = "Dọn dữ liệu thừa", IsEnabled = false };
    private readonly Button _refresh = new() { Content = "Xem trước lại" };
    private bool _working;

    internal ProjectStorageCleanupWindow(ProjectRepository repository, ProjectSession session)
    {
        _repository = repository;
        _session = session;
        Title = "QT Studio · Dọn dữ liệu thừa";
        Width = 760;
        Height = 560;
        MinWidth = 620;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("SurfaceBrush");

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "Dọn dữ liệu thừa", FontSize = 25, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock
        {
            Text = "Chỉ xóa bản before/after của transaction đã hoàn tất. Transaction cần phục hồi và dữ liệu retained luôn được giữ.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.FindResource("MutedBrush"),
            Margin = new Thickness(0, 4, 0, 12)
        });
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var summaryCard = new Border
        {
            Style = (Style)Application.Current.FindResource("Card"),
            Child = _summary,
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(summaryCard, 1);
        root.Children.Add(summaryCard);

        _details.BorderThickness = new Thickness(1);
        _details.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        Grid.SetRow(_details, 2);
        root.Children.Add(_details);

        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _refresh.Click += async (_, _) => await LoadPreviewAsync();
        _cleanup.SetResourceReference(StyleProperty, "PrimaryButton");
        _cleanup.Click += async (_, _) => await CleanupAsync();
        var close = new Button { Content = "Đóng" };
        close.Click += (_, _) => Close();
        buttons.Children.Add(_refresh);
        buttons.Children.Add(_cleanup);
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);
        Grid.SetRow(bottom, 3);
        root.Children.Add(bottom);
        Content = root;

        Loaded += async (_, _) => await LoadPreviewAsync();
        Closing += (_, e) => { if (_working) e.Cancel = true; else _cancellation.Cancel(); };
    }

    internal string? LastResult { get; private set; }

    private async Task LoadPreviewAsync()
    {
        if (_working) return;
        SetWorking(true, "Đang kiểm tra transaction…");
        try
        {
            ProjectTransactionCleanupPreview preview = await _repository.PreviewTransactionCleanupAsync(_session, _cancellation.Token);
            Render(preview);
            LastResult = preview.DeletableCount == 0
                ? "Không có transaction đã hoàn tất cần dọn."
                : $"Có {preview.DeletableCount:N0} transaction, {FormatBytes(preview.DeletableBytes)} có thể dọn.";
            _status.Text = LastResult;
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            _status.Text = "Không kiểm tra được: " + error.Message;
            _status.Foreground = (Brush)Application.Current.FindResource("DangerBrush");
        }
        finally { SetWorking(false); }
    }

    private async Task CleanupAsync()
    {
        if (_working || _cleanup.Tag is not ProjectTransactionCleanupPreview preview || preview.DeletableCount == 0) return;
        if (MessageBox.Show(this,
                $"Xóa {preview.DeletableCount:N0} transaction đã hoàn tất và giải phóng khoảng {FormatBytes(preview.DeletableBytes)}?\n\n" +
                "Transaction chưa hoàn tất, lỗi phục hồi hoặc có dữ liệu retained sẽ được giữ nguyên.",
                "Xác nhận dọn dữ liệu thừa", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SetWorking(true, "Đang dọn dữ liệu thừa…");
        try
        {
            ProjectTransactionCleanupResult result = await _repository.CleanupTransactionsAsync(_session, _cancellation.Token);
            LastResult = $"Đã dọn {result.DeletedCount:N0} transaction, giải phóng {FormatBytes(result.FreedBytes)}.";
            if (result.Failures.Count > 0)
                LastResult += $" {result.Failures.Count:N0} mục đang bị khóa hoặc đã đổi được giữ để thử lại.";
            _status.Text = LastResult;
            _status.Foreground = (Brush)Application.Current.FindResource(
                result.Failures.Count == 0 ? "SuccessBrush" : "WarningBrush");
            Render(result.Remaining);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            _status.Text = "Dọn dẹp chưa hoàn tất: " + error.Message + " Có thể bấm thử lại; dữ liệu Project được giữ nguyên.";
            _status.Foreground = (Brush)Application.Current.FindResource("DangerBrush");
        }
        finally { SetWorking(false); }
    }

    private void Render(ProjectTransactionCleanupPreview preview)
    {
        _summary.Text = $"Tổng: {preview.TotalCount:N0} transaction · {FormatBytes(preview.TotalBytes)}\n" +
                        $"Có thể dọn: {preview.DeletableCount:N0} · {FormatBytes(preview.DeletableBytes)}   |   " +
                        $"Giữ lại: {preview.KeptCount:N0}";
        string[] kept = preview.Items.Where(item => !item.CanDelete)
            .GroupBy(item => item.Reason)
            .Select(group => $"Giữ {group.Count():N0} mục · {group.Key}")
            .Order(StringComparer.CurrentCulture)
            .ToArray();
        _details.ItemsSource = kept.Length == 0
            ? new[] { "Không có transaction cần phục hồi hoặc dữ liệu retained." }
            : kept;
        _cleanup.Tag = preview;
        _cleanup.IsEnabled = !_working && preview.DeletableCount > 0;
    }

    private void SetWorking(bool working, string? message = null)
    {
        _working = working;
        _refresh.IsEnabled = !working;
        _cleanup.IsEnabled = !working && _cleanup.Tag is ProjectTransactionCleanupPreview preview && preview.DeletableCount > 0;
        if (message is not null)
        {
            _status.Text = message;
            _status.Foreground = (Brush)Application.Current.FindResource("MutedBrush");
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}
