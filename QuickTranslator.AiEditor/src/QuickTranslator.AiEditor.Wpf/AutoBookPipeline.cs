using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using QuickTranslator.AiEditor.Core.AutoTranslate;

namespace QuickTranslator.AiEditor.Wpf;

public enum AutoBookStep { Download, Survey, Translate }

/// <summary>One book in an automatic run: download → new project → Codex survey → translate.</summary>
public sealed class AutoBookRow(Uri url) : INotifyPropertyChanged
{
    private string _title = url.ToString(), _stage = "Chờ", _detail = "";
    private bool _canRetry;
    public Uri Url { get; } = url;
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Stage { get => _stage; set => Set(ref _stage, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    /// <summary>Stopped, paused or failed: "Thử lại" resumes it at <see cref="Step"/>.</summary>
    public bool CanRetry { get => _canRetry; set => Set(ref _canRetry, value); }

    /// <summary>The stage the book is in, or resumes at; earlier stages' results are kept so they are not redone.</summary>
    internal AutoBookStep Step { get; set; }
    internal AutoBookDownload? Download { get; set; }
    internal string? ManifestPath { get; set; }
    /// <summary>In a stage or waiting for one.</summary>
    internal bool Busy { get; set; }
    /// <summary>Translated, prepared only, or stopped for good (content): nothing left to retry.</summary>
    internal bool Finished { get; set; }
    internal bool Removed { get; set; }
    /// <summary>The user saw that the link's project is incomplete and retried: make a new, complete project.</summary>
    internal bool NewProject { get; set; }
    /// <summary>The library held no project for this link when it was looked up.</summary>
    internal bool CheckedExisting { get; set; }
    /// <summary>Set by a stage that paused on a passing limit (the site throttled): the pipeline downloads again after it.</summary>
    internal TimeSpan? RetryAfter { get; set; }
    /// <summary>Chapters downloaded at the last pause, and how many automatic retries in a row added none.</summary>
    internal int LastDownloaded { get; set; } = -1;
    internal int StalledRetries { get; set; }
    internal CancellationTokenSource? Cancellation { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }
}

/// <summary>The work of each stage. A stage that returns null/false has already said why on the row.</summary>
internal interface IAutoBookStages
{
    /// <summary>A project already made from this link, if any.</summary>
    Task<AutoBookExisting?> FindExistingAsync(AutoBookRow row, AutoTranslateSettings settings, CancellationToken cancellationToken);
    Task<AutoBookDownload?> DownloadAsync(AutoBookRow row, AutoTranslateSettings settings, CancellationToken cancellationToken);
    Task<string?> PrepareAsync(AutoBookRow row, AutoBookDownload download, AutoTranslateSettings settings, CancellationToken cancellationToken);
    Task<bool> TranslateAsync(AutoBookRow row, string manifestPath, CancellationToken cancellationToken);
}

/// <summary>
/// Downloading, the Codex survey and translation run side by side, each one book at a time, so the next book downloads
/// while Codex surveys this one. Books can be added or retried while it runs; it ends once no book is left in it.
/// </summary>
internal sealed class AutoBookPipeline(IAutoBookStages stages, AutoTranslateSettings settings)
{
    private readonly Channel<AutoBookRow> _downloads = Channel.CreateUnbounded<AutoBookRow>(),
        _surveys = Channel.CreateUnbounded<AutoBookRow>(), _translations = Channel.CreateUnbounded<AutoBookRow>();
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private int _active;
    private bool _closed;

    public AutoTranslateSettings Settings => settings;
    public int Active { get { lock (_gate) return _active; } }

    /// <summary>Queues the book at the stage it stopped in. False once the run has ended: start a new run for it.</summary>
    public bool TryEnqueue(AutoBookRow row)
    {
        lock (_gate)
        {
            if (_closed) return false;
            if (row.Busy || row.Finished || row.Removed) return true;
            if (row.Step == AutoBookStep.Translate && row.ManifestPath is null) row.Step = AutoBookStep.Survey;
            if (row.Step == AutoBookStep.Survey && row.Download is null) row.Step = AutoBookStep.Download;
            (Channel<AutoBookRow> target, string stage) = row.Step switch
            {
                AutoBookStep.Translate => (_translations, "Chờ dịch"),
                AutoBookStep.Survey => (_surveys, "Đã tải, chờ Codex"),
                _ => (_downloads, "Chờ tải")
            };
            row.Cancellation?.Dispose();
            row.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            row.Busy = true;
            row.CanRetry = false;
            row.Stage = stage;
            _active++;
            target.Writer.TryWrite(row);
            return true;
        }
    }

    /// <summary>Drops one book: a running stage is cancelled (a translation queue is paused safely).</summary>
    public static void Remove(AutoBookRow row)
    {
        row.Removed = true;
        row.CanRetry = false;
        row.Cancellation?.Cancel();
    }

    public void Stop() => _stop.Cancel();

    public async Task RunAsync()
    {
        try
        {
            await Task.WhenAll(RunStageAsync(_downloads, DownloadAsync), RunStageAsync(_surveys, SurveyAsync),
                RunStageAsync(_translations, TranslateAsync));
        }
        finally { _stop.Dispose(); }
    }

    private async Task RunStageAsync(Channel<AutoBookRow> stage, Func<AutoBookRow, CancellationToken, Task<Channel<AutoBookRow>?>> work)
    {
        await foreach (AutoBookRow row in stage.Reader.ReadAllAsync())
        {
            Channel<AutoBookRow>? next = null;
            CancellationToken token = row.Cancellation!.Token;
            try
            {
                token.ThrowIfCancellationRequested();
                next = await work(row, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (!row.Removed) row.Stage = row.Step == AutoBookStep.Translate ? "Đã tạm dừng" : "Đã dừng";
            }
            catch (Exception ex)
            {
                row.Stage = row.Step switch { AutoBookStep.Translate => "Lỗi dịch", AutoBookStep.Survey => "Lỗi khảo sát", _ => "Lỗi tải" };
                row.Detail = ex.Message;
            }
            Advance(row, token.IsCancellationRequested ? null : next);
        }
    }

    private async Task<Channel<AutoBookRow>?> DownloadAsync(AutoBookRow row, CancellationToken cancellationToken)
    {
        row.Step = AutoBookStep.Download;
        AutoBookExisting? existing = null;
        if (!row.NewProject && !row.CheckedExisting)
        {
            existing = await stages.FindExistingAsync(row, settings, cancellationToken);
            // Looked up once: a throttled download coming back on its own goes straight on downloading.
            row.CheckedExisting = existing is null;
        }
        if (existing is not null)
        {
            row.Title = existing.Title;
            if (existing.Complete)
            {
                // Surveyed with every chapter: translate what is left in it; never survey or create it again.
                row.ManifestPath = existing.ManifestPath;
                row.Step = AutoBookStep.Translate;
                row.Detail = "Dùng dự án đã có: " + existing.ManifestPath;
                if (!settings.PrepareOnly) return _translations;
                row.Stage = "Đã có dự án";
                row.Finished = true;
                return null;
            }
            row.NewProject = true;
            row.Stage = "Đã có dự án chưa đủ";
            row.Detail = (existing.Surveyed
                    ? $"Dự án đã có chỉ có {existing.Chapters}/{existing.SourceChapters} chương."
                    : "Dự án đã có chưa được Codex khảo sát.") +
                " Bấm Thử lại để tải đủ, khảo sát và tạo dự án mới; dự án cũ được giữ nguyên.";
            return null;
        }
        if (await stages.DownloadAsync(row, settings, cancellationToken) is not { } download) return null;
        row.Download = download;
        row.Step = AutoBookStep.Survey;
        return _surveys;
    }

    private async Task<Channel<AutoBookRow>?> SurveyAsync(AutoBookRow row, CancellationToken cancellationToken)
    {
        if (await stages.PrepareAsync(row, row.Download!, settings, cancellationToken) is not { } manifest) return null;
        row.ManifestPath = manifest;
        row.Download = null;
        row.Step = AutoBookStep.Translate;
        if (!settings.PrepareOnly) return _translations;
        row.Finished = true;
        return null;
    }

    private async Task<Channel<AutoBookRow>?> TranslateAsync(AutoBookRow row, CancellationToken cancellationToken)
    {
        row.Finished = await stages.TranslateAsync(row, row.ManifestPath!, cancellationToken);
        return null;
    }

    private void Advance(AutoBookRow row, Channel<AutoBookRow>? next)
    {
        lock (_gate)
        {
            if (next is not null)
            {
                if (next == _translations) row.Stage = "Chờ dịch";
                next.Writer.TryWrite(row);
                return;
            }
            // Throttled: the book stays in the run (still counted) and downloads again on its own after the wait.
            if (row.RetryAfter is { } wait && !row.Removed && !row.Cancellation!.IsCancellationRequested)
            {
                row.RetryAfter = null;
                _ = RequeueLaterAsync(row, wait);
                return;
            }
            row.RetryAfter = null;
            Leave(row);
        }
    }

    private async Task RequeueLaterAsync(AutoBookRow row, TimeSpan wait)
    {
        try
        {
            await Task.Delay(wait, row.Cancellation!.Token).ConfigureAwait(true);
            lock (_gate)
            {
                row.Stage = "Chờ tải";
                _downloads.Writer.TryWrite(row);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (!row.Removed) row.Stage = "Đã dừng";
                Leave(row);
            }
        }
    }

    /// <summary>The book leaves the run; the run ends with its last book.</summary>
    private void Leave(AutoBookRow row)
    {
        row.Busy = false;
        row.CanRetry = !row.Finished && !row.Removed;
        if (--_active > 0) return;
        _closed = true;
        _downloads.Writer.TryComplete();
        _surveys.Writer.TryComplete();
        _translations.Writer.TryComplete();
    }
}
