using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace QuickTranslator.AiEditor.Core.Importing;

public enum ImportPreviewIssueCode
{
    NoChapterHeadings,
    MissingNumber,
    MissingTitle,
    DuplicateNumber,
    ReversedNumber,
    NumberJump,
    UncertainBoundary,
    UnclassifiedSource,
    EmptyContent,
    UnreviewedExclusion,
    ExcludedDifferentContent,
    /// <summary>Prose outside every chapter: kept in the snapshot, not imported (warning).</summary>
    OutsideChapterProse,
    /// <summary>A chapter with text that is not imported (excluded by the user or by a default decision): kept in the snapshot.</summary>
    ChapterNotImported
}

public sealed record ImportPreviewIssue(
    ImportPreviewIssueCode Code,
    DetectionIssueSeverity Severity,
    string Message,
    ImmutableArray<Guid> ItemIds);

public sealed record ImportPreviewItem(
    Guid Id,
    Guid ChapterId,
    PlannedRegionKind Kind,
    SourceSpan Span,
    SourceSpan? HeadingSpan,
    SourceSpan ContentSpan,
    int? Number,
    string Title,
    string? VolumeTitle,
    string RawHeading,
    bool Included,
    CandidateDecision Decision,
    int? OriginalCandidateIndex)
{
    public bool IsChapter => Kind is PlannedRegionKind.Chapter or PlannedRegionKind.SpecialChapter;
    public bool AutomaticallyExcludedDuplicate { get; init; }
    // An excluded candidate carrying text requires an explicit user decision.
    public bool ExplicitlyExcluded { get; init; }
    public string? VietnameseTitle { get; init; }
    public SourceStructureKind StructureKind { get; init; } = SourceStructureKind.Unclassified;
    public string? VolumeIdentity { get; init; }
    /// <summary>Titles of unnumbered headings folded into this chapter by default (their text is imported here).</summary>
    public ImmutableArray<string> AbsorbedTitles { get; init; } = ImmutableArray<string>.Empty;
}

public sealed class ImportPreviewPlan
{
    internal ImportPreviewPlan(RawSourceSnapshot source, ImmutableArray<SourceLine> lines,
        ImmutableArray<ImportPreviewItem> items, int revision)
    {
        Source = source;
        Lines = lines;
        Items = items;
        Revision = revision;
        ValidateExactCoverage();
        DuplicateEvidence = ChapterReconstructionAnalyzer.Analyze(source, items);
        CoverageAudit = SourceCoverageAuditor.Audit(source, items, DuplicateEvidence);
        Issues = Analyze(items, source, DuplicateEvidence, CoverageAudit);
        Fingerprint = CalculateFingerprint(source, items);
    }

    public RawSourceSnapshot Source { get; }
    public ImmutableArray<SourceLine> Lines { get; }
    public ImmutableArray<ImportPreviewItem> Items { get; }
    public ImmutableArray<ImportPreviewIssue> Issues { get; }
    public int Revision { get; }
    public string Fingerprint { get; }
    public ImmutableArray<ChapterDuplicateEvidence> DuplicateEvidence { get; }
    public SourceCoverageAudit CoverageAudit { get; }
    public IEnumerable<ImportPreviewItem> Chapters => Items.Where(x => x.IsChapter);
    public IEnumerable<ImportPreviewItem> ChaptersInImportOrder => OrderForImport(Chapters);
    public IEnumerable<ImportPreviewItem> ImportableChapters => ChaptersInImportOrder.Where(x => x.Included);
    public int AutomaticallySkippedDuplicateCount => Chapters.Count(x => x.AutomaticallyExcludedDuplicate);
    public bool WasAutomaticallyReordered => !Chapters.Where(x => x.Included).Select(x => x.Id)
        .SequenceEqual(ImportableChapters.Select(x => x.Id));
    public bool HasBlockingIssues => Issues.Any(x => x.Severity == DetectionIssueSeverity.Error);

    public string Slice(SourceSpan span) => Source.Text.Substring(span.StartUtf16, span.LengthUtf16);
    public string ReconstructSource() => string.Concat(Items.Select(x => Slice(x.Span)));

