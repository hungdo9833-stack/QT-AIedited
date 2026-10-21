using System.Security.Cryptography;
using System.Text;
using System.IO;
using System.Windows;
using QuickTranslator.AiEditor.Core.Importing;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private async Task<ChapterTitleRuntime> CreateChapterTitleRuntimeAsync(
        ChapterTitleTranslationMethod method, CancellationToken cancellationToken)
    {
        if (method == ChapterTitleTranslationMethod.KeepChinese)
            return new(new KeepChineseChapterTitleTranslationBackend(), new ModelOptions(), null, "none");
        if (method is ChapterTitleTranslationMethod.HanViet or ChapterTitleTranslationMethod.VietPhrase)
        {
            if (_traditionalService.CurrentSnapshot is null)
                throw new InvalidOperationException("Chưa nạp Data QuickTranslator. Hãy chọn thư mục Data trong Thiết lập trước.");
            return new(new TraditionalChapterTitleTranslationBackend(_traditionalService,
                    method == ChapterTitleTranslationMethod.HanViet), new ModelOptions(), null,
                method == ChapterTitleTranslationMethod.HanViet ? "Hán Việt" : "VietPhrase");
        }
        await EnsureGpuBackendsLoadedAsync();
        GpuBackendProfile profile = _gpuBackends.ActiveProfile;
        AiRuntimeProfile runtime = profile.Runtime ?? AiRuntimeProfile.CreateDefault(profile.Model);
        IReadOnlyList<ConfirmedTerm> glossary = _projectSession is null ? [] :
            (await new ProjectResourceStore(_projectSession).LoadAsync(cancellationToken)).Terms;
        return new(new LlamaServerChapterTitleTranslationBackend(_gpuBackends, runtime.ModelOptions, glossary),
            runtime.ModelOptions, profile.Id, runtime.ModelOptions.Model);
    }

    private async Task TranslateChapterTitlesFromManagerAsync(IReadOnlyList<Guid> chapterIds)
    {
        if (_projectSession is null) throw new InvalidOperationException("Project đã đóng.");
        Guid[] ids = chapterIds.Distinct().ToArray();
        if (ids.Length == 0) return;
        EnsureChaptersNotOwnedByActiveTranslationJob(ids);
        ProjectSession session = _projectSession;
        ProjectChapterEntry[] entries = ids.Select(id => session.Manifest.Chapters.SingleOrDefault(entry =>
                entry.ChapterId == id && entry.DeletedAt is null)
            ?? throw new FileNotFoundException($"Không tìm thấy chương hoạt động {id:D}.")).ToArray();
        string identity = string.Join("\n", entries.OrderBy(entry => entry.Number).Select(entry =>
            $"{entry.ChapterId:D}|{entry.Number}|{entry.ChineseTitle}"));
        string sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        string sourceKey = "project-titles:" + sourceHash;
        ChapterTitleTranslationSource[] sources = entries.Select(entry => new ChapterTitleTranslationSource(
            entry.ChapterId, entry.Number, entry.ChineseTitle, entry.VietnameseTitle,
            ProjectRepository.IsProtectedVietnameseTitle(entry))).ToArray();
        var dialog = new ChapterTitleTranslationWindow(session.Manifest.ProjectId, sourceKey, sources,
            CreateChapterTitleRuntimeAsync, session.Resolve($"Cache/title-translation-project-{sourceHash[..24]}.json"))
        { Owner = this };
        if (dialog.ShowDialog() != true || dialog.ResultDraft is not ChapterTitleTranslationDraft draft) return;
        ExistingVietnameseTitlePolicy policy = dialog.OverwriteProtected
            ? ExistingVietnameseTitlePolicy.OverwriteConfirmed : ExistingVietnameseTitlePolicy.KeepExisting;
        if (policy == ExistingVietnameseTitlePolicy.OverwriteConfirmed &&
            ThemedDialog.Show(dialog.Owner, "Các tên tiếng Việt hiện có trong phạm vi này sẽ được thay bằng bản xem trước. " +
                "Nguyên văn, segments, drafts, approvals, history và queue checkpoint vẫn giữ nguyên. Tiếp tục?",
                "Xác nhận thay tiêu đề Việt", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            throw new OperationCanceledException("Đã hủy trước khi ghi đè tiêu đề Việt hiện có.");
        ProjectChapterTitleProposal[] proposals = draft.Items.Select(item =>
            new ProjectChapterTitleProposal(item.Id, item.VietnameseTitle)).ToArray();
        bool completed = await RunWorkspaceTransitionAsync(async () =>
        {
            ProjectChapterTitleUpdatePreview preview = await _projectRepository.PreviewChapterTitleUpdateAsync(
                session, proposals, policy);
            if (preview.ApplyCount == 0) return;
            await _projectRepository.ApplyChapterTitleUpdateAsync(session, preview);
            if (_service is not null && ids.Contains(_service.GetSnapshot().Id))
                await LoadProjectChapterCoreAsync(_service.GetSnapshot().Id);
            else RefreshProjectChapterList();
        });
        if (!completed) throw new OperationCanceledException("Chưa cập nhật tiêu đề vì workspace chưa sẵn sàng.");
    }
}
