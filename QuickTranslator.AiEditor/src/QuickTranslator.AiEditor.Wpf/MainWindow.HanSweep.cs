using System.IO;
using System.Net.Http;
using System.Windows;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.TextCleaning;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    /// <summary>Dự án → "Sửa chữ Hán sót cả thư viện…": the repair new translations get, run over every saved book.</summary>
    private async void OnSweepLibraryHan(object sender, RoutedEventArgs e)
    {
        if (_projectTranslationCoordinator?.IsRunning == true || _autoTranslateWindow?.IsRunning == true)
        {
            ThemedDialog.Show(this, "Đang có truyện dịch. Hãy đợi dịch xong (hoặc dừng) rồi chạy sửa chữ Hán, " +
                "để hai bên không cùng ghi một chương.", "Sửa chữ Hán sót", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ProjectLibrary library = await _projectLibraryStore.LoadAsync();
        RecentProject[] books = library.RecentProjects.Where(book => !book.Missing && File.Exists(book.ManifestPath))
            .DistinctBy(book => Path.GetFullPath(book.ManifestPath), StringComparer.OrdinalIgnoreCase).ToArray();
        if (books.Length == 0)
        {
            ThemedDialog.Show(this, "Thư viện chưa có truyện nào.", "Sửa chữ Hán sót", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        bool gpu = _translationClient is GpuBackendManager && _localLlama.Endpoint is not null;
        string subtitle = $"{books.Length} truyện trong thư viện. Mỗi đoạn còn chữ Hán: tên viết bằng Hán-Việt, từ Quảng Đông " +
            "thay bằng nghĩa, " + (gpu ? "đoạn dịch lại bằng llama-server trên máy này, " : "(llama-server chưa chạy nên không dịch lại đoạn) ") +
            "chữ còn lại viết Hán-Việt. Bản cũ vẫn nằm trong lịch sử của đoạn.";
        var window = new HanSweepWindow(subtitle, (undo, progress, report, token) => SweepBooksAsync(books, gpu, undo, progress, report, token))
            { Owner = this };
        window.ShowDialog();
    }

    private async Task SweepBooksAsync(RecentProject[] books, bool gpu, bool undo, IProgress<HanSweepProgress> progress,
        Action<HanSweepResult> report, CancellationToken cancellationToken)
    {
        Guid? currentChapterId = _service?.GetSnapshot().Id;
        if (_service is not null) await SaveCurrentAsync();
        using var http = new HttpClient();
        GpuBackendProfile profile = _gpuBackends.GetProfile(GpuBackendProfile.DefaultLocalId);
        ModelOptions options = (profile.Runtime ?? AiRuntimeProfile.CreateDefault(profile.Model)).ModelOptions;
        using LlamaServerClient? llama = gpu && _localLlama.Endpoint is { } endpoint
            ? new LlamaServerClient(http, endpoint, profile.ParallelRequests) : null;
        HanRepairTranslator? local = llama is null ? null : new HanRepairTranslator(llama, options);
        var repairer = new HanResidueRepairClient((ILlmClient?)llama ?? new NoTranslator(), () => local, DictionaryHanViet,
            askForNoHan: false, vietPhrase: DictionaryVietPhrase);
        var sweep = new HanResidueSweep(_projectRepository, repairer, options);
        bool touchedCurrent = false;
        for (int i = 0; i < books.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecentProject book = books[i];
            try
            {
                bool current = _projectSession is not null && string.Equals(Path.GetFullPath(_projectSession.ManifestPath),
                    Path.GetFullPath(book.ManifestPath), StringComparison.OrdinalIgnoreCase);
                ProjectSession session = current ? _projectSession! : (await _projectRepository.OpenAsync(book.ManifestPath, cancellationToken)).Session;
                int index = i + 1;
                HanSweepResult result = await Task.Run(() => undo
                    ? sweep.UndoAsync(session, progress, index, books.Length, cancellationToken)
                    : sweep.SweepAsync(session, progress, index, books.Length, cancellationToken), cancellationToken);
                if (current && result.FixedSegments > 0) touchedCurrent = true;
                report(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report(new HanSweepResult(book.Title, 0, 0, 0, 0, ex.Message));
            }
        }
        if (touchedCurrent && currentChapterId is Guid chapterId) await ReloadCurrentChapterAfterProjectBatchAsync(chapterId);
    }

    /// <summary>Without llama-server no paragraph is translated again: names, Cantonese and Hán-Việt still apply.</summary>
    private sealed class NoTranslator : ILlmClient
    {
        public Task<LlmCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmCompletion("", false, "skipped", new GenerationMetrics()));
    }
}