    public void ValidateExactCoverage()
    {
        if (Source.Text.Length == 0)
        {
            if (!Items.IsEmpty) throw new InvalidDataException("Nguồn rỗng không được có item preview.");
            return;
        }
        if (Items.IsEmpty) throw new InvalidDataException("Preview không phủ nguồn.");
        int expected = 0;
        var ids = new HashSet<Guid>();
        var chapterIds = new HashSet<Guid>();
        foreach (ImportPreviewItem item in Items)
        {
            if (item.Id == Guid.Empty || !ids.Add(item.Id) || item.Span.LengthUtf16 <= 0 || item.Span.StartUtf16 != expected)
                throw new InvalidDataException("Preview có ID trùng hoặc vùng nguồn bị hở/chồng/rỗng.");
            ValidateSpan(item.Span, Source.Text.Length, "item");
            ValidateSpan(item.ContentSpan, Source.Text.Length, "nội dung");
            if (item.ContentSpan.StartUtf16 < item.Span.StartUtf16 || item.ContentSpan.EndUtf16 > item.Span.EndUtf16)
                throw new InvalidDataException("Nội dung nằm ngoài item preview.");
            if (item.HeadingSpan is SourceSpan heading)
            {
                ValidateSpan(heading, Source.Text.Length, "tiêu đề");
                if (heading.StartUtf16 < item.Span.StartUtf16 || heading.EndUtf16 > item.Span.EndUtf16)
                    throw new InvalidDataException("Tiêu đề nằm ngoài item preview.");
            }
            if (item.IsChapter && (item.ChapterId == Guid.Empty || !chapterIds.Add(item.ChapterId)))
                throw new InvalidDataException("Preview có ChapterId rỗng hoặc trùng.");
            expected = item.Span.EndUtf16;
        }
        if (expected != Source.Text.Length || !string.Equals(ReconstructSource(), Source.Text, StringComparison.Ordinal))
            throw new InvalidDataException("Không thể tái tạo chính xác nguyên văn từ preview.");
    }

