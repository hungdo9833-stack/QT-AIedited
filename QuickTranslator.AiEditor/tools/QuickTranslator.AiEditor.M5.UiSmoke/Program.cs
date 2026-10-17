using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickTranslator.AiEditor.Core.Importing;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Reading;
using QuickTranslator.AiEditor.Core.Translation;
using QuickTranslator.AiEditor.Wpf;

namespace QuickTranslator.AiEditor.M5.UiSmoke;

internal static class Program
{
    private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static void Main(string[] args)
    {
        string mode = args.FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal)) ?? "two";
        string? capturePath = ValueAfter(args, "--capture");
        double captureDpi = NumberAfter(args, "--dpi") ?? 96;
        double? requestedWidth = NumberAfter(args, "--width");
        double? requestedHeight = NumberAfter(args, "--height");
        string root = ValueAfter(args, "--root") ?? Path.Combine(Path.GetTempPath(),
            "qt-studio-m5-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var application = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.InitializeComponent();
        if (string.Equals(mode, "queue-benchmark", StringComparison.OrdinalIgnoreCase))
        {
            int count = (int)(NumberAfter(args, "--count") ?? 1000);
            RunQueueBenchmark(root, count);
            application.Shutdown();
            return;
        }
        if (string.Equals(mode, "editor-refresh-benchmark", StringComparison.OrdinalIgnoreCase))
        {
            int count = (int)(NumberAfter(args, "--count") ?? 500);
            RunEditorRefreshBenchmark(root, count);
            application.Shutdown();
            return;
        }
        ProjectSession session = CreateSyntheticProjectAsync(root).GetAwaiter().GetResult();
        Window window = CreateWindow(mode, session, root);
        if (requestedWidth is > 0) window.Width = requestedWidth.Value;
        if (requestedHeight is > 0) window.Height = requestedHeight.Value;
        if (window is MainWindow && requestedWidth is > 0)
            Invoke(window, "UpdateResponsiveShell", requestedWidth.Value);
        application.MainWindow = window;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (window is MainWindow)
        {
            if (string.Equals(mode, "reader", StringComparison.OrdinalIgnoreCase))
                Invoke(window, "ShowReaderWorkspace");
            else
                Invoke(window, "ShowTranslationWorkspace", string.Equals(mode, "four", StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(capturePath))
        {
            PrepareCaptureLayout(window);
            Capture(window, capturePath, captureDpi);
            AllowClose(window);
            window.Close();
            application.Shutdown();
            return;
        }

        window.Show();
        PumpDispatcher(TimeSpan.FromMilliseconds(900));
        window.Closed += (_, _) => application.Shutdown();
        application.Run();
    }

    private static void RunQueueBenchmark(string root, int count)
    {
        ProjectSession session = new ProjectRepository().CreateAsync(Path.Combine(root, "QueueBenchmark"),
            "Queue Benchmark").GetAwaiter().GetResult();
        var client = new SyntheticClient();
        var resources = new ProjectResources { ProjectId = session.Manifest.ProjectId };
        var coordinator = new ProjectTranslationCoordinator(session, new ProjectRepository(), client,
            _ => Task.FromResult(new ProjectTranslationEnvironment(new ModelOptions(), PromptPreset.Default, resources)));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        const string hash = "0000000000000000000000000000000000000000000000000000000000000000";
        var plan = new ProjectTranslationJob
        {
            ProjectId = session.Manifest.ProjectId,
            Status = ProjectTranslationJobStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
            Items = Enumerable.Range(1, count).Select(number => new ProjectTranslationItem
            {
                ChapterId = Guid.NewGuid(), Number = number, Order = number, Title = "Chương " + number,
                SourceFingerprint = hash, ProtectedFingerprint = hash,
                TotalSegments = 12, CompletedSegments = number % 12
            }).ToList()
        };
        var window = new ProjectTranslationQueueWindow("Benchmark", coordinator, plan);
        try
        {
            _ = window.MeasureRefresh();
            int repetitions = count >= 5000 ? 50 : 200;
            TimeSpan cpuBefore = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
            var wall = System.Diagnostics.Stopwatch.StartNew();
            double refreshMilliseconds = 0;
            long allocatedBytes = 0;
            ProjectTranslationQueueWindow.QueueRefreshMeasurement measured = default!;
            for (int index = 0; index < repetitions; index++)
            {
                measured = window.MeasureRefresh();
                refreshMilliseconds += measured.Elapsed.TotalMilliseconds;
                allocatedBytes += measured.AllocatedBytes;
            }
            wall.Stop();
            double cpuMilliseconds = (System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds;
            Console.WriteLine($"QUEUE_UI count={count} repetitions={repetitions} " +
                $"avg_refresh_ms={refreshMilliseconds / repetitions:F3} avg_allocated_bytes={allocatedBytes / repetitions} " +
                $"cpu_ms={cpuMilliseconds:F3} wall_ms={wall.Elapsed.TotalMilliseconds:F3} " +
                $"row_objects={measured.RowObjectCreations}");
        }
        finally { window.Close(); }
    }

    private static void RunEditorRefreshBenchmark(string root, int count)
    {
        ProjectSession session = CreateSyntheticProjectAsync(root).GetAwaiter().GetResult();
        MainWindow window = CreateEditor(session, root, fourPane: false);
        try
        {
            string source = string.Join("\n", Enumerable.Repeat("王秀兰看见李明走进客栈。", count));
            ChapterDocument chapter = TranslationService.CreateChapter("Refresh benchmark", source,
                new SegmenterOptions { MinimumHanCharacters = 1, TargetHanCharacters = 8,
                    MaximumHanCharacters = 16, MaximumTextElements = 2000 });
            foreach (TranslationSegment segment in chapter.Segments)
            {
                segment.UserDraft = "Bản dịch tổng hợp cho phép đo Refresh.";
                segment.Status = SegmentStatus.AwaitingReview;
                segment.Attempts.Add(new TranslationAttempt
                {
                    StartedAt = DateTimeOffset.UtcNow,
                    FinishedAt = DateTimeOffset.UtcNow,
                    Outcome = AttemptOutcome.Completed,
                    AiText = segment.UserDraft,
                    Prompt = "Synthetic benchmark prompt " + new string('x', 256),
                    PromptVersion = "benchmark"
                });
            }
            Set(window, "_projectSession", null);
            Set(window, "_store", new ChapterFileStore(Path.Combine(root, "RefreshBenchmark")));
            Invoke(window, "AttachChapter", chapter);
            Set(window, "_selectedId", chapter.Segments[0].Id);
            Invoke(window, "ApplyInputs", chapter);
            Invoke(window, "Refresh");

            const int repetitions = 20;
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            for (int index = 0; index < repetitions; index++) Invoke(window, "Refresh");
            stopwatch.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Console.WriteLine($"EDITOR_REFRESH segments={chapter.Segments.Count} repetitions={repetitions} " +
                $"avg_refresh_ms={stopwatch.Elapsed.TotalMilliseconds / repetitions:F3} " +
                $"avg_allocated_bytes={allocated / repetitions}");
        }
        finally
        {
            Set(window, "_allowClose", true);
            window.Close();
        }
    }

    private static async Task<ProjectSession> CreateSyntheticProjectAsync(string root)
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(root, "Tam-Gioi-Ky"), "Tam Giới Ký");
        session.Manifest.ChineseTitle = "三界记";
        session.Manifest.Author = "Mặc Vân";
        session.Manifest.Status = ProjectStatus.Translating;
        session.Manifest.Description = "Project tổng hợp chỉ dùng để kiểm tra giao diện QT Studio M5.";
        await repository.SaveManifestAsync(session);
        string[] chinese =
        [
            "夜雨落在青石长街上，王秀兰撑伞走进客栈。\n她抬头看见李明，轻声问道：‘你也来了？’\n窗外风声渐紧，故事从这一夜开始。",
            "晨光越过山门，众弟子已经聚在演武场。\n李明握紧木剑，等待师父宣布试炼。",
            "古城的钟声响了三次。\n商队沿着河岸缓慢前行，没有人注意到水面的黑影。"
        ];
        for (int number = 1; number <= 24; number++)
        {
            string title = number switch { 1 => "Mưa đêm nơi cổ trấn", 2 => "Thử luyện sơn môn", _ => $"Hành trình phía Bắc {number}" };
            ChapterDocument chapter = TranslationService.CreateChapter(title, chinese[(number - 1) % chinese.Length],
                new SegmenterOptions { MinimumHanCharacters = 1, TargetHanCharacters = 24, MaximumHanCharacters = 60 });
            chapter.ChineseTitle = $"第{number}章 " + (number == 1 ? "古镇夜雨" : number == 2 ? "山门试炼" : "北境行记");
            for (int index = 0; index < chapter.Segments.Count; index++)
            {
                TranslationSegment segment = chapter.Segments[index];
                if (number <= 18 || number == 1)
                {
                    segment.UserDraft = number == 1
                        ? new[] { "Mưa đêm rơi trên con phố lát đá xanh, Vương Tú Lan che ô bước vào khách điếm.",
                            "Nàng ngẩng đầu nhìn thấy Lý Minh, khẽ hỏi: “Huynh cũng tới rồi sao?”",
                            "Gió ngoài cửa sổ mỗi lúc một mạnh, câu chuyện bắt đầu từ đêm ấy." }[Math.Min(index, 2)]
                        : $"Bản Việt tổng hợp của chương {number}, đoạn {index + 1}.";
                    segment.DraftRevision = 1;
                    segment.Status = number <= 14 ? SegmentStatus.Approved : SegmentStatus.AwaitingReview;
                    if (segment.Status == SegmentStatus.Approved)
                        segment.Approvals.Add(new ApprovalRecord { ApprovedAt = DateTimeOffset.UtcNow,
                            DraftRevision = 1, Text = segment.UserDraft });
                }
            }
            await repository.AddChapterAsync(session, chapter, number, chapter.ChineseTitle);
        }
        session.Manifest.CurrentChapterId = session.Manifest.Chapters[0].ChapterId;
        session.Manifest.Status = ProjectStatus.Translating;
        await repository.SaveManifestAsync(session);
        return session;
    }

    private static Window CreateWindow(string mode, ProjectSession session, string root)
    {
        var repository = new ProjectRepository();
        switch (mode.ToLowerInvariant())
        {
            case "library":
                return CreateLibrary(session);
            case "chapter":
                return new ChapterManagerWindow(session, new ProjectCatalog(repository), new ChapterManagerCommands(
                    _ => Task.CompletedTask, (_, _, _) => Task.CompletedTask, _ => Task.CompletedTask,
                    _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask,
                    _ => Task.CompletedTask));
            case "queue":
            {
                var client = new SyntheticClient();
                var resources = new ProjectResources { ProjectId = session.Manifest.ProjectId, Revision = 3,
                    Characters = new CharacterProject { Id = session.Manifest.ProjectId, Name = session.Manifest.VietnameseTitle } };
                var coordinator = new ProjectTranslationCoordinator(session, repository, client,
                    _ => Task.FromResult(new ProjectTranslationEnvironment(new ModelOptions { Model = "translategemma:12b" },
                        PromptPreset.Default, resources)));
                ProjectTranslationJob plan = coordinator.PlanAsync(session.Manifest.Chapters.Skip(14).Take(8)
                    .Select(chapter => chapter.ChapterId)).GetAwaiter().GetResult();
                return new ProjectTranslationQueueWindow(session.Manifest.VietnameseTitle, coordinator, plan);
            }
            case "parallel-queue":
            {
                var resources = new ProjectResources { ProjectId = session.Manifest.ProjectId, Revision = 3,
                    Characters = new CharacterProject { Id = session.Manifest.ProjectId, Name = session.Manifest.VietnameseTitle } };
                var options = new ModelOptions { Model = "translategemma:12b", ContextSize = 4096,
                    MaximumOutputTokens = 2048, Temperature = 0.1 };
                var remoteOptions = options with { ContextSize = 8192 };
                ProjectTranslationWorkerDefinition[] workers =
                [
                    new(Guid.Parse("11111111-1111-4111-8111-111111111111"), OllamaBackendSnapshot.LocalDefault(options.Model),
                        new SyntheticClient(), options, new SegmenterOptions { MinimumHanCharacters = 250,
                            TargetHanCharacters = 375, MaximumHanCharacters = 500 }, 0),
                    new(Guid.Parse("22222222-2222-4222-8222-222222222222"),
                        new OllamaBackendSnapshot(Guid.Parse("33333333-3333-4333-8333-333333333333"),
                            OllamaBackendKind.Remote, "RTX 5060 Ti Remote", "http://127.0.0.1:43117/api/chat", remoteOptions.Model),
                        new SyntheticClient(), remoteOptions, new SegmenterOptions { MinimumHanCharacters = 350,
                            TargetHanCharacters = 550, MaximumHanCharacters = 750 }, 0.45m, "synthetic-ssh-host")
                ];
                var coordinator = new ProjectTranslationCoordinator(session, repository, workers,
                    _ => Task.FromResult(new ProjectTranslationEnvironment(options, PromptPreset.Default, resources)));
                ProjectTranslationJob plan = coordinator.PlanAsync(session.Manifest.Chapters.Skip(8).Take(12)
                    .Select(chapter => chapter.ChapterId)).GetAwaiter().GetResult();
                return new ProjectTranslationQueueWindow(session.Manifest.VietnameseTitle, coordinator, plan);
            }
            case "gpu":
                return CreateGpuManager(root);
            case "titles":
                return CreateTitleTranslationWindow(session, root);
            case "four":
                return CreateEditor(session, root, fourPane: true);
            case "reader":
                return CreateReader(session, root);
            default:
                return CreateEditor(session, root, fourPane: false);
        }
    }

    private static GpuManagerWindow CreateGpuManager(string root)
    {
        var store = new BackendProfileStore(Path.Combine(root, "gpu-profiles.json"), new SyntheticProtector());
        var manager = new OllamaBackendManager(store, _ => new SyntheticBackendClient());
        manager.LoadAsync().GetAwaiter().GetResult();
        var remote = new OllamaBackendProfile
        {
            Id = Guid.Parse("33333333-3333-4333-8333-333333333333"), Name = "RTX 5060 Ti Remote",
            Kind = OllamaBackendKind.Remote, Endpoint = "http://127.0.0.1:11434", Model = "translategemma:12b",
            Runtime = new AiRuntimeProfile
            {
                ModelOptions = new ModelOptions { Model = "translategemma:12b", ContextSize = 8192,
                    MaximumOutputTokens = 2048, Temperature = 0.1, RequestTimeout = TimeSpan.FromSeconds(300) },
                SegmentationOptions = new SegmenterOptions { MinimumHanCharacters = 350,
                    TargetHanCharacters = 550, MaximumHanCharacters = 750 }
            },
            HourlyPrice = 0.45m, RemoteDisclosureAccepted = true,
            Ssh = new() { Host = "gpu-rental.example", Port = 22022, Username = "ubuntu",
                Authentication = SshAuthenticationKind.Password, Password = "synthetic",
                HostKeyFingerprint = "SHA256:abcdefghijklmnopqrstuv", RemoteOllamaPort = 11434 }
        };
        manager.SaveProfileAsync(remote, activate: true).GetAwaiter().GetResult();
        var preferences = new GpuTranslationPreferences { Mode = GpuTranslationMode.ParallelLocalRemote,
            RemoteProfileId = remote.Id };
        return new GpuManagerWindow(manager, new GpuConnectionManager(), preferences);
    }

    private static ChapterTitleTranslationWindow CreateTitleTranslationWindow(ProjectSession session, string root)
    {
        ChapterTitleTranslationSource[] sources = session.Manifest.Chapters.Take(20).Select((entry, index) =>
            new ChapterTitleTranslationSource(entry.ChapterId, entry.Number, entry.ChineseTitle,
                index is 3 or 11 ? entry.VietnameseTitle : "", index is 3 or 11)).ToArray();
        var window = new ChapterTitleTranslationWindow(session.Manifest.ProjectId, "smoke-titles", sources,
            (method, _) => Task.FromResult(new ChapterTitleRuntime(new KeepChineseChapterTitleTranslationBackend(),
                new ModelOptions(), null, "none")), Path.Combine(root, "title-draft.json"));
        ChapterTitleTranslationDraft draft = ChapterTitleTranslationDraft.Create(session.Manifest.ProjectId,
            "smoke-titles", ChapterTitleTranslationMethod.Ollama, sources, Guid.NewGuid(), "translategemma:12b");
        for (int index = 0; index < draft.Items.Count; index++)
        {
            ChapterTitleTranslationItem item = draft.Items[index];
            if (item.ExistingTitleProtected) continue;
            if (index == 7)
            {
                item.Status = ChapterTitleTranslationStatus.Failed;
                item.Error = "Timeout · có thể Retry riêng batch lỗi";
                continue;
            }
            item.TranslatedName = "Tên chương Việt " + item.ChapterNumber;
            item.VietnameseTitle = $"Chương {item.ChapterNumber}: Tên chương Việt {item.ChapterNumber}";
            item.Status = ChapterTitleTranslationStatus.Completed;
        }
        Set(window, "_draft", draft);
        Invoke(window, "RefreshRows");
        return window;
    }

    private static MainWindow CreateReader(ProjectSession session, string root)
    {
        MainWindow window = CreateEditor(session, root, fourPane: false);
        Set(window, "_readerPreferences", new ReaderPreferences { Theme = ReaderTheme.Dark,
            FontFamily = "Segoe UI Variable Text", FontSize = 20, LineHeight = 1.65,
            ParagraphSpacing = 10, PageWidth = ReaderPageWidth.Comfortable, TableOfContentsVisible = true });
        Set(window, "_readerReady", true);
        Invoke(window, "LoadReaderAppearanceControls");
        Invoke(window, "ApplyReaderAppearance");
        Invoke(window, "RefreshReaderCatalog");
        Invoke(window, "ShowReaderWorkspace");
        return window;
    }

    private static ProjectLibraryWindow CreateLibrary(ProjectSession session)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        var cards = new[]
        {
            new ProjectLibraryCard(session.Manifest.ProjectId, "Tam Giới Ký", "Mặc Vân", ProjectStatus.Translating,
                session.ManifestPath, null, 1240, 638, 0.5145, "Chương 639 · Vượt Hắc Thủy", now, false, null),
            new ProjectLibraryCard(Guid.NewGuid(), "Kiếm Đạo Trường Sinh", "Thanh Phong", ProjectStatus.Editing,
                @"C:\Synthetic\KiemDao\project.qtproj", null, 860, 742, 0.8628, "Chương 743 · Cổ kiếm", now.AddDays(-1), false, null),
            new ProjectLibraryCard(Guid.NewGuid(), "Minh Triều Dạ Hành", "Vân Gian", ProjectStatus.Translating,
                @"C:\Synthetic\MinhTrieu\project.qtproj", null, 510, 201, 0.3941, "Chương 202 · Kinh thành", now.AddDays(-3), false, null),
            new ProjectLibraryCard(Guid.NewGuid(), "Sơn Hải Du Ký", "Hải Khách", ProjectStatus.Completed,
                @"C:\Synthetic\SonHai\project.qtproj", null, 320, 320, 1, "Chương 320 · Đại kết cục", now.AddDays(-8), false, null),
            new ProjectLibraryCard(Guid.NewGuid(), "Tinh Hà Vấn Đạo", "Cửu Tiêu", ProjectStatus.Planning,
                @"C:\Synthetic\TinhHa\project.qtproj", null, 96, 0, 0, null, now.AddDays(-12), false, null)
        };
        return new ProjectLibraryWindow(cards, session.Manifest.ProjectId);
    }

    private static MainWindow CreateEditor(ProjectSession session, string root, bool fourPane)
    {
        ChapterDocument chapter = new ProjectRepository().LoadChapterAsync(session,
            session.Manifest.CurrentChapterId!.Value).GetAwaiter().GetResult().Chapter;
        var window = new MainWindow(null, initializeApplication: false) { Width = 1600, Height = 900 };
        Set(window, "_workspaceLayoutStore", new WorkspaceLayoutStore(Path.Combine(root, "workspace-layout.json")));
        Set(window, "_presetStore", new PromptPresetStore(Path.Combine(root, "settings", "presets.qtaip.json")));
        Set(window, "_readerStore", new ReaderPreferencesStore(Path.Combine(root, "settings", "reader.json")));
        Set(window, "_characterStore", new CharacterProjectStore(Path.Combine(root, "characters")));
        Set(window, "_projectSession", session);
        Set(window, "_store", session.ChapterStore);
        var projectSwitcher = (ComboBox)window.FindName("ProjectSwitcher");
        projectSwitcher.DisplayMemberPath = "";
        projectSwitcher.ItemsSource = new[] { session.Manifest.VietnameseTitle };
        projectSwitcher.SelectedIndex = 0;
        Invoke(window, "InitializeChapterView");
        ((ListBox)window.FindName("ProjectChapterList")).ItemsSource = (IEnumerable)Get(window, "_projectChapters");
        Invoke(window, "InitializeProductionShell");

        Invoke(window, "SetGlossaryTerms", (object)new[] { new ConfirmedTerm("王秀兰", "Vương Tú Lan"),
            new ConfirmedTerm("李明", "Lý Minh"), new ConfirmedTerm("客栈", "khách điếm") });
        ((TextBox)window.FindName("ContextNotesInput")).Text =
            "Cổ trang huyền huyễn. Vương Tú Lan và Lý Minh vừa gặp lại tại cổ trấn trước kỳ thử luyện.";
        Invoke(window, "LoadCharacterProject", new CharacterProject { Id = session.Manifest.ProjectId,
            Name = session.Manifest.VietnameseTitle, Profiles =
            [
                new CharacterProfile { ChineseName = "王秀兰", VietnameseName = "Vương Tú Lan", PrimaryNarrativePronoun = "nàng" },
                new CharacterProfile { ChineseName = "李明", VietnameseName = "Lý Minh", PrimaryNarrativePronoun = "hắn" }
            ] });
        Invoke(window, "AttachChapter", chapter);
        Set(window, "_selectedId", chapter.SelectedSegmentId ?? chapter.Segments[0].Id);
        Invoke(window, "ApplyInputs", chapter);
        Invoke(window, "RefreshProjectChapterList");
        Invoke(window, "Refresh");
        Invoke(window, "ShowTranslationWorkspace", fourPane);
        return window;
    }

    private static void Capture(Window window, string path, double dpi)
    {
        FrameworkElement visual = window.Content as FrameworkElement ?? window;
        var available = new Size(window.Width, window.Height);
        visual.Measure(available);
        visual.Arrange(new Rect(0, 0, available.Width, available.Height));
        visual.UpdateLayout();
        // Star-sized DataGrid columns settle after their parent has an actual size.
        // Run a second layout pass so off-screen acceptance captures match a shown window.
        visual.InvalidateMeasure();
        visual.Measure(available);
        visual.Arrange(new Rect(0, 0, available.Width, available.Height));
        visual.UpdateLayout();
        double scale = dpi / 96d;
        int width = Math.Max(1, (int)Math.Ceiling(available.Width * scale));
        int height = Math.Max(1, (int)Math.Ceiling(available.Height * scale));
        var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void PrepareCaptureLayout(Window window)
    {
        // Star column sizing is normally finalized by PresentationSource when a window is
        // shown. The smoke tool renders off-screen, so give those columns their equivalent
        // acceptance-capture widths without changing production XAML.
        if (window.FindName("TitleGrid") is DataGrid titleGrid && titleGrid.Columns.Count == 6)
        {
            double[] widths = [75, 220, 220, 300, 125, 190];
            for (int index = 0; index < widths.Length; index++)
                titleGrid.Columns[index].Width = widths[index];
        }
        if (window.FindName("ChapterGrid") is DataGrid chapterGrid && chapterGrid.Columns.Count == 10)
        {
            chapterGrid.Columns[2].Width = 210;
            chapterGrid.Columns[3].Width = 180;
        }
        if (window.FindName("QueueGrid") is DataGrid queueGrid && queueGrid.Columns.Count == 7)
        {
            queueGrid.Columns[2].Width = 280;
            queueGrid.Columns[6].Width = 320;
        }
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        using var timer = new System.Threading.Timer(_ => dispatcher.BeginInvoke(
            DispatcherPriority.Send, () => frame.Continue = false), null, duration, Timeout.InfiniteTimeSpan);
        Dispatcher.PushFrame(frame);
    }

    private static void AllowClose(Window window)
    {
        if (window is MainWindow) Set(window, "_allowClose", true);
    }

    private static string? ValueAfter(string[] args, string name)
    {
        int index = Array.FindIndex(args, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static double? NumberAfter(string[] args, string name) =>
        double.TryParse(ValueAfter(args, name), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : null;

    private static object Get(object target, string name) => target.GetType().GetField(name, PrivateInstance)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, PrivateInstance)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, PrivateInstance)!.Invoke(target, args);

    private sealed class SyntheticClient : IOllamaClient
    {
        public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options,
            CancellationToken cancellationToken = default) => Task.FromResult(new OllamaCompletion(
                "Bản dịch tổng hợp.", true, "stop", new GenerationMetrics()));
    }

    private sealed class SyntheticBackendClient : IOllamaBackendClient
    {
        public Task CheckConnectionAsync(string model, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListModelsAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["translategemma:12b"]);
        public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options,
            CancellationToken cancellationToken = default) => Task.FromResult(new OllamaCompletion(
                "Bản dịch tổng hợp.", true, "stop", new GenerationMetrics()));
        public void Dispose() { }
    }

    private sealed class SyntheticProtector : IOllamaSecretProtector
    {
        public string Protect(string value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
        public string Unprotect(string value) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }
}
