using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.AutoTranslate;

namespace QuickTranslator.AiEditor.Wpf;

public sealed record AutoTranslateSettings(CodexSurveyOptions Codex, TimeSpan DownloadDelay, bool PrepareOnly = false);

public partial class AutoTranslateWindow : Window
{
    private readonly IAutoBookStages _stages;
    private readonly ObservableCollection<AutoBookRow> _rows = [];
    private AutoBookPipeline? _pipeline;

    internal AutoTranslateWindow(IAutoBookStages stages)
    {
        InitializeComponent();
        _stages = stages;
        BookGrid.ItemsSource = _rows;
    }

    internal bool IsRunning => _pipeline is not null;
    internal IReadOnlyList<AutoBookRow> Rows => _rows;

    /// <summary>"Bắt đầu", or "Thêm vào hàng chờ" while running: new links join the run; a link already listed is retried.</summary>
    private void OnStart(object sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<Uri> links = ParseLinks(LinksInput.Text);
            foreach (Uri link in links)
            {
                AutoBookRow row = _rows.FirstOrDefault(existing => existing.Url == link) ?? AddRow(link);
                Enqueue(row);
            }
            LinksInput.Clear();
        }
        catch (InvalidOperationException ex) { StatusText.Text = ex.Message; }
    }

    private void OnRetryRow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AutoBookRow row && row.CanRetry) Enqueue(row);
    }

    private void OnRemoveRow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AutoBookRow row) return;
        AutoBookPipeline.Remove(row);
        _rows.Remove(row);
        UpdateStatus();
    }

    private AutoBookRow AddRow(Uri link)
    {
        var row = new AutoBookRow(link);
        // A book leaving the run flips CanRetry just before the run's count drops: refresh once both have happened.
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AutoBookRow.CanRetry)) Dispatcher.BeginInvoke(UpdateStatus);
        };
        _rows.Add(row);
        return row;
    }

    internal void Enqueue(AutoBookRow row)
    {
        if (_pipeline?.TryEnqueue(row) != true)
        {
            AutoTranslateSettings settings;
            try { settings = ReadSettings(); }
            catch (InvalidOperationException ex) { StatusText.Text = ex.Message; return; }
            var pipeline = new AutoBookPipeline(_stages, settings);
            pipeline.TryEnqueue(row);
            _ = RunAsync(pipeline);
        }
        UpdateStatus();
    }

    private async Task RunAsync(AutoBookPipeline pipeline)
    {
        _pipeline = pipeline;
        SetRunning(true);
        try { await pipeline.RunAsync(); }
        catch (Exception ex) { StatusText.Text = "Lỗi: " + ex.Message; }
        finally
        {
            if (ReferenceEquals(_pipeline, pipeline)) _pipeline = null;
            SetRunning(IsRunning);
            UpdateStatus();
        }
    }

    private void UpdateStatus()
    {
        int active = _pipeline?.Active ?? 0, waiting = _rows.Count(row => row.CanRetry);
        StatusText.Text = active > 0
            ? $"Đang xử lý {active} truyện" + (waiting > 0 ? $"; {waiting} truyện chờ Thử lại." : ". Có thể dán thêm link vào hàng chờ.")
            : _rows.Count == 0 ? "Dán link rồi bấm Bắt đầu."
            : waiting > 0 ? $"Đã xử lý xong hàng chờ; {waiting} truyện đang dừng, bấm Thử lại ở từng truyện khi muốn chạy tiếp."
            : "Đã xử lý xong hàng chờ.";
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _pipeline?.Stop();
        StopButton.IsEnabled = false;
        StatusText.Text = "Đang dừng sau bước hiện tại (hàng đợi dịch được tạm dừng an toàn)…";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!IsRunning) return;
        e.Cancel = true;
        StatusText.Text = "Hãy bấm Dừng và chờ dừng hẳn trước khi đóng.";
    }

    /// <summary>Links can be added any time; the settings belong to the run and are fixed while it lasts.</summary>
    private void SetRunning(bool running)
    {
        ModelInput.IsEnabled = EffortInput.IsEnabled = WorkspaceInput.IsEnabled = DelayInput.IsEnabled =
            PrepareOnlyCheck.IsEnabled = !running;
        StartButton.Content = running ? "Thêm vào hàng chờ" : "Bắt đầu";
        StopButton.IsEnabled = running;
    }

    internal static IReadOnlyList<Uri> ParseLinks(string text)
    {
        var links = new List<Uri>();
        foreach (string line in text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0))
        {
            if (!Uri.TryCreate(line, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
                throw new InvalidOperationException($"Link không hợp lệ: {line}");
            if (!links.Contains(uri)) links.Add(uri);
        }
        if (links.Count == 0) throw new InvalidOperationException("Hãy dán ít nhất một link truyện.");
        return links;
    }

    private AutoTranslateSettings ReadSettings()
    {
        if (!double.TryParse(DelayInput.Text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out double seconds) || !double.IsFinite(seconds) || seconds < 0.5 || seconds > 60)
            throw new InvalidOperationException("Thời gian nghỉ phải từ 0,5 đến 60 giây.");
        string model = ModelInput.Text.Trim();
        if (model.Length == 0 || model.Any(char.IsWhiteSpace)) throw new InvalidOperationException("Model Codex không hợp lệ.");
        string workspace = ((ComboBoxItem)WorkspaceInput.SelectedItem).Content.ToString()!;
        var codex = new CodexSurveyOptions
        {
            Model = model,
            Effort = ((ComboBoxItem)EffortInput.SelectedItem).Content.ToString()!,
            CodexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-" + workspace)
        };
        return new(codex, TimeSpan.FromSeconds(seconds), PrepareOnlyCheck.IsChecked == true);
    }
}