    private static ImmutableArray<ImportPreviewIssue> Analyze(ImmutableArray<ImportPreviewItem> items, RawSourceSnapshot source,
        ImmutableArray<ChapterDuplicateEvidence> duplicateEvidence, SourceCoverageAudit coverage)
    {
        var issues = ImmutableArray.CreateBuilder<ImportPreviewIssue>();
        ImportPreviewItem[] chapters = items.Where(x => x.IsChapter).ToArray();
        ImportPreviewItem[] included = chapters.Where(x => x.Included).ToArray();
        if (chapters.Length == 0)
            issues.Add(new(ImportPreviewIssueCode.NoChapterHeadings, DetectionIssueSeverity.Error,
                "Không có tiêu đề chương nào trong preview.", []));

        foreach (ImportPreviewItem item in chapters)
        {
            if (!item.AbsorbedTitles.IsEmpty && item.Included)
                issues.Add(new(ImportPreviewIssueCode.UncertainBoundary, DetectionIssueSeverity.Warning,
                    $"Chương {item.Number} gồm cả phần không đánh số “{string.Join("”, “", item.AbsorbedTitles)}” ngay sau nó " +
                    "(gộp để không mất nội dung). Dùng “Tách tại dòng…” nếu muốn thành chương riêng.", [item.Id]));
            if (item.Decision == CandidateDecision.NeedsReview && item.Included)
                issues.Add(new(ImportPreviewIssueCode.UncertainBoundary, DetectionIssueSeverity.Warning,
                    "Ranh giới có độ tin cậy thấp; vẫn được nhập theo mặc định. Nếu đây không phải chương thật, bấm “Loại ranh giới giả”.", [item.Id]));
            if (item.Number is not > 0)
                issues.Add(new(ImportPreviewIssueCode.MissingNumber,
                    item.Included ? DetectionIssueSeverity.Error : DetectionIssueSeverity.Warning,
                    item.Included
                        ? "Chương được chọn chưa có số hợp lệ; nhập số chương hoặc loại khỏi import."
                        : "Chương chưa có số nên không được nhập (vẫn giữ trong bản nguồn); nhập số chương để đưa vào.", [item.Id]));
            if (!item.Included)
            {
                bool hasText = !string.IsNullOrWhiteSpace(source.Text.Substring(item.ContentSpan.StartUtf16, item.ContentSpan.LengthUtf16));
                if (hasText && !item.AutomaticallyExcludedDuplicate)
                    issues.Add(new(ImportPreviewIssueCode.ChapterNotImported, DetectionIssueSeverity.Warning,
                        $"Chương {item.Number?.ToString() ?? "chưa đánh số"} có nội dung nhưng không được nhập; vẫn giữ trong bản nguồn. Bấm “Đưa vào import” nếu cần.", [item.Id]));
                continue;
            }
            if (string.IsNullOrWhiteSpace(item.Title))
                issues.Add(new(ImportPreviewIssueCode.MissingTitle, DetectionIssueSeverity.Error,
                    "Chương được chọn chưa có tên.", [item.Id]));
            if (string.IsNullOrWhiteSpace(source.Text.Substring(item.ContentSpan.StartUtf16, item.ContentSpan.LengthUtf16)))
                issues.Add(new(ImportPreviewIssueCode.EmptyContent, DetectionIssueSeverity.Error,
                    "Chương được chọn không có nội dung nguyên văn.", [item.Id]));
        }

        foreach (IGrouping<(string Volume, int Number), ImportPreviewItem> duplicate in chapters.Where(x => x.Number > 0)
                     .GroupBy(x => (x.VolumeIdentity ?? "", x.Number!.Value)).Where(x => x.Count() > 1))
        {
            ImportPreviewItem[] duplicateItems = duplicate.OrderBy(x => x.Span.StartUtf16).ToArray();
            int includedCount = duplicateItems.Count(x => x.Included);
            if (includedCount > 1)
                issues.Add(new(ImportPreviewIssueCode.DuplicateNumber, DetectionIssueSeverity.Error,
                    $"Số chương {duplicate.Key.Number} trong cùng quyển đang được chọn {includedCount} lần; cần giải quyết quan hệ nội dung.",
                    duplicateItems.Select(x => x.Id).ToImmutableArray()));
            else if (duplicateItems.Length > 1)
            {
                bool differentContent = duplicateItems.Select(x => source.Text.Substring(x.ContentSpan.StartUtf16, x.ContentSpan.LengthUtf16))
                    .Distinct(StringComparer.Ordinal).Skip(1).Any();
                issues.Add(new(differentContent ? ImportPreviewIssueCode.ExcludedDifferentContent : ImportPreviewIssueCode.DuplicateNumber,
                    differentContent ? DetectionIssueSeverity.Warning : DetectionIssueSeverity.Information,
                    differentContent
                        ? $"Số chương {duplicate.Key.Number} có nhiều phần nội dung KHÁC NHAU. Các phần đã loại vẫn nằm trong snapshot nguyên văn, KHÔNG nằm trong chương nhập. Hãy đối chiếu trước khi tiếp tục."
                        : $"Số chương {duplicate.Key.Number} có {duplicateItems.Length} bản cùng nội dung; các bản đã loại vẫn có trong snapshot nguyên văn.",
                    duplicateItems.Select(x => x.Id).ToImmutableArray()));
            }
        }

        foreach (IGrouping<string, ImportPreviewItem> volume in included.Where(x => x.Number > 0)
                     .GroupBy(x => x.VolumeIdentity ?? "", StringComparer.Ordinal))
        {
            ImportPreviewItem[] sourceOrdered = volume.OrderBy(x => x.Span.StartUtf16).ToArray();
            ImportPreviewItem[] ordered = sourceOrdered.OrderBy(x => x.Number).ThenBy(x => x.Span.StartUtf16).ToArray();
            if (!sourceOrdered.Select(x => x.Id).SequenceEqual(ordered.Select(x => x.Id)))
                issues.Add(new(ImportPreviewIssueCode.ReversedNumber, DetectionIssueSeverity.Information,
                    "Các chương lộn thứ tự đã được tự động sắp theo số; nguyên văn nguồn vẫn được giữ nguyên.",
                    []));
            for (int index = 1; index < ordered.Length; index++)
            {
                int previous = ordered[index - 1].Number!.Value;
                int current = ordered[index].Number!.Value;
                if (current > previous + 1)
                    issues.Add(new(ImportPreviewIssueCode.NumberJump, DetectionIssueSeverity.Warning,
                        current == previous + 2
                            ? $"Nguồn không có chương {previous + 1} (giữa {previous} và {current}). Chỉ là cảnh báo; vẫn nhập được."
                            : $"Nguồn không có chương {previous + 1}–{current - 1} (giữa {previous} và {current}). Chỉ là cảnh báo; vẫn nhập được.",
                        [ordered[index - 1].Id, ordered[index].Id]));
            }
        }

        foreach (ImportPreviewItem item in items.Where(x => x.Kind == PlannedRegionKind.Unclassified))
        {
            string text = source.Text.Substring(item.Span.StartUtf16, item.Span.LengthUtf16);
            switch (OutsideChapterText.Classify(text))
            {
                case OutsideChapterKind.Prose:
                    issues.Add(new(ImportPreviewIssueCode.OutsideChapterProse, DetectionIssueSeverity.Warning,
                        $"Vùng ngoài chương có {OutsideChapterText.ProseLength(text):N0} ký tự nội dung; giữ trong bản nguồn, không nhập. " +
                        "Có thể “Gộp vào chương trước” hoặc “Tạo chương từ vùng này”.", [item.Id]));
                    break;
                case OutsideChapterKind.SourceNote:
                    issues.Add(new(ImportPreviewIssueCode.UnclassifiedSource, DetectionIssueSeverity.Information,
                        "Ghi chú của nguồn (dấu phân cách, tên quyển, thông tin truyện, liên kết) được giữ trong bản nguồn, không nhập.", [item.Id]));
                    break;
            }
        }
        foreach (string issue in coverage.BlockingIssues)
            issues.Add(new(ImportPreviewIssueCode.UnclassifiedSource, DetectionIssueSeverity.Error, issue, []));
        return issues.ToImmutable();
    }

