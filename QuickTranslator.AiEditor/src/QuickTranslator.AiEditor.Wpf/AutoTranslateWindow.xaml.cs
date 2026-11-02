using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.AutoTranslate;

namespace QuickTranslator.AiEditor.Wpf;

/// <summary>One book in an automatic run: download → new project → Codex survey → translate.</summary>
public sealed class AutoBookRow(Uri url) : INotifyPropertyChanged
{
    private string _title = url.ToString(), _stage = "Chờ", _detail = "";
    public Uri Url { get; } = url;
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Stage { get => _stage; set => Set(ref _stage, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }
}

public sealed record AutoTranslateSettings(CodexSurveyOptions Codex, TimeSpan DownloadDelay, bool PrepareOnly = false);

public partial class AutoTranslateWindow : Window
{
    private readonly Func<IReadOnlyList<AutoBookRow>, AutoTranslateSettings, CancellationToken, Task> _run;
    private readonly ObservableCollection<AutoBookRow> _rows = [];
    private CancellationTokenSource? _cancellation;

    internal AutoTranslateWindow(Func<IReadOnlyList<AutoBookRow>, AutoTranslateSettings, CancellationToken, Task> run)
    {
        InitializeComponent();
        _run = run;
        BookGrid.ItemsSource = _rows;
    }

    internal bool IsRunning => _cancellation is not null;

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (IsRunning) return;
        try
        {
            IReadOnlyList<Uri> links = ParseLinks(LinksInput.Text);
            AutoTranslateSettings settings = ReadSettings();
            _rows.Clear();
            foreach (Uri link in links) _rows.Add(new(link));
            _cancellation = new CancellationTokenSource();
            SetRunning(true);
            StatusText.Text = $"Đang xử lý {links.Count} truyện…";
            await _run(_rows.ToArray(), settings, _cancellation.Token);
            StatusText.Text = _cancellation.IsCancellationRequested ? "Đã dừng." : "Đã xử lý xong danh sách.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Đã dừng."; }
        catch (Exception ex) { StatusText.Text = "Lỗi: " + ex.Message; }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            SetRunning(false);
        }
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
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

    private void SetRunning(bool running)
    {
        StartButton.IsEnabled = LinksInput.IsEnabled = ModelInput.IsEnabled = EffortInput.IsEnabled =
            WorkspaceInput.IsEnabled = DelayInput.IsEnabled = PrepareOnlyCheck.IsEnabled = !running;
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
