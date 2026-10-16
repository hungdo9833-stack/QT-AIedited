using System.Security.Cryptography;
using System.Text;
using System.Windows;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private BookIntelligenceReviewWindow? _bookIntelligenceWindow;

    private void OnOpenBookIntelligence(object sender, RoutedEventArgs e)
    {
        if (_projectSession is null || _projectResources is null)
        {
            ShowError(new InvalidOperationException("Hãy mở một Project trước khi khảo sát nguyên tác."));
            return;
        }
        if (_bookIntelligenceWindow is { IsVisible: true })
        {
            _bookIntelligenceWindow.Activate();
            return;
        }
        _bookIntelligenceWindow = new BookIntelligenceReviewWindow(
            _projectSession,
            _projectRepository,
            _projectResources,
            _ollamaBackends.Profiles,
            EditSuggestedCharacter,
            ApplyBookIntelligenceResources,
            OpenBookIntelligenceEvidenceAsync,
            ApplyBookIntelligenceState)
        { Owner = IsVisible ? this : null };
        _bookIntelligenceWindow.Closed += (_, _) => _bookIntelligenceWindow = null;
        _bookIntelligenceWindow.Show();
    }

    private void ApplyBookIntelligenceState(BookIntelligenceState state, bool contextEnabled)
    {
        _bookIntelligenceState = BookIntelligenceStore.Clone(state);
        _bookKnowledgeContextEnabled = contextEnabled;
        ApplyCurrentProjectResources();
    }

    private CharacterProfile? EditSuggestedCharacter(IntelligenceCharacterSuggestion suggestion)
    {
        CharacterProject snapshot = CharacterProjectStore.Clone(_characterProject);
        try
        {
            var profile = new CharacterProfile
            {
                ChineseName = suggestion.ChineseName,
                VietnameseName = suggestion.VietnameseName,
                Aliases = suggestion.Aliases.ToList(),
                Gender = suggestion.Gender,
                PrimaryNarrativePronoun = suggestion.PrimaryNarrativePronoun,
                SecondaryNarrativePronoun = suggestion.SecondaryNarrativePronoun,
                NormalizationEnabled = suggestion.Gender != CharacterGender.Unknown ||
                    !string.IsNullOrWhiteSpace(suggestion.PrimaryNarrativePronoun),
                AddressingNotes = suggestion.Reason
            };
            profile.ApplyGenderDefaults();
            return ShowCharacterEditor(profile);
        }
        finally
        {
            // The existing editor validates/previews relationships against the in-memory project.
            // The review service owns the actual resource commit, so do not mutate it before that commit succeeds.
            _characterProject = snapshot;
            RefreshCharacterGrid();
        }
    }

    private void ApplyBookIntelligenceResources(ProjectResources resources)
    {
        _projectResources = resources.Clone();
        LoadCharacterProject(_projectResources.Characters);
        SetGlossaryTerms(_projectResources.Terms);
        ApplyCurrentProjectResources();
        SetStatus("Đã lưu đề xuất được xác nhận vào tài nguyên Project.");
    }

    private async Task OpenBookIntelligenceEvidenceAsync(CorpusEvidence evidence)
    {
        await LoadProjectChapterAsync(evidence.ChapterId);
        string currentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ChapterInput.Text)));
        if (!evidence.IsCurrent || !string.Equals(currentHash, evidence.SourceSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Nguyên tác đã đổi sau khi tạo chứng cứ này. Hãy chạy quét lại; excerpt cũ vẫn được giữ trong hàng chờ để đối chiếu.");
        ChapterInput.Focus();
        int start = Math.Min(evidence.StartUtf16, ChapterInput.Text.Length);
        int length = Math.Min(evidence.LengthUtf16, ChapterInput.Text.Length - start);
        ChapterInput.Select(start, length);
        ChapterInput.ScrollToLine(ChapterInput.GetLineIndexFromCharacterIndex(start));
        SetStatus("Đã mở đúng chương và vùng nguyên tác dùng làm chứng cứ khảo sát.");
    }
}
