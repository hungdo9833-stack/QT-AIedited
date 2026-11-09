using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickTranslator.AiEditor.Core.AutoTranslate;
using QuickTranslator.AiEditor.Wpf;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class AutoTranslateWorkerUiTests
{
    private static readonly IReadOnlyList<WorkerChoice> Choices =
    [
        new("local", "GPU máy này", "IndexTeam/Index-Translate-9B-GGUF:Q6_K", true),
        new("gemini", "Gemini (pool API key)", "3 API key", true),
        new("remote:a", "Remote: GPU Remote mới", "Q8_0 · 12 luồng · đã kết nối", true),
        new("remote:b", "Remote: GPU 5060Ti", "Q8_0 · 6 luồng · đã kết nối", true),
        new("codex", "Codex (ChatGPT)", "Chỉ dùng để khảo sát nhân vật", false, "QT Studio chưa có backend dịch bằng Codex.")
    ];

    [Fact]
    public void Workers_are_numbered_in_order_and_make_the_lane_id()
    {
        var row = new AutoBookRow(new Uri("https://example.com/a"));
        Assert.True(row.FollowsApp);
        Assert.Equal(TranslationLanes.FollowAppId, row.LaneId);

        row.SetWorkers([new("remote:a", "Remote: 3090"), new("gemini", "Gemini"), new("remote:a", "Remote: 3090")]);
        Assert.Equal(["Worker 1 · Remote: 3090", "Worker 2 · Gemini"], row.Workers.Select(worker => worker.Label));
        Assert.Equal("remote:a,gemini", row.LaneId);
        Assert.False(row.FollowsApp);

        row.SetWorkers(row.Workers.Options().Where(option => option.Id != "remote:a"));
        Assert.Equal(["Worker 1 · Gemini"], row.Workers.Select(worker => worker.Label));
        row.SetWorkers([]);
        Assert.Equal(TranslationLanes.FollowAppId, row.LaneId);
    }

    [Fact]
    public Task Each_book_shows_its_worker_chips_and_a_plus_worker_button() => StaHost.Run(async () =>
    {
        var window = new AutoTranslateWindow(new NoStages(), () => Choices)
        {
            Width = 1400, Height = 820, Left = -10000, Top = -10000, ShowActivated = false
        };
        window.Show();
        try
        {
            AutoBookRow a = Add(window, "https://example.com/a", "Năm Thứ Ba Của Đôi Oan Gia");
            a.SetWorkers([new("remote:a", "Remote: GPU Remote mới"), new("remote:b", "Remote: GPU 5060Ti")]);
            AutoBookRow b = Add(window, "https://example.com/b", "Trọng Sinh Xuyên Không");
            b.SetWorkers([new("gemini", "Gemini (pool API key)")]);
            Add(window, "https://example.com/c", "Kiếp Này Không Gả Hoàng Tôn");
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            window.UpdateLayout();

            Capture(window, "auto-translate-workers.png");
            var grid = (DataGrid)window.FindName("BookGrid");
            Assert.Contains(grid.Columns, column => Equals(column.Header, "Worker dịch"));
            List<Button> plus = Descendants<Button>(grid).Where(button => Equals(button.Content, "+ Worker")).ToList();
            Assert.Equal(3, plus.Count);
            List<string> chips = Descendants<TextBlock>(grid).Select(text => text.Text).Where(text => text.StartsWith("Worker ") && text.Contains(" · ")).ToList();
            Assert.Equal(["Worker 1 · Remote: GPU Remote mới", "Worker 2 · Remote: GPU 5060Ti", "Worker 1 · Gemini (pool API key)"], chips);
            Capture(window, "auto-translate-workers.png");

            var picker = new WorkerPickerWindow("Năm Thứ Ba Của Đôi Oan Gia", Choices, new HashSet<string> { "remote:a" })
            {
                Left = -10000, Top = -10000, ShowActivated = false
            };
            picker.Show();
            picker.UpdateLayout();
            List<Button> buttons = Descendants<Button>(picker).ToList();
            Assert.Equal("Đã thêm", buttons.Single(button => button.Name == "" && Equals(button.Content, "Đã thêm")).Content);
            Assert.False(buttons.Single(button => Equals(button.Content, "Chưa dùng được")).IsEnabled);
            Assert.Equal(3, buttons.Count(button => Equals(button.Content, "Thêm") && button.IsEnabled));
            Capture(picker, "worker-picker.png");
            picker.Close();
        }
        finally { window.Close(); }
    });

    private static AutoBookRow Add(AutoTranslateWindow window, string url, string title)
    {
        var rows = (System.Collections.ObjectModel.ObservableCollection<AutoBookRow>)((DataGrid)window.FindName("BookGrid")).ItemsSource;
        var row = new AutoBookRow(new Uri(url)) { Title = title, Stage = "Chờ worker rảnh" };
        rows.Add(row);
        return row;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }

    /// <summary>Optional visual QA artifact, as the other UI tests do.</summary>
    private static void Capture(Window window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("QTSTUDIO_UI_CAPTURE_DIR");
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }

    private sealed class NoStages : IAutoBookStages
    {
        public Task<AutoBookExisting?> FindExistingAsync(AutoBookRow row, AutoTranslateSettings settings, CancellationToken cancellationToken) =>
            Task.FromResult<AutoBookExisting?>(null);
        public Task<AutoBookDownload?> DownloadAsync(AutoBookRow row, AutoTranslateSettings settings, CancellationToken cancellationToken) =>
            Task.FromResult<AutoBookDownload?>(null);
        public Task<string?> PrepareAsync(AutoBookRow row, AutoBookDownload download, AutoTranslateSettings settings,
            CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<bool> TranslateAsync(AutoBookRow row, string manifestPath, CancellationToken cancellationToken) => Task.FromResult(true);
        public IReadOnlySet<string> TranslationResources(AutoBookRow row) => new HashSet<string>();
    }
}
