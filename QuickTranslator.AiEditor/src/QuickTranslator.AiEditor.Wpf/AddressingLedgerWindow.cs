using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using QuickTranslator.AiEditor.Core.Addressing;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Wpf;

internal sealed record AddressingRow(AddressingProposal Proposal, string Subject, string Status, string Check, string Summary, string Decision);

/// <summary>
/// Addressing Ledger step 3 (docs/ADDRESSING-LEDGER-DESIGN.vi.md). "Hoàn thành" writes every verified proposal to the
/// Character Project at once and switches the translation lock on (undoable); per character / per pair review is optional,
/// for checking or editing. Locked or conflicting data is never overwritten.
/// </summary>
public sealed class AddressingLedgerWindow : Window
{
    private static readonly (string Label, CharacterGender Value)[] Genders =
        [("Nữ", CharacterGender.Female), ("Nam", CharacterGender.Male), ("Khác", CharacterGender.Other), ("Chưa xác định", CharacterGender.Unknown)];
    private readonly ProjectSession _session;
    private readonly ProjectRepository _repository;
    private readonly Action<ProjectResources> _applyResources;
    private readonly AddressingReviewService _service;
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly CheckBox _lock = new() { Content = "Bật khóa nhất quán Project (trình dịch dùng hồ sơ nhân vật)", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _onlyValid = new() { Content = "Chỉ hiện đề xuất đã kiểm chứng", IsChecked = true, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _reload = new() { Content = "Nạp lại" };
    private readonly TabControl _tabs = new();
    private readonly DataGrid _characters = ProposalGrid(), _pairs = ProposalGrid();
    private readonly ComboBox _phase = new(), _gender = new() { ItemsSource = Genders.Select(g => g.Label).ToArray(), SelectedIndex = 3 };
    private readonly TextBox _vietName = Field(), _narrative = Field(), _secondary = Field(), _self = Field(), _address = Field(), _relationship = Field(), _note = Field();
    private readonly TextBox _forms = new() { AcceptsReturn = true, Height = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap };
    private readonly TextBox _details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel _characterFields = new(), _pairFields = new();
    private static Style Themed(string key) => (Style)Application.Current.FindResource(key);
    private static Button WithGlyph(Button button, string glyph) { ButtonIcon.SetGlyph(button, glyph); return button; }
    private readonly Button _accept = new() { Content = "Chấp nhận & ghi vào hồ sơ" }, _reject = new() { Content = "Bác bỏ", Style = Themed("DangerButton") }, _defer = new() { Content = "Để sau" };
    // "Hoàn thành" is the window's one primary action.
    private readonly Button _complete = WithGlyph(new() { Content = "Hoàn thành: ghi tất cả vào hồ sơ và dùng khi dịch", Style = Themed("PrimaryButton"), Padding = new Thickness(12, 6, 12, 6) }, Glyphs.Accept);
    private readonly Button _undo = WithGlyph(new() { Content = "Hoàn tác lần Hoàn thành gần nhất", Margin = new Thickness(8, 0, 0, 0), IsEnabled = false }, Glyphs.Undo);
    private readonly TextBox _completion = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0) };
    internal AddressingCompletion? Preview { get; private set; }
    internal string CompletionText => _completion.Text;
    // Survey (step 2/2b): one button runs the whole pipeline into the Project's ledger; nothing is written to profiles.
    private readonly IReadOnlyList<OllamaBackendProfile> _profiles;
    private readonly ComboBox _backend = new() { Width = 190, DisplayMemberPath = nameof(OllamaBackendProfile.Name) };
    private readonly ComboBox _model = new() { Width = 190, IsEditable = true, Margin = new Thickness(6, 0, 0, 0) };
    private readonly TextBox _maxRequests = new() { Text = "10", Width = 45, Margin = new Thickness(4, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _survey = WithGlyph(new() { Content = "Khảo sát / chạy tiếp", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) }, Glyphs.Play);
    private readonly Button _stop = WithGlyph(new() { Content = "Dừng", Margin = new Thickness(6, 0, 0, 0), IsEnabled = false }, Glyphs.Stop);
    private readonly TextBlock _surveyStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private CancellationTokenSource? _surveyCancel;
    private IReadOnlyList<CensusSource> _sources = [];
    internal AddressingSurveyWork? Work { get; private set; }
    internal AddressingSurveyResult? LastSurvey { get; private set; }
    internal string SurveyStatus => _surveyStatus.Text;
    /// <summary>Asked before any source text leaves the computer; tests answer it.</summary>
    internal Func<string, bool> ConfirmSend { get; set; }
    /// <summary>Builds the provider for one click with its request limit (Gemini via the key vault, or the selected Ollama profile).</summary>
    internal Func<OllamaBackendProfile, ModelOptions, int, IJsonSchemaOllamaClient> BackendFactory { get; set; }
    private Dictionary<string, AddressingTask> _tasks = [];
    private bool _updatingLock;
    internal AddressingLedgerState State { get; private set; } = new();
    internal ProjectResources? CurrentResources { get; private set; }
    internal string? LastError { get; private set; }
    internal AddressingProposal? Selected => (_tabs.SelectedIndex == 0 ? _characters : _pairs).SelectedItem is AddressingRow row ? row.Proposal : null;

    public AddressingLedgerWindow(ProjectSession session, ProjectRepository repository, Action<ProjectResources> applyResources,
        IReadOnlyList<OllamaBackendProfile>? profiles = null)
    {
        _session = session; _repository = repository; _applyResources = applyResources; _service = new AddressingReviewService(session);
        _profiles = [GeminiSurveyProfile.Create(), .. (profiles ?? []).Where(p => p.Id != GeminiSurveyProfile.Id)];
        ConfirmSend = message => ThemedDialog.Show(this, message, "Sổ xưng hô · khảo sát", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        BackendFactory = (profile, options, maximumRequests) =>
        {
            var budget = new SemanticSurveyBudget(maximumRequests, 100_000_000);
            if (profile.Id != GeminiSurveyProfile.Id) return SemanticSurveyBackend.Ollama(profile, options, budget);
            string key = GeminiApiKeyVault.ResolveForSurvey();
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Nhập Gemini key trong Cài đặt → AI & hiệu năng trước khi khảo sát.");
            return SemanticSurveyBackend.Gemini(key, GeminiSurveyProjectScope.Resolve(key, Environment.GetEnvironmentVariable("QTSTUDIO_GEMINI_PROJECT_ID")), options, budget);
        };
        Title = "QT Studio · Sổ xưng hô"; Width = 1200; Height = 760; MinWidth = 820; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        Content = Build();
        Loaded += async (_, _) => await LoadAsync();
    }

    private static DataGrid ProposalGrid()
    {
        var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single };
        foreach (var (header, path, width, min) in new[] { ("Chủ thể", nameof(AddressingRow.Subject), 1.4, 110.0), ("AI", nameof(AddressingRow.Status), 0.8, 80.0),
                     ("Kiểm chứng", nameof(AddressingRow.Check), 1.2, 90.0), ("Đề xuất", nameof(AddressingRow.Summary), 2.6, 220.0), ("Duyệt", nameof(AddressingRow.Decision), 0.8, 60.0) })
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = new DataGridLength(width, DataGridLengthUnitType.Star), MinWidth = min });
        return grid;
    }
    private static TextBox Field() => new() { Margin = new Thickness(0, 0, 0, 6) };
    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 4, 0, 2) };

    private UIElement Build()
    {
        var root = new DockPanel { Margin = new Thickness(16) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top);

        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "Sổ xưng hô", Style = Themed("WindowTitle") });
        heading.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(_session.Manifest.VietnameseTitle) ? "Kết quả khảo sát" : _session.Manifest.VietnameseTitle,
            Style = Themed("WindowSubtitle")
        });
        header.Children.Add(heading);
        var headerActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        _reload.Margin = new Thickness(0, 0, 8, 0);
        headerActions.Children.Add(_reload);
        var help = WithGlyph(new Button { Width = 32, Height = 32, Padding = new Thickness(0), ToolTip = "Hướng dẫn" }, Glyphs.Question);
        AutomationProperties.SetName(help, "Hướng dẫn");
        headerActions.Children.Add(help);
        Grid.SetColumn(headerActions, 1); header.Children.Add(headerActions);
        top.Children.Add(header);

        var instructions = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 12), Text = "① Khảo sát: CPU tìm nhân vật, lời thoại và cảnh, rồi AI xác định người nói, đại từ kể, tên phụ và xưng hô theo cặp; " +
            "mọi câu trả lời được kiểm chứng với nguyên văn. Mỗi lần bấm gửi tối đa số request đã đặt; bấm lại để chạy tiếp, phần đã xong không gửi lại. " +
            "② Hoàn thành: ghi toàn bộ kết quả đã kiểm chứng vào hồ sơ rồi dịch ngay. Hồ sơ hoặc quy tắc bạn đã khóa / đã có sẵn không bao giờ bị ghi đè; " +
            "mục không tự quyết được chỉ được liệt kê, không chặn Hoàn thành. Có thể hoàn tác. Duyệt từng mục bên dưới là tùy chọn." };
        help.Click += (_, _) => instructions.Visibility = instructions.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        top.Children.Add(instructions);

        static TextBlock StepTitle(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
        static TextBlock FieldLabel(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(3, 0, 3, 3) };
        static Border Card(UIElement content) => new() { Style = Themed("Card"), Margin = new Thickness(0), Child = content };

        var surveyCardContent = new StackPanel();
        surveyCardContent.Children.Add(StepTitle("1 · Khảo sát"));
        var surveyFields = new Grid();
        surveyFields.ColumnDefinitions.Add(new() { Width = new GridLength(5, GridUnitType.Star) });
        surveyFields.ColumnDefinitions.Add(new() { Width = new GridLength(5, GridUnitType.Star) });
        surveyFields.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star) });
        surveyFields.RowDefinitions.Add(new() { Height = GridLength.Auto });
        surveyFields.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var aiLabel = FieldLabel("AI");
        var modelLabel = FieldLabel("Model"); Grid.SetColumn(modelLabel, 1);
        var maximumLabel = FieldLabel("Tối đa"); Grid.SetColumn(maximumLabel, 2);
        surveyFields.Children.Add(aiLabel); surveyFields.Children.Add(modelLabel); surveyFields.Children.Add(maximumLabel);
        _backend.Width = _model.Width = _maxRequests.Width = double.NaN;
        _backend.Margin = new Thickness(0, 0, 6, 0); _model.Margin = new Thickness(0, 0, 6, 0); _maxRequests.Margin = new Thickness(0);
        Grid.SetRow(_backend, 1); surveyFields.Children.Add(_backend);
        Grid.SetRow(_model, 1); Grid.SetColumn(_model, 1); surveyFields.Children.Add(_model);
        Grid.SetRow(_maxRequests, 1); Grid.SetColumn(_maxRequests, 2); surveyFields.Children.Add(_maxRequests);
        surveyCardContent.Children.Add(surveyFields);
        var surveyActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        _survey.Margin = new Thickness(0, 0, 6, 0); _stop.Margin = new Thickness(0);
        surveyActions.Children.Add(_survey); surveyActions.Children.Add(_stop); surveyCardContent.Children.Add(surveyActions);
        var progress = new ProgressBar { Height = 4, IsIndeterminate = true, Margin = new Thickness(0, 8, 0, 0) };
        progress.SetBinding(VisibilityProperty, new Binding(nameof(IsEnabled)) { Source = _stop, Converter = new BooleanToVisibilityConverter() });
        AutomationProperties.SetLiveSetting(_surveyStatus, AutomationLiveSetting.Polite);
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))
            .AddValueChanged(_surveyStatus, (_, _) => Accessibility.Announce(_surveyStatus));
        surveyCardContent.Children.Add(progress); surveyCardContent.Children.Add(_surveyStatus);

        var reviewCardContent = new StackPanel();
        reviewCardContent.Children.Add(StepTitle("2 · Xem lại (không bắt buộc)"));
        reviewCardContent.Children.Add(_summary);
        _onlyValid.Margin = new Thickness(0, 4, 0, 0); _lock.Margin = new Thickness(0, 6, 0, 0);
        reviewCardContent.Children.Add(_onlyValid); reviewCardContent.Children.Add(_lock);

        var completionSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetLiveSetting(completionSummary, AutomationLiveSetting.Polite);
        void ShowCompletionFirstLine()
        {
            completionSummary.Text = _completion.Text.Split(["\r\n", "\n"], StringSplitOptions.None)[0];
            Accessibility.Announce(completionSummary);
        }
        _completion.TextChanged += (_, _) => ShowCompletionFirstLine();
        ShowCompletionFirstLine();
        var finishCardContent = new StackPanel();
        finishCardContent.Children.Add(StepTitle("3 · Hoàn thành"));
        finishCardContent.Children.Add(completionSummary);
        _complete.HorizontalAlignment = HorizontalAlignment.Stretch; _complete.Margin = new Thickness(0, 0, 0, 8);
        _undo.HorizontalAlignment = HorizontalAlignment.Left; _undo.Margin = new Thickness(0);
        finishCardContent.Children.Add(_complete); finishCardContent.Children.Add(_undo);

        var surveyCard = Card(surveyCardContent);
        var reviewCard = Card(reviewCardContent);
        var finishCard = Card(finishCardContent);
        finishCard.SetResourceReference(Border.BorderBrushProperty, "AccentTextBrush");
        finishCard.BorderThickness = new Thickness(1.5);
        var cards = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        void ArrangeCards(bool stacked)
        {
            cards.ColumnDefinitions.Clear(); cards.RowDefinitions.Clear();
            if (stacked)
            {
                cards.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                for (int row = 0; row < 5; row++) cards.RowDefinitions.Add(new() { Height = row is 1 or 3 ? new GridLength(12) : GridLength.Auto });
                Grid.SetRow(surveyCard, 0); Grid.SetColumn(surveyCard, 0);
                Grid.SetRow(reviewCard, 2); Grid.SetColumn(reviewCard, 0);
                Grid.SetRow(finishCard, 4); Grid.SetColumn(finishCard, 0);
            }
            else
            {
                for (int column = 0; column < 5; column++) cards.ColumnDefinitions.Add(new() { Width = column is 1 or 3 ? new GridLength(12) : new GridLength(1, GridUnitType.Star) });
                cards.RowDefinitions.Add(new() { Height = GridLength.Auto });
                Grid.SetRow(surveyCard, 0); Grid.SetColumn(surveyCard, 0);
                Grid.SetRow(reviewCard, 0); Grid.SetColumn(reviewCard, 2);
                Grid.SetRow(finishCard, 0); Grid.SetColumn(finishCard, 4);
            }
        }
        cards.Children.Add(surveyCard); cards.Children.Add(reviewCard); cards.Children.Add(finishCard);
        ArrangeCards(Width < 1100);
        SizeChanged += (_, e) => ArrangeCards(e.NewSize.Width < 1100);
        top.Children.Add(cards);

        var completionDetails = new Expander { Header = "Mục được giữ lại · không chặn Hoàn thành", IsExpanded = false, Content = _completion, Margin = new Thickness(0, 0, 0, 12) };
        completionDetails.SetResourceReference(BackgroundProperty, "WarningSubtleBrush");
        _completion.SetResourceReference(Control.BackgroundProperty, "WarningSubtleBrush");
        top.Children.Add(completionDetails);
        root.Children.Add(top);

        var body = new Grid();
        body.ColumnDefinitions.Add(new() { Width = new GridLength(3, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star) });
        AutomationProperties.SetName(_characters, "Danh sách đề xuất nhân vật");
        AutomationProperties.SetName(_pairs, "Danh sách cặp xưng hô");
        _tabs.Items.Add(new TabItem { Header = "Nhân vật (lời kể + cách gọi)", Content = _characters });
        _tabs.Items.Add(new TabItem { Header = "Cặp xưng hô (lời thoại)", Content = _pairs });
        body.Children.Add(_tabs);

        var editor = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        editor.Children.Add(Label("Giai đoạn áp dụng mặc định (giai đoạn khác được ghi chú cho bản dịch theo chương sau này)")); editor.Children.Add(_phase);
        foreach (var e in new UIElement[] { Label("Giới tính"), _gender, Label("Tên tiếng Việt (tên đầy đủ)"), _vietName, Label("Đại từ kể chính (khóa)"), _narrative,
                     Label("Đại từ kể phụ"), _secondary, Label("Cách gọi giữ lại · mỗi dòng: chữ Hán=tiếng Việt (chỉ những dòng AI đề xuất)"), _forms })
            _characterFields.Children.Add(e);
        foreach (var e in new UIElement[] { Label("Người nói tự xưng"), _self, Label("Người nói gọi người nghe"), _address, Label("Nhãn quan hệ"), _relationship })
            _pairFields.Children.Add(e);
        editor.Children.Add(_characterFields); editor.Children.Add(_pairFields);
        editor.Children.Add(Label("Ghi chú duyệt")); editor.Children.Add(_note);
        var actions = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        actions.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var normalActions = new StackPanel { Orientation = Orientation.Horizontal };
        _accept.Margin = new Thickness(0, 0, 6, 0); _defer.Margin = new Thickness(0);
        normalActions.Children.Add(_accept); normalActions.Children.Add(_defer); actions.Children.Add(normalActions);
        _reject.Margin = new Thickness(16, 0, 0, 0); Grid.SetColumn(_reject, 1); actions.Children.Add(_reject);
        editor.Children.Add(actions);
        editor.Children.Add(Label("Lý do, lỗi kiểm chứng và mẫu nguyên văn"));
        _details.Height = 220; editor.Children.Add(_details);
        var scroll = new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 1); body.Children.Add(scroll);
        root.Children.Add(body);

        _reload.Click += async (_, _) => await LoadAsync();
        _onlyValid.Click += (_, _) => Refresh();
        _tabs.SelectionChanged += (_, e) => { if (e.Source == _tabs) ShowSelection(); };
        _characters.SelectionChanged += (_, _) => ShowSelection(); _pairs.SelectionChanged += (_, _) => ShowSelection();
        _accept.Click += async (_, _) => await AcceptAsync(); _reject.Click += async (_, _) => await DecideAsync(AddressingDecision.Rejected);
        _defer.Click += async (_, _) => await DecideAsync(AddressingDecision.Deferred);
        _lock.Click += async (_, _) => await SetLockAsync(_lock.IsChecked == true);
        _complete.Click += async (_, _) => await CompleteAsync();
        _undo.Click += async (_, _) => await UndoAsync(discardLaterEdits: false);
        _backend.ItemsSource = _profiles;
        _backend.SelectionChanged += (_, _) => { PopulateModels(); OnSurveyModelChanged(); };
        _model.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(OnSurveyModelChanged); // Text follows the selection after this event.
        _model.LostKeyboardFocus += (_, _) => OnSurveyModelChanged();
        _backend.SelectedIndex = 0;
        _survey.Click += async (_, _) => await SurveyAsync();
        _stop.Click += (_, _) => _surveyCancel?.Cancel();
        ShowSelection();
        return root;
    }

    internal async Task LoadAsync()
    {
        await Run(async () =>
        {
            State = await _service.LoadAsync();
            CurrentResources = await new ProjectResourceStore(_session).LoadAsync();
            // Re-plan locally (CPU, no AI): what is left to survey, and the samples each task was sent with.
            _sources = await EntityMemoryProjectInputs.LoadSourcesAsync(_session, _repository);
            var (state, resources, sources) = (State, CurrentResources, _sources);
            Work = await Task.Run(() => AddressingSurveyRunner.Plan(_session.Manifest.ProjectId, sources, resources, state));
            _tasks = Work.Scenes.Concat(Work.Tasks).GroupBy(t => t.TaskId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            ShowWork(Work);
            _updatingLock = true; _lock.IsChecked = CurrentResources.Characters.ConsistencyLockEnabled; _updatingLock = false;
            await RefreshCompletionAsync();
            Refresh();
        }, "Đã nạp sổ xưng hô.");
    }

    private void Refresh()
    {
        AddressingRow Row(AddressingProposal p)
        {
            string summary = p.Kind == AddressingTaskKind.Character
                ? $"{GenderLabel(p.Gender)} · " + string.Join("; ", p.Phases.Select(x => $"{Range(x)} {x.NarrativeVi}")) + (p.NameForms.Count > 0 ? " · " + string.Join(", ", p.NameForms.Select(f => $"{f.Zh}={f.Vi}")) : "")
                : $"{p.Relationship} · " + string.Join("; ", p.Phases.Select(x => $"{Range(x)} xưng {x.SelfVi} / gọi {x.AddressVi}"));
            string decision = State.LatestReview(p.Id)?.Decision switch { AddressingDecision.Accepted => "Đã ghi", AddressingDecision.Rejected => "Bác bỏ", AddressingDecision.Deferred => "Để sau", _ => "Chờ" } + (State.LatestReview(p.Id)?.Note == "Tự động · Hoàn thành" ? " (tự động)" : "");
            return new(p, p.Listener is null ? p.Subject : $"{p.Subject} → {p.Listener}", p.Status, p.EvidenceValid ? "✅ hợp lệ" : "❌ " + string.Join(", ", p.Issues), summary, decision);
        }
        bool onlyValid = _onlyValid.IsChecked == true;
        Guid? keep = Selected?.Id; // Keep the reviewer's place after an action.
        var visible = State.Proposals.Where(p => !onlyValid || p.EvidenceValid).OrderBy(p => p.Subject, StringComparer.Ordinal).ThenBy(p => p.Listener, StringComparer.Ordinal).ToArray();
        foreach (var (grid, kind) in new[] { (_characters, AddressingTaskKind.Character), (_pairs, AddressingTaskKind.Pair) })
        {
            var rows = visible.Where(p => p.Kind == kind).Select(Row).ToArray();
            grid.ItemsSource = rows;
            grid.SelectedItem = rows.FirstOrDefault(r => r.Proposal.Id == keep);
        }
        // Scene proposals are evidence for pair tasks only; they are counted apart and never reviewed here.
        var reviewable = State.Proposals.Where(p => p.Kind != AddressingTaskKind.Scene).ToArray();
        int pending = reviewable.Count(p => p.EvidenceValid && State.LatestReview(p.Id)?.Decision is null or AddressingDecision.Reverted);
        int scenes = State.Proposals.Count(p => p.Kind == AddressingTaskKind.Scene && p.EvidenceValid);
        _summary.Text = $"{reviewable.Length} đề xuất ({reviewable.Count(p => p.EvidenceValid)} đã kiểm chứng, {pending} chờ duyệt) · {State.Reviews.Count} quyết định · " +
            (scenes > 0 ? $"{scenes} cảnh đã xác định lượt nói ({State.SceneTurns().Count} lượt) · " : "") +
            $"hồ sơ: {CurrentResources?.Characters.Profiles.Count ?? 0} nhân vật · khóa nhất quán {(CurrentResources?.Characters.ConsistencyLockEnabled == true ? "BẬT" : "TẮT")}." +
            (reviewable.Length == 0 ? " Chưa có đề xuất: chạy khảo sát AI Bước 2 trước." : "");
        ShowSelection();
    }

    private void ShowSelection()
    {
        var p = Selected;
        bool character = _tabs.SelectedIndex == 0;
        _characterFields.Visibility = character ? Visibility.Visible : Visibility.Collapsed;
        _pairFields.Visibility = character ? Visibility.Collapsed : Visibility.Visible;
        bool decided = p is not null && State.LatestReview(p.Id)?.Decision == AddressingDecision.Accepted;
        _accept.IsEnabled = p is { EvidenceValid: true } && !decided && p.Phases.Count > 0;
        _reject.IsEnabled = _defer.IsEnabled = p is not null && !decided;
        if (p is null) { _details.Text = ""; _phase.ItemsSource = null; return; }
        _phase.ItemsSource = p.Phases.Select(x => Range(x) + " · " + (p.Kind == AddressingTaskKind.Character ? x.NarrativeVi : $"{x.SelfVi}/{x.AddressVi}")).ToArray();
        int current = p.Phases.Select((x, i) => (x, i)).Where(t => t.x.ToChapter == 0).Select(t => t.i).DefaultIfEmpty(Math.Max(0, p.Phases.Count - 1)).First();
        _phase.SelectedIndex = p.Phases.Count == 0 ? -1 : current;
        _phase.SelectionChanged -= OnPhaseChanged; _phase.SelectionChanged += OnPhaseChanged;
        FillFromPhase(p);
        if (p.Kind == AddressingTaskKind.Character)
        {
            _gender.SelectedIndex = Array.FindIndex(Genders, g => g.Value == (p.Gender switch { "female" => CharacterGender.Female, "male" => CharacterGender.Male, _ => CharacterGender.Unknown }));
            var existing = CurrentResources?.Characters.Profiles.FirstOrDefault(x => x.ChineseName == p.Subject);
            _vietName.Text = existing?.VietnameseName ?? p.NameForms.FirstOrDefault(f => f.Zh == p.Subject)?.Vi ?? "";
            _forms.Text = string.Join(Environment.NewLine, p.NameForms.Select(f => $"{f.Zh}={f.Vi}"));
        }
        else _relationship.Text = p.Relationship;
        var lines = new List<string> { "Lý do (AI): " + p.Reason };
        if (p.Issues.Count > 0) lines.Add("Lỗi kiểm chứng: " + string.Join(", ", p.Issues));
        if (State.LatestReview(p.Id) is { } review) lines.Add($"Quyết định: {review.Decision} {review.At:yyyy-MM-dd HH:mm} {review.Applied} {review.Note}".TrimEnd());
        if (_tasks.TryGetValue(p.TaskId, out var task))
        {
            if (task.Kind == AddressingTaskKind.Character) lines.Add($"CPU: 他 sau tên {task.He} · 她 {task.She} → giới tính theo nguyên văn: {task.SourceGender}");
            var cited = p.Phases.SelectMany(x => x.SampleIds).ToHashSet(StringComparer.Ordinal);
            lines.Add("Mẫu nguyên văn (★ = AI trích dẫn):");
            // A pair task holds both directions: the reverse proposal (B → A) was answered from the R samples.
            var shown = task.Kind == AddressingTaskKind.Pair && p.Subject != task.Subject ? task.ReverseSamples : task.Samples;
            lines.AddRange(shown.Select(s => $"{(cited.Contains(s.SampleId) ? "★" : " ")} {s.SampleId} ch.{s.ChapterNumber}: {s.Lead} 「{s.Text}」".Replace(" 「", task.Kind == AddressingTaskKind.Pair ? " 「" : " ")
                .Replace("」", task.Kind == AddressingTaskKind.Pair ? "」" : "")));
        }
        else lines.Add("Mẫu nguyên văn không còn khớp (nguyên tác hoặc danh sách tên đã đổi kể từ lúc khảo sát).");
        _details.Text = string.Join(Environment.NewLine, lines);
    }

    private void OnPhaseChanged(object sender, SelectionChangedEventArgs e) { if (Selected is { } p) FillFromPhase(p); }
    private void FillFromPhase(AddressingProposal p)
    {
        if (_phase.SelectedIndex < 0 || _phase.SelectedIndex >= p.Phases.Count) return;
        var phase = p.Phases[_phase.SelectedIndex];
        _narrative.Text = phase.NarrativeVi; _secondary.Text = phase.SecondaryVi; _self.Text = phase.SelfVi; _address.Text = phase.AddressVi;
    }

    internal async Task AcceptAsync()
    {
        if (Selected is not { } p || CurrentResources is null) return;
        await Run(async () =>
        {
            var saved = p.Kind == AddressingTaskKind.Character
                ? await _service.AcceptCharacterAsync(p.Id, new(Genders[Math.Max(0, _gender.SelectedIndex)].Value, _vietName.Text, _narrative.Text, _secondary.Text,
                    ParseForms(_forms.Text), _phase.SelectedIndex, _note.Text), CurrentResources.Revision)
                : await _service.AcceptPairAsync(p.Id, new(_self.Text, _address.Text, _relationship.Text, _phase.SelectedIndex, _note.Text), CurrentResources.Revision);
            CurrentResources = saved; _applyResources(saved); State = await _service.LoadAsync(); Refresh(); await RefreshCompletionAsync();
        }, "Đã ghi vào hồ sơ nhân vật.");
    }

    internal async Task DecideAsync(AddressingDecision decision)
    {
        if (Selected is not { } p) return;
        await Run(async () =>
        {
            State = decision == AddressingDecision.Rejected ? await _service.RejectAsync(p.Id, _note.Text) : await _service.DeferAsync(p.Id, _note.Text);
            await RefreshCompletionAsync();
            Refresh();
        }, decision == AddressingDecision.Rejected ? "Đã bác bỏ đề xuất." : "Đã để sau.");
    }

    internal async Task SetLockAsync(bool enabled)
    {
        if (_updatingLock || CurrentResources is null) return;
        await Run(async () =>
        {
            var saved = await _service.SetConsistencyLockAsync(enabled, CurrentResources.Revision);
            CurrentResources = saved; _applyResources(saved); Refresh();
        }, enabled ? "Đã bật khóa nhất quán: trình dịch sẽ dùng hồ sơ nhân vật." : "Đã tắt khóa nhất quán.");
    }

    private void PopulateModels()
    {
        if (_backend.SelectedItem is not OllamaBackendProfile profile) return;
        _model.ItemsSource = profile.Id == GeminiSurveyProfile.Id
            ? new[] { "gemini-3.6-flash", "gemini-3.8-flash", "gemini-3.5-flash-lite" } : new[] { profile.Model };
        _model.SelectedIndex = 0;
    }

    /// <summary>Same model options as the other survey windows: Gemini gets a large context and output budget.</summary>
    private static ModelOptions SurveyOptions(OllamaBackendProfile profile, string model)
    {
        var runtime = (profile.Runtime ?? AiRuntimeProfile.CreateDefault(profile.Model)).ModelOptions;
        bool gemini = profile.Id == GeminiSurveyProfile.Id;
        var options = runtime with
        {
            Model = model.Trim(), ContextSize = gemini ? Math.Max(65536, runtime.ContextSize) : runtime.ContextSize,
            // Sixteen tasks expect at most ~5.6K output tokens. 10K leaves safe structured-output headroom
            // without reserving an unnecessarily large share of Gemini's 65K survey context for the answer.
            MaximumOutputTokens = gemini ? Math.Max(10_240, runtime.MaximumOutputTokens) : runtime.MaximumOutputTokens
        };
        options.Validate(); return options;
    }

    private void ShowWork(AddressingSurveyWork work)
    {
        if (work.Complete)
        {
            _surveyStatus.Text = $"Đã khảo sát xong: {work.Scenes.Count} cảnh, {work.Tasks.Count(t => t.Kind == AddressingTaskKind.Character)} nhân vật, {work.Tasks.Count(t => t.Kind == AddressingTaskKind.Pair)} cặp. Bấm Hoàn thành để ghi vào hồ sơ.";
            return;
        }
        // The split follows the selected AI's own settings (context, output length in Quản lý GPU), recomputed on every change.
        ModelOptions? options = SelectedSurveyOptions();
        string budget = options is null ? "mỗi request gom nhiều nhiệm vụ tới giới hạn token"
            : $"chia theo {options.Model}: context {options.ContextSize:N0}, đầu ra tối đa {options.MaximumOutputTokens:N0} token";
        _surveyStatus.Text = $"Còn {work.PendingScenes.Count} cảnh, {work.PendingCharacters} nhân vật, {work.PendingPairs} cặp, {work.PendingIdentities} câu hỏi \"cùng một người?\" chưa khảo sát " +
            $"(khoảng {work.EstimateRequests(options)} request, {budget}; cặp mới có thể xuất hiện sau khi xong các cảnh).";
    }

    private ModelOptions? SelectedSurveyOptions()
    {
        if (_backend.SelectedItem is not OllamaBackendProfile profile || string.IsNullOrWhiteSpace(_model.Text)) return null;
        try { return SurveyOptions(profile, _model.Text); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
    }

    // Changing AI or model re-splits the remaining work for that model's context right away.
    private void OnSurveyModelChanged()
    {
        if (Work is not null && _surveyCancel is null) ShowWork(Work);
    }

    private static string StopReason(string? code) => code switch
    {
        null => "",
        "RequestBudget" => "Đã dùng hết số request của lần này: bấm Khảo sát lần nữa để chạy tiếp.",
        "RetryNextRun" => "Một số câu trả lời không qua kiểm chứng: bấm Khảo sát lần nữa để hỏi lại (mỗi nhiệm vụ tối đa 2 lần).",
        "Cancelled" => "Đã dừng theo yêu cầu. Phần đã xong được giữ lại.",
        "ProviderRefusal" => "AI từ chối nội dung của một request (thường do nội dung nhạy cảm). Phần đó được giữ lại, không gửi lại; thử Ollama cục bộ cho truyện này.",
        "ContextBudgetExceeded" => "Một nhiệm vụ không vừa context của model kể cả khi gửi riêng: tăng Context trong Quản lý GPU (Thiết lập nâng cao) hoặc chọn model có context lớn hơn.",
        _ => $"Dừng vì lỗi {code}. Phần đã xong được giữ lại; thử lại sau hoặc đổi model."
    };

    internal void SetSurveyInputs(int backendIndex, string model, int maximumRequests)
    {
        _backend.SelectedIndex = backendIndex; _model.Text = model; _maxRequests.Text = maximumRequests.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// One click: plan (CPU) → scenes → characters and pairs (planned again with the scene turns), into the Project's
    /// ledger. Asks before sending; stops at the request limit, on Dừng, or on the first service error.
    /// </summary>
    internal async Task SurveyAsync()
    {
        if (CurrentResources is null || _backend.SelectedItem is not OllamaBackendProfile profile || _surveyCancel is not null) return;
        LastError = null;
        if (!int.TryParse(_maxRequests.Text, out int maximum) || maximum is < 1 or > 1000) { LastError = "Số request phải từ 1 đến 1000."; _surveyStatus.Text = LastError; return; }
        ModelOptions options;
        try { options = SurveyOptions(profile, _model.Text); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { LastError = ex.Message; _surveyStatus.Text = ex.Message; return; }
        if (Work is { Complete: true }) { ShowWork(Work); return; }
        string where = profile.Id == GeminiSurveyProfile.Id ? "Google Gemini (dịch vụ trên mạng)" : profile.Name;
        if (!ConfirmSend($"Gửi trích đoạn nguyên tác tới {where}, model {options.Model}? Tối đa {maximum} request lần này, mỗi request thử một lần. " +
                         "Kết quả chỉ ghi vào sổ khảo sát của Project; hồ sơ nhân vật chỉ đổi khi bạn bấm Hoàn thành.")) return;
        _surveyCancel = new(); var ct = _surveyCancel.Token;
        foreach (var c in new UIElement[] { _survey, _complete, _undo, _reload, _accept, _reject, _defer, _lock, _backend, _model, _maxRequests }) c.IsEnabled = false;
        _stop.IsEnabled = true;
        try
        {
            var client = BackendFactory(profile, options, maximum);
            using var lifetime = client as IDisposable;
            var progress = new Progress<AddressingSurveyProgress>(p => _surveyStatus.Text = $"{p.Stage} · {p.RequestsSent}/{p.MaximumRequests} request · {p.Message}");
            LastSurvey = await new AddressingSurveyRunner(client).RunAsync(_session, _sources, CurrentResources, options, maximum, progress: progress, ct: ct);
        }
        catch (OperationCanceledException) { LastSurvey = null; }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or ArgumentException)
        {
            LastError = ex.Message;
            if (IsVisible) ThemedDialog.Show(this, ex.Message, "Sổ xưng hô", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _surveyCancel.Dispose(); _surveyCancel = null; _stop.IsEnabled = false;
            foreach (var c in new UIElement[] { _survey, _reload, _accept, _reject, _defer, _lock, _backend, _model, _maxRequests }) c.IsEnabled = true;
        }
        string sent = LastSurvey is null ? "" : $"Lần này đã gửi {LastSurvey.RequestsSent} request. {StopReason(LastSurvey.StoppedBy)}";
        await LoadAsync(); // Shows what is left and what "Hoàn thành" would write.
        _surveyStatus.Text = (LastError is null ? sent : "Không khảo sát được: " + LastError) + " " + _surveyStatus.Text;
    }

    /// <summary>What "Hoàn thành" would write now, shown above the lists; nothing is written.</summary>
    private async Task RefreshCompletionAsync()
    {
        Preview = await _service.PreviewCompletionAsync();
        _undo.IsEnabled = await _service.CanUndoCompletionAsync();
        _complete.IsEnabled = Preview.Applied.Count > 0;
        _completion.Text = Preview.Applied.Count == 0 && Preview.Held.Count == 0 ? "Chưa có gì để ghi: chạy khảo sát trước, hoặc mọi đề xuất đã được ghi."
            : $"Hoàn thành sẽ ghi {Count(Preview.Applied, AddressingTaskKind.Character)} nhân vật và {Count(Preview.Applied, AddressingTaskKind.Pair)} cặp xưng hô." +
              (Preview.Held.Count == 0 ? "" : Environment.NewLine + Held(Preview.Held));
    }
    private static int Count(IEnumerable<AddressingAutoItem> items, AddressingTaskKind kind) => items.Count(x => x.Kind == kind);
    private static string Held(IReadOnlyList<AddressingAutoItem> held) =>
        $"Giữ lại {held.Count} mục (không chặn Hoàn thành; bản dịch dùng mặc định cho các mục này):" + Environment.NewLine +
        string.Join(Environment.NewLine, held.Select(h => $"• {h.Subject}{(h.Listener is null ? "" : " → " + h.Listener)}: {h.Detail}"));

    internal async Task CompleteAsync()
    {
        if (CurrentResources is null) return;
        AddressingCompletion? done = null;
        await Run(async () =>
        {
            done = await _service.CompleteAsync(CurrentResources.Revision);
            CurrentResources = done.Resources; _applyResources(done.Resources);
            State = await _service.LoadAsync();
            _updatingLock = true; _lock.IsChecked = done.Resources.Characters.ConsistencyLockEnabled; _updatingLock = false;
            Refresh(); await RefreshCompletionAsync();
        }, "Đã Hoàn thành.");
        if (done is not null)
            _completion.Text = $"Đã ghi {Count(done.Applied, AddressingTaskKind.Character)} nhân vật và {Count(done.Applied, AddressingTaskKind.Pair)} cặp xưng hô; khóa nhất quán đã BẬT, có thể dịch ngay." +
                (done.Held.Count == 0 ? "" : Environment.NewLine + Held(done.Held));
    }

    internal async Task UndoAsync(bool discardLaterEdits)
    {
        await Run(async () =>
        {
            ProjectResources saved;
            try { saved = await _service.UndoCompletionAsync(discardLaterEdits); }
            catch (AddressingUndoConflictException ex) when (IsVisible && !discardLaterEdits &&
                ThemedDialog.Show(this, ex.Message + " Vẫn hoàn tác?", "Sổ xưng hô", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                saved = await _service.UndoCompletionAsync(discardLaterEdits: true);
            }
            CurrentResources = saved; _applyResources(saved); State = await _service.LoadAsync();
            _updatingLock = true; _lock.IsChecked = saved.Characters.ConsistencyLockEnabled; _updatingLock = false;
            Refresh(); await RefreshCompletionAsync();
        }, "Đã hoàn tác lần Hoàn thành gần nhất.");
    }

    internal void SelectRow(AddressingTaskKind kind, string subject)
    {
        _tabs.SelectedIndex = kind == AddressingTaskKind.Character ? 0 : 1;
        var grid = kind == AddressingTaskKind.Character ? _characters : _pairs;
        grid.SelectedItem = (grid.ItemsSource as IEnumerable<AddressingRow>)?.FirstOrDefault(r => r.Proposal.Subject == subject);
        ShowSelection();
    }
    internal void SetCharacterInputs(string vietnameseName, string narrative, string forms) { _vietName.Text = vietnameseName; _narrative.Text = narrative; _forms.Text = forms; }
    internal void SetPairInputs(string self, string address) { _self.Text = self; _address.Text = address; }

    private static IReadOnlyList<AddressingNameForm> ParseForms(string text) => text.Split('\n')
        .Select(l => l.Trim()).Where(l => l.Length > 0)
        .Select(l => l.Split('=', 2) is [var zh, var vi] ? new AddressingNameForm(zh.Trim(), vi.Trim()) : throw new InvalidOperationException("Mỗi dòng cách gọi phải có dạng chữ Hán=tiếng Việt."))
        .ToArray();
    private static string Range(AddressingPhase p) => $"ch.{p.FromChapter}–{(p.ToChapter == 0 ? "…" : p.ToChapter.ToString())}";
    private static string GenderLabel(string gender) => gender switch { "female" => "Nữ", "male" => "Nam", _ => "Chưa rõ" };

    private async Task Run(Func<Task> action, string success)
    {
        try { LastError = null; IsEnabled = false; await action(); _summary.Text = success + " " + _summary.Text; }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or ArgumentException)
        {
            LastError = ex.Message; _summary.Text = "Không thực hiện được: " + ex.Message;
            if (IsVisible) ThemedDialog.Show(this, ex.Message, "Sổ xưng hô", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { IsEnabled = true; }
    }
}