    private static string CalculateFingerprint(RawSourceSnapshot source, ImmutableArray<ImportPreviewItem> items)
    {
        var builder = new StringBuilder(source.RawSha256).Append('|').Append(source.TextSha256);
        foreach (ImportPreviewItem item in items)
            builder.Append('\n').Append(item.Id.ToString("D")).Append('|').Append(item.ChapterId.ToString("D"))
                .Append('|').Append(item.Kind).Append('|').Append(item.Span.StartUtf16).Append(':').Append(item.Span.LengthUtf16)
                .Append('|').Append(item.ContentSpan.StartUtf16).Append(':').Append(item.ContentSpan.LengthUtf16)
                .Append('|').Append(item.Number?.ToString(CultureInfo.InvariantCulture) ?? "-")
                .Append('|').Append(item.Included ? '1' : '0').Append('|').Append(item.ExplicitlyExcluded ? '1' : '0')
                .Append('|').Append(item.Decision).Append('|').Append(item.StructureKind).Append('|').Append(item.Title).Append('|')
                .Append(item.VietnameseTitle).Append('|').Append(item.VolumeTitle).Append('|').Append(item.VolumeIdentity);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private IEnumerable<ImportPreviewItem> OrderForImport(IEnumerable<ImportPreviewItem> chapters)
    {
        Dictionary<string, int> volumeOrder = Items.Where(x => x.IsChapter)
            .GroupBy(x => x.VolumeIdentity ?? "", StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Min(x => x.Span.StartUtf16), StringComparer.Ordinal);
        // Keyed by VolumeIdentity (as built above): a volume whose identity differs from its title used to throw here.
        return chapters.OrderBy(x => volumeOrder.GetValueOrDefault(x.VolumeIdentity ?? "", x.Span.StartUtf16))
            .ThenBy(x => x.Number is > 0 ? 0 : 1)
            .ThenBy(x => x.Number ?? int.MaxValue)
            .ThenBy(x => x.Span.StartUtf16);
    }

    private static void ValidateSpan(SourceSpan span, int length, string label)
    {
        if (span.StartUtf16 < 0 || span.LengthUtf16 < 0 || span.EndUtf16 > length)
            throw new InvalidDataException($"Offset {label} không hợp lệ.");
    }
}

public sealed class ImportPreviewEditor
{
    private readonly Stack<ImportPreviewPlan> _undo = new();

    private ImportPreviewEditor(ImportPreviewPlan initial) => Current = initial;

    public ImportPreviewPlan Current { get; private set; }
    public bool CanUndo => _undo.Count > 0;

    public static ImportPreviewEditor Create(ImmutableImportPlan detected)
    {
        ArgumentNullException.ThrowIfNull(detected);
        detected.ValidateExactCoverage();
        // Never silently exclude same-number chapters: their bodies may be different continuations.
        ImmutableArray<ImportPreviewItem> items =
            detected.Regions.Select(region => FromRegion(detected, region)).ToImmutableArray();
        items = ResolveExactDuplicates(detected.Source, items);
        items = ApplyDefaultDecisions(detected.Source, items);
        return new(new ImportPreviewPlan(detected.Source, detected.Lines, items, 0));
    }

