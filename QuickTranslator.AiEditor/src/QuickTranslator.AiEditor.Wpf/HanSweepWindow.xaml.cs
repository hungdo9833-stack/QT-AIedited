using System.Collections.ObjectModel;
using System.Windows;
using QuickTranslator.AiEditor.Core.TextCleaning;

namespace QuickTranslator.AiEditor.Wpf;

/// <summary>Runs the Chinese-residue sweep over the library's books and shows each book's result.</summary>
public partial class HanSweepWindow : Window
{
    /// <summary>Runs over the books: (undo?, progress, report each book, cancellation).</summary>
    private readonly Func<bool, IProgress<HanSweepProgress>, Action<HanSweepResult>, CancellationToken, Task> _run;
    private readonly ObservableCollection<Row> _rows = [];
    private CancellationTokenSource? _cancellation;
    private bool _undo;

    private readonly string _doneVerb = "sửa";
    private readonly Func<HanSweepResult, bool, string>? _detail;

    internal HanSweepWindow(string subtitle, Func<bool, IProgress<HanSweepProgress>, Action<HanSweepResult>, CancellationToken, Task> run)
    {
        InitializeComponent();
        _run = run;
        SubtitleText.Text = subtitle;
        ResultList.ItemsSource = _rows;
        ProgressText.Text = "Chưa chạy. Bấm Bắt đầu.";
    }

    /// <summary>The same window for another per-segment batch tool (Biên tập nâng cao): its own titles and result line.</summary>
    internal HanSweepWindow(string title, string heading, string subtitle, string doneVerb, string undoLabel,
        Func<HanSweepResult, bool, string> detail,
        Func<bool, IProgress<HanSweepProgress>, Action<HanSweepResult>, CancellationToken, Task> run) : this(subtitle, run)
    {
        Title = title;
        HeadingText.Text = heading;
        UndoButton.Content = undoLabel;
        _doneVerb = doneVerb;
        _detail = detail;
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (ThemedDialog.Show(this, "Trả lại nội dung cũ cho mọi đoạn đã sửa bằng công cụ này (chỉ khi chưa có sửa nào khác " +
                "sau đó)?", "Hoàn tác", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _undo = true;
        OnStart(sender, e);
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        bool undo = _undo;
        _undo = false;
        StartButton.IsEnabled = UndoButton.IsEnabled = false;
        CloseButton.Content = "Dừng";
        _rows.Clear();
        _cancellation = new CancellationTokenSource();
        var progress = new Progress<HanSweepProgress>(value =>
        {
            ProgressText.Text = $"Truyện {value.BookIndex}/{value.BookCount}: {value.Book} · chương {value.Chapter}/{value.ChapterCount} · " +
                                $"đã {(undo ? "hoàn tác" : _doneVerb)} {value.FixedSegments} đoạn";
            BookProgress.Maximum = Math.Max(1, value.BookCount * 1000);
            BookProgress.Value = (value.BookIndex - 1) * 1000 + 1000.0 * value.Chapter / Math.Max(1, value.ChapterCount);
        });
        try
        {
            await _run(undo, progress, result => Dispatcher.Invoke(() => _rows.Add(new Row(result, undo, _detail))), _cancellation.Token);
            int fixedSegments = _rows.Sum(row => row.Result.FixedSegments);
            ProgressText.Text = $"Xong {_rows.Count} truyện: đã {(undo ? "hoàn tác" : _doneVerb)} {fixedSegments} đoạn.";
            BookProgress.Value = BookProgress.Maximum;
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = $"Đã dừng sau {_rows.Count} truyện. Các chương đã sửa vẫn được lưu.";
        }
        catch (Exception ex)
        {
            ProgressText.Text = "Lỗi: " + ex.Message;
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            CloseButton.Content = "Đóng";
            StartButton.IsEnabled = UndoButton.IsEnabled = true;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_cancellation is not null) { _cancellation.Cancel(); return; }
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_cancellation is not null) { e.Cancel = true; _cancellation.Cancel(); }
        base.OnClosing(e);
    }

    /// <summary>One book's line in the result list.</summary>
    public sealed record Row(HanSweepResult Result, bool Undo = false, Func<HanSweepResult, bool, string>? Format = null)
    {
        public string Title => Result.Title;
        public string Detail => Result.Error is not null ? "Không mở được: " + Result.Error
            : Format is not null ? Format(Result, Undo)
            : $"{Result.Chapters} chương · {(Undo ? "hoàn tác" : "sửa")} {Result.FixedSegments} đoạn" +
              (Result.StillHanSegments > 0 ? $" · {Result.StillHanSegments} đoạn vẫn còn chữ lạ" : "") +
              (Result.FailedChapters > 0 ? $" · {Result.FailedChapters} chương lỗi" : "");
    }
}
