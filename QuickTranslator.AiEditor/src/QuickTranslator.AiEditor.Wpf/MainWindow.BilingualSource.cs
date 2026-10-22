using System.Windows;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.Traditional;

namespace QuickTranslator.AiEditor.Wpf;

/// <summary>
/// The left cell of the two-pane bilingual grid can show the Chinese source or its offline Hán Việt / VP đơn ý
/// rendering (the same QuickTranslator data as the four-pane view). Renderings are cached per source text and options.
/// </summary>
public partial class MainWindow
{
    private const int BilingualSourceCacheLimit = 600;
    private readonly Dictionary<string, TraditionalTranslationResult> _bilingualSourceCache = new(StringComparer.Ordinal);
    private int _bilingualSourceGeneration;
    private bool _syncingBilingualSourceView;

    private void ApplyBilingualSourceViewSelection()
    {
        _syncingBilingualSourceView = true;
        try { BilingualSourceViewInput.SelectedIndex = (int)_workspaceLayout.BilingualSourceView; }
        finally { _syncingBilingualSourceView = false; }
    }

    private async void OnBilingualSourceViewChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingBilingualSourceView || BilingualSourceViewInput.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !Enum.TryParse(tag, out BilingualSourceView view)) return;
        _workspaceLayout.BilingualSourceView = view;
        RefreshBilingualSourceDisplay();
        try { await SaveWorkspaceLayoutAsync(); }
        catch (Exception ex) { SetStatus("Chưa lưu được lựa chọn ô 1: " + ex.Message); }
    }

    private void RefreshBilingualSourceDisplay()
    {
        int generation = ++_bilingualSourceGeneration;
        BilingualSourceView view = _workspaceLayout.BilingualSourceView;
        if (view == BilingualSourceView.Chinese)
        {
            foreach (ChapterBlock block in _chapterBlocks) block.DisplaySource = block.SourceText;
            return;
        }
        var snapshot = _traditionalService.CurrentSnapshot;
        if (snapshot is null)
        {
            foreach (ChapterBlock block in _chapterBlocks)
                block.DisplaySource = "Chưa nạp Data QuickTranslator (Thiết lập › Dịch truyền thống).\n\n" + block.SourceText;
            return;
        }
        TraditionalTranslationOptions options = new()
        {
            PrioritizeNames = TraditionalPrioritizeNamesInput.IsChecked == true,
            TranslationAlgorithm = Math.Clamp(TraditionalAlgorithmInput.SelectedIndex, 0, 2)
        };
        string prefix = $"{snapshot.Manifest.Fingerprint}:{options.PrioritizeNames}:{options.TranslationAlgorithm}:";
        var pending = new List<ChapterBlock>();
        foreach (ChapterBlock block in _chapterBlocks)
        {
            if (_bilingualSourceCache.TryGetValue(prefix + block.SourceText, out TraditionalTranslationResult? cached))
                block.DisplaySource = Rendering(cached, view);
            else
            {
                block.DisplaySource = view == BilingualSourceView.HanViet ? "Đang chuyển Hán Việt…" : "Đang dịch VP đơn ý…";
                pending.Add(block);
            }
        }
        if (pending.Count > 0) FillBilingualSourcesAsync(pending, prefix, options, view, generation);
    }

    // One segment at a time, in reading order, so the visible top of the chapter fills first.
    private async void FillBilingualSourcesAsync(IReadOnlyList<ChapterBlock> blocks, string prefix,
        TraditionalTranslationOptions options, BilingualSourceView view, int generation)
    {
        foreach (ChapterBlock block in blocks)
        {
            if (generation != _bilingualSourceGeneration || _closing) return;
            string source = block.SourceText;
            try
            {
                if (!_bilingualSourceCache.TryGetValue(prefix + source, out TraditionalTranslationResult? result))
                {
                    result = await _traditionalService.TranslateAsync(source, options);
                    if (_bilingualSourceCache.Count >= BilingualSourceCacheLimit) _bilingualSourceCache.Clear();
                    _bilingualSourceCache[prefix + source] = result;
                }
                if (generation == _bilingualSourceGeneration && block.SourceText == source)
                    block.DisplaySource = Rendering(result, view);
            }
            catch (Exception ex)
            {
                if (generation != _bilingualSourceGeneration) return;
                block.DisplaySource = (view == BilingualSourceView.HanViet ? "Không tạo được Hán Việt: " : "Không tạo được VP đơn ý: ") +
                    ex.Message + "\n\n" + source;
            }
        }
    }

    private static string Rendering(TraditionalTranslationResult result, BilingualSourceView view) =>
        view == BilingualSourceView.HanViet ? result.HanVietText : result.VietPhraseText;

    /// <summary>A bilingual left cell that currently shows Vietnamese, so its selection is not Chinese source text.</summary>
    private bool ShowsTranslatedSource(TextBox box) =>
        box.Tag is Guid && _workspaceLayout.BilingualSourceView != BilingualSourceView.Chinese;
}