    /// <summary>
    /// Gives every chapter a decision so the preview can always be imported, keeping all text accounted for:
    /// continuations are folded into the chapter before them before inclusion is decided (their text stays imported);
    /// a low-confidence numbered heading with text is imported; an included chapter without text is left out; a missing
    /// title becomes "Chương N"; anything else not imported is an explicit "kept in the snapshot" decision, listed as a warning.
    /// The user can change each of these in the preview.
    /// </summary>
    private static ImmutableArray<ImportPreviewItem> ApplyDefaultDecisions(RawSourceSnapshot source, ImmutableArray<ImportPreviewItem> items)
    {
        var list = items.ToList();
        for (int index = 0; index < list.Count; index++)
        {
            ImportPreviewItem item = list[index];
            if (!item.IsChapter || item.AutomaticallyExcludedDuplicate) continue;
            bool followsChapter = index > 0 && list[index - 1].IsChapter && list[index - 1].Span.EndUtf16 == item.Span.StartUtf16;
            // An unnumbered heading that is not a confirmed special chapter, or the same number again right after itself with no
            // title of its own ("第24章" then "第24章1" / "第24章（下）": one chapter posted in parts), continues the chapter before it.
            // The same number with a different title is not merged (it may be another version): see the duplicate rule below.
            ImportPreviewItem? before = followsChapter ? list[index - 1] : null;
            bool continuation = before is not null && (
                item.Number is not > 0 ||
                item.Number is > 0 && before.Number == item.Number && IsPartTitle(item.Title, before.Title) &&
                string.Equals(before.VolumeIdentity ?? "", item.VolumeIdentity ?? "", StringComparison.Ordinal));
            if (continuation)
            {
                ImportPreviewItem previous = list[index - 1];
                list[index - 1] = previous with
                {
                    Span = new(previous.Span.StartUtf16, item.Span.EndUtf16 - previous.Span.StartUtf16),
                    ContentSpan = new(previous.ContentSpan.StartUtf16, item.Span.EndUtf16 - previous.ContentSpan.StartUtf16),
                    // An unnumbered part (番外, 后记...) is named in a warning so the user can split it out again.
                    AbsorbedTitles = item.Number is > 0 ? previous.AbsorbedTitles : previous.AbsorbedTitles.Add(item.Title)
                };
                list.RemoveAt(index--);
                continue;
            }
        }
        for (int index = 0; index < list.Count; index++)
        {
            ImportPreviewItem item = list[index];
            if (!item.IsChapter || item.AutomaticallyExcludedDuplicate) continue;
            bool hasText = !string.IsNullOrWhiteSpace(source.Text.Substring(item.ContentSpan.StartUtf16, item.ContentSpan.LengthUtf16));
            if (!item.Included && item.Number is > 0 && hasText && item.Decision == CandidateDecision.NeedsReview)
                item = item with { Included = true };
            if (item.Included && !hasText) item = item with { Included = false };
            if (item.Included && string.IsNullOrWhiteSpace(item.Title)) item = item with { Title = $"Chương {item.Number}" };
            if (!item.Included) item = item with { ExplicitlyExcluded = true };
            list[index] = item;
        }
        // The same number again elsewhere (different text): the first one is imported, later ones are kept in the snapshot
        // and listed for comparison; the user can renumber and include them.
        foreach (var group in list.Select((item, index) => (item, index)).Where(x => x.item.IsChapter && x.item.Included && x.item.Number > 0)
                     .GroupBy(x => (x.item.VolumeIdentity ?? "", x.item.Number!.Value)).Where(g => g.Count() > 1))
            foreach (var (item, index) in group.OrderBy(x => x.item.Span.StartUtf16).Skip(1))
                list[index] = item with { Included = false, ExplicitlyExcluded = true };
        return list.ToImmutableArray();
    }

