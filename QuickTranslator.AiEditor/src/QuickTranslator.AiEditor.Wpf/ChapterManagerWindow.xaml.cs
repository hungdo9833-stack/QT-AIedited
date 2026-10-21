using System.Windows;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

public partial class ChapterManagerWindow : Window
{
    internal ChapterManagerWindow(ProjectSession session, ProjectCatalog catalog, ChapterManagerCommands commands)
    {
        InitializeComponent();
        View = new ChapterManagerView(session, catalog, commands);
        ViewHost.Content = View;
        View.CloseRequested += OnCloseRequested;
        Closed += OnClosed;
        MainWindow.ClampWindowToWorkArea(this, 1320, 820);
        View.Attach();
    }

    internal ChapterManagerView View { get; }
    internal IReadOnlyList<ChapterManagerRow> CurrentRows => View.CurrentRows;
    internal bool UsesRecyclingVirtualization => View.UsesRecyclingVirtualization;
    internal int SelectChapterRange(int fromNumber, int toNumber) => View.SelectChapterRange(fromNumber, toNumber);

    public new object FindName(string name) => View.FindName(name) ?? base.FindName(name) ??
        throw new ArgumentException($"Không tìm thấy điều khiển '{name}'.", nameof(name));

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        View.CloseRequested -= OnCloseRequested;
        View.Detach();
    }

    internal sealed record ChapterManagerRow(
        Guid ChapterId, int Number, long DisplayOrder, string VietnameseTitle, string ChineseTitle,
        string VolumeLabel, ProjectChapterState TranslationState, string TranslationLabel,
        ProjectCatalogApprovalState ApprovalState, string ApprovalLabel, string ProgressLabel,
        DateTimeOffset UpdatedAt, string UpdatedLabel, bool IsDeleted, string DeletedLabel)
    {
        public ChapterManagerRow(ProjectCatalogChapter chapter) : this(
            chapter.ChapterId, chapter.Number, chapter.DisplayOrder, chapter.VietnameseTitle, chapter.ChineseTitle,
            chapter.VolumeTitle ?? "—", chapter.TranslationState, StateLabel(chapter.TranslationState), chapter.ApprovalState,
            chapter.ApprovalState == ProjectCatalogApprovalState.Approved ? "Đã duyệt" : "Chưa duyệt xong",
            chapter.SegmentCount == 0 ? "0/0" : $"{chapter.CompletedSegmentCount:N0}/{chapter.SegmentCount:N0}",
            chapter.UpdatedAt, chapter.UpdatedAt.LocalDateTime.ToString("g"), chapter.IsDeleted,
            chapter.IsDeleted ? "Đã xóa" : "Hoạt động") { }

        private static string StateLabel(ProjectChapterState state) => state switch
        {
            ProjectChapterState.Untranslated => "Chưa dịch", ProjectChapterState.Translating => "Đang dịch",
            ProjectChapterState.AwaitingReview => "Chờ duyệt", ProjectChapterState.NeedsInspection => "Cần kiểm tra",
            ProjectChapterState.Approved => "Đã duyệt", _ => "Lỗi"
        };
    }
}
