using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
        ProjectSession session = CreateSyntheticProjectAsync(root).GetAwaiter().GetResult();
        Window window = CreateWindow(mode, session, root);
        if (requestedWidth is > 0) window.Width = requestedWidth.Value;
        if (requestedHeight is > 0) window.Height = requestedHeight.Value;
        application.MainWindow = window;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Show();
        PumpDispatcher(TimeSpan.FromMilliseconds(900));
        if (window is MainWindow)
        {
            if (string.Equals(mode, "reader", StringComparison.OrdinalIgnoreCase))
                Invoke(window, "ShowReaderWorkspace");
            else
                Invoke(window, "ShowTranslationWorkspace", string.Equals(mode, "four", StringComparison.OrdinalIgnoreCase));
            PumpDispatcher(TimeSpan.FromMilliseconds(250));
        }

        if (!string.IsNullOrWhiteSpace(capturePath))
        {
            Capture(window, capturePath, captureDpi);
            AllowClose(window);
            window.Close();
            application.Shutdown();
            return;
        }

        window.Closed += (_, _) => application.Shutdown();
        application.Run();
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
                    _ => Task.CompletedTask, _ => Task.CompletedTask));
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
            case "four":
                return CreateEditor(session, root, fourPane: true);
            case "reader":
                return CreateReader(session, root);
            default:
                return CreateEditor(session, root, fourPane: false);
        }
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
        window.Activate();
        window.UpdateLayout();
        double scale = dpi / 96d;
        int width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * scale));
        int height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * scale));
        var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
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
}
