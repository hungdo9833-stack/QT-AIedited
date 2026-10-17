using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Wpf;

public sealed class BookIntelligenceReviewWindow : Window
{
    internal static Guid GeminiFlashPoolProfileId { get; } = Guid.Parse("c528d3a8-147a-4b7d-8471-e4cbe39d6f38");
    private readonly ProjectSession _session;
    private readonly ProjectRepository _repository;
    private ProjectResources _resources;
    private readonly IReadOnlyList<OllamaBackendProfile> _profiles;
    private readonly Func<IntelligenceCharacterSuggestion, CharacterProfile?> _editCharacter;
    private readonly Action<ProjectResources> _resourcesChanged;
    private readonly Func<CorpusEvidence, Task> _openEvidence;
    private readonly Action<BookIntelligenceState, bool> _stateChanged;
    private BookIntelligenceState? _state;
    private CancellationTokenSource? _cancellation;

    private readonly ComboBox _profile = new() { Width = 210, DisplayMemberPath = nameof(OllamaBackendProfile.Name) };
    private readonly ComboBox _model = new() { Width = 240, IsEditable = false };
    private readonly Button _run = new() { Content = "Bắt đầu khảo sát" };
    private readonly Button _cancel = new() { Content = "Tạm dừng", IsEnabled = false };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Height = 5, Minimum = 0, Maximum = 1 };
    private readonly DataGrid _queue = new() { AutoGenerateColumns = false, IsReadOnly = true,
        CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Extended };
    private readonly TextBox _details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 150 };
    private readonly ListBox _evidence = new() { DisplayMemberPath = nameof(EvidenceRow.Label), MinHeight = 100 };
    private readonly ComboBox _mergeTarget = new() { Width = 220, DisplayMemberPath = nameof(CharacterProfile.VietnameseName),
        SelectedValuePath = nameof(CharacterProfile.Id) };
    private readonly Button _save = new() { Content = "Chỉnh sửa rồi lưu" };
    private readonly Button _merge = new() { Content = "Gộp vào nhân vật đã có" };
    private readonly Button _later = new() { Content = "Xem xét sau" };
    private readonly Button _reject = new() { Content = "Không phải / Bỏ đề xuất" };
    private readonly Button _open = new() { Content = "Mở đoạn nguyên tác" };
    private readonly ComboBox _scanMode = new() { Width = 170, ItemsSource = Enum.GetValues<IntelligenceScanMode>() };
    private readonly CheckBox _translationContext = new() { Content = "Dùng Knowledge Context khi dịch (thử nghiệm)", Margin = new Thickness(10, 4, 0, 4) };
    private readonly TextBox _search = new() { Width = 190, ToolTip = "Tìm tên Trung, Việt hoặc bí danh" };
    private readonly ComboBox _kindFilter = new() { Width = 145, ItemsSource = new[] { "Tất cả loại", "Nhân vật", "Quan hệ", "Suy luận", "Glossary" }, SelectedIndex = 0 };
    private readonly ComboBox _statusFilter = new() { Width = 145, ItemsSource = new[] { "Tất cả trạng thái", "Chờ duyệt", "Đã xử lý", "Xung đột/Stale" }, SelectedIndex = 0 };
    private bool _loading;

    public BookIntelligenceReviewWindow(ProjectSession session, ProjectRepository repository,
        ProjectResources resources, IReadOnlyList<OllamaBackendProfile> profiles,
        Func<IntelligenceCharacterSuggestion, CharacterProfile?> editCharacter,
        Action<ProjectResources> resourcesChanged, Func<CorpusEvidence, Task> openEvidence,
        Action<BookIntelligenceState, bool> stateChanged)
    {
        _session = session;
        _repository = repository;
        _resources = resources.Clone();
        _profiles =
        [
            CreateGeminiFlashPoolProfile(),
            .. profiles.Where(profile => profile.Id != GeminiFlashPoolProfileId)
        ];
        _editCharacter = editCharacter;
        _resourcesChanged = resourcesChanged;
        _openEvidence = openEvidence;
        _stateChanged = stateChanged;
        Title = "QT Studio · Khảo sát nhân vật & Glossary bằng AI";
        Width = 1220; Height = 780; MinWidth = 900; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = BuildContent();
        Loaded += OnLoaded;
    }

    private UIElement BuildContent()
    {
        var root = new DockPanel { Margin = new Thickness(14) };
        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top);
        header.Children.Add(new TextBlock { Text = "Khảo sát nguyên tác bằng AI", FontSize = 25, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock
        {
            Text = "CPU đọc toàn bộ OriginalText; AI chỉ nhận các đoạn có tín hiệu. Mọi đề xuất phải được bạn xác nhận trước khi lưu.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8)
        });
        var controls = new WrapPanel();
        controls.Children.Add(new TextBlock { Text = "Backend khảo sát", VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(_profile);
        controls.Children.Add(new TextBlock { Text = "Model độc lập", VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(_model); controls.Children.Add(new TextBlock { Text = "Chế độ", VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(_scanMode); controls.Children.Add(_run); controls.Children.Add(_cancel);
        controls.Children.Add(_translationContext);
        var filters = new WrapPanel { Margin = new Thickness(0, 5, 0, 3) };
        filters.Children.Add(new TextBlock { Text = "Lọc", VerticalAlignment = VerticalAlignment.Center });
        filters.Children.Add(_kindFilter); filters.Children.Add(_statusFilter); filters.Children.Add(_search);
        header.Children.Add(filters);
        header.Children.Add(controls); header.Children.Add(_progress); header.Children.Add(_summary);
        root.Children.Add(header);

        var footer = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        footer.Children.Add(_save); footer.Children.Add(_mergeTarget); footer.Children.Add(_merge);
        footer.Children.Add(_later); footer.Children.Add(_reject); footer.Children.Add(_open);
        root.Children.Add(footer);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        _queue.Columns.Add(new DataGridTextColumn { Header = "Loại", Binding = new Binding(nameof(ReviewRow.Kind)), Width = 95 });
        _queue.Columns.Add(new DataGridTextColumn { Header = "Đề xuất", Binding = new Binding(nameof(ReviewRow.Title)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _queue.Columns.Add(new DataGridTextColumn { Header = "Mức tin cậy", Binding = new Binding(nameof(ReviewRow.Confidence)), Width = 105 });
        _queue.Columns.Add(new DataGridTextColumn { Header = "Trạng thái", Binding = new Binding(nameof(ReviewRow.Status)), Width = 110 });
        _queue.Columns.Add(new DataGridTextColumn { Header = "Nguồn", Binding = new Binding(nameof(ReviewRow.Origin)), Width = 85 });
        _queue.Columns.Add(new DataGridTextColumn { Header = "Chứng cứ", Binding = new Binding(nameof(ReviewRow.EvidenceCount)), Width = 75 });
        _queue.SelectionChanged += (_, _) => ShowSelection();
        grid.Children.Add(_queue);
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.SlateGray };
        Grid.SetColumn(splitter, 1); grid.Children.Add(splitter);
        var right = new Grid(); right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.Children.Add(_details);
        var evidenceTitle = new TextBlock { Text = "Chứng cứ (ChapterId · UTF-16)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 3) };
        Grid.SetRow(evidenceTitle, 1); right.Children.Add(evidenceTitle); Grid.SetRow(_evidence, 2); right.Children.Add(_evidence);
        Grid.SetColumn(right, 2); grid.Children.Add(right); root.Children.Add(grid);

        _run.Click += async (_, _) => await RunAsync();
        _cancel.Click += (_, _) => _cancellation?.Cancel();
        _save.Click += async (_, _) => await SaveSelectedAsync();
        _merge.Click += async (_, _) => await MergeSelectedAsync();
        _later.Click += async (_, _) => await SetStatusAsync(IntelligenceReviewStatus.ReviewLater);
        _reject.Click += async (_, _) => await SetStatusAsync(IntelligenceReviewStatus.Rejected);
        _open.Click += async (_, _) => await OpenSelectedEvidenceAsync();
        _evidence.MouseDoubleClick += async (_, _) => await OpenSelectedEvidenceAsync();
        _translationContext.Click += async (_, _) => await SaveContextFeatureFlagAsync();
        _profile.SelectionChanged += (_, _) => SelectSurveyBackend();
        _model.SelectionChanged += (_, _) => { if (!_loading && _profile.SelectedItem is OllamaBackendProfile p && p.Id == GeminiFlashPoolProfileId) ShowGeminiConfiguration(); };
        _search.TextChanged += (_, _) => RefreshQueue();
        _kindFilter.SelectionChanged += (_, _) => RefreshQueue();
        _statusFilter.SelectionChanged += (_, _) => RefreshQueue();
        return root;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _loading = true;
            IntelligenceCommitResult reconciled = await new BookIntelligenceReviewService(_session).ReconcileAsync();
            _state = reconciled.State; _resources = reconciled.Resources;
            // Idempotent, safe normalization of pending results from earlier survey runs.
            // Never modify accepted or rejected user decisions.
            if (RelationshipLabelNormalizer.NormalizePending(_state))
                _state = await new BookIntelligenceStore(_session).SaveAsync(_state);
            ProjectTranslationSettings translation = await new ProjectSettingsStore(_session).LoadTranslationAsync();
            _translationContext.IsChecked = translation.BookKnowledgeContextEnabled;
            _scanMode.SelectedItem = _state.ScanMode;
            bool stateUsesGemini = _state.AnalyzerModel?.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase) == true;
            bool hasGeminiKey = !string.IsNullOrWhiteSpace(GeminiApiKeyVault.ResolveForSurvey());
            OllamaBackendProfile selected = (stateUsesGemini || hasGeminiKey
                    ? _profiles.FirstOrDefault(p => p.Id == GeminiFlashPoolProfileId)
                    : _profiles.FirstOrDefault(p => p.Id == _state.AnalyzerProfileId))
                ?? _profiles.FirstOrDefault(p => p.Id == OllamaBackendProfile.DefaultLocalId)
                ?? _profiles.First();
            _profile.ItemsSource = _profiles; _profile.SelectedItem = selected;
            PopulateModelDropdown(selected, stateUsesGemini ? _state.AnalyzerModel! : selected.Model);
            RefreshQueue();
            if (selected.Id == GeminiFlashPoolProfileId) ShowGeminiConfiguration();
        }
        catch (Exception ex) { ShowFailure(ex); }
        finally { _loading = false; }
    }

    private async Task RunAsync()
    {
        if (_state is null || _profile.SelectedItem is not OllamaBackendProfile profile || string.IsNullOrWhiteSpace(_model.Text)) return;
        SetBusy(true); _cancellation = new CancellationTokenSource();
        try
        {
            var store = new BookIntelligenceStore(_session);
            _summary.Text = "Đang quét 100% nguyên tác bằng CPU…";
            _state.AnalyzerProfileId = profile.Id; _state.AnalyzerModel = _model.Text.Trim();
            _state.ScanMode = _scanMode.SelectedItem is IntelligenceScanMode mode ? mode : IntelligenceScanMode.FastAdaptive;
            var indexProgress = new Progress<CorpusIndexProgress>(p =>
            {
                _progress.Maximum = Math.Max(1, p.TotalChapters); _progress.Value = p.ProcessedChapters;
                _summary.Text = $"CPU: {p.ProcessedChapters}/{p.TotalChapters} chương · quét lại {p.RescannedChapters} · {p.EvidenceCount} đoạn chứng cứ.";
            });
            _state = await new ProjectCorpusIndexer(_repository).IndexAsync(_session, _state, store,
                _resources.Characters, indexProgress, _cancellation.Token);
            _summary.Text = "Đã lập chỉ mục. Đang phân tích các đoạn có tín hiệu bằng model khảo sát…";
            bool isGemini = profile.Id == GeminiFlashPoolProfileId ||
                _model.Text.Trim().StartsWith("gemini-", StringComparison.OrdinalIgnoreCase);
            if (!isGemini && profile.Kind == OllamaBackendKind.Remote && !profile.RemoteDisclosureAccepted)
                throw new InvalidOperationException("Profile Remote chưa xác nhận quyền gửi các đoạn nguyên tác để khảo sát.");
            if (isGemini && MessageBox.Show(this,
                "Khảo sát bằng Gemini sẽ gửi các đoạn nguyên tác và thông tin nhân vật liên quan đến Google API. Tiếp tục?",
                "Xác nhận gửi dữ liệu khảo sát lên cloud", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            ModelOptions baseOptions = (profile.Runtime ?? AiRuntimeProfile.CreateDefault(profile.Model)).ModelOptions;
            ModelOptions options = baseOptions with
            {
                Model = _model.Text.Trim(),
                Temperature = Math.Min(0.2, baseOptions.Temperature),
                ContextSize = isGemini ? Math.Max(65_536, baseOptions.ContextSize) : baseOptions.ContextSize,
                MaximumOutputTokens = isGemini ? Math.Max(8192, baseOptions.MaximumOutputTokens) : baseOptions.MaximumOutputTokens
            };
            var aiProgress = new Progress<AnalyzerProgress>(p =>
            {
                _progress.Maximum = Math.Max(1, p.TotalEvidence); _progress.Value = p.ProcessedEvidence;
                _summary.Text = $"AI: {p.ProcessedEvidence}/{p.TotalEvidence} đoạn · {p.PendingSuggestions} đề xuất chờ duyệt.";
            });

            using var httpHandler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(httpHandler) { Timeout = Timeout.InfiniteTimeSpan };
            IOllamaBackendClient? ollama = null;
            GeminiSurveyPoolClient? gemini = null;
            try
            {
                IOllamaClient backend;
                if (isGemini)
                {
                    string apiKey = GeminiApiKeyVault.ResolveForSurvey();
                    if (string.IsNullOrWhiteSpace(apiKey))
                        throw new InvalidOperationException(
                            "Chưa có Gemini API key. Vào Cài đặt → AI & hiệu năng → Gemini API để nhập và lưu key.");
                    var models = GeminiSurveyPoolClient.ModelsFromSettings(
                        Environment.GetEnvironmentVariable("QTSTUDIO_GEMINI_SURVEY_MODELS"));
                    // A deliberate dropdown choice sends requests ONLY to that model.
                    // In particular, Lite quota is never used without explicit selection.
                    models = GeminiSurveyPoolClient.ModelsFromSettings(options.Model);
                    if (!models.Any(model => string.Equals(model.Model, options.Model,
                            StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException(
                            "Model đã chọn không nằm trong Gemini Survey Pool. Chọn một model trong danh sách.");
                    _summary.Text = "Flash Survey Pool: " + string.Join(", ", models.Select(model => model.Model));
                    // Google project scope, not QT Studio book ProjectId.
                    Guid quotaScope = GeminiSurveyProjectScope.Resolve(apiKey,
                        Environment.GetEnvironmentVariable("QTSTUDIO_GEMINI_PROJECT_ID"));
                    int? maximumRequests = GeminiSurveyPoolClient.MaximumRequestsFromSettings(
                        Environment.GetEnvironmentVariable("QTSTUDIO_GEMINI_SURVEY_MAX_REQUESTS"));
                    gemini = new GeminiSurveyPoolClient(http, apiKey, quotaScope, models, maximumRequests);
                    backend = gemini;
                    _state.AnalyzerProfileId = null;
                    _state.AnalysisConcurrency = gemini.RecommendedConcurrency;
                }
                else
                {
                    ollama = OllamaClient.Create(profile);
                    backend = ollama;
                }
                _state = await new AdaptiveBookIntelligenceAnalyzer(backend).AnalyzeAsync(_session, _state, store,
                    _resources, options, aiProgress, _cancellation.Token);
            }
            finally { ollama?.Dispose(); gemini?.Dispose(); }
            var scenes = new SceneContextService();
            foreach (ProjectChapterEntry entry in _session.Manifest.Chapters.Where(x => x.DeletedAt is null).OrderBy(x => x.Number))
            {
                ChapterDocument chapter = (await _repository.LoadChapterAsync(_session, entry.ChapterId, _cancellation.Token)).Chapter;
                scenes.RebuildChapter(_state, chapter, _resources);
            }
            _state = await store.SaveAsync(_state, _cancellation.Token);
            _stateChanged(_state, _translationContext.IsChecked == true);
            RefreshQueue();
            ReviewRow? newest = Rows().FirstOrDefault(x => x.Status == IntelligenceReviewStatus.Pending.ToString());
            _summary.Text = newest is null ? "Khảo sát hoàn tất; không có đề xuất mới có chứng cứ."
                : $"Phát hiện {newest.Kind.ToLowerInvariant()} mới: {newest.Title}. Có {_state.PendingCount} mục chờ duyệt.";
        }
        catch (OperationCanceledException) { _summary.Text = "Đã tạm dừng. Checkpoint đã hoàn thành được giữ để tiếp tục lần sau."; }
        catch (Exception ex) { ShowFailure(ex); }
        finally { SetBusy(false); _cancellation?.Dispose(); _cancellation = null; }
    }

    private async Task SaveSelectedAsync()
    {
        if (_state is null || _queue.SelectedItem is not ReviewRow row) return;
        try
        {
            var review = new BookIntelligenceReviewService(_session);
            IntelligenceCommitResult result;
            switch (row.Value)
            {
                case IntelligenceCharacterSuggestion character:
                    CharacterProfile? edited = _editCharacter(character);
                    if (edited is null) return;
                    result = await review.AcceptCharacterAsync(_state, character.Id, edited, _resources.Revision);
                    break;
                case IntelligenceGlossarySuggestion glossary:
                    string translated = AskText("Xác nhận Glossary", glossary.Chinese, glossary.Vietnamese);
                    if (translated.Length == 0) return;
                    result = await review.AcceptGlossaryAsync(_state, glossary.Id, translated, _resources.Revision);
                    break;
                case IntelligenceRelationshipSuggestion relationship:
                    if (!EditRelationship(relationship)) return;
                    result = await review.AcceptRelationshipAsync(_state, relationship.Id, _resources.Revision);
                    break;
                case DerivedRelationship derived:
                    result = await review.AcceptDerivedRelationshipAsync(_state, derived.Id);
                    break;
                default: return;
            }
            ApplyCommit(result); _summary.Text = "Đã lưu mục được xác nhận. Các mục khác vẫn ở hàng chờ.";
        }
        catch (Exception ex) { ShowFailure(ex); }
    }

    private static OllamaBackendProfile CreateGeminiFlashPoolProfile()
    {
        const string model = "gemini-3.8-flash";
        return new OllamaBackendProfile
        {
            Id = GeminiFlashPoolProfileId,
            Name = "Gemini Flash Pool",
            Kind = OllamaBackendKind.Remote,
            Model = model,
            Runtime = AiRuntimeProfile.CreateDefault(model),
            RemoteDisclosureAccepted = true
        };
    }

    private void PopulateModelDropdown(OllamaBackendProfile profile, string? preferred)
    {
        string[] choices = profile.Id == GeminiFlashPoolProfileId
            ? ["gemini-3.8-flash", "gemini-3.6-flash", "gemini-3.5-flash-lite", "gemini-3.1-flash-lite"]
            : [profile.Model];
        // A saved or externally configured model is not silently replaced.
        // Nonstandard models are only selectable via their backend's configured profile.
        string selected = choices.FirstOrDefault(x => string.Equals(x, preferred,
            StringComparison.OrdinalIgnoreCase)) ?? choices[0];
        _model.ItemsSource = choices;
        _model.SelectedItem = selected;
    }

    private void SelectSurveyBackend()
    {
        if (_loading || _profile.SelectedItem is not OllamaBackendProfile profile ||
            profile.Id != GeminiFlashPoolProfileId) return;
        try
        {
            PopulateModelDropdown(profile, _model.SelectedItem as string);
            ShowGeminiConfiguration();
        }
        catch (Exception ex) { ShowFailure(ex); }
    }

    private void ShowGeminiConfiguration()
    {
        IReadOnlyList<GeminiSurveyModel> models = GeminiSurveyPoolClient.ModelsFromSettings(_model.Text);
        int? maximumRequests = GeminiSurveyPoolClient.MaximumRequestsFromSettings(
            Environment.GetEnvironmentVariable("QTSTUDIO_GEMINI_SURVEY_MAX_REQUESTS"));
        bool hasKey = !string.IsNullOrWhiteSpace(GeminiApiKeyVault.ResolveForSurvey());
        _summary.Text = $"Model khảo sát: {string.Join(", ", models.Select(x => x.Model))} · " +
            $"API key: {(hasKey ? "đã cấu hình" : "chưa cấu hình")} · " +
            $"ngân sách: {(maximumRequests is int limit ? $"tối đa {limit} HTTP request/lần chạy" : "chưa đặt giới hạn")}.";
    }

    private async Task MergeSelectedAsync()
    {
        if (_state is null || _queue.SelectedItem is not ReviewRow { Value: IntelligenceCharacterSuggestion suggestion } ||
            _mergeTarget.SelectedValue is not Guid target) return;
        try { ApplyCommit(await new BookIntelligenceReviewService(_session).MergeCharacterAsync(_state,
            suggestion.Id, target, _resources.Revision)); }
        catch (Exception ex) { ShowFailure(ex); }
    }

    private async Task SetStatusAsync(IntelligenceReviewStatus status)
    {
        if (_state is null || _queue.SelectedItems.Count == 0) return;
        try
        {
            ReviewRow[] selected = _queue.SelectedItems.Cast<ReviewRow>().Where(x => x.Value is not DerivedRelationship d ||
                d.ValidityStatus == InferenceValidityStatus.Valid).ToArray();
            foreach (ReviewRow row in selected)
                _state = await new BookIntelligenceReviewService(_session).SetReviewStatusAsync(_state, row.Id, status);
            RefreshQueue();
        }
        catch (Exception ex) { ShowFailure(ex); }
    }

    private async Task OpenSelectedEvidenceAsync()
    {
        if (_evidence.SelectedItem is EvidenceRow item) await _openEvidence(item.Evidence);
    }

    private void ApplyCommit(IntelligenceCommitResult result)
    {
        _resources = result.Resources; _state = result.State; _resourcesChanged(_resources);
        _stateChanged(_state, _translationContext.IsChecked == true); RefreshQueue();
    }

    private async Task SaveContextFeatureFlagAsync()
    {
        if (_loading || _state is null) return;
        try
        {
            var settings = new ProjectSettingsStore(_session);
            ProjectTranslationSettings translation = await settings.LoadTranslationAsync();
            translation.BookKnowledgeContextEnabled = _translationContext.IsChecked == true;
            await settings.SaveTranslationAsync(translation);
            _stateChanged(_state, translation.BookKnowledgeContextEnabled);
            _summary.Text = translation.BookKnowledgeContextEnabled
                ? "Knowledge Context đã bật. Chỉ dữ liệu đã duyệt và đúng timeline được thêm vào request mới."
                : "Knowledge Context đã tắt. Pipeline dịch dùng hành vi cũ.";
        }
        catch (Exception ex) { _translationContext.IsChecked = false; ShowFailure(ex); }
    }

    private void RefreshQueue()
    {
        _queue.ItemsSource = Rows();
        _mergeTarget.ItemsSource = _resources.Characters.Profiles.OrderBy(x => x.VietnameseName).ToList();
        if (_state is not null) _summary.Text = $"{_state.Evidence.Count(x => x.IsCurrent)} đoạn chứng cứ · {_state.PendingCount} đề xuất chờ duyệt · AutoApplyVerified = false.";
        ShowSelection();
    }

    private List<ReviewRow> Rows()
    {
        if (_state is null) return [];
        IEnumerable<ReviewRow> rows = _state.Characters.Select(x => new ReviewRow(x.Id, "Nhân vật", $"{x.ChineseName} → {x.VietnameseName}", x.Confidence.ToString(), x.Status.ToString(), "AI trực tiếp", x.EvidenceIds.Count, x))
            .Concat(_state.Relationships.Select(x => new ReviewRow(x.Id, "Quan hệ", $"{x.SpeakerChineseName} → {x.ListenerChineseName}: {RelationshipLabelNormalizer.Display(x.Relationship)}", x.Confidence.ToString(), x.Status.ToString(), "AI trực tiếp", x.EvidenceIds.Count, x)))
            .Concat(_state.DerivedRelationships.Select(x => new ReviewRow(x.Id, "Suy luận", $"{CharacterName(x.SubjectCharacterId)} → {CharacterName(x.TargetCharacterId)}: {x.OutputRelation}",
                x.ValidityStatus.ToString(), x.Status.ToString(), x.RuleId, x.SupportingEvidenceIds.Count, x)))
            .Concat(_state.Glossary.Select(x => new ReviewRow(x.Id, "Glossary", $"{x.Chinese} → {x.Vietnamese}", x.Confidence.ToString(), x.Status.ToString(), x.Scope.ToString(), x.EvidenceIds.Count, x)));
        // Keep auto-merged items available under "Đã xử lý", but avoid cluttering
        // the default review list with labels that are already represented elsewhere.
        if (_statusFilter.SelectedIndex == 0)
            rows = rows.Where(x => x.Value is not IntelligenceRelationshipSuggestion r ||
                !(r.Status == IntelligenceReviewStatus.Merged &&
                  r.Reason.Contains("Trùng nhãn đã chuẩn hóa;", StringComparison.Ordinal)));
        string query = _search.Text.Trim();
        if (query.Length > 0) rows = rows.Where(x => x.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            x.Value is IntelligenceCharacterSuggestion c && c.Aliases.Any(a => a.Contains(query, StringComparison.OrdinalIgnoreCase)));
        if (_kindFilter.SelectedIndex > 0 && _kindFilter.SelectedItem is string kind) rows = rows.Where(x => x.Kind == kind);
        rows = _statusFilter.SelectedIndex switch
        {
            1 => rows.Where(x => x.Status.Contains("Pending", StringComparison.OrdinalIgnoreCase) || x.Status.Contains("Review", StringComparison.OrdinalIgnoreCase) || x.Status.Contains("Awaiting", StringComparison.OrdinalIgnoreCase)),
            2 => rows.Where(x => x.Status.Contains("Accepted", StringComparison.OrdinalIgnoreCase) || x.Status.Contains("Merged", StringComparison.OrdinalIgnoreCase) || x.Status.Contains("Rejected", StringComparison.OrdinalIgnoreCase)),
            3 => rows.Where(x => x.Status.Contains("Conflict", StringComparison.OrdinalIgnoreCase) || x.Status.Contains("Stale", StringComparison.OrdinalIgnoreCase) || x.Confidence is "Conflict" or "Stale" or "Ambiguous"),
            _ => rows
        };
        return rows
            .OrderBy(x => x.Status is nameof(IntelligenceReviewStatus.Pending) or nameof(IntelligenceReviewStatus.ReviewLater) ? 0 : 1)
            .ThenBy(x => x.Kind).ThenBy(x => x.Title).ToList();
    }

    private void ShowSelection()
    {
        if (_state is null || _queue.SelectedItem is not ReviewRow row)
        { _details.Text = "Chọn một đề xuất để xem lý do và chứng cứ."; _evidence.ItemsSource = null; return; }
        string reason;
        IEnumerable<Guid> ids;
        switch (row.Value)
        {
            case IntelligenceCharacterSuggestion x:
                reason = $"Tên Trung: {x.ChineseName}\nTên Việt đề xuất: {x.VietnameseName}\nBí danh: {string.Join(", ", x.Aliases)}\nGiới tính: {x.Gender}\nĐại từ: {x.PrimaryNarrativePronoun} / {x.SecondaryNarrativePronoun}\n\n{x.Reason}";
                ids = x.EvidenceIds; break;
            case IntelligenceRelationshipSuggestion x:
                reason = $"{x.SpeakerChineseName} → {x.ListenerChineseName}\nQuan hệ: {RelationshipLabelNormalizer.Display(x.Relationship)}\nXưng hô: {x.SelfReference} → {x.AddresseeReference}\nChiều ngược đề xuất: {x.ReverseSelfReference} → {x.ReverseAddresseeReference}\n\n{x.Reason}";
                ids = x.EvidenceIds; break;
            case IntelligenceGlossarySuggestion x:
                reason = $"{x.Chinese} → {x.Vietnamese}\nLoại: {x.Category}\nPhụ thuộc ngữ cảnh: {(x.ContextDependent ? "Có — cần thận trọng" : "Không")}\n\n{x.Reason}";
                ids = x.EvidenceIds; break;
            case DerivedRelationship x:
                reason = $"Quan hệ suy luận: {CharacterName(x.SubjectCharacterId)} → {CharacterName(x.TargetCharacterId)} = {x.OutputRelation}\nQuy tắc: {x.RuleId}\nĐộ sâu: {x.InferenceDepth}\nValidity: {x.ValidityStatus}\nInput relations: {string.Join(", ", x.InputRelationIds)}\n\n{x.Explanation}\n\nQuan hệ thực tế này không tự tạo cách xưng hô.";
                ids = x.SupportingEvidenceIds; break;
            default: reason = ""; ids = Array.Empty<Guid>(); break;
        }
        _details.Text = reason;
        Dictionary<Guid, CorpusEvidence> map = _state.Evidence.ToDictionary(x => x.Id);
        _evidence.ItemsSource = ids.Where(map.ContainsKey).Select(id => new EvidenceRow(map[id],
            $"{ShortChapter(map[id].ChapterId)} · {map[id].StartUtf16}+{map[id].LengthUtf16} · {Preview(map[id].Excerpt)}")).ToList();
        _merge.IsEnabled = row.Value is IntelligenceCharacterSuggestion;
        _mergeTarget.IsEnabled = _merge.IsEnabled;
    }

    private string CharacterName(Guid id) => _resources.Characters.Profiles.SingleOrDefault(x => x.Id == id)?.VietnameseName
        ?? _state?.CandidateIdentities.SingleOrDefault(x => x.Id == id)?.VietnameseName
        ?? id.ToString("D");

    private bool EditRelationship(IntelligenceRelationshipSuggestion item)
    {
        var panel = new StackPanel { Margin = new Thickness(12) };
        TextBox Field(string label, string value) { panel.Children.Add(new TextBlock { Text = label }); var box = new TextBox { Text = value }; panel.Children.Add(box); return box; }
        TextBox relationship = Field("Quan hệ", item.Relationship), self = Field("Người nói tự xưng", item.SelfReference),
            address = Field("Người nói gọi đối phương", item.AddresseeReference),
            reverseSelf = Field("Chiều ngược tự xưng (để trống nếu chưa xác nhận)", item.ReverseSelfReference),
            reverseAddress = Field("Chiều ngược gọi đối phương", item.ReverseAddresseeReference),
            validFrom = Field("Có hiệu lực từ chương (để trống nếu chưa rõ)", item.ValidFromChapter?.ToString() ?? ""),
            validTo = Field("Có hiệu lực đến chương (để trống nếu chưa rõ)", item.ValidToChapter?.ToString() ?? ""),
            revealedAt = Field("Được tiết lộ từ chương", item.RevealedAtChapter?.ToString() ?? "");
        var save = new Button { Content = "Xác nhận", IsDefault = true }; panel.Children.Add(save);
        var dialog = new Window { Owner = this, Title = "Xác nhận quan hệ & xưng hô", Width = 500, Height = 610,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        save.Click += (_, _) => {
            if (!TryChapter(validFrom.Text, out int? from) || !TryChapter(validTo.Text, out int? to) ||
                !TryChapter(revealedAt.Text, out int? revealed) || from is not null && to is not null && from > to)
            { MessageBox.Show(dialog, "Mốc chương không hợp lệ hoặc chương bắt đầu lớn hơn chương kết thúc.", "QT Studio", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            item.Relationship = relationship.Text.Trim(); item.SelfReference = self.Text.Trim();
            item.AddresseeReference = address.Text.Trim(); item.ReverseSelfReference = reverseSelf.Text.Trim();
            item.ReverseAddresseeReference = reverseAddress.Text.Trim(); item.ValidFromChapter = from;
            item.ValidToChapter = to; item.RevealedAtChapter = revealed; dialog.DialogResult = true; };
        return dialog.ShowDialog() == true;
    }

    private static bool TryChapter(string value, out int? chapter)
    {
        if (string.IsNullOrWhiteSpace(value)) { chapter = null; return true; }
        if (int.TryParse(value.Trim(), out int parsed) && parsed > 0) { chapter = parsed; return true; }
        chapter = null; return false;
    }

    private string AskText(string title, string chinese, string value)
    {
        var box = new TextBox { Text = value, Margin = new Thickness(10), MinWidth = 380 };
        var button = new Button { Content = "Xác nhận lưu", IsDefault = true, Margin = new Thickness(10) };
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = chinese, FontSize = 18, Margin = new Thickness(10, 10, 10, 0) }); panel.Children.Add(box); panel.Children.Add(button);
        var dialog = new Window { Owner = this, Title = title, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        button.Click += (_, _) => dialog.DialogResult = true;
        return dialog.ShowDialog() == true ? box.Text.Trim() : "";
    }

    private void SetBusy(bool busy) { _run.IsEnabled = !busy; _cancel.IsEnabled = busy; _profile.IsEnabled = !busy; _model.IsEnabled = !busy; }
    private void ShowFailure(Exception ex) { _summary.Text = "Lỗi: " + ex.Message + " Dữ liệu đã xác nhận và checkpoint trước đó vẫn được giữ."; }
    private string ShortChapter(Guid id) => _session.Manifest.Chapters.SingleOrDefault(x => x.ChapterId == id) is { } c ? $"Chương {c.Number}" : id.ToString("D");
    private static string Preview(string text) { string one = text.Replace('\r', ' ').Replace('\n', ' ').Trim(); return one.Length <= 90 ? one : one[..90] + "…"; }

    private sealed record ReviewRow(Guid Id, string Kind, string Title, string Confidence, string Status,
        string Origin, int EvidenceCount, object Value);
    private sealed record EvidenceRow(CorpusEvidence Evidence, string Label);
}