    private static readonly System.Text.RegularExpressions.Regex PartSuffix = new(
        @"^[（(【\[〔]?\s*([0-9０-９]{1,2}|[一二三四五六七八九十]|上|中|下|续|續|完|终|終|part\s*[0-9]+)\s*[）)】\]〕]?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string CleanTitle(string value) => value.Trim().Trim('﻿', '​', '‌', '‍', '⁠').Trim();

    private static readonly System.Text.RegularExpressions.Regex UploaderNote = new(@"[（(【\[〔][^（(【\[〔）)】\]〕]{1,16}[）)】\]〕]\s*$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A later heading with the same number names no chapter of its own: empty, the same title again, or a part suffix.</summary>
    private static bool IsPartTitle(string title, string previousTitle)
    {
        string later = Strip(title), earlier = Strip(previousTitle);
        if (later.Length == 0 || string.Equals(later, earlier, StringComparison.Ordinal)) return true;
        if (earlier.Length > 0 && later.StartsWith(earlier, StringComparison.Ordinal)) later = later[earlier.Length..].Trim();
        return later.Length == 0 || PartSuffix.IsMatch(later);

        // The recognizer may keep "第24章" in the title; compare only what follows the number marker, without a short
        // bracketed note from the uploader at the end ("(PC修改)", "(Android端修改1)", "（修）", "【补】").
        static string Strip(string value)
        {
            string text = CleanTitle(value);
            int marker = text.IndexOfAny(['章', '回', '节', '節']);
            text = (marker >= 0 && marker <= 12 && text.StartsWith('第') ? text[(marker + 1)..] : text).Trim();
            return UploaderNote.Replace(text, "").Trim();
        }
    }

    /// <summary>Imports a region outside chapters (or a chapter) as part of the chapter right before it.</summary>
    public void MergeIntoPreviousChapter(Guid itemId)
    {
        int index = IndexOf(itemId);
        ImportPreviewItem item = Current.Items[index];
        if (item.Kind == PlannedRegionKind.Volume) throw new InvalidOperationException("Không gộp tiêu đề quyển vào chương.");
        if (index <= 0 || !Current.Items[index - 1].IsChapter)
            throw new InvalidOperationException("Không có chương liền trước để gộp vào.");
        ImportPreviewItem previous = Current.Items[index - 1];
        if (previous.Span.EndUtf16 != item.Span.StartUtf16) throw new InvalidOperationException("Vùng này không liền kề chương trước.");
        var merged = previous with
        {
            Span = new(previous.Span.StartUtf16, item.Span.EndUtf16 - previous.Span.StartUtf16),
            ContentSpan = new(previous.ContentSpan.StartUtf16, item.Span.EndUtf16 - previous.ContentSpan.StartUtf16)
        };
        Mutate(Current.Items.RemoveAt(index).SetItem(index - 1, merged));
    }

    /// <summary>Turns a region outside chapters into a chapter of its own (the whole region becomes its text).</summary>
    public void PromoteToChapter(Guid itemId, int number, string title)
    {
        if (number <= 0) throw new ArgumentOutOfRangeException(nameof(number), "Số chương phải lớn hơn 0.");
        int index = IndexOf(itemId);
        ImportPreviewItem item = Current.Items[index];
        if (item.Kind != PlannedRegionKind.Unclassified) throw new InvalidOperationException("Chỉ tạo chương từ vùng ngoài chương.");
        ImportPreviewItem? neighbour = Current.Items.Take(index).LastOrDefault(x => x.IsChapter) ?? Current.Items.Skip(index + 1).FirstOrDefault(x => x.IsChapter);
        var chapter = new ImportPreviewItem(
            StableId(Current.Source.RawSha256, "preview-item", item.Span, PlannedRegionKind.Chapter),
            StableId(Current.Source.RawSha256, "chapter", item.Span, PlannedRegionKind.Chapter),
            PlannedRegionKind.Chapter, item.Span, null, item.Span, number,
            string.IsNullOrWhiteSpace(title) ? $"Chương {number}" : title.Trim(),
            neighbour?.VolumeTitle, "", true, CandidateDecision.Accepted, null)
        { StructureKind = SourceStructureKind.Chapter, VolumeIdentity = neighbour?.VolumeIdentity };
        Mutate(Current.Items.SetItem(index, chapter));
    }

    private int IndexOf(Guid itemId)
    {
        for (int index = 0; index < Current.Items.Length; index++)
            if (Current.Items[index].Id == itemId) return index;
        throw new KeyNotFoundException("Không tìm thấy item preview.");
    }

    public void UpdateChapter(Guid itemId, int? number, string title)
    {
        ImportPreviewItem item = RequireChapter(itemId);
        if (number is <= 0) throw new ArgumentOutOfRangeException(nameof(number), "Số chương phải lớn hơn 0 hoặc để trống.");
        Replace(itemId, item with
        {
            Number = number,
            Title = title?.Trim() ?? "",
            VietnameseTitle = null,
            AutomaticallyExcludedDuplicate = false
        });
    }

    public void UpdateVietnameseTitle(Guid itemId, string? title)
    {
        ImportPreviewItem item = RequireChapter(itemId);
        Replace(itemId, item with { VietnameseTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim() });
    }

    public void ApplyVietnameseTitles(IReadOnlyDictionary<Guid, string> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);
        if (titles.Count == 0) return;
        ImmutableArray<ImportPreviewItem>.Builder items = Current.Items.ToBuilder();
        var applied = new HashSet<Guid>();
        bool changed = false;
        for (int index = 0; index < items.Count; index++)
        {
            ImportPreviewItem item = items[index];
            if (!item.IsChapter || !titles.TryGetValue(item.Id, out string? title)) continue;
            if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Tên tiếng Việt không được rỗng.", nameof(titles));
            string normalized = title.Trim();
            if (!string.Equals(item.VietnameseTitle, normalized, StringComparison.Ordinal))
            {
                items[index] = item with { VietnameseTitle = normalized };
                changed = true;
            }
            applied.Add(item.Id);
        }
        if (applied.Count != titles.Count) throw new KeyNotFoundException("Có ID tiêu đề không thuộc preview hiện tại.");
        if (changed) Mutate(items.ToImmutable());
    }

    public void SetIncluded(Guid itemId, bool included)
    {
        ImportPreviewItem item = RequireChapter(itemId);
        Replace(itemId, item with { Included = included, AutomaticallyExcludedDuplicate = false,
            ExplicitlyExcluded = !included });
    }

    public void ConfirmBoundary(Guid itemId)
    {
        ImportPreviewItem item = RequireChapter(itemId);
        Replace(itemId, item with
        {
            Decision = CandidateDecision.Accepted,
            Included = item.Number is > 0,
            ExplicitlyExcluded = false,
            AutomaticallyExcludedDuplicate = false
        });
    }

    public void RejectFalseBoundary(Guid itemId)
    {
        ImportPreviewItem item = RequireChapter(itemId);
        int index = Current.Items.IndexOf(item);
        if (index <= 0 || !Current.Items[index - 1].IsChapter)
            throw new InvalidOperationException("Chỉ có thể loại ranh giới giả khi có chương vật lý liền trước để nhận lại nguyên văn.");
        ImportPreviewItem previous = Current.Items[index - 1];
        if (previous.Span.EndUtf16 != item.Span.StartUtf16)
            throw new InvalidOperationException("Ranh giới giả không liền kề chương chứa nó.");

        var reconstructed = previous with
        {
            Span = new(previous.Span.StartUtf16, item.Span.EndUtf16 - previous.Span.StartUtf16),
            ContentSpan = new(previous.ContentSpan.StartUtf16, item.Span.EndUtf16 - previous.ContentSpan.StartUtf16)
        };
        Mutate(Current.Items.RemoveAt(index).SetItem(index - 1, reconstructed));
    }

    public void Split(Guid itemId, int splitAtUtf16, int? newNumber, string newTitle)
    {
        ImportPreviewItem item = RequireChapter(itemId);
        if (newNumber is <= 0) throw new ArgumentOutOfRangeException(nameof(newNumber));
        if (splitAtUtf16 <= item.ContentSpan.StartUtf16 || splitAtUtf16 >= item.Span.EndUtf16 || !IsLineStart(splitAtUtf16))
            throw new InvalidOperationException("Chỉ có thể tách tại đầu một dòng nằm bên trong nội dung chương.");

        int index = Current.Items.IndexOf(item);
        var left = item with
        {
            Span = new(item.Span.StartUtf16, splitAtUtf16 - item.Span.StartUtf16),
            ContentSpan = new(item.ContentSpan.StartUtf16, splitAtUtf16 - item.ContentSpan.StartUtf16)
        };
        var rightSpan = new SourceSpan(splitAtUtf16, item.Span.EndUtf16 - splitAtUtf16);
        var right = new ImportPreviewItem(
            StableId(Current.Source.RawSha256, "preview-item", rightSpan, item.Kind),
            StableId(Current.Source.RawSha256, "chapter", rightSpan, item.Kind),
            PlannedRegionKind.Chapter, rightSpan, null, rightSpan, newNumber, newTitle?.Trim() ?? "",
            item.VolumeTitle, "", true, CandidateDecision.NeedsReview, null)
        { StructureKind = SourceStructureKind.Chapter, VolumeIdentity = item.VolumeIdentity };
        Mutate(Current.Items.RemoveAt(index).InsertRange(index, ImmutableArray.Create(left, right)));
    }

    public void MergeWithNext(Guid itemId)
    {
        ImportPreviewItem item = RequireChapter(itemId);
        int index = Current.Items.IndexOf(item);
        if (index < 0 || index + 1 >= Current.Items.Length || !Current.Items[index + 1].IsChapter)
            throw new InvalidOperationException("Không có chương liền kề phía sau để gộp.");
        ImportPreviewItem next = Current.Items[index + 1];
        if (item.Span.EndUtf16 != next.Span.StartUtf16)
            throw new InvalidOperationException("Hai chương không liền kề trong nguồn.");
        var merged = item with
        {
            Span = new(item.Span.StartUtf16, next.Span.EndUtf16 - item.Span.StartUtf16),
            ContentSpan = new(item.ContentSpan.StartUtf16, next.Span.EndUtf16 - item.ContentSpan.StartUtf16),
            Decision = CandidateDecision.NeedsReview
        };
        Mutate(Current.Items.RemoveAt(index).RemoveAt(index).Insert(index, merged));
    }

    public void MoveBoundary(Guid leftItemId, int boundaryUtf16)
    {
        ImportPreviewItem left = RequireChapter(leftItemId);
        int index = Current.Items.IndexOf(left);
        if (index < 0 || index + 1 >= Current.Items.Length || !Current.Items[index + 1].IsChapter)
            throw new InvalidOperationException("Không có chương liền kề để dời ranh giới.");
        ImportPreviewItem right = Current.Items[index + 1];
        if (boundaryUtf16 <= left.ContentSpan.StartUtf16 || boundaryUtf16 >= right.Span.EndUtf16 || !IsLineStart(boundaryUtf16))
            throw new InvalidOperationException("Ranh giới mới phải là đầu dòng nằm giữa hai chương.");
        var movedLeft = left with
        {
            Span = new(left.Span.StartUtf16, boundaryUtf16 - left.Span.StartUtf16),
            ContentSpan = new(left.ContentSpan.StartUtf16, boundaryUtf16 - left.ContentSpan.StartUtf16),
            Decision = CandidateDecision.NeedsReview
        };
        var rightSpan = new SourceSpan(boundaryUtf16, right.Span.EndUtf16 - boundaryUtf16);
        var movedRight = right with
        {
            Span = rightSpan,
            HeadingSpan = null,
            ContentSpan = rightSpan,
            RawHeading = "",
            Decision = CandidateDecision.NeedsReview
        };
        var items = Current.Items.SetItem(index, movedLeft).SetItem(index + 1, movedRight);
        Mutate(items);
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        Current = _undo.Pop();
    }

    private void Replace(Guid id, ImportPreviewItem replacement)
    {
        int index = -1;
        for (int candidate = 0; candidate < Current.Items.Length; candidate++)
            if (Current.Items[candidate].Id == id) { index = candidate; break; }
        if (index < 0) throw new KeyNotFoundException("Không tìm thấy item preview.");
        Mutate(Current.Items.SetItem(index, replacement));
    }

    private void Mutate(ImmutableArray<ImportPreviewItem> items)
    {
        var next = new ImportPreviewPlan(Current.Source, Current.Lines, items, checked(Current.Revision + 1));
        _undo.Push(Current);
        Current = next;
    }

    private ImportPreviewItem RequireChapter(Guid id)
    {
        ImportPreviewItem item = Current.Items.FirstOrDefault(x => x.Id == id)
            ?? throw new KeyNotFoundException("Không tìm thấy item preview.");
        if (!item.IsChapter) throw new InvalidOperationException("Thao tác chỉ áp dụng cho chương.");
        return item;
    }

    private bool IsLineStart(int offset) => Current.Lines.Any(x => x.FullSpan.StartUtf16 == offset);

    private static ImportPreviewItem FromRegion(ImmutableImportPlan detected, PlannedSourceRegion region)
    {
        HeadingCandidate? candidate = region.CandidateIndex is int index ? detected.Candidates[index] : null;
        string rawHeading = region.HeadingSpan is SourceSpan heading ? detected.Slice(heading) : "";
        // Titles never keep zero-width characters (a BOM left in the middle of concatenated files) into the project.
        string title = CleanTitle(!string.IsNullOrWhiteSpace(region.Title) ? region.Title : rawHeading);
        bool isChapter = region.Kind is PlannedRegionKind.Chapter or PlannedRegionKind.SpecialChapter;
        bool included = isChapter && candidate?.Decision == CandidateDecision.Accepted && region.ChapterNumber is > 0;
        Guid chapterId = isChapter ? StableId(detected.Source.RawSha256, "chapter", region.Span, region.Kind) : Guid.Empty;
        SourceStructureKind structureKind = detected.Structure.FirstOrDefault(x => x.Span == region.Span)?.Kind ??
            (isChapter ? SourceStructureKind.Chapter : SourceStructureKind.Unclassified);
        return new(StableId(detected.Source.RawSha256, "preview-item", region.Span, region.Kind), chapterId,
            region.Kind, region.Span, region.HeadingSpan, region.BodySpan, region.ChapterNumber, title,
            region.VolumeTitle, rawHeading, included, candidate?.Decision ?? CandidateDecision.Rejected, region.CandidateIndex)
        { StructureKind = structureKind, VolumeIdentity = region.VolumeIdentity };
    }

    private static ImmutableArray<ImportPreviewItem> ResolveExactDuplicates(RawSourceSnapshot source,
        ImmutableArray<ImportPreviewItem> items)
    {
        ImmutableArray<ChapterDuplicateEvidence> evidence = ChapterReconstructionAnalyzer.Analyze(source, items);
        HashSet<Guid> exactLaterCopies = evidence.Where(x => x.Kind == ChapterDuplicateKind.ExactDuplicate && x.IsAutomaticallyVerifiable)
            .Select(x => x.SecondItemId).ToHashSet();
        if (exactLaterCopies.Count == 0) return items;
        return items.Select(item => exactLaterCopies.Contains(item.Id)
            ? item with { Included = false, AutomaticallyExcludedDuplicate = true }
            : item).ToImmutableArray();
    }

    private static Guid StableId(string sourceHash, string purpose, SourceSpan span, PlannedRegionKind kind)
    {
        string value = $"qtaie-m2:{purpose}:{sourceHash}:{kind}:{span.StartUtf16}:{span.LengthUtf16}";
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
    }
}
