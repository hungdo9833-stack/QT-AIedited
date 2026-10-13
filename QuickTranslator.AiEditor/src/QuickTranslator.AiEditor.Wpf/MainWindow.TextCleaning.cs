using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.TextCleaning;
using QuickTranslator.AiEditor.Core.Traditional;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private readonly ChineseTextScanner _chineseTextScanner = new();
    private readonly BatchReplacementEngine _batchReplacementEngine = new();

    private void OnOpenBatchReplace(object sender, RoutedEventArgs e) => OpenTextCleanerUi(scanFirst: false);
    private void OnScanChineseText(object sender, RoutedEventArgs e) => OpenTextCleanerUi(scanFirst: true);

    private void OpenTextCleanerUi(bool scanFirst)
    {
        try { ShowTextCleanerWindow(scanFirst); }
        catch (Exception ex) { EndUiOperation(); ShowError(ex); }
    }

    private async void OnUndoBatchReplace(object sender, RoutedEventArgs e)
    {
        if (_service is null || WorkActive || _uiOperation) return;
        var snapshot = _service.GetSnapshot();
        var latest = snapshot.BatchReplacementHistory.Where(x => !x.Reverted && !x.Automatic)
            .OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        if (latest is null) { SetStatus("Không có lần thay thế thủ công nào để hoàn tác."); return; }
        if (MessageBox.Show(this,
                $"Hoàn tác đợt thay thế lúc {latest.CreatedAt.LocalDateTime:g}? Nếu bản dịch đã được sửa sau đó, ứng dụng sẽ từ chối ghi đè.",
                "Hoàn tác thay thế", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!BeginUiOperation()) return;
        try
        {
            if (latest.Scope == BatchReplacementScope.CurrentProject)
            {
                await SaveCurrentAsync();
                var coordinator = new BatchProjectReplacementService(_store);
                var chapters = await coordinator.LoadProjectChaptersAsync(snapshot.CharacterProject.Id, snapshot);
                var report = await coordinator.UndoAsync(chapters, latest.Id);
                await ReloadCurrentChapterAfterProjectBatchAsync(snapshot.Id);
                ShowProjectReport(report, "Hoàn tác nhiều chương");
            }
            else
            {
                var result = await _service.UndoLatestBatchReplacementAsync(latest.Id);
                SetStatus($"Đã hoàn tác {result.OccurrencesChanged} vị trí trong {result.SegmentsChanged} segment.");
            }
            Refresh();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private void ShowTextCleanerWindow(bool scanFirst)
    {
        if (_service is null) { ShowError(new InvalidOperationException("Hãy chia hoặc mở một chương trước.")); return; }
        if (WorkActive)
        {
            ShowError(new InvalidOperationException("Hãy bấm Tạm dừng và chờ request hiện tại lưu xong trước khi quét hoặc thay thế."));
            return;
        }
        if (_uiOperation) return;
        _uiOperation = true;
        UpdateButtons();

        var find = new TextBox { MinWidth = 170, ToolTip = "Văn bản cần tìm" };
        var replace = new TextBox { MinWidth = 170, ToolTip = "Văn bản thay thế; có thể để trống để xóa" };
        var manualRules = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 64,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ToolTip = "Nhiều quy tắc, mỗi dòng: chữ cần tìm => chữ thay thế"
        };
        var scope = new ComboBox { Width = 245, DisplayMemberPath = "Label" };
        scope.ItemsSource = new[]
        {
            new ScopeOption(BatchReplacementScope.SelectedSegment, "Segment đang chọn"),
            new ScopeOption(BatchReplacementScope.CurrentChapter, "Toàn bộ chương hiện tại"),
            new ScopeOption(BatchReplacementScope.CurrentProject, "Tất cả chương trong bộ truyện")
        };
        scope.SelectedIndex = 1;
        var useConfirmed = new CheckBox { Content = "Glossary & hồ sơ đã xác nhận", IsChecked = true, Margin = new Thickness(8, 3, 4, 3) };
        var useTraditional = new CheckBox { Content = "Kèm gợi ý Data truyền thống (không tự chọn)", Margin = new Thickness(8, 3, 4, 3) };
        var search = new TextBox { Width = 220, ToolTip = "Lọc kết quả" };
        var classification = new ComboBox { Width = 175 };
        classification.ItemsSource = new[] { "Tất cả trạng thái", "Đã xác nhận", "Chưa biết", "Xung đột", "Giữ nguyên", "Được bảo vệ" };
        classification.SelectedIndex = 0;
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(6) };
        var progress = new ProgressBar { Height = 5, IsIndeterminate = true, Visibility = Visibility.Collapsed };
        var previewRows = new ObservableCollection<BatchPreviewRow>();
        var chineseRows = new ObservableCollection<ChineseOccurrenceRow>();
        var previewGrid = CreatePreviewGrid();
        var chineseGrid = CreateChineseGrid();
        previewGrid.ItemsSource = previewRows;
        chineseGrid.ItemsSource = chineseRows;
        var previewView = CollectionViewSource.GetDefaultView(previewRows);
        var chineseView = CollectionViewSource.GetDefaultView(chineseRows);
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Xem trước thay đổi", Content = previewGrid });
        tabs.Items.Add(new TabItem { Header = "Chữ Hán còn sót", Content = chineseGrid });

        var findButton = new Button { Content = "Tìm tất cả / Xem trước", Style = (Style)FindResource("PrimaryButton") };
        var scanButton = new Button { Content = "Quét chữ Hán" };
        var retranslate = new Button
        {
            Content = "Dịch lại…",
            ToolTip = "Dịch lại các vị trí chọn bằng Ctrl/Shift hoặc toàn bộ Project; có thể dịch cả segment hoặc chỉ cụm chữ Hán"
        };
        var cancelRetranslation = new Button { Content = "Hủy dịch lại", IsEnabled = false };
        var selectEligible = new Button { Content = "Chọn vị trí đủ điều kiện" };
        var applyCurrent = new Button { Content = "Thay vị trí đang chọn" };
        var applySelected = new Button { Content = "Thay tất cả vị trí được chọn", Style = (Style)FindResource("PrimaryButton") };
        var undo = new Button { Content = "Hoàn tác lần thay thế" };
        var keep = new Button { Content = "Đánh dấu giữ nguyên" };
        var addGlossary = new Button { Content = "Xác nhận vào Glossary…" };
        var addCharacter = new Button { Content = "Xác nhận hồ sơ nhân vật…" };
        var close = new Button { Content = "Đóng" };
        var top = new StackPanel();
        var direct = new WrapPanel();
        direct.Children.Add(LabelFor("Tìm", find)); direct.Children.Add(find);
        direct.Children.Add(LabelFor("Thay", replace)); direct.Children.Add(replace);
        direct.Children.Add(LabelFor("Phạm vi", scope)); direct.Children.Add(scope);
        top.Children.Add(direct);
        top.Children.Add(new TextBlock { Text = "Nhiều quy tắc (mỗi dòng: Trung => Việt)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 4, 4, 0) });
        top.Children.Add(manualRules);
        var options = new WrapPanel(); options.Children.Add(useConfirmed); options.Children.Add(useTraditional);
        options.Children.Add(findButton); options.Children.Add(scanButton); top.Children.Add(options);
        var filter = new WrapPanel(); filter.Children.Add(LabelFor("Lọc", search)); filter.Children.Add(search);
        filter.Children.Add(classification); top.Children.Add(filter); top.Children.Add(progress); top.Children.Add(status);
        var bottom = new WrapPanel();
        foreach (var button in new[] { selectEligible, retranslate, cancelRetranslation, applyCurrent, applySelected,
                     undo, keep, addGlossary, addCharacter, close }) bottom.Children.Add(button);
        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom); root.Children.Add(tabs);
        var window = new Window
        {
            Owner = this, Title = "Tìm & thay thế hàng loạt · Quét chữ Hán còn sót",
            Width = 1180, Height = 760, MinWidth = 850, MinHeight = 560, Content = root
        };

        IReadOnlyList<ChapterDocument> loadedChapters = [_service.GetSnapshot()];
        NavigationRequest? navigation = null;
        CancellationTokenSource? retranslationCancellation = null;
        bool working = false;

        void SetWorking(bool value, string message = "")
        {
            working = value; progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            findButton.IsEnabled = scanButton.IsEnabled = applyCurrent.IsEnabled = applySelected.IsEnabled =
                undo.IsEnabled = keep.IsEnabled = addGlossary.IsEnabled = addCharacter.IsEnabled =
                    retranslate.IsEnabled = !value;
            if (message.Length > 0) status.Text = message;
        }

        async Task<IReadOnlyList<ChapterDocument>> ReadScopeAsync()
        {
            var current = _service!.GetWorkingSnapshot();
            var selectedScope = ((ScopeOption)scope.SelectedItem).Scope;
            if (selectedScope != BatchReplacementScope.CurrentProject) return [current];
            return await ReadProjectScopeAsync();
        }

        async Task<IReadOnlyList<ChapterDocument>> ReadProjectScopeAsync()
        {
            var current = _service!.GetWorkingSnapshot();
            await SaveCurrentAsync();
            var coordinator = new BatchProjectReplacementService(_store);
            IReadOnlyList<ChapterDocument> chapters = await coordinator.LoadProjectChaptersAsync(current.CharacterProject.Id, current);
            if (_projectSession is not null)
            {
                var readingOrder = _projectSession.Manifest.Chapters.Where(entry => entry.DeletedAt is null)
                    .Select((entry, index) => (entry.ChapterId, index))
                    .ToDictionary(item => item.ChapterId, item => item.index);
                chapters = chapters.OrderBy(chapter => readingOrder.GetValueOrDefault(chapter.Id, int.MaxValue)).ToArray();
            }
            return _projectResources is null ? chapters : chapters.Select(chapter =>
                chapter.ProjectId == _projectResources.ProjectId ? _projectResources.CreateWorkingChapter(chapter) : chapter).ToArray();
        }

        IReadOnlyList<BatchReplacementRule> BuildRules(IReadOnlyList<ChapterDocument> chapters)
        {
            var manual = new List<BatchReplacementRule>();
            if (!string.IsNullOrEmpty(find.Text))
                manual.Add(new() { Find = find.Text, Replacement = replace.Text, Confirmed = true,
                    Source = ReplacementRuleSource.Manual, SourceLabel = "Quy tắc nhập trực tiếp" });
            manual.AddRange(ReplacementRuleCatalog.ParseManualRules(manualRules.Text));
            var rules = new List<BatchReplacementRule>(manual);
            var manualKeys = manual.Select(x => x.Find).ToHashSet(StringComparer.Ordinal);
            if (useConfirmed.IsChecked == true)
                foreach (var chapter in chapters)
                    rules.AddRange(ReplacementRuleCatalog.FromChapter(chapter).Rules
                        .Where(x => x.Confirmed && !manualKeys.Contains(x.Find)));
            if (useTraditional.IsChecked == true && _traditionalService.CurrentSnapshot is { } traditional)
            {
                var existing = rules.Select(x => x.Find).ToHashSet(StringComparer.Ordinal);
                foreach (string cluster in chapters.SelectMany(chapter => chapter.Segments)
                             .SelectMany(segment => ChineseTextScanner.FindClusters(segment.EditableText))
                             .Select(x => x.Text).Distinct(StringComparer.Ordinal))
                {
                    string? value = traditional.Names.TryGetValue(cluster, out string? name) ? FirstTraditionalMeaning(name) :
                        traditional.CombinedOneMeaning.TryGetValue(cluster, out string? phrase) ? phrase : null;
                    if (value is not null && !existing.Contains(cluster))
                    {
                        rules.Add(new() { Find = cluster, Replacement = value, Confirmed = false,
                            Source = ReplacementRuleSource.TraditionalNames,
                            SourceLabel = traditional.Names.ContainsKey(cluster) ? "Gợi ý Names/Names2 truyền thống" : "Gợi ý VietPhrase truyền thống" });
                        existing.Add(cluster);
                    }
                    if (cluster.EnumerateRunes().Count() == 1 && traditional.HanViet.TryGetValue(cluster, out string? hanViet) &&
                        !existing.Contains(cluster))
                    {
                        rules.Add(new() { Find = cluster, Replacement = hanViet, Confirmed = false,
                            Source = ReplacementRuleSource.TraditionalHanViet, SourceLabel = "Gợi ý Hán Việt truyền thống" });
                        existing.Add(cluster);
                    }
                }
            }
            if (rules.Count == 0) throw new InvalidOperationException("Hãy nhập quy tắc hoặc bật ánh xạ đã xác nhận.");
            return rules;
        }

        async Task PreviewAsync()
        {
            if (working) return;
            SetWorking(true, "Đang nạp chương và tạo preview; chưa ghi dữ liệu…");
            try
            {
                loadedChapters = await ReadScopeAsync();
                var selectedScope = ((ScopeOption)scope.SelectedItem).Scope;
                var rules = BuildRules(loadedChapters);
                var preview = await Task.Run(() => _batchReplacementEngine.Preview(loadedChapters, rules,
                    selectedScope, _selectedId));
                previewRows.Clear();
                foreach (var item in preview) previewRows.Add(new(item));
                status.Text = $"Tìm thấy {preview.Count} vị trí trong {loadedChapters.Count} chương. " +
                    $"Đã chọn sẵn {previewRows.Count(x => x.IsSelected)} ánh xạ xác nhận, không xung đột và không được bảo vệ.";
                tabs.SelectedIndex = 0; ApplyFilter();
            }
            catch (Exception ex) { status.Text = "Lỗi: " + ex.Message; status.Foreground = Brushes.Firebrick; }
            finally { SetWorking(false); }
        }

        async Task ScanAsync()
        {
            if (working) return;
            SetWorking(true, "Đang quét Unicode CJK ngoài luồng giao diện…");
            try
            {
                loadedChapters = await ReadScopeAsync();
                Guid projectId = _service!.GetSnapshot().CharacterProject.Id;
                var report = await Task.Run(() => _chineseTextScanner.Scan(loadedChapters, projectId));
                var counts = report.Groups.ToDictionary(x => x.Text, x => x.Count, StringComparer.Ordinal);
                chineseRows.Clear();
                foreach (var item in report.Occurrences)
                    chineseRows.Add(new(item, counts[item.Text]));
                status.Foreground = Brushes.DarkSlateGray;
                status.Text = $"Có {report.TotalOccurrences} vị trí · chưa biết {report.UnknownOccurrences} · xung đột {report.ConflictOccurrences}. " +
                    "Nhấp đúp để mở đúng segment; ứng dụng không suy đoán tên chưa xác nhận.";
                tabs.SelectedIndex = 1; ApplyFilter();
            }
            catch (Exception ex) { status.Text = "Lỗi: " + ex.Message; status.Foreground = Brushes.Firebrick; }
            finally { SetWorking(false); }
        }

        async Task<BatchReplacementPreviewItem> CreateRetranslationPreviewAsync(ChapterDocument chapter,
            ChineseTextOccurrence occurrence,
            ChineseRetranslationChoice choice, CancellationToken cancellationToken)
        {
            TranslationSegment segment = chapter.Segments.SingleOrDefault(item => item.Id == occurrence.SegmentId)
                ?? throw new InvalidOperationException("Segment đã thay đổi; hãy quét lại.");
            string editable = segment.EditableText;
            if (occurrence.StartUtf16 < 0 || occurrence.LengthUtf16 <= 0 ||
                occurrence.StartUtf16 > editable.Length - occurrence.LengthUtf16 ||
                !editable.AsSpan(occurrence.StartUtf16, occurrence.LengthUtf16).SequenceEqual(occurrence.Text))
                throw new InvalidOperationException("Nội dung chữ Hán đã đổi sau lần quét; hãy quét lại.");
            string source = choice.Mode == ChineseRetranslationMode.EntireSegment
                ? segment.SourceText : occurrence.Text;
            string translated;
            if (choice.Method == ChineseRetranslationMethod.Ollama)
            {
                await EnsureOllamaBackendsLoadedAsync();
                EnsureOllamaBackendReady();
                OllamaBackendProfile profile = _ollamaBackends.ActiveProfile;
                AiRuntimeProfile runtime = profile.Runtime ?? AiRuntimeProfile.CreateDefault(profile.Model);
                string prompt = BuildChineseRetranslationPrompt(chapter, segment, occurrence, choice.Mode,
                    _presetManager.Selected);
                OllamaCompletion completion = await _translationClient.TranslateAsync(prompt,
                    runtime.ModelOptions, cancellationToken);
                translated = CleanRetranslationOutput(completion.Text);
            }
            else
            {
                TraditionalTranslationResult result = await _traditionalService.TranslateAsync(source,
                    cancellationToken: cancellationToken);
                translated = choice.Method == ChineseRetranslationMethod.VietPhrase
                    ? result.VietPhraseText : result.HanVietText;
            }
            return ChineseRetranslationPreviewBuilder.Build(chapter, occurrence, choice.Mode, choice.Method, translated);
        }

        void ApplyFilter()
        {
            string query = search.Text.Trim();
            string selectedFilter = classification.SelectedItem as string ?? "Tất cả trạng thái";
            previewView.Filter = value => value is BatchPreviewRow row &&
                (query.Length == 0 || row.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase)) &&
                FilterPreview(row, selectedFilter);
            chineseView.Filter = value => value is ChineseOccurrenceRow row &&
                (query.Length == 0 || row.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase)) &&
                FilterChinese(row, selectedFilter);
            previewView.Refresh(); chineseView.Refresh();
        }

        async Task ApplyRowsAsync(IReadOnlyList<BatchPreviewRow> rows)
        {
            if (working) return;
            if (rows.Count == 0) { status.Text = "Chưa chọn vị trí nào."; return; }
            if (rows.Any(x => x.Item.Conflict)) { status.Text = "Bỏ chọn hoặc giải quyết các ánh xạ xung đột trước."; return; }
            bool protectedText = rows.Any(x => x.Item.Protected);
            Guid currentChapterId = _service!.GetSnapshot().Id;
            bool project = ((ScopeOption)scope.SelectedItem).Scope == BatchReplacementScope.CurrentProject ||
                           rows.Any(row => row.Item.ChapterId != currentChapterId);
            string warning = $"Áp dụng {rows.Count} vị trí trong {rows.Select(x => x.Item.ChapterId).Distinct().Count()} chương?";
            if (protectedText) warning += "\n\nCó bản đã sửa/duyệt được bảo vệ. Tiếp tục sẽ tạo revision mới và đưa về chờ duyệt.";
            if (project) warning += "\n\nỨng dụng sẽ sao lưu các chương trước khi ghi và báo riêng từng chương thất bại.";
            if (MessageBox.Show(window, warning, "Xác nhận thay thế", MessageBoxButton.YesNo,
                    protectedText || project ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            SetWorking(true, "Đang ghi an toàn…");
            try
            {
                var items = rows.Select(x => x.Item).ToArray();
                if (project)
                {
                    await SaveCurrentAsync();
                    var currentId = _service!.GetSnapshot().Id;
                    var coordinator = new BatchProjectReplacementService(_store);
                    var report = await coordinator.ApplyAsync(loadedChapters, items, protectedText);
                    await ReloadCurrentChapterAfterProjectBatchAsync(currentId);
                    ShowProjectReport(report, "Thay thế nhiều chương");
                    status.Text = $"Đã thay {report.Chapters.Sum(x => x.OccurrencesChanged)} vị trí; " +
                        $"thành công {report.AppliedChapters} chương, lỗi {report.FailedChapters}. Backup: {report.BackupDirectory}";
                }
                else
                {
                    var result = await _service!.ApplyBatchReplacementAsync(items, protectedText,
                        ((ScopeOption)scope.SelectedItem).Scope);
                    status.Text = $"Đã thay {result.OccurrencesChanged} vị trí trong {result.SegmentsChanged} segment. Bản AI gốc vẫn nằm trong lịch sử.";
                }
                Refresh();
                loadedChapters = await ReadScopeAsync();
                previewRows.Clear();
                SetWorking(false);
                await ScanAsync();
            }
            catch (Exception ex) { status.Text = "Lỗi: " + ex.Message; status.Foreground = Brushes.Firebrick; }
            finally { SetWorking(false); }
        }

        findButton.Click += async (_, _) => await PreviewAsync();
        scanButton.Click += async (_, _) => await ScanAsync();
        retranslate.Click += async (_, _) =>
        {
            if (working) return;
            ChineseOccurrenceRow[] selectedRows = chineseGrid.SelectedItems.Cast<ChineseOccurrenceRow>().ToArray();
            ChineseRetranslationChoice? choice = ShowChineseRetranslationOptions(window, selectedRows);
            if (choice is null) return;
            retranslationCancellation = new CancellationTokenSource();
            cancelRetranslation.IsEnabled = true;
            SetWorking(true, "Đang chuẩn bị bản dịch lại; chưa ghi dữ liệu…");
            try
            {
                Guid currentChapterId = _service!.GetSnapshot().Id;
                bool needsProject = choice.ChapterScope == ChineseRetranslationChapterScope.EntireProject ||
                                    selectedRows.Any(row => row.Item.ChapterId != currentChapterId);
                loadedChapters = needsProject ? await ReadProjectScopeAsync() : [_service.GetWorkingSnapshot()];
                IReadOnlyList<ChineseTextOccurrence> sourceOccurrences;
                if (choice.ChapterScope == ChineseRetranslationChapterScope.EntireProject)
                {
                    Guid projectId = _service.GetSnapshot().CharacterProject.Id;
                    ChineseTextScanReport scan = await Task.Run(() => _chineseTextScanner.Scan(loadedChapters, projectId),
                        retranslationCancellation.Token);
                    sourceOccurrences = scan.Occurrences
                        .Where(item => item.Classification != ChineseTextClassification.Ignored).ToArray();
                    scope.SelectedIndex = 2;
                }
                else sourceOccurrences = selectedRows.Select(row => row.Item).ToArray();
                IReadOnlyList<ChineseTextOccurrence> targets = ChineseRetranslationPreviewBuilder.PlanTargets(
                    loadedChapters, sourceOccurrences, choice.Mode);
                if (targets.Count == 0)
                    throw new InvalidOperationException("Không có vị trí chữ Hán cần dịch lại trong phạm vi đã chọn.");

                previewRows.Clear();
                var failures = new List<string>();
                for (int index = 0; index < targets.Count; index++)
                {
                    retranslationCancellation.Token.ThrowIfCancellationRequested();
                    ChineseTextOccurrence target = targets[index];
                    ChapterDocument chapter = loadedChapters.Single(item => item.Id == target.ChapterId);
                    string method = choice.Method switch
                    {
                        ChineseRetranslationMethod.Ollama => "Ollama",
                        ChineseRetranslationMethod.VietPhrase => "VietPhrase",
                        _ => "Hán Việt"
                    };
                    status.Text = $"Đang tạo preview {index + 1:N0}/{targets.Count:N0} bằng {method}; chưa ghi dữ liệu…";
                    try
                    {
                        BatchReplacementPreviewItem item = await CreateRetranslationPreviewAsync(chapter, target, choice,
                            retranslationCancellation.Token);
                        previewRows.Add(new(item));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failures.Add($"{chapter.Title} · đoạn {target.SegmentOrder}: {ex.Message}");
                    }
                }
                if (previewRows.Count == 0 && failures.Count > 0)
                    throw new InvalidOperationException("Không tạo được preview nào. " + failures[0]);
                previewGrid.SelectedItem = previewRows.FirstOrDefault();
                tabs.SelectedIndex = 0;
                status.Foreground = failures.Count == 0 ? Brushes.DarkSlateGray : Brushes.DarkOrange;
                status.Text = $"Đã tạo {previewRows.Count:N0} preview; lỗi/bỏ qua {failures.Count:N0}. " +
                    "Kiểm tra câu trước/sau rồi chọn các dòng cần áp dụng; chưa có dữ liệu nào được ghi." +
                    (failures.Count == 0 ? "" : "\n" + string.Join("\n", failures.Take(3)));
            }
            catch (OperationCanceledException)
            {
                status.Foreground = Brushes.DarkSlateGray;
                status.Text = $"Đã hủy dịch lại sau {previewRows.Count:N0} preview; nội dung chapter chưa thay đổi.";
            }
            catch (Exception ex)
            {
                status.Foreground = Brushes.Firebrick;
                status.Text = $"Lỗi dịch lại sau {previewRows.Count:N0} preview: {ex.Message} Nội dung chapter chưa thay đổi.";
            }
            finally
            {
                retranslationCancellation.Dispose();
                retranslationCancellation = null;
                cancelRetranslation.IsEnabled = false;
                SetWorking(false);
            }
        };
        cancelRetranslation.Click += (_, _) => retranslationCancellation?.Cancel();
        selectEligible.Click += (_, _) =>
        {
            foreach (var row in previewRows) row.IsSelected = row.Item.RuleConfirmed && !row.Item.Protected && !row.Item.Conflict;
            status.Text = $"Đã chọn {previewRows.Count(x => x.IsSelected)} vị trí đủ điều kiện.";
        };
        applyCurrent.Click += async (_, _) =>
        {
            if (previewGrid.SelectedItem is BatchPreviewRow row) await ApplyRowsAsync([row]);
            else status.Text = "Hãy chọn một dòng preview trước.";
        };
        applySelected.Click += async (_, _) => await ApplyRowsAsync(previewRows.Where(x => x.IsSelected).ToArray());
        undo.Click += (_, _) => { window.Close(); OnUndoBatchReplace(undo, new RoutedEventArgs()); };
        async Task<bool> RunDialogActionAsync(Func<Task<bool>> action)
        {
            if (working) return false;
            SetWorking(true, "Đang lưu thay đổi…");
            try
            {
                if (!await action()) return false;
                Refresh();
                return true;
            }
            catch (Exception ex)
            {
                status.Text = "Chưa hoàn tất: " + ex.Message +
                    " Thay đổi trong bộ nhớ được giữ; kiểm tra quyền ghi/dung lượng rồi bấm lại thao tác để thử lưu.";
                status.Foreground = Brushes.Firebrick;
                ShowError(ex);
                return false;
            }
            finally { SetWorking(false); }
        }

        keep.Click += async (_, _) =>
        {
            if (await RunDialogActionAsync(async () =>
            {
                if (chineseGrid.SelectedItem is not ChineseOccurrenceRow row) { status.Text = "Hãy chọn một cụm chữ Hán."; return false; }
                if (row.Item.ChapterId != _service!.GetSnapshot().Id) { status.Text = "Hãy nhấp đúp mở chương đó trước khi đánh dấu giữ nguyên."; return false; }
                var chapter = _service.GetSnapshot();
                var ignored = chapter.TextCleaner.IgnoredChineseText.Append(row.Item.Text).Distinct(StringComparer.Ordinal).ToArray();
                await _service.ConfigureTextCleanerAsync(chapter.TextCleaner.AutoApplyConfirmedMappings, ignored);
                return true;
            })) await ScanAsync();
        };
        addGlossary.Click += async (_, _) =>
        {
            if (await RunDialogActionAsync(async () =>
            {
                if (chineseGrid.SelectedItem is not ChineseOccurrenceRow row) { status.Text = "Hãy chọn một cụm chữ Hán."; return false; }
                if (row.Item.ChapterId != _service!.GetSnapshot().Id) { status.Text = "Hãy nhấp đúp mở chương đó trước khi xác nhận Glossary."; return false; }
                var edited = ShowGlossaryEditor(null, row.Item.Text);
                if (edited is null) return false;
                var existing = _glossaryRows.SingleOrDefault(x => string.Equals(x.Chinese, edited.Chinese, StringComparison.Ordinal));
                if (existing is not null && MessageBox.Show(window,
                        $"Mục này đang là “{existing.Vietnamese}”. Thay bằng “{edited.Vietnamese}”?", "Cập nhật Glossary",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
                if (existing is null) _glossaryRows.Add(edited); else _glossaryRows[_glossaryRows.IndexOf(existing)] = edited;
                GlossaryChanged();
                if (!await PersistCurrentGlossaryAsync())
                    throw new InvalidOperationException("Glossary chưa được lưu; hãy lưu chương trước khi thử lại.");
                return true;
            })) await ScanAsync();
        };
        addCharacter.Click += async (_, _) =>
        {
            if (await RunDialogActionAsync(async () =>
            {
                if (chineseGrid.SelectedItem is not ChineseOccurrenceRow row) { status.Text = "Hãy chọn một cụm chữ Hán."; return false; }
                if (row.Item.ChapterId != _service!.GetSnapshot().Id) { status.Text = "Hãy nhấp đúp mở chương đó trước khi xác nhận hồ sơ."; return false; }
                var existing = _characterProject.Profiles.FirstOrDefault(x =>
                    string.Equals(x.ChineseName, row.Item.Text, StringComparison.Ordinal));
                var seed = existing is null ? new CharacterProfile { ChineseName = row.Item.Text, NormalizationEnabled = false } : CloneProfile(existing);
                var edited = ShowCharacterEditor(seed);
                if (edited is null) return false;
                if (existing is null) _characterProject.Profiles.Add(edited);
                else _characterProject.Profiles[_characterProject.Profiles.IndexOf(existing)] = edited;
                await SaveCharacterProjectAsync();
                return true;
            })) await ScanAsync();
        };
        search.TextChanged += (_, _) => ApplyFilter();
        classification.SelectionChanged += (_, _) => ApplyFilter();
        close.Click += (_, _) => window.Close();
        previewGrid.MouseDoubleClick += (_, _) =>
        {
            if (working) return;
            if (previewGrid.SelectedItem is BatchPreviewRow row)
            { navigation = new(row.Item.ChapterId, row.Item.SegmentId, row.Item.StartUtf16, row.Item.LengthUtf16); window.Close(); }
        };
        chineseGrid.MouseDoubleClick += (_, _) =>
        {
            if (working) return;
            if (chineseGrid.SelectedItem is ChineseOccurrenceRow row)
            { navigation = new(row.Item.ChapterId, row.Item.SegmentId, row.Item.StartUtf16, row.Item.LengthUtf16); window.Close(); }
        };
        window.Loaded += async (_, _) => { if (scanFirst) await ScanAsync(); };
        window.Closing += (_, args) => { if (working) { args.Cancel = true; status.Text = "Đang ghi/đọc dữ liệu; hãy chờ thao tác hoàn tất trước khi đóng."; } };
        try { window.ShowDialog(); }
        finally { _uiOperation = false; UpdateButtons(); }
        // Navigation owns a new operation lock only after the dialog's lock has been released.
        // OpenTextLocationAsync handles and reports its own errors.
        if (navigation is not null) _ = OpenTextLocationAsync(navigation);
    }

    private async Task ReloadCurrentChapterAfterProjectBatchAsync(Guid chapterId)
    {
        var loaded = await _store.LoadAsync(_store.GetPath(chapterId));
        AttachChapter(loaded.Chapter);
        _selectedId = loaded.Chapter.SelectedSegmentId ?? loaded.Chapter.Segments[0].Id;
        _shownAttemptId = null;
        ApplyInputs(loaded.Chapter);
        SelectChapterPreset(loaded.Chapter.SelectedPreset);
    }

    private async Task OpenTextLocationAsync(NavigationRequest request)
    {
        try
        {
            if (_service?.GetSnapshot().Id != request.ChapterId)
            {
                if (_projectSession is not null) await LoadProjectChapterAsync(request.ChapterId);
                else await RunWorkspaceTransitionAsync(async () =>
                {
                    var loaded = await _store.LoadAsync(_store.GetPath(request.ChapterId));
                    AttachChapter(loaded.Chapter); ApplyInputs(loaded.Chapter); SelectChapterPreset(loaded.Chapter.SelectedPreset);
                });
            }
            if (_service?.GetSnapshot().Id != request.ChapterId) return;
            _selectedId = request.SegmentId; _shownAttemptId = null;
            ReaderModeButton.IsChecked = false; EditorModeButton.IsChecked = true; EditorTabs.SelectedIndex = 1;
            Refresh();
            int start = Math.Clamp(request.StartUtf16, 0, DraftText.Text.Length);
            int length = Math.Min(request.LengthUtf16, DraftText.Text.Length - start);
            DraftText.Focus(); DraftText.Select(start, length);
            int line = DraftText.GetLineIndexFromCharacterIndex(start); if (line >= 0) DraftText.ScrollToLine(line);
            SetStatus("Đã mở đúng chương, segment và vị trí chữ Hán trong bản dịch có thể chỉnh sửa.");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ShowProjectReport(BatchProjectApplyReport report, string title)
    {
        string details = string.Join(Environment.NewLine, report.Chapters.Select(x =>
            $"{(x.Applied ? "ĐÃ GHI" : "CHƯA GHI")} · {x.ChapterTitle} · {x.OccurrencesChanged} vị trí" +
            (x.Error is null ? "" : " · " + x.Error)));
        MessageBox.Show(this, $"Backup: {report.BackupDirectory}\n\n{details}", title,
            MessageBoxButton.OK, report.FailedChapters == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private static ChineseRetranslationChoice? ShowChineseRetranslationOptions(Window owner,
        IReadOnlyList<ChineseOccurrenceRow> selectedRows)
    {
        var chapterScope = new ComboBox { MinWidth = 390, DisplayMemberPath = nameof(RetranslationChapterScopeOption.Label) };
        var chapterScopeOptions = new List<RetranslationChapterScopeOption>();
        if (selectedRows.Count > 0)
            chapterScopeOptions.Add(new(ChineseRetranslationChapterScope.SelectedOccurrences,
                $"Các vị trí đã chọn ({selectedRows.Count:N0})"));
        chapterScopeOptions.Add(new(ChineseRetranslationChapterScope.EntireProject,
            "Toàn bộ dự án · mọi chapter còn chữ Hán"));
        chapterScope.ItemsSource = chapterScopeOptions;
        chapterScope.SelectedIndex = 0;
        var mode = new ComboBox { MinWidth = 330, DisplayMemberPath = nameof(RetranslationModeOption.Label) };
        mode.ItemsSource = new[]
        {
            new RetranslationModeOption(ChineseRetranslationMode.ResidualHanOnly,
                "Chỉ dịch cụm chữ Hán còn sót (ít thay đổi)"),
            new RetranslationModeOption(ChineseRetranslationMode.EntireSegment,
                "Dịch lại toàn bộ đoạn từ nguyên văn Trung")
        };
        mode.SelectedIndex = 0;
        var method = new ComboBox { MinWidth = 330, DisplayMemberPath = nameof(RetranslationMethodOption.Label) };
        method.ItemsSource = new[]
        {
            new RetranslationMethodOption(ChineseRetranslationMethod.Ollama, "Ollama · profile AI đang chọn"),
            new RetranslationMethodOption(ChineseRetranslationMethod.VietPhrase, "VietPhrase · Data QuickTranslator"),
            new RetranslationMethodOption(ChineseRetranslationMethod.HanViet, "Hán Việt · Data QuickTranslator")
        };
        method.SelectedIndex = 0;
        var create = new Button { Content = "Tạo preview", IsDefault = true, MinWidth = 110 };
        if (owner.TryFindResource("PrimaryButton") is Style primary) create.Style = primary;
        var cancel = new Button { Content = "Hủy", IsCancel = true, MinWidth = 80 };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(create); actions.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = selectedRows.Count == 1
                ? $"Đã chọn: {selectedRows[0].Chapter} · đoạn {selectedRows[0].Segment} · {selectedRows[0].Text}"
                : selectedRows.Count > 1
                    ? $"Đã chọn {selectedRows.Count:N0} vị trí trong {selectedRows.Select(row => row.Item.ChapterId).Distinct().Count():N0} chapter."
                    : "Chưa chọn vị trí riêng; QT Studio sẽ quét và tạo preview cho toàn bộ dự án.",
            TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12)
        });
        panel.Children.Add(new TextBlock { Text = "Phạm vi chapter", Margin = new Thickness(0, 4, 0, 3) });
        panel.Children.Add(chapterScope);
        panel.Children.Add(new TextBlock { Text = "Phần văn bản cần dịch", Margin = new Thickness(0, 12, 0, 3) });
        panel.Children.Add(mode);
        panel.Children.Add(new TextBlock { Text = "Phương pháp", Margin = new Thickness(0, 12, 0, 3) });
        panel.Children.Add(method);
        panel.Children.Add(new TextBlock
        {
            Text = selectedRows.Any(row => row.Item.Protected)
                ? "Đoạn này có bản sửa hoặc Approval. Preview vẫn được tạo; khi áp dụng QT Studio sẽ yêu cầu xác nhận và giữ lịch sử cũ."
                : "Toàn bộ dự án sẽ bỏ qua các cụm đã đánh dấu Giữ nguyên. Kết quả chỉ được đưa vào preview; dữ liệu chưa đổi cho tới khi xác nhận áp dụng.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 14),
            Foreground = selectedRows.Any(row => row.Item.Protected) ? Brushes.DarkOrange : Brushes.DarkSlateGray
        });
        panel.Children.Add(actions);
        var dialog = new Window
        {
            Owner = owner,
            Title = "Dịch lại nội dung còn chữ Hán",
            Width = 540,
            SizeToContent = SizeToContent.Height,
            MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Content = panel
        };
        ChineseRetranslationChoice? result = null;
        create.Click += (_, _) =>
        {
            result = new(((RetranslationChapterScopeOption)chapterScope.SelectedItem).Scope,
                ((RetranslationModeOption)mode.SelectedItem).Mode,
                ((RetranslationMethodOption)method.SelectedItem).Method);
            dialog.DialogResult = true;
        };
        return dialog.ShowDialog() == true ? result : null;
    }

    private static string BuildChineseRetranslationPrompt(ChapterDocument chapter, TranslationSegment segment,
        ChineseTextOccurrence occurrence, ChineseRetranslationMode mode, PromptPreset preset)
    {
        string target = mode == ChineseRetranslationMode.EntireSegment ? segment.SourceText : occurrence.Text;
        GlossaryEntry[] glossary = chapter.ConfirmedTerms
            .Where(term => target.Contains(term.Chinese, StringComparison.Ordinal) ||
                           occurrence.Sentence.Contains(term.Chinese, StringComparison.Ordinal))
            .Select(term => new GlossaryEntry(term.Chinese, term.Vietnamese)).DistinctBy(term => term.Chinese).ToArray();
        if (mode == ChineseRetranslationMode.EntireSegment)
            return new TranslationPromptBuilder().Build(segment.SourceText, glossary,
                chapter.ConfirmedContextNotes, preset);
        var prompt = new StringBuilder();
        prompt.AppendLine("Dịch chính xác cụm chữ Hán còn sót sau sang tiếng Việt để chèn vào câu đã dịch.");
        prompt.AppendLine("Chỉ trả phần tiếng Việt thay thế cho cụm đó; không giải thích, không Markdown, không thêm dấu ngoặc.");
        prompt.AppendLine("Cụm chữ Hán: " + occurrence.Text);
        prompt.AppendLine("Câu hiện tại để tham khảo ngữ cảnh: " + occurrence.Sentence);
        if (glossary.Length > 0)
        {
            prompt.AppendLine("Thuật ngữ đã xác nhận:");
            foreach (GlossaryEntry term in glossary) prompt.AppendLine($"{term.Chinese} = {term.Vietnamese}");
        }
        if (!string.IsNullOrWhiteSpace(chapter.ConfirmedContextNotes))
            prompt.AppendLine("Ghi chú ngữ cảnh: " + chapter.ConfirmedContextNotes);
        return prompt.ToString();
    }

    private static string CleanRetranslationOutput(string output)
    {
        string text = output?.Trim() ?? "";
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int firstLine = text.IndexOf('\n');
            int lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine) text = text[(firstLine + 1)..lastFence].Trim();
        }
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"' && !text.Contains('\n'))
            text = text[1..^1].Trim();
        if (text.Length == 0) throw new InvalidDataException("Backend trả kết quả dịch lại rỗng.");
        return text;
    }

    private static DataGrid CreatePreviewGrid()
    {
        var grid = BaseGrid();
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Chọn", Binding = new Binding(nameof(BatchPreviewRow.IsSelected)) });
        grid.Columns.Add(TextColumn("Chương", nameof(BatchPreviewRow.Chapter), 150));
        grid.Columns.Add(TextColumn("Đoạn", nameof(BatchPreviewRow.Segment), 58));
        grid.Columns.Add(TextColumn("Câu trước", nameof(BatchPreviewRow.Before), 260));
        grid.Columns.Add(TextColumn("Thay đổi", nameof(BatchPreviewRow.Change), 180));
        grid.Columns.Add(TextColumn("Câu sau", nameof(BatchPreviewRow.After), 260));
        grid.Columns.Add(TextColumn("Nguồn", nameof(BatchPreviewRow.Source), 180));
        grid.Columns.Add(TextColumn("Bảo vệ / xung đột", nameof(BatchPreviewRow.Protection), 190));
        return grid;
    }

    private static DataGrid CreateChineseGrid()
    {
        var grid = BaseGrid();
        grid.IsReadOnly = true;
        grid.SelectionMode = DataGridSelectionMode.Extended;
        grid.Columns.Add(TextColumn("Cụm", nameof(ChineseOccurrenceRow.Text), 130));
        grid.Columns.Add(TextColumn("Số lần", nameof(ChineseOccurrenceRow.Count), 70));
        grid.Columns.Add(TextColumn("Phân loại", nameof(ChineseOccurrenceRow.Classification), 130));
        grid.Columns.Add(TextColumn("Chương", nameof(ChineseOccurrenceRow.Chapter), 150));
        grid.Columns.Add(TextColumn("Đoạn", nameof(ChineseOccurrenceRow.Segment), 58));
        grid.Columns.Add(TextColumn("Câu chứa", nameof(ChineseOccurrenceRow.Sentence), 330));
        grid.Columns.Add(TextColumn("Ánh xạ / nguồn", nameof(ChineseOccurrenceRow.Candidates), 260));
        grid.Columns.Add(TextColumn("Bảo vệ", nameof(ChineseOccurrenceRow.Protection), 100));
        return grid;
    }

    private static DataGrid BaseGrid() => new()
    {
        AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
        SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow,
        EnableRowVirtualization = true, EnableColumnVirtualization = true,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 6)
    };

    private static DataGridTextColumn TextColumn(string header, string path, double width) => new()
    {
        Header = header, Binding = new Binding(path), Width = new DataGridLength(width)
    };

    private static TextBlock LabelFor(string label, Control control) => new()
    {
        Text = label, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 4, 4, 4), ToolTip = control.ToolTip
    };

    private static bool FilterPreview(BatchPreviewRow row, string filter) => filter switch
    {
        "Đã xác nhận" => row.Item.RuleConfirmed,
        "Xung đột" => row.Item.Conflict,
        "Được bảo vệ" => row.Item.Protected,
        _ => true
    };

    private static bool FilterChinese(ChineseOccurrenceRow row, string filter) => filter switch
    {
        "Đã xác nhận" => row.Item.Classification is ChineseTextClassification.ConfirmedCharacter or ChineseTextClassification.ConfirmedGlossary,
        "Chưa biết" => row.Item.Classification == ChineseTextClassification.Unknown,
        "Xung đột" => row.Item.Classification == ChineseTextClassification.Conflict,
        "Giữ nguyên" => row.Item.Classification == ChineseTextClassification.Ignored,
        "Được bảo vệ" => row.Item.Protected,
        _ => true
    };

    private static string FirstTraditionalMeaning(string value)
    {
        int separator = value.IndexOfAny(['/', '|']);
        return (separator < 0 ? value : value[..separator]).Trim();
    }

    private sealed record ScopeOption(BatchReplacementScope Scope, string Label);
    private sealed record NavigationRequest(Guid ChapterId, Guid SegmentId, int StartUtf16, int LengthUtf16);
    private enum ChineseRetranslationChapterScope { SelectedOccurrences, EntireProject }
    private sealed record RetranslationChapterScopeOption(ChineseRetranslationChapterScope Scope, string Label);
    private sealed record RetranslationModeOption(ChineseRetranslationMode Mode, string Label);
    private sealed record RetranslationMethodOption(ChineseRetranslationMethod Method, string Label);
    private sealed record ChineseRetranslationChoice(ChineseRetranslationChapterScope ChapterScope,
        ChineseRetranslationMode Mode, ChineseRetranslationMethod Method);

    private sealed class BatchPreviewRow(BatchReplacementPreviewItem item) : INotifyPropertyChanged
    {
        private bool _isSelected = item.RuleConfirmed && !item.Protected && !item.Conflict;
        public BatchReplacementPreviewItem Item { get; } = item;
        public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; OnChanged(); } }
        public string Chapter => Item.ChapterTitle;
        public int Segment => Item.SegmentOrder;
        public string Before => Item.BeforeSentence;
        public string After => Item.AfterSentence;
        public string Change => $"【{Item.Original}】 → 【{Item.Replacement ?? "XUNG ĐỘT"}】";
        public string Source => Item.SourceLabel + (Item.RuleConfirmed ? " · đã xác nhận" : " · chỉ gợi ý");
        public string Protection => Item.Conflict ? Item.ConflictMessage : Item.Protected ? "Đã sửa/duyệt · mặc định bỏ chọn" : StatusLabel(Item.SegmentStatus);
        public string SearchText => string.Join(" ", Chapter, Segment, Before, After, Change, Source, Protection);
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    }

    private sealed class ChineseOccurrenceRow(ChineseTextOccurrence item, int count)
    {
        public ChineseTextOccurrence Item { get; } = item;
        public string Text => Item.Text;
        public int Count { get; } = count;
        public string Classification => Item.Classification switch
        {
            ChineseTextClassification.ConfirmedCharacter => "Tên đã xác nhận",
            ChineseTextClassification.ConfirmedGlossary => "Thuật ngữ đã xác nhận",
            ChineseTextClassification.Conflict => "Xung đột",
            ChineseTextClassification.Ignored => "Giữ nguyên",
            _ => "Chưa biết"
        };
        public string Chapter => Item.ChapterTitle;
        public int Segment => Item.SegmentOrder;
        public string Sentence => Item.Sentence;
        public string Candidates => Item.Candidates.Count == 0 ? "Chưa có ánh xạ" : string.Join("; ",
            Item.Candidates.Select(x => $"{x.Chinese} → {x.Vietnamese} ({x.SourceLabel}{(x.Conflict ? ", xung đột" : "")})"));
        public string Protection => Item.Protected ? "Đã bảo vệ" : "Có thể sửa";
        public string SearchText => string.Join(" ", Text, Classification, Chapter, Segment, Sentence, Candidates, Protection);
    }
}
