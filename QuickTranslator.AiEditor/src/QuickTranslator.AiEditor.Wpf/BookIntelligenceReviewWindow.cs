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
    private readonly ProjectSession _session;
    private readonly ProjectRepository _repository;
    private ProjectResources _resources;
    private readonly IReadOnlyList<OllamaBackendProfile> _profiles;
    private readonly Func<IntelligenceCharacterSuggestion, CharacterProfile?> _editCharacter;
    private readonly Action<ProjectResources> _resourcesChanged;
    private readonly Func<CorpusEvidence, Task> _openEvidence;
    private BookIntelligenceState? _state;
    private CancellationTokenSource? _cancellation;

    private readonly ComboBox _profile = new() { Width = 210, DisplayMemberPath = nameof(OllamaBackendProfile.Name) };
    private readonly TextBox _model = new() { Width = 240 };
    private readonly Button _run = new() { Content = "Bắt đầu khảo sát" };
    private readonly Button _cancel = new() { Content = "Tạm dừng", IsEnabled = false };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Height = 5, Minimum = 0, Maximum = 1 };
    private readonly DataGrid _queue = new() { AutoGenerateColumns = false, IsReadOnly = true,
        CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single };
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

    public BookIntelligenceReviewWindow(ProjectSession session, ProjectRepository repository,
        ProjectResources resources, IReadOnlyList<OllamaBackendProfile> profiles,
        Func<IntelligenceCharacterSuggestion, CharacterProfile?> editCharacter,
        Action<ProjectResources> resourcesChanged, Func<CorpusEvidence, Task> openEvidence)
    {
        _session = session;
        _repository = repository;
        _resources = resources.Clone();
        _profiles = profiles;
        _editCharacter = editCharacter;
        _resourcesChanged = resourcesChanged;
        _openEvidence = openEvidence;
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
        controls.Children.Add(_model); controls.Children.Add(_run); controls.Children.Add(_cancel);
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
        return root;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _state = await new BookIntelligenceStore(_session).LoadAsync();
            OllamaBackendProfile selected = _profiles.FirstOrDefault(p => p.Id == _state.AnalyzerProfileId)
                ?? _profiles.FirstOrDefault(p => p.Id == OllamaBackendProfile.DefaultLocalId)
                ?? _profiles.First();
            _profile.ItemsSource = _profiles; _profile.SelectedItem = selected;
            _model.Text = string.IsNullOrWhiteSpace(_state.AnalyzerModel) ? selected.Model : _state.AnalyzerModel;
            RefreshQueue();
        }
        catch (Exception ex) { ShowFailure(ex); }
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
            var indexProgress = new Progress<CorpusIndexProgress>(p =>
            {
                _progress.Maximum = Math.Max(1, p.TotalChapters); _progress.Value = p.ProcessedChapters;
                _summary.Text = $"CPU: {p.ProcessedChapters}/{p.TotalChapters} chương · quét lại {p.RescannedChapters} · {p.EvidenceCount} đoạn chứng cứ.";
            });
            _state = await new ProjectCorpusIndexer(_repository).IndexAsync(_session, _state, store,
                _resources.Characters, indexProgress, _cancellation.Token);
            _summary.Text = "Đã lập chỉ mục. Đang phân tích các đoạn có tín hiệu bằng model khảo sát…";
            if (profile.Kind == OllamaBackendKind.Remote && !profile.RemoteDisclosureAccepted)
                throw new InvalidOperationException("Profile Remote chưa xác nhận quyền gửi các đoạn nguyên tác để khảo sát.");
            using IOllamaBackendClient backend = OllamaClient.Create(profile);
            ModelOptions baseOptions = (profile.Runtime ?? AiRuntimeProfile.CreateDefault(profile.Model)).ModelOptions;
            ModelOptions options = baseOptions with { Model = _model.Text.Trim(), Temperature = Math.Min(0.2, baseOptions.Temperature) };
            var aiProgress = new Progress<AnalyzerProgress>(p =>
            {
                _progress.Maximum = Math.Max(1, p.TotalEvidence); _progress.Value = p.ProcessedEvidence;
                _summary.Text = $"AI: {p.ProcessedEvidence}/{p.TotalEvidence} đoạn · {p.PendingSuggestions} đề xuất chờ duyệt.";
            });
            _state = await new AdaptiveBookIntelligenceAnalyzer(backend).AnalyzeAsync(_session, _state, store,
                _resources, options, aiProgress, _cancellation.Token);
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
                default: return;
            }
            ApplyCommit(result); _summary.Text = "Đã lưu mục được xác nhận. Các mục khác vẫn ở hàng chờ.";
        }
        catch (Exception ex) { ShowFailure(ex); }
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
        if (_state is null || _queue.SelectedItem is not ReviewRow row) return;
        try
        {
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
        _resources = result.Resources; _state = result.State; _resourcesChanged(_resources); RefreshQueue();
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
        return _state.Characters.Select(x => new ReviewRow(x.Id, "Nhân vật", $"{x.ChineseName} → {x.VietnameseName}", x.Confidence.ToString(), x.Status.ToString(), x))
            .Concat(_state.Relationships.Select(x => new ReviewRow(x.Id, "Quan hệ", $"{x.SpeakerChineseName} → {x.ListenerChineseName}: {x.Relationship}", x.Confidence.ToString(), x.Status.ToString(), x)))
            .Concat(_state.Glossary.Select(x => new ReviewRow(x.Id, "Glossary", $"{x.Chinese} → {x.Vietnamese}", x.Confidence.ToString(), x.Status.ToString(), x)))
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
                reason = $"{x.SpeakerChineseName} → {x.ListenerChineseName}\nQuan hệ: {x.Relationship}\nXưng hô: {x.SelfReference} → {x.AddresseeReference}\nChiều ngược đề xuất: {x.ReverseSelfReference} → {x.ReverseAddresseeReference}\n\n{x.Reason}";
                ids = x.EvidenceIds; break;
            case IntelligenceGlossarySuggestion x:
                reason = $"{x.Chinese} → {x.Vietnamese}\nLoại: {x.Category}\nPhụ thuộc ngữ cảnh: {(x.ContextDependent ? "Có — cần thận trọng" : "Không")}\n\n{x.Reason}";
                ids = x.EvidenceIds; break;
            default: reason = ""; ids = Array.Empty<Guid>(); break;
        }
        _details.Text = reason;
        Dictionary<Guid, CorpusEvidence> map = _state.Evidence.ToDictionary(x => x.Id);
        _evidence.ItemsSource = ids.Where(map.ContainsKey).Select(id => new EvidenceRow(map[id],
            $"{ShortChapter(map[id].ChapterId)} · {map[id].StartUtf16}+{map[id].LengthUtf16} · {Preview(map[id].Excerpt)}")).ToList();
        _merge.IsEnabled = row.Value is IntelligenceCharacterSuggestion;
        _mergeTarget.IsEnabled = _merge.IsEnabled;
    }

    private bool EditRelationship(IntelligenceRelationshipSuggestion item)
    {
        var panel = new StackPanel { Margin = new Thickness(12) };
        TextBox Field(string label, string value) { panel.Children.Add(new TextBlock { Text = label }); var box = new TextBox { Text = value }; panel.Children.Add(box); return box; }
        TextBox relationship = Field("Quan hệ", item.Relationship), self = Field("Người nói tự xưng", item.SelfReference),
            address = Field("Người nói gọi đối phương", item.AddresseeReference),
            reverseSelf = Field("Chiều ngược tự xưng (để trống nếu chưa xác nhận)", item.ReverseSelfReference),
            reverseAddress = Field("Chiều ngược gọi đối phương", item.ReverseAddresseeReference);
        var save = new Button { Content = "Xác nhận", IsDefault = true }; panel.Children.Add(save);
        var dialog = new Window { Owner = this, Title = "Xác nhận quan hệ & xưng hô", Width = 460, Height = 410,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        save.Click += (_, _) => { item.Relationship = relationship.Text.Trim(); item.SelfReference = self.Text.Trim();
            item.AddresseeReference = address.Text.Trim(); item.ReverseSelfReference = reverseSelf.Text.Trim();
            item.ReverseAddresseeReference = reverseAddress.Text.Trim(); dialog.DialogResult = true; };
        return dialog.ShowDialog() == true;
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

    private sealed record ReviewRow(Guid Id, string Kind, string Title, string Confidence, string Status, object Value);
    private sealed record EvidenceRow(CorpusEvidence Evidence, string Label);
}
