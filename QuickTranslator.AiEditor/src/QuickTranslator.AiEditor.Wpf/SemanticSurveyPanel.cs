using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Wpf;

public sealed record SemanticSurveySelection(OllamaBackendProfile Profile, ModelOptions Options, bool IsGemini)
{
    public string Signature => (IsGemini ? "gemini:" : "ollama:") + EntityMemoryIdentity.Fingerprint(new { Profile.Id, Profile.Kind, Profile.Endpoint, Options.Model });
}
internal sealed record SemanticUiRow(Guid Id, string Type, string Identities, string Status, string Actuality, int Chapter);

/// <summary>Staging and explicit review, deliberately isolated from the translation provider/context flag.</summary>
public sealed class SemanticSurveyPanel : UserControl
{
    private readonly ProjectSession _session;
    private readonly ProjectRepository _repository;
    private readonly Func<CorpusEvidence, Task> _openEvidence;
    private readonly SemanticSurveySelection? _selection;
    private readonly Button _dry = new() { Content = "Dry-run (không gọi AI)" }, _run = new() { Content = "Khảo sát / Resume AI" }, _pause = new() { Content = "Tạm dừng" },
        _reload = new() { Content = "Nạp lại" }, _accept = new() { Content = "Duyệt đề xuất" }, _confirm = new() { Content = "Xác nhận chủ thể + nội dung + mốc" },
        _reject = new() { Content = "Bác bỏ" }, _undo = new() { Content = "Hoàn tác" }, _open = new() { Content = "Mở chứng cứ" }, _preview = new() { Content = "Preview ngữ cảnh chương" };
    private readonly TextBox _requests = new() { Text = "5", Width = 45 }, _tokens = new() { Text = "150000", Width = 85 }, _context = new() { Width = 75 },
        _chapter = new() { Width = 55 }, _character = new() { Width = 160 };
    private readonly ComboBox _kind = new() { ItemsSource = new[] { "Tất cả" }.Concat(Enum.GetNames<SemanticFindingType>()).ToArray(), SelectedIndex = 0, Width = 200 },
        _status = new() { ItemsSource = new[] { "Tất cả", "Proposed", "Accepted", "Confirmed", "Rejected", "Stale", "Conflict", "InvalidEvidence", "Ambiguous" }, SelectedIndex = 0, Width = 140 },
        _batchSize = new() { ItemsSource = new[] { 2, 3, 4, 5, 6 }, SelectedItem = 4, Width = 45 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly DataGrid _grid = new() { IsReadOnly = true, CanUserAddRows = false, AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBox _details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private CancellationTokenSource? _cancel;
    private EntityMemoryState _memory = new();
    private IReadOnlyList<CensusSource> _sources = [];
    private SemanticDryRun? _plan;
    private EntityMemoryQuery? _query;
    private int _loadGeneration;
    internal SemanticSurveyState State { get; private set; } = new();
    internal bool Busy { get; private set; }
    internal string? LastError { get; private set; }
    internal Func<bool> CanOperate { get; set; } = () => true;
    internal Action<bool>? BusyChanged { get; set; }
    internal Func<string, bool> ConfirmAction { get; set; }
    internal Func<SemanticProposal, MemoryReviewStatus, SemanticPromotion?> EditPromotion { get; set; }
    internal Func<SemanticSurveySelection, SemanticSurveyBudget, IJsonSchemaOllamaClient> BackendFactory { get; set; }

    public SemanticSurveyPanel(ProjectSession session, ProjectRepository repository, Func<CorpusEvidence, Task> openEvidence, SemanticSurveySelection? selection = null)
    {
        _session = session; _repository = repository; _openEvidence = openEvidence; _selection = selection;
        _context.Text = (selection?.Options.ContextSize ?? 32768).ToString(CultureInfo.InvariantCulture);
        ConfirmAction = message => MessageBox.Show(Window.GetWindow(this), message, "Semantic Survey 2B", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        EditPromotion = (proposal, action) => { var dialog = new SemanticPromotionDialog(proposal, _memory, action) { Owner = Window.GetWindow(this) }; return dialog.ShowDialog() == true ? dialog.Result : null; };
        BackendFactory = (selected, budget) =>
        {
            if (!selected.IsGemini) return SemanticSurveyBackend.Ollama(selected.Profile, Options(), budget);
            string key = GeminiApiKeyVault.ResolveForSurvey();
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Nhập Gemini key trong Cài đặt → AI & hiệu năng. Key không được lưu trong survey sidecar.");
            return SemanticSurveyBackend.Gemini(key, GeminiSurveyProjectScope.Resolve(key, Environment.GetEnvironmentVariable("QTSTUDIO_GEMINI_PROJECT_ID")), Options(), budget);
        };
        Content = Build(); Loaded += async (_, _) => { if (!Busy) await Operate(LoadAsync, false); };
    }
    private UIElement Build()
    {
        var root = new DockPanel { Margin = new Thickness(12) }; var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(new TextBlock { Text = "AI Semantic Survey · đề xuất chưa duyệt, tách khỏi CPU hypothesis", FontSize = 20 });
        top.Children.Add(new TextBlock { Text = $"Backend đã chọn: {_selection?.Options.Model ?? "chưa chọn — quay lại cửa sổ khảo sát"}. Không tự đổi model/kết nối cloud. Audit toàn truyện; preview theo chương không nối pipeline dịch.", TextWrapping = TextWrapping.Wrap });
        var controls = new WrapPanel();
        foreach (UIElement item in new UIElement[] { _dry, _run, _pause, _reload, Label("Max HTTP"), _requests, Label("Token reserve"), _tokens, Label("Context"), _context, Label("Max evidence/batch"), _batchSize }) controls.Children.Add(item);
        top.Children.Add(controls); var filters = new WrapPanel();
        foreach (UIElement item in new UIElement[] { _kind, _status, Label("Nhân vật"), _character, Label("Chương"), _chapter, _preview }) filters.Children.Add(item);
        top.Children.Add(filters); top.Children.Add(_summary); root.Children.Add(top);
        var footer = new WrapPanel(); DockPanel.SetDock(footer, Dock.Bottom);
        foreach (var button in new[] { _accept, _confirm, _reject, _undo, _open }) footer.Children.Add(button); root.Children.Add(footer);
        var body = new Grid(); body.ColumnDefinitions.Add(new() { Width = new GridLength(3, GridUnitType.Star) }); body.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star) });
        foreach (var column in new[] { ("Loại AI", nameof(SemanticUiRow.Type)), ("Danh tính đề xuất", nameof(SemanticUiRow.Identities)), ("Review", nameof(SemanticUiRow.Status)), ("Actuality", nameof(SemanticUiRow.Actuality)), ("Chương", nameof(SemanticUiRow.Chapter)) })
            _grid.Columns.Add(new DataGridTextColumn { Header = column.Item1, Binding = new Binding(column.Item2), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        body.Children.Add(_grid); Grid.SetColumn(_details, 1); body.Children.Add(_details); root.Children.Add(body);
        _dry.Click += async (_, _) => await DryRunAsync(); _run.Click += async (_, _) => await SurveyAsync(); _pause.Click += (_, _) => Cancel(); _reload.Click += async (_, _) => await Operate(LoadAsync);
        _accept.Click += async (_, _) => await ReviewAsync(MemoryReviewStatus.Accepted); _confirm.Click += async (_, _) => await ReviewAsync(MemoryReviewStatus.Confirmed); _reject.Click += async (_, _) => await ReviewAsync(MemoryReviewStatus.Rejected);
        _undo.Click += async (_, _) => await UndoAsync(); _open.Click += async (_, _) => await OpenAsync(); _preview.Click += async (_, _) => await PreviewAsync();
        _grid.SelectionChanged += (_, _) => Details(); _kind.SelectionChanged += (_, _) => Filter(); _status.SelectionChanged += (_, _) => Filter(); _character.TextChanged += (_, _) => Filter(); _chapter.TextChanged += (_, _) => Filter();
        _requests.TextChanged += (_, _) => _plan = null; _tokens.TextChanged += (_, _) => _plan = null; _context.TextChanged += (_, _) => _plan = null;
        _batchSize.SelectionChanged += (_, _) => _plan = null;
        Buttons(); return root;
        static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 3, 0) };
    }
    private ModelOptions Options()
    {
        if (_selection is null) throw new InvalidOperationException("Chọn backend và model trong cửa sổ khảo sát trước khi mở Memory.");
        if (!int.TryParse(_context.Text, out int n)) throw new ArgumentException("Context phải là số nguyên.");
        var options = _selection.Options with { ContextSize = n }; options.Validate(); return options;
    }
    private SemanticSurveyBudget Budget()
    {
        if (!int.TryParse(_requests.Text, out int requests) || !int.TryParse(_tokens.Text, out int tokens)) throw new ArgumentException("Ngân sách phải là số nguyên dương.");
        var result = new SemanticSurveyBudget(requests, tokens); result.Validate(); return result;
    }
    private SemanticBatchingOptions Batching(SemanticSurveyBudget budget) => new((int)_batchSize.SelectedItem, budget.MaximumReservedTokens);
    internal async Task LoadAsync()
    {
        int generation = ++_loadGeneration;
        var state = await new SemanticSurveyStore(_session).LoadAsync(); var memory = await new EntityMemoryStore(_session).LoadAsync();
        var sources = await EntityMemoryProjectInputs.LoadSourcesAsync(_session, _repository);
        if (generation != _loadGeneration) return;
        State = state; _memory = memory; _sources = sources; _query = new(memory, sources);
        _summary.Text = "Lịch sử batch: " + string.Join(" · ", Enum.GetValues<SemanticBatchStatus>().Select(s => s + "=" + State.Batches.Count(b => State.Status(b.Id) == s))) +
            $" · {State.Proposals.Count} đề xuất AI. Validated = kiểm tra kỹ thuật, KHÔNG phải accuracy verified. Đoạn blocked/failed/pending chưa được khảo sát thành công.";
        Filter();
    }
    internal async Task DryRunAsync() => await Operate(async () =>
    {
        await LoadAsync(); var options = Options(); var legacy = await new BookIntelligenceStore(_session).LoadAsync();
        var budget = Budget(); var batching = Batching(budget);
        var requests = await Task.Run(() => SemanticSurveyRequests.Plan(_memory, _sources, legacy, _selection!.Signature, options, batching: batching));
        _plan = SemanticSurveyEngine.DryRun(requests, State, legacy);
        _summary.Text = $"DRY-RUN · model={options.Model} · context={options.ContextSize:N0} · {_plan.TotalEvidence} evidence · {_plan.TotalBatches} batches · {_plan.PendingRequests} pending calls ({_plan.PendingEvidence} evidence) · {_plan.CompletedEvidence} completed evidence · {_plan.BlockedEvidence} blocked evidence · input estimate {_plan.EstimatedInputTokens:N0} · conservative reserve {_plan.ConservativeReservedTokens:N0}. " +
            $"Evidence/batch (k:số batch): {string.Join(", ", _plan.EvidencePerBatch.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}"))} · vượt context={_plan.OverContextBatches}. Estimate là heuristic, reserve dùng trần UTF-8 bytes + output, KHÔNG phải token thực tế. " +
            $"Mỗi lần chạy: tối đa {budget.MaximumRequests} HTTP (tính cả metadata/retry), reserve {budget.MaximumReservedTokens:N0} token. Chưa gửi dữ liệu. " +
            (_plan.OverContextBatches > 0 ? "Context quá nhỏ: tăng Context trước khi chạy." : "");
    }, false);
    internal async Task SurveyAsync()
    {
        if (_plan is null || _selection is null) { await DryRunAsync(); return; }
        SemanticSurveyBudget approvedBudget;
        try { approvedBudget = Budget(); } catch (ArgumentException ex) { LastError = ex.Message; _summary.Text = ex.Message; return; }
        if (!ConfirmAction($"Gửi nguyên tác tới backend/model đã chọn {_selection.Options.Model}? Tối đa {approvedBudget.MaximumRequests} HTTP (metadata/retry), reserve {approvedBudget.MaximumReservedTokens:N0} tokens. Không tự đổi model. Không ghi Resources hoặc bật context dịch.")) return;
        await Operate(async () =>
        {
            var budget = Budget(); var options = Options(); var legacy = await new BookIntelligenceStore(_session).LoadAsync();
            var currentSources = await EntityMemoryProjectInputs.LoadSourcesAsync(_session, _repository); var currentMemory = await new EntityMemoryStore(_session).LoadAsync();
            var batching = Batching(budget);
            var plan = await Task.Run(() => SemanticSurveyRequests.Plan(currentMemory, currentSources, legacy, _selection.Signature, options, batching: batching));
            if (SemanticSurveyEngine.DryRun(plan, State, legacy).Fingerprint != _plan.Fingerprint) throw new InvalidOperationException("Nguồn/context/model/context size đã đổi; Dry-run lại trước khi gửi.");
            _cancel = new(); Buttons(); var ct = _cancel.Token;
            var backend = BackendFactory(_selection, budget); using var lifetime = backend as IDisposable;
            await new SemanticSurveyEngine(backend).RunAsync(new(_session), plan, currentSources, legacy, options, budget, _plan.Fingerprint,
                new Progress<SemanticSurveyProgress>(p => _summary.Text = $"{p.Processed}/{p.Total} · batch attempts={p.Requests} · reserve={p.ReservedTokens} · {p.Message}"), ct,
                async r =>
                {
                    // Recheck only source chapters actually used by this request/context, not the whole corpus per window.
                    var freshSession = await EntityMemoryProjectInputs.ReloadSessionAsync(_session, ct);
                    var memory = await new EntityMemoryStore(freshSession).LoadAsync(ct); if (memory.Revision != currentMemory.Revision) return false;
                    var ids = r.Units.Select(x => x.Evidence.ChapterId).ToHashSet(); var visited = new HashSet<Guid>();
                    void Inputs(Guid id) { if (!visited.Add(id)) return; var f = memory.Facts.Single(x => x.Id == id); foreach (var proof in f.EvidenceIds) ids.Add(memory.Evidence.Single(x => x.Id == proof).ChapterId); foreach (var input in f.InputFactIds) Inputs(input); }
                    foreach (var id in r.ContextFactIds) Inputs(id);
                    foreach (var id in r.ContextEventIds)
                        foreach (var proof in memory.Events.Single(x => x.Id == id).EvidenceIds) ids.Add(memory.Evidence.Single(x => x.Id == proof).ChapterId);
                    foreach (var id in ids)
                    {
                        var entry = freshSession.Manifest.Chapters.SingleOrDefault(x => x.ChapterId == id && x.DeletedAt is null); var original = currentSources.SingleOrDefault(x => x.ChapterId == id);
                        if (entry is null || original is null || entry.Number != original.ChapterNumber) return false;
                        var chapter = (await _repository.LoadChapterAsync(freshSession, id, ct)).Chapter;
                        if (chapter.OriginalText != original.OriginalText) return false;
                    }
                    return true;
                });
            _plan = null;
        });
    }
    internal void Cancel() => _cancel?.Cancel();
    private async Task Operate(Func<Task> action, bool reload = true)
    {
        if (Busy || !CanOperate()) return; Busy = true; ++_loadGeneration; LastError = null; BusyChanged?.Invoke(true); Buttons();
        try { await action(); }
        catch (OperationCanceledException) { LastError = "Đã dừng; giữ checkpoint để Resume."; }
        catch (Exception ex) { LastError = ex is ArgumentException or InvalidOperationException ? ex.Message : "Không hoàn tất thao tác; dữ liệu đã lưu được giữ. " + ex.GetType().Name; }
        finally { _cancel?.Dispose(); _cancel = null; Busy = false; BusyChanged?.Invoke(false); if (reload) { try { await LoadAsync(); } catch { LastError ??= "Không đọc được sidecar."; } } if (LastError is not null) _summary.Text = LastError; Buttons(); }
    }
    private string Status(SemanticProposal proposal)
    {
        var review = State.Reviews.LastOrDefault(x => x.ProposalId == proposal.Id); var query = _query!;
        if (review?.Status == MemoryReviewStatus.Confirmed && review.MemoryItemId is Guid id) return query.Status(id).ToString();
        var batch = State.Batches.Single(x => x.Id == proposal.BatchId);
        if (batch.Keys.Any(k => !SemanticDependencies.Current(batch, State.Evidence.Single(x => x.Id == k.EvidenceId), _memory, _sources, query))) return "Stale";
        return review?.Status.ToString() ?? (!proposal.Audit.EvidenceValid ? "InvalidEvidence" : proposal.Audit.Issues.Contains("ContradictionWithConfirmedMemory") ? "Conflict" : !proposal.Audit.CanPromote ? "Ambiguous" : "Proposed");
    }
    private void Filter()
    {
        if (State.ProjectId == Guid.Empty) return;
        var rows = State.Proposals.Select(p => { var f = p.Audit.Finding; var batch = State.Batches.Single(x => x.Id == p.BatchId); return new SemanticUiRow(p.Id, f.FindingType.ToString(),
            string.Join(" → ", SemanticQualityAuditor.Identities(f).Select(i => i.Name).Distinct()), Status(p), f.EventActuality.ToString(), State.Evidence.Single(x => x.Id == batch.EvidenceId).ChapterNumber); });
        _grid.ItemsSource = rows.Where(r => (_kind.SelectedIndex == 0 || r.Type == _kind.SelectedItem as string) && (_status.SelectedIndex == 0 || r.Status == _status.SelectedItem as string) &&
            (string.IsNullOrWhiteSpace(_chapter.Text) || int.TryParse(_chapter.Text, out int n) && n == r.Chapter) && r.Identities.Contains(_character.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        Details();
    }
    private void Details()
    {
        if (_grid.SelectedItem is not SemanticUiRow row) { _details.Clear(); Buttons(); return; }
        var proposal = State.Proposals.Single(x => x.Id == row.Id); var f = proposal.Audit.Finding; var batch = State.Batches.Single(x => x.Id == proposal.BatchId); var e = State.Evidence.Single(x => x.Id == batch.EvidenceId);
        _details.Text = $"AI hypothesis · {row.Status} · {f.EventActuality}\n{f.Reason}\nUncertainties: {string.Join("; ", f.Uncertainties)}\nAudit: {string.Join(",", proposal.Audit.Issues)}\nVerifiedParticipants (đề xuất, chưa duyệt): {string.Join(", ", f.VerifiedParticipants.Select(x => x.Name))}\n" +
            string.Join("\n", SemanticQualityAuditor.Identities(f).Distinct().Select(x => $"Identity: {x.Name} · model kind={x.Kind} · {x.Status} · id={x.EntityId?.ToString() ?? "local:" + x.LocalId}")) +
            $"\nSpeaker: {f.SpeakerIdentity?.EntityId?.ToString() ?? f.SpeakerIdentity?.LocalId ?? "Unresolved"}; listener: {f.ListenerIdentity?.EntityId?.ToString() ?? f.ListenerIdentity?.LocalId ?? "Unresolved"}; xưng={f.SelfReference}; gọi={f.AddresseeReference}\n" +
            string.Join("\n", f.SupportSpans.Select(span =>
            {
                var origin = State.Evidence.SingleOrDefault(x => x.Id == span.EvidenceId);
                return origin is null ? "[foreign evidence]" : $"evidence={origin.Id} chapter={origin.ChapterId} sha={origin.SourceSha256} UTF-16 {span.StartUtf16}+{span.LengthUtf16}\n" + Quote(origin, span);
            })) +
            "\nHistory:\n" + string.Join("\n", State.Reviews.Where(x => x.ProposalId == row.Id).Select(x => $"{x.At:O} {x.Status}: {x.Note}")); Buttons();
    }
    private string Quote(MemoryEvidence e, SemanticSupport span)
    {
        var s = _sources.SingleOrDefault(x => x.ChapterId == e.ChapterId);
        return s is not null && CorpusIntegrityAuditor.SourceHash(s.OriginalText) == e.SourceSha256 && span.StartUtf16 >= 0 && (long)span.StartUtf16 + span.LengthUtf16 <= s.OriginalText.Length
            ? s.OriginalText.Substring(span.StartUtf16, span.LengthUtf16) : "[stale/invalid source]";
    }
    private void Buttons()
    {
        var row = _grid.SelectedItem as SemanticUiRow; _dry.IsEnabled = _run.IsEnabled = _reload.IsEnabled = !Busy; _pause.IsEnabled = Busy && _cancel is not null;
        _requests.IsEnabled = _tokens.IsEnabled = _context.IsEnabled = !Busy;
        _batchSize.IsEnabled = !Busy;
        _accept.IsEnabled = _reject.IsEnabled = _open.IsEnabled = !Busy && row is not null;
        _confirm.IsEnabled = !Busy && row?.Status is "Proposed" or "Accepted" && State.Proposals.Single(x => x.Id == row.Id).Audit.CanPromote;
        _undo.IsEnabled = !Busy && row is not null && State.Reviews.Any(x => x.ProposalId == row.Id); _preview.IsEnabled = !Busy;
    }
    internal async Task ReviewAsync(MemoryReviewStatus action)
    {
        if (_grid.SelectedItem is not SemanticUiRow row || Busy) return; var proposal = State.Proposals.Single(x => x.Id == row.Id); var edit = EditPromotion(proposal, action);
        if (edit is null || !ConfirmAction("Lưu quyết định này? Confirmed chỉ sau khi bạn đã kiểm tra nội dung, chủ thể và mốc. Không sửa Resources/prompt.")) return;
        await Operate(async () => { await new SemanticMemoryReviewService(_session, _repository).ReviewAsync(row.Id, action, edit, State.Revision); _plan = null; });
    }
    internal async Task UndoAsync()
    {
        if (_grid.SelectedItem is not SemanticUiRow row || !ConfirmAction("Hoàn tác về Proposed, giữ lịch sử; không tự khôi phục xác nhận cũ?")) return;
        await Operate(async () => { await new SemanticMemoryReviewService(_session, _repository).UndoAsync(row.Id, State.Revision, "Explicit semantic UI undo"); _plan = null; });
    }
    internal async Task OpenAsync() => await Operate(async () =>
    {
        if (_grid.SelectedItem is not SemanticUiRow row) return; var proposal = State.Proposals.Single(x => x.Id == row.Id); var batch = State.Batches.Single(x => x.Id == proposal.BatchId);
        var sources = await EntityMemoryProjectInputs.LoadSourcesAsync(_session, _repository); var span = proposal.Audit.Finding.SupportSpans.FirstOrDefault();
        if (span is not null && !batch.Keys.Any(x => x.EvidenceId == span.EvidenceId)) throw new InvalidOperationException("Support không thuộc batch.");
        var proof = State.Evidence.Single(x => x.Id == (span?.EvidenceId ?? batch.EvidenceId));
        var memory = new EntityMemoryState { ProjectId = State.ProjectId, Evidence = [proof with { StartUtf16 = span?.StartUtf16 ?? proof.StartUtf16, LengthUtf16 = span?.LengthUtf16 ?? proof.LengthUtf16 }] };
        await _openEvidence(new EntityMemoryQuery(memory, sources).Navigation(proof.Id));
    });
    internal async Task PreviewAsync() => await Operate(async () =>
    {
        if (!int.TryParse(_chapter.Text, out int n) || n <= 0) throw new ArgumentException("Nhập chương dương để preview.");
        var snapshot = new EntityMemoryQuery(await new EntityMemoryStore(_session).LoadAsync(), await EntityMemoryProjectInputs.LoadSourcesAsync(_session, _repository)).AtChapter(n);
        _details.Text = "PREVIEW ONLY — chưa nối vào BookKnowledgeContextProvider:\n" + JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true });
    }, false);
}

