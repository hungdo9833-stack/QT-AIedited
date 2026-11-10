using System.Windows;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.TextCleaning;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Wpf;

/// <summary>
/// The two quality commands on the chapter list: "Dịch nâng cao" translates again with an editing pass after each
/// translation (2 requests per segment, the book's Dịch nâng cao option); "Biên tập nâng cao" only edits the saved
/// translations (1 request per segment).
/// </summary>
public partial class MainWindow
{
    private const string AdvancedTranslateTitle = "Dịch nâng cao";
    private const string AdvancedEditTitle = "Biên tập nâng cao";

    private bool TranslationBusy(string title)
    {
        if (_projectTranslationCoordinator?.IsRunning != true && _autoTranslateWindow?.IsRunning != true) return false;
        ThemedDialog.Show(this, "Đang có truyện dịch. Hãy đợi dịch xong (hoặc dừng) rồi chạy lại, để hai bên không cùng ghi một chương.",
            title, MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    private async void OnAdvancedTranslateProjectChapters(object sender, RoutedEventArgs e)
    {
        if (_projectSession is null || TranslationBusy(AdvancedTranslateTitle)) return;
        Guid[] selected = SelectedProjectChapters().Select(x => x.Id).Distinct().ToArray();
        if (selected.Length == 0) return;
        try
        {
            if (AdvancedEditingCheck.IsChecked != true)
            {
                if (ThemedDialog.Show(this,
                        $"Dịch nâng cao: mỗi đoạn được dịch rồi biên tập lại thành văn tiếng Việt tự nhiên, tốn 2 lượt gọi AI mỗi đoạn.{Environment.NewLine}{Environment.NewLine}" +
                        "Chế độ này sẽ được bật cho cả truyện (mọi lần dịch sau cũng chạy 2 lượt) cho đến khi bạn bỏ tích " +
                        "“Dịch nâng cao” trong Nhân vật → Tùy chọn hồ sơ & xưng hô. Tiếp tục?",
                        AdvancedTranslateTitle, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                AdvancedEditingCheck.IsChecked = true;
                await SaveProjectResourcesAsync();
            }
            await OpenProjectTranslationQueueAsync(selected);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void OnAdvancedEditProjectChapters(object sender, RoutedEventArgs e)
    {
        if (_projectSession is null || TranslationBusy(AdvancedEditTitle)) return;
        Guid[] selected = SelectedProjectChapters().Select(x => x.Id).Distinct().ToArray();
        if (selected.Length == 0) return;
        ProjectSession session = _projectSession;
        var owned = new List<IDisposable>();
        try
        {
            if (_service is not null) await SaveCurrentAsync();
            var counter = new AdvancedEditingSweep(_projectRepository, []);
            (int segments, int userSegments) = await counter.CountAsync(session, selected);
            if (segments == 0)
            {
                ThemedDialog.Show(this, "Các chương đã chọn chưa có bản dịch nào để biên tập. Hãy dịch trước, hoặc dùng Dịch nâng cao.",
                    AdvancedEditTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            bool includeUser = false;
            if (userSegments > 0)
            {
                MessageBoxResult answer = ThemedDialog.Show(this,
                    $"{selected.Length} chương có {segments} đoạn đã dịch, trong đó {userSegments} đoạn bạn đã sửa tay hoặc đã duyệt.{Environment.NewLine}{Environment.NewLine}" +
                    $"• Có: biên tập tất cả {segments} đoạn, kể cả đoạn đã sửa/duyệt.{Environment.NewLine}" +
                    $"• Không: chỉ biên tập {segments - userSegments} đoạn AI dịch; đoạn đã sửa/duyệt giữ nguyên.{Environment.NewLine}" +
                    "• Hủy: không làm gì.",
                    AdvancedEditTitle, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel || answer == MessageBoxResult.None) return;
                includeUser = answer == MessageBoxResult.Yes;
            }
            var definitions = new List<ProjectTranslationWorkerDefinition>();
            if (_gpuPreferences.Source == TranslationSource.Gpu && _translationClient is GpuBackendManager &&
                _gpuPreferences.Mode is GpuTranslationMode.LocalOnly or GpuTranslationMode.ParallelLocalRemote)
                await _localLlama.StartAsync(_gpuBackends.GetProfile(GpuBackendProfile.DefaultLocalId));
            AddCurrentSourceWorkers(definitions, owned);
            AdvancedEditingLane[] lanes = definitions.Select(worker =>
                new AdvancedEditingLane(worker.Client, worker.ModelOptions, worker.Slots)).ToArray();
            var sweep = new AdvancedEditingSweep(_projectRepository, lanes);
            int requests = includeUser ? segments : segments - userSegments;
            string subtitle = $"{selected.Length} chương · khoảng {requests} lượt gọi AI ({DescribeCurrentGpuSetup()}). " +
                "Mỗi đoạn đã dịch được gửi kèm nguyên văn để biên tập lại thành văn tiếng Việt tự nhiên; không dịch lại. " +
                "Bản biên tập lệch số đoạn, lệch độ dài hoặc còn chữ Hán sẽ bị bỏ, đoạn giữ nguyên. Bản cũ vẫn nằm trong lịch sử của đoạn.";
            Guid? currentId = _service?.GetSnapshot().Id;
            bool touchedCurrent = false;
            var window = new HanSweepWindow(AdvancedEditTitle, "Biên tập nâng cao các chương đã dịch", subtitle, "biên tập",
                "Hoàn tác các lần biên tập trước",
                (result, undo) => $"{result.Chapters} chương · {(undo ? "hoàn tác" : "biên tập")} {result.FixedSegments} đoạn" +
                    (result.StillHanSegments > 0 ? $" · giữ nguyên {result.StillHanSegments} đoạn (bản biên tập không đạt)" : "") +
                    (result.FailedChapters > 0 ? $" · {result.FailedChapters} chương lỗi" : ""),
                async (undo, progress, report, token) =>
                {
                    HanSweepResult result = await Task.Run(() => undo
                        ? sweep.UndoAsync(session, selected, progress, token)
                        : sweep.EditAsync(session, selected, includeUser, progress, token), token);
                    if (result.FixedSegments > 0 && currentId is Guid id && selected.Contains(id)) touchedCurrent = true;
                    report(result);
                }) { Owner = this };
            window.ShowDialog();
            if (touchedCurrent && currentId is Guid chapterId && ReferenceEquals(_projectSession, session))
                await ReloadCurrentChapterAfterProjectBatchAsync(chapterId);
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            foreach (IDisposable client in owned) client.Dispose();
        }
    }
}
