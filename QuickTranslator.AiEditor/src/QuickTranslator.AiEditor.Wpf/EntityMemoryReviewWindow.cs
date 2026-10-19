using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

internal sealed record MemoryReviewRow(Guid Id, bool IsEvent, string Kind, string Title, MemoryReviewStatus Status,
    MemoryTimeline Timeline, IReadOnlyList<Guid> EntityIds, IReadOnlyList<Guid> EvidenceIds, string Reason)
{
    public string Period => $"{Timeline.EffectiveFromChapter?.ToString() ?? "?"}–{Timeline.EffectiveToChapter?.ToString() ?? "…"}; lộ {Timeline.RevealedAtChapter?.ToString() ?? "?"}";
}
internal sealed record MemoryReviewEdit(MemoryTimeline Timeline, string Note, TranslationImpactEventType Type,
    MemoryEventEffect Effect, IReadOnlyList<Guid> EntityIds, IReadOnlyList<Guid> AffectedFactIds, MemoryFactKind? PromotionKind = null);
internal sealed record MemoryChoice(Guid? Id, string Label);

/// <summary>2A audit/review only. No translation provider, API key or feature-flag write.</summary>
public sealed class EntityMemoryReviewWindow : Window
{
    private readonly ProjectSession _session;
    private readonly ProjectRepository _repository;
    private readonly Func<CorpusEvidence, Task> _openEvidence;
    private readonly Button _scan = new() { Content = "Lập / Tiếp tục Memory (CPU)" };
    private readonly Button _pause = new() { Content = "Tạm dừng CPU", IsEnabled = false };
    private readonly Button _reload = new() { Content = "Nạp lại" };
    private readonly Button _reason = new() { Content = "Đề xuất bằng luật" };
    private readonly ComboBox _kind = new() { Width = 130, ItemsSource = new[] { "Tất cả", "Dữ kiện", "Sự kiện" }, SelectedIndex = 0 };
    private readonly ComboBox _status = new() { Width = 140, ItemsSource = new[] { "Tất cả" }.Concat(Enum.GetNames<MemoryReviewStatus>()).ToArray(), SelectedIndex = 0 };
    private readonly ComboBox _character = new() { Width = 250, DisplayMemberPath = nameof(MemoryChoice.Label) };
    private readonly TextBox _chapter = new() { Width = 60, ToolTip = "Lọc theo mốc hoặc chương chứng cứ; để trống = tất cả" };
    private readonly DataGrid _rows = new() { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBox _details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ListBox _evidence = new() { DisplayMemberPath = nameof(MemoryChoice.Label), MaxHeight = 125 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _accept = new() { Content = "Duyệt (Accepted)" };
    private readonly Button _confirm = new() { Content = "Xác nhận nguồn + mốc" };
    private readonly Button _reject = new() { Content = "Bác bỏ" };
    private readonly Button _undo = new() { Content = "Hoàn tác duyệt" };
    private readonly Button _promote = new() { Content = "Xác nhận alias/title từ audit", Visibility = Visibility.Collapsed };
    private readonly Button _open = new() { Content = "Mở chứng cứ" };
    private readonly Button _preview = new() { Content = "Xem ngữ cảnh chương (không dùng khi dịch)" };
    private CancellationTokenSource? _cancellation;
    private int _reloadGeneration;
    internal EntityMemoryState? Memory { get; private set; }
    internal EntityMemoryQuery? Query { get; private set; }
    internal bool Busy { get; private set; }
    internal string? LastError { get; private set; }
    internal Func<string, bool> ConfirmAction { get; set; }
    internal Func<MemoryReviewRow, MemoryReviewStatus, bool, MemoryReviewEdit?> EditReview { get; set; }
    internal SemanticSurveyPanel SemanticPanel { get; }

    public EntityMemoryReviewWindow(ProjectSession session, ProjectRepository repository, Func<CorpusEvidence, Task> openEvidence, SemanticSurveySelection? selection = null)
    {
        _session = session; _repository = repository; _openEvidence = openEvidence;
        Title = "QT Studio · Entity Memory & Event Ledger (2A)"; Width = 1180; Height = 750; MinWidth = 900; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ConfirmAction = message => MessageBox.Show(this, message, Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        EditReview = (row, action, promotion) => { var dialog = new EntityMemoryReviewDialog(row, Memory!, action, promotion) { Owner = this }; return dialog.ShowDialog() == true ? dialog.Result : null; };
        var cpu = Build(); SemanticPanel = new(session, repository, openEvidence, selection) { CanOperate = () => !Busy };
        SemanticPanel.BusyChanged = busy => cpu.IsEnabled = !busy;
        var tabs = new TabControl { Items = { new TabItem { Header = "Memory / CPU hypotheses (2A)", Content = cpu }, new TabItem { Header = "AI Semantic Survey (2B)", Content = SemanticPanel } } };
        tabs.SelectionChanged += async (sender, args) => { if (ReferenceEquals(args.OriginalSource, tabs) && tabs.SelectedIndex == 0 && !Busy && !SemanticPanel.Busy) await ReloadAsync(); };
        Content = tabs;
        Loaded += async (_, _) => await ReloadAsync(); Closed += (_, _) => { _cancellation?.Cancel(); SemanticPanel.Cancel(); };
    }
    private UIElement Build()
    {
        var root = new DockPanel { Margin = new Thickness(12) }; var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(new TextBlock { Text = "Entity Memory · Translation-Impact Event Ledger", FontSize = 22, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = "Chỉ kiểm toán 2A. Accepted ≠ Confirmed. Không tự gộp danh tính, không ghi Resources/prompt, không bật Knowledge Context. Bộ lọc là audit toàn truyện, không phải ngữ cảnh dịch.", TextWrapping = TextWrapping.Wrap });
        var controls = new WrapPanel { Margin = new Thickness(0, 8, 0, 5) };
        foreach (UIElement item in new UIElement[] { _scan, _pause, _reload, _reason, _kind, _status, _character, new TextBlock { Text = "Chương", VerticalAlignment = VerticalAlignment.Center }, _chapter }) controls.Children.Add(item);
        top.Children.Add(controls); top.Children.Add(_summary); root.Children.Add(top);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom);
        footer.Children.Add(_evidence); var actions = new WrapPanel();
        foreach (var button in new[] { _accept, _confirm, _reject, _undo, _promote, _open, _preview }) actions.Children.Add(button);
        footer.Children.Add(actions); root.Children.Add(footer);
        var body = new Grid(); body.ColumnDefinitions.Add(new() { Width = new GridLength(3, GridUnitType.Star) }); body.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star) });
        foreach (var column in new[] { ("Loại", nameof(MemoryReviewRow.Kind), 110d), ("Dữ kiện / sự kiện", nameof(MemoryReviewRow.Title), 290d),
                     ("Trạng thái", nameof(MemoryReviewRow.Status), 100d), ("Hiệu lực / tiết lộ", nameof(MemoryReviewRow.Period), 150d) })
            _rows.Columns.Add(new DataGridTextColumn { Header = column.Item1, Binding = new Binding(column.Item2), Width = column.Item3 });
        body.Children.Add(_rows); Grid.SetColumn(_details, 1); body.Children.Add(_details); root.Children.Add(body);
        _scan.Click += async (_, _) => await ScanAsync(); _pause.Click += (_, _) => _cancellation?.Cancel();
        _reload.Click += async (_, _) => await ReloadAsync(); _reason.Click += async (_, _) => await ProposeAsync();
        _kind.SelectionChanged += (_, _) => Filter(); _status.SelectionChanged += (_, _) => Filter(); _character.SelectionChanged += (_, _) => Filter(); _chapter.TextChanged += (_, _) => Filter();
        _rows.SelectionChanged += (_, _) => ShowSelection(); _evidence.SelectionChanged += (_, _) => Buttons();
        _accept.Click += async (_, _) => await ReviewSelectedAsync(MemoryReviewStatus.Accepted);
        _confirm.Click += async (_, _) => await ReviewSelectedAsync(MemoryReviewStatus.Confirmed);
        _reject.Click += async (_, _) => await ReviewSelectedAsync(MemoryReviewStatus.Rejected);
        _promote.Click += async (_, _) => await ReviewSelectedAsync(MemoryReviewStatus.Confirmed, true);
        _undo.Click += async (_, _) => await UndoAsync(); _open.Click += async (_, _) => await OpenEvidenceAsync();
        _evidence.MouseDoubleClick += async (_, _) => await OpenEvidenceAsync();
        _preview.Click += (_, _) =>
        {
            if (Query is not null && int.TryParse(_chapter.Text, out int n) && n > 0)
                _details.Text = "Snapshot kiểm thử; KHÔNG nối vào pipeline dịch:\n" + JsonSerializer.Serialize(Query.AtChapter(n), new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true });
        };
        Buttons(); return root;
    }
    internal async Task ReloadAsync()
    {
        if (Busy) return;
        int generation = ++_reloadGeneration;
        try
        {
            var memory = await new EntityMemoryStore(_session).LoadAsync();
            var query = new EntityMemoryQuery(memory, await EntityMemoryProjectInputs.LoadSourcesAsync(_session, _repository));
            if (generation != _reloadGeneration || Busy) return;
            Memory = memory; Query = query;
            var selected = (_character.SelectedItem as MemoryChoice)?.Id;
            var choices = new[] { new MemoryChoice(null, "Tất cả nhân vật / danh tính") }.Concat(Memory.Entities.Select(x => new MemoryChoice(x.Id, $"{x.Label} · {x.Kind} · {x.Id.ToString()[..8]}"))).ToArray();
            _character.ItemsSource = choices; _character.SelectedItem = choices.FirstOrDefault(x => x.Id == selected) ?? choices[0];
            _summary.Text = $"Revision {Memory.Revision} · {Memory.Entities.Count} handles · {Memory.Facts.Count} dữ kiện · {Memory.Events.Count} event candidates · {Memory.Reviews.Count} quyết định. Chưa kiểm chứng độ đầy đủ.";
            Filter();
        }
        catch (Exception ex)
        {
            if (generation != _reloadGeneration || Busy) return;
            Memory = null; Query = null; LastError = ex.Message; _summary.Text = "Không nạp được Memory: " + ex.Message; _rows.ItemsSource = null; ShowSelection();
        }
    }
    internal async Task ScanAsync() => await RunAsync(async () =>
    {
        _cancellation = new(); Buttons();
        var progress = new Progress<MemoryScanProgress>(p => _summary.Text = $"CPU {p.ProcessedChapters}/{p.TotalChapters} chương · {p.EventCandidates} candidates · checkpoint rev {p.Revision}");
        await new TranslationImpactScanner().ScanProjectAsync(_session, _repository, progress, _cancellation.Token);
    });
    internal async Task ProposeAsync() => await RunAsync(async () =>
    {
        var sources = await EntityMemoryProjectInputs.LoadSourcesAsync(_session, _repository); var store = new EntityMemoryStore(_session);
        var latest = await store.LoadAsync(); await store.UpdateAsync(latest.Revision, s => EntityMemoryReasoning.Propose(s, sources));
    });
    private async Task RunAsync(Func<Task> operation)
    {
        if (Busy) return; Busy = true; ++_reloadGeneration; LastError = null; Buttons();
        bool cancelled = false;
        try { await operation(); }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception ex) { LastError = ex.Message; }
        finally
        {
            _cancellation?.Dispose(); _cancellation = null; Busy = false; await ReloadAsync();
            if (cancelled) _summary.Text += " Đã dừng CPU; checkpoint đã commit vẫn được giữ.";
            if (LastError is not null) _summary.Text = LastError; Buttons();
        }
    }
    private void Filter()
    {
        if (Memory is null || Query is null) return;
        int? chapter = null;
        if (!string.IsNullOrWhiteSpace(_chapter.Text))
        {
            if (!int.TryParse(_chapter.Text, out int n) || n <= 0) { _rows.ItemsSource = Array.Empty<MemoryReviewRow>(); _summary.Text = "Chương phải là số nguyên dương; để trống để bỏ lọc."; return; }
            chapter = n;
        }
        string Label(Guid id) => Memory.Entities.Single(x => x.Id == id).Label;
        var facts = Memory.Facts.Select(x => new MemoryReviewRow(x.Id, false, x.Kind.ToString(), $"{Label(x.SubjectId)}{(x.TargetId is Guid target ? " → " + Label(target) : "")} · {x.Value}",
            Query.Status(x.Id), Query.Timeline(x.Id), x.TargetId is Guid t ? new[] { x.SubjectId, t } : new[] { x.SubjectId }, x.EvidenceIds,
            x.Provenance.Origin + " · " + x.Provenance.Explanation + "\nInput facts: " + string.Join(", ", x.InputFactIds)));
        var events = Memory.Events.Select(x => new MemoryReviewRow(x.Id, true, x.Type.ToString(), string.Join(", ", x.EntityIds.Select(Label)),
            Query.Status(x.Id), Query.Timeline(x.Id), x.EntityIds, x.EvidenceIds, x.Provenance.Explanation + "\nEffect: " + x.Effect + "\nAffected facts: " + string.Join(", ", x.AffectedFactIds)));
        Guid? character = (_character.SelectedItem as MemoryChoice)?.Id; string? status = _status.SelectedItem as string;
        _rows.ItemsSource = facts.Concat(events).Where(x => (_kind.SelectedIndex == 0 || x.IsEvent == (_kind.SelectedIndex == 2)) &&
            (status is null or "Tất cả" || x.Status.ToString() == status) && (character is null || x.EntityIds.Contains(character.Value)) &&
            (chapter is null || x.Timeline.DiscoveredAtChapter == chapter || x.Timeline.RevealedAtChapter == chapter ||
                x.Timeline.EffectiveFromChapter is not null && x.Timeline.EffectiveFromChapter <= chapter && (x.Timeline.EffectiveToChapter is null || x.Timeline.EffectiveToChapter >= chapter) ||
                x.EvidenceIds.Any(id => Memory.Evidence.Single(e => e.Id == id).ChapterNumber == chapter))).ToArray();
        ShowSelection();
    }
    private void ShowSelection()
    {
        if (Memory is null || Query is null || _rows.SelectedItem is not MemoryReviewRow row) { _details.Clear(); _evidence.ItemsSource = null; Buttons(); return; }
        _details.Text = $"{row.Title}\n{row.Status} · {row.Period}\n{row.Reason}\n\n" + string.Join("\n", Memory.Reviews.Where(x => x.TargetId == row.Id)
            .Select(x => $"{x.At:O} · {x.Status} · {x.Note} · id={x.Id}"));
        _evidence.ItemsSource = row.EvidenceIds.Select(id => { var e = Memory.Evidence.Single(x => x.Id == id); return new MemoryChoice(id,
            $"Chương {e.ChapterNumber} · {e.ChapterId} · UTF-16 {e.StartUtf16}+{e.LengthUtf16} · {(Query.EvidenceCurrent(id) ? "Current" : "Stale")}"); }).ToArray();
        if (_evidence.Items.Count > 0) _evidence.SelectedIndex = 0; Buttons();
    }
    private void Buttons()
    {
        var row = _rows.SelectedItem as MemoryReviewRow;
        _scan.IsEnabled = _reload.IsEnabled = _reason.IsEnabled = !Busy; _pause.IsEnabled = Busy && _cancellation is not null;
        _accept.IsEnabled = _reject.IsEnabled = !Busy && row is not null;
        _confirm.IsEnabled = !Busy && row is not null && row.Status != MemoryReviewStatus.Stale && Query?.IsFresh(row.Id) == true;
        _undo.IsEnabled = !Busy && row is not null && Memory?.Reviews.Any(x => x.TargetId == row.Id) == true;
        bool audit = row is not null && Memory?.Facts.SingleOrDefault(x => x.Id == row.Id)?.Provenance.Origin == MemoryOrigin.AuditOnly;
        _promote.Visibility = audit ? Visibility.Visible : Visibility.Collapsed; _promote.IsEnabled = _confirm.IsEnabled && audit;
        _open.IsEnabled = !Busy && _evidence.SelectedItem is MemoryChoice { Id: Guid id } && Query?.EvidenceCurrent(id) == true;
        _preview.IsEnabled = !Busy && Query is not null && int.TryParse(_chapter.Text, out int n) && n > 0;
    }
    internal async Task ReviewSelectedAsync(MemoryReviewStatus action, bool promote = false)
    {
        if (Busy || Memory is null || _rows.SelectedItem is not MemoryReviewRow row) return;
        var edit = EditReview(row, action, promote); if (edit is null || !ConfirmAction("Lưu quyết định vào Memory? Không sửa Project Resources, không bật hay ghi prompt dịch.")) return;
        long revision = Memory.Revision;
        await RunAsync(async () =>
        {
            var service = new EntityMemoryReviewService(_session, _repository);
            if (promote) await service.PromoteAuditAliasAsync(row.Id, edit.PromotionKind ?? throw new ArgumentException("Chọn alias hoặc title."), edit.Timeline, revision, edit.Note);
            else if (row.IsEvent && action is MemoryReviewStatus.Confirmed or MemoryReviewStatus.Accepted)
                await service.RefineEventAsync(row.Id, edit.Type, edit.Effect, edit.EntityIds, edit.AffectedFactIds, edit.Timeline, action, revision, edit.Note);
            else await service.ReviewAsync(row.Id, action, edit.Timeline, revision, edit.Note);
        });
    }
    internal async Task UndoAsync()
    {
        if (Busy || Memory is null || _rows.SelectedItem is not MemoryReviewRow row || Memory.Reviews.LastOrDefault(x => x.TargetId == row.Id) is not { } last ||
            !ConfirmAction("Hoàn tác quyết định mới nhất về Proposed? Giữ nguyên toàn bộ lịch sử, không tự khôi phục xác nhận cũ.")) return;
        await RunAsync(async () => await new EntityMemoryReviewService(_session, _repository).RollbackAsync(last.Id, Memory.Revision, "Explicit UI rollback to Proposed"));
    }
    internal async Task OpenEvidenceAsync()
    {
        if (Busy || Memory is null || _evidence.SelectedItem is not MemoryChoice { Id: Guid id }) return;
        await RunAsync(async () =>
        {
            var current = new EntityMemoryQuery(Memory, await EntityMemoryProjectInputs.LoadSourcesAsync(_session, _repository));
            await _openEvidence(current.Navigation(id));
        });
    }
}