internal sealed class SemanticPromotionDialog : Window
{
    internal SemanticPromotion? Result { get; private set; }
    internal SemanticPromotionDialog(SemanticProposal proposal, EntityMemoryState memory, MemoryReviewStatus action)
    {
        Title = "Duyệt AI hypothesis · " + action; Width = 620; MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 40); SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(14) }; var f = proposal.Audit.Finding;
        panel.Children.Add(new TextBlock { Text = $"{f.FindingType}: {f.Reason}\nActuality={f.EventActuality}. HistoricalReference không có mốc thì để trống, không dùng chương nhắc lại làm ngày bắt đầu. Hủy không lưu.", TextWrapping = TextWrapping.Wrap });
        var from = new TextBox { Text = f.EffectiveChapter?.ToString() ?? "" }; var to = new TextBox { Text = f.EffectiveToChapter?.ToString() ?? "" }; var reveal = new TextBox { Text = f.RevealedChapter?.ToString() ?? "" }; var note = new TextBox { AcceptsReturn = true, Height = 70 };
        Add("Hiệu lực từ chương", from); Add("Đến chương (trống = chưa biết)", to); Add("Tiết lộ từ chương", reveal);
        var bindings = new Dictionary<string, ComboBox>();
        foreach (var identity in SemanticQualityAuditor.Identities(f).Where(x => x.EntityId is null && x.LocalId.Length > 0).DistinctBy(x => x.LocalId))
        {
            var choices = new[] { new MemoryChoice(null, "Giữ danh tính mới riêng biệt — không tự gộp") }.Concat(memory.Entities.Where(x => x.Kind == identity.Kind)
                .Select(x => new MemoryChoice(x.Id, $"{x.Label} · {x.Kind} · {x.Id}"))).ToArray();
            var box = new ComboBox { ItemsSource = choices, DisplayMemberPath = nameof(MemoryChoice.Label), SelectedIndex = 0 }; bindings[identity.LocalId] = box;
            Add($"{identity.Name} ({identity.Kind}, local {identity.LocalId}) — chỉ liên kết nếu bạn xác nhận", box);
        }
        Add("Ghi chú nguồn/chủ thể/thời điểm đã kiểm tra", note); var error = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(error);
        var buttons = new WrapPanel(); var cancel = new Button { Content = "Hủy", IsCancel = true }; var save = new Button { Content = "Lưu " + action, IsDefault = true }; buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        save.Click += (_, _) =>
        {
            try
            {
                int? Parse(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : int.TryParse(box.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : throw new ArgumentException("Mốc chương phải dương hoặc trống.");
                var time = new MemoryTimeline(Parse(from), Parse(to), null, Parse(reveal)); time.Validate();
                if (action == MemoryReviewStatus.Confirmed && (time.EffectiveFromChapter is null || time.RevealedAtChapter is null)) throw new ArgumentException("Confirmed cần xác nhận riêng mốc hiệu lực và tiết lộ.");
                if (string.IsNullOrWhiteSpace(note.Text) || note.Text.Length > 2000) throw new ArgumentException("Cần ghi chú, tối đa 2.000 ký tự.");
                Result = new(time, note.Text.Trim(), bindings.Where(x => x.Value.SelectedItem is MemoryChoice { Id: not null }).ToDictionary(x => x.Key, x => ((MemoryChoice)x.Value.SelectedItem).Id!.Value)); DialogResult = true;
            }
            catch (Exception ex) when (ex is ArgumentException or System.IO.InvalidDataException) { error.Text = ex.Message; }
        };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        void Add(string label, Control box) { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 2) }); panel.Children.Add(box); }
    }
}
