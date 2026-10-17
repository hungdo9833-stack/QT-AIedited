using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Traditional;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.Importing;

public enum ChapterTitleTranslationMethod { Ollama, HanViet, VietPhrase, KeepChinese }
public enum ChapterTitleTranslationStatus { Pending, Translating, Completed, Failed, Protected }

public sealed record ChapterTitleTranslationSource(Guid Id, int? ChapterNumber, string ChineseTitle,
    string ExistingVietnameseTitle = "", bool ProtectExistingVietnameseTitle = false);

public sealed class ChapterTitleTranslationItem
{
    public Guid Id { get; set; }
    public int? ChapterNumber { get; set; }
    public string ChineseTitle { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string TranslatedName { get; set; } = "";
    public string VietnameseTitle { get; set; } = "";
    public string ExistingVietnameseTitle { get; set; } = "";
    public bool ExistingTitleProtected { get; set; }
    public ChapterTitleTranslationStatus Status { get; set; }
    public string? Error { get; set; }
    public int Attempts { get; set; }

    internal void Validate()
    {
        if (Id == Guid.Empty || ChapterNumber is <= 0 || ChineseTitle.Length > 1000 || SourceName.Length > 1000 ||
            TranslatedName.Length > 1000 || VietnameseTitle.Length > 1200 || ExistingVietnameseTitle.Length > 1200 ||
            Error?.Length > 4000 || Attempts < 0 || !Enum.IsDefined(Status))
            throw new InvalidDataException("Mục dịch tiêu đề không hợp lệ.");
    }
}

public sealed class ChapterTitleTranslationDraft
{
    public int SchemaVersion { get; set; } = 1;
    public Guid ProjectId { get; set; }
    public string SourceKey { get; set; } = "";
    public ChapterTitleTranslationMethod Method { get; set; }
    public Guid? BackendProfileId { get; set; }
    public string Model { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ChapterTitleTranslationItem> Items { get; set; } = [];

    public void Validate()
    {
        if (SchemaVersion != 1 || ProjectId == Guid.Empty || string.IsNullOrWhiteSpace(SourceKey) || SourceKey.Length > 256 ||
            !Enum.IsDefined(Method) || Model.Length > 200 || CreatedAt == default || UpdatedAt == default ||
            Items is null || Items.Count > 10_000 || Items.Any(item => item is null) ||
            Items.Select(item => item.Id).Distinct().Count() != Items.Count)
            throw new InvalidDataException("Bản nháp dịch tiêu đề không hợp lệ.");
        foreach (ChapterTitleTranslationItem item in Items) item.Validate();
    }

    public static ChapterTitleTranslationDraft Create(Guid projectId, string sourceKey,
        ChapterTitleTranslationMethod method, IEnumerable<ChapterTitleTranslationSource> sources,
        Guid? backendProfileId = null, string model = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        ChapterTitleTranslationSource[] input = sources?.ToArray() ?? throw new ArgumentNullException(nameof(sources));
        if (input.Length == 0 || input.Length > 10_000 || input.Any(item => item.Id == Guid.Empty) ||
            input.Select(item => item.Id).Distinct().Count() != input.Length)
            throw new ArgumentException("Danh sách tiêu đề phải có từ 1 đến 10.000 ID duy nhất.", nameof(sources));
        var draft = new ChapterTitleTranslationDraft
        {
            ProjectId = projectId,
            SourceKey = sourceKey.Trim(),
            Method = method,
            BackendProfileId = backendProfileId,
            Model = model.Trim(),
            Items = input.Select(source =>
            {
                string chinese = source.ChineseTitle?.Trim() ?? "";
                string existing = source.ExistingVietnameseTitle?.Trim() ?? "";
                bool protect = source.ProtectExistingVietnameseTitle && existing.Length > 0;
                return new ChapterTitleTranslationItem
                {
                    Id = source.Id,
                    ChapterNumber = source.ChapterNumber,
                    ChineseTitle = chinese,
                    SourceName = ChapterTitleFormatter.ExtractName(chinese),
                    ExistingVietnameseTitle = existing,
                    ExistingTitleProtected = protect,
                    VietnameseTitle = protect ? existing : "",
                    Status = protect ? ChapterTitleTranslationStatus.Protected : ChapterTitleTranslationStatus.Pending
                };
            }).ToList()
        };
        draft.Validate();
        return draft;
    }
}

public sealed record ChapterTitleBackendInput(Guid Id, string ChineseName);
public sealed record ChapterTitleBackendOutput(Guid Id, string TranslatedTitle);

public interface IChapterTitleTranslationBackend
{
    Task<IReadOnlyList<ChapterTitleBackendOutput>> TranslateBatchAsync(
        IReadOnlyList<ChapterTitleBackendInput> titles, CancellationToken cancellationToken = default);
}

public sealed record ChapterTitleTranslationProgress(int Completed, int Total, int Failed, string Message);

public sealed class ChapterTitleTranslationService
{
    public async Task TranslateAsync(ChapterTitleTranslationDraft draft, IChapterTitleTranslationBackend backend,
        ModelOptions modelOptions, bool retryFailedOnly = false, IReadOnlyCollection<Guid>? selectedIds = null,
        bool overwriteProtected = false, IProgress<ChapterTitleTranslationProgress>? progress = null,
        Func<ChapterTitleTranslationDraft, CancellationToken, Task>? checkpoint = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft); ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(modelOptions); modelOptions.Validate(); draft.Validate();
        HashSet<Guid>? selected = selectedIds is null ? null : selectedIds.ToHashSet();
        ChapterTitleTranslationItem[] candidates = draft.Items.Where(item =>
            (selected is null || selected.Contains(item.Id)) &&
            (overwriteProtected || !item.ExistingTitleProtected) &&
            (retryFailedOnly ? item.Status == ChapterTitleTranslationStatus.Failed :
                item.Status is ChapterTitleTranslationStatus.Pending or ChapterTitleTranslationStatus.Failed ||
                overwriteProtected && item.Status == ChapterTitleTranslationStatus.Protected)).ToArray();
        foreach (ChapterTitleTranslationItem item in candidates.Where(item => string.IsNullOrWhiteSpace(item.SourceName) &&
                     item.ChapterNumber is > 0))
        {
            item.TranslatedName = "";
            item.VietnameseTitle = draft.Method == ChapterTitleTranslationMethod.KeepChinese
                ? item.ChineseTitle : ChapterTitleFormatter.Compose(item.ChapterNumber, "");
            item.Status = ChapterTitleTranslationStatus.Completed;
            item.Error = null;
        }
        ChapterTitleTranslationItem[] targets = candidates.Where(item =>
            item.Status is ChapterTitleTranslationStatus.Pending or ChapterTitleTranslationStatus.Failed or
                ChapterTitleTranslationStatus.Protected).ToArray();
        if (targets.Length == 0)
        {
            draft.UpdatedAt = DateTimeOffset.UtcNow;
            if (checkpoint is not null) await checkpoint(draft, cancellationToken).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<ChapterTitleTranslationItem[]> batches = ChapterTitleBatchPlanner.Plan(targets, modelOptions);
        foreach (ChapterTitleTranslationItem[] batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (ChapterTitleTranslationItem item in batch)
            {
                item.Status = ChapterTitleTranslationStatus.Translating;
                item.Error = null;
                item.Attempts++;
            }
            try
            {
                IReadOnlyList<ChapterTitleBackendOutput> output = await backend.TranslateBatchAsync(
                    batch.Select(item => new ChapterTitleBackendInput(item.Id, item.SourceName)).ToArray(), cancellationToken)
                    .ConfigureAwait(false);
                Dictionary<Guid, ChapterTitleBackendOutput> verified = ValidateOutput(batch, output);
                foreach (ChapterTitleTranslationItem item in batch)
                {
                    string translated = verified[item.Id].TranslatedTitle.Trim();
                    item.TranslatedName = translated;
                    item.VietnameseTitle = draft.Method == ChapterTitleTranslationMethod.KeepChinese
                        ? item.ChineseTitle
                        : ChapterTitleFormatter.Compose(item.ChapterNumber, translated, fallbackChinese: item.SourceName);
                    item.Status = ChapterTitleTranslationStatus.Completed;
                }
            }
            catch (OperationCanceledException)
            {
                foreach (ChapterTitleTranslationItem item in batch.Where(item => item.Status == ChapterTitleTranslationStatus.Translating))
                    item.Status = ChapterTitleTranslationStatus.Pending;
                draft.UpdatedAt = DateTimeOffset.UtcNow;
                if (checkpoint is not null) await checkpoint(draft, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception error) when (error is InvalidDataException or OllamaRequestException or TimeoutException or IOException)
            {
                foreach (ChapterTitleTranslationItem item in batch)
                {
                    item.Status = ChapterTitleTranslationStatus.Failed;
                    item.Error = error.Message;
                }
            }
            draft.UpdatedAt = DateTimeOffset.UtcNow;
            draft.Validate();
            if (checkpoint is not null) await checkpoint(draft, cancellationToken).ConfigureAwait(false);
            progress?.Report(new(draft.Items.Count(item => item.Status is ChapterTitleTranslationStatus.Completed or
                    ChapterTitleTranslationStatus.Protected), draft.Items.Count,
                draft.Items.Count(item => item.Status == ChapterTitleTranslationStatus.Failed),
                $"Đã xử lý {draft.Items.Count(item => item.Status is ChapterTitleTranslationStatus.Completed or ChapterTitleTranslationStatus.Protected):N0}/{draft.Items.Count:N0} tiêu đề."));
        }
    }

    private static Dictionary<Guid, ChapterTitleBackendOutput> ValidateOutput(
        IReadOnlyList<ChapterTitleTranslationItem> batch, IReadOnlyList<ChapterTitleBackendOutput> output)
    {
        if (output is null) throw new InvalidDataException("Backend trả danh sách tiêu đề rỗng.");
        Guid[] expected = batch.Select(item => item.Id).ToArray();
        if (output.Any(item => item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.TranslatedTitle)))
            throw new InvalidDataException("Backend trả ID hoặc translated_title rỗng.");
        if (output.GroupBy(item => item.Id).Any(group => group.Count() > 1))
            throw new InvalidDataException("Backend trả ID tiêu đề trùng.");
        Dictionary<Guid, ChapterTitleBackendOutput> result = output.ToDictionary(item => item.Id);
        if (result.Count != expected.Length || expected.Any(id => !result.ContainsKey(id)) || result.Keys.Any(id => !expected.Contains(id)))
            throw new InvalidDataException("Backend trả thiếu hoặc thừa ID; batch chưa được áp dụng.");
        return result;
    }
}

public static class ChapterTitleBatchPlanner
{
    public static IReadOnlyList<ChapterTitleTranslationItem[]> Plan(IReadOnlyList<ChapterTitleTranslationItem> items,
        ModelOptions options, int maximumBatchSize = 20)
    {
        ArgumentNullException.ThrowIfNull(items); ArgumentNullException.ThrowIfNull(options); options.Validate();
        maximumBatchSize = Math.Clamp(maximumBatchSize, 1, 40);
        int inputBudget = Math.Max(256, options.ContextSize - options.MaximumOutputTokens - 512);
        int outputBudget = Math.Max(64, options.MaximumOutputTokens - 128);
        var result = new List<ChapterTitleTranslationItem[]>();
        var current = new List<ChapterTitleTranslationItem>();
        int estimatedInputTokens = 0, estimatedOutputTokens = 0;
        foreach (ChapterTitleTranslationItem item in items)
        {
            int runes = Math.Max(1, item.SourceName.EnumerateRunes().Count());
            int itemInputTokens = 24 + runes;
            // Output repeats a GUID and JSON property names. Vietnamese can also require
            // more tokens than the source, so reserve a conservative per-title budget.
            int itemOutputTokens = 32 + runes * 2;
            if (current.Count > 0 && (current.Count >= maximumBatchSize ||
                                     estimatedInputTokens + itemInputTokens > inputBudget ||
                                     estimatedOutputTokens + itemOutputTokens > outputBudget))
            {
                result.Add(current.ToArray()); current.Clear();
                estimatedInputTokens = 0; estimatedOutputTokens = 0;
            }
            current.Add(item);
            estimatedInputTokens += itemInputTokens;
            estimatedOutputTokens += itemOutputTokens;
        }
        if (current.Count > 0) result.Add(current.ToArray());
        return result;
    }
}

public sealed class OllamaChapterTitleTranslationBackend(IOllamaClient client, ModelOptions options,
    IReadOnlyList<ConfirmedTerm>? glossary = null) : IChapterTitleTranslationBackend
{
    public async Task<IReadOnlyList<ChapterTitleBackendOutput>> TranslateBatchAsync(
        IReadOnlyList<ChapterTitleBackendInput> titles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(titles);
        if (titles.Count == 0) return [];
        try
        {
            return await TranslateOnceAsync(titles, false, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException firstError) when (titles.Count > 1)
        {
            // Some models occasionally add prose or truncate a large JSON array. Retry only
            // this failed batch as small groups; completed batches remain checkpointed.
            int retrySize = Math.Min(10, Math.Max(1, (titles.Count + 1) / 2));
            var retried = new List<ChapterTitleBackendOutput>(titles.Count);
            try
            {
                for (int start = 0; start < titles.Count; start += retrySize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ChapterTitleBackendInput[] group = titles.Skip(start).Take(retrySize).ToArray();
                    retried.AddRange(await TranslateOnceAsync(group, true, cancellationToken).ConfigureAwait(false));
                }
                return retried;
            }
            catch (InvalidDataException retryError)
            {
                throw new InvalidDataException(
                    "Ollama trả JSON tiêu đề không hợp lệ sau khi QT Studio đã thử lại bằng batch nhỏ. " +
                    "Hãy tăng Output Tokens hoặc dùng Dịch lại mục lỗi. " + retryError.Message, firstError);
            }
        }
    }

    private async Task<IReadOnlyList<ChapterTitleBackendOutput>> TranslateOnceAsync(
        IReadOnlyList<ChapterTitleBackendInput> titles, bool isRetry, CancellationToken cancellationToken)
    {
        string terms = glossary is null || glossary.Count == 0 ? "(không có)" : string.Join("\n",
            glossary.Take(200).Select(term => $"- {term.Chinese} => {term.Vietnamese}"));
        string input = JsonSerializer.Serialize(titles.Select(title => new
        {
            id = title.Id.ToString("D"), chinese_title = title.ChineseName
        }), DocumentJson.Options);
        string prompt = "Bạn dịch tên chương tiểu thuyết Trung-Việt. Chỉ dịch phần tên, không thêm số chương. " +
            "Giữ nguyên chính xác mọi id. Trả duy nhất JSON array, mỗi phần tử có id và translated_title; " +
            "không giải thích, không Markdown, không bỏ mục.\n" +
            (isRetry ? "Đây là lần thử lại batch nhỏ: bắt buộc đóng đầy đủ dấu ngoặc JSON.\n" : "") +
            "Thuật ngữ đã xác nhận:\n" + terms + "\nDữ liệu:\n" + input;
        OllamaCompletion completion = await client.TranslateAsync(prompt, options, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ChapterTitleBackendOutput> output = Parse(completion.Text);
        ValidateExpectedIds(titles, output);
        return output;
    }

    internal static IReadOnlyList<ChapterTitleBackendOutput> Parse(string response)
    {
        string text = response?.Trim().TrimStart('\uFEFF') ?? "";
        Exception? lastError = null;
        foreach (string candidate in EnumerateJsonCandidates(text))
        {
            try { return ParseCandidate(candidate); }
            catch (Exception error) when (error is JsonException or InvalidDataException) { lastError = error; }
        }
        throw new InvalidDataException(
            "Ollama không trả được JSON tiêu đề hoàn chỉnh; phản hồi có thể kèm giải thích hoặc bị cắt do giới hạn Output Tokens.",
            lastError);
    }

    private static IReadOnlyList<ChapterTitleBackendOutput> ParseCandidate(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        JsonElement root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in root.EnumerateObject())
            {
                string name = NormalizePropertyName(property.Name);
                if (name is "translations" or "results" or "items" or "data" or "titles")
                    return ParseCollection(property.Value);
            }
            if (TryParseItem(root, out ChapterTitleBackendOutput single)) return [single];
            if (TryParseIdMap(root, out IReadOnlyList<ChapterTitleBackendOutput>? mapped)) return mapped!;
        }
        if (root.ValueKind == JsonValueKind.Array) return ParseCollection(root);
        throw new InvalidDataException("JSON Ollama không chứa danh sách id và translated_title.");
    }

    private static IReadOnlyList<ChapterTitleBackendOutput> ParseCollection(JsonElement collection)
    {
        if (collection.ValueKind == JsonValueKind.Object &&
            TryParseIdMap(collection, out IReadOnlyList<ChapterTitleBackendOutput>? mapped)) return mapped!;
        if (collection.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Trường kết quả của Ollama không phải JSON array.");
        var result = new List<ChapterTitleBackendOutput>();
        foreach (JsonElement item in collection.EnumerateArray())
        {
            if (!TryParseItem(item, out ChapterTitleBackendOutput parsed))
                throw new InvalidDataException("Ollama trả mục tiêu đề thiếu id hoặc translated_title.");
            result.Add(parsed);
        }
        return result;
    }

    private static bool TryParseItem(JsonElement item, out ChapterTitleBackendOutput output)
    {
        output = default!;
        if (item.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(item, out JsonElement idElement, "id", "chapterid") ||
            idElement.ValueKind != JsonValueKind.String || !Guid.TryParse(idElement.GetString(), out Guid id) ||
            !TryGetProperty(item, out JsonElement titleElement, "translatedtitle", "vietnamesetitle", "translation", "title") ||
            titleElement.ValueKind != JsonValueKind.String)
            return false;
        output = new(id, titleElement.GetString() ?? "");
        return true;
    }

    private static bool TryParseIdMap(JsonElement value, out IReadOnlyList<ChapterTitleBackendOutput>? output)
    {
        output = null;
        if (value.ValueKind != JsonValueKind.Object) return false;
        var result = new List<ChapterTitleBackendOutput>();
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!Guid.TryParse(property.Name, out Guid id) || property.Value.ValueKind != JsonValueKind.String)
                return false;
            result.Add(new(id, property.Value.GetString() ?? ""));
        }
        if (result.Count == 0) return false;
        output = result;
        return true;
    }

    private static bool TryGetProperty(JsonElement item, out JsonElement value, params string[] normalizedNames)
    {
        foreach (JsonProperty property in item.EnumerateObject())
            if (normalizedNames.Contains(NormalizePropertyName(property.Name), StringComparer.Ordinal))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
    }

    private static string NormalizePropertyName(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static IEnumerable<string> EnumerateJsonCandidates(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        yield return text;
        for (int start = 0; start < text.Length; start++)
        {
            if (text[start] is not ('[' or '{')) continue;
            if (TryFindJsonEnd(text, start, out int end))
            {
                string candidate = text[start..(end + 1)];
                if (!string.Equals(candidate, text, StringComparison.Ordinal)) yield return candidate;
                start = end;
            }
        }
    }

    private static bool TryFindJsonEnd(string text, int start, out int end)
    {
        var stack = new Stack<char>();
        bool inString = false, escaped = false;
        for (int index = start; index < text.Length; index++)
        {
            char current = text[index];
            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (current == '\\') { escaped = true; continue; }
                if (current == '"') inString = false;
                continue;
            }
            if (current == '"') { inString = true; continue; }
            if (current is '[' or '{') stack.Push(current);
            else if (current is ']' or '}')
            {
                if (stack.Count == 0 || current == ']' && stack.Peek() != '[' || current == '}' && stack.Peek() != '{')
                    break;
                stack.Pop();
                if (stack.Count == 0) { end = index; return true; }
            }
        }
        end = -1;
        return false;
    }

    private static void ValidateExpectedIds(IReadOnlyList<ChapterTitleBackendInput> input,
        IReadOnlyList<ChapterTitleBackendOutput> output)
    {
        if (output.Any(item => item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.TranslatedTitle)) ||
            output.GroupBy(item => item.Id).Any(group => group.Count() != 1))
            throw new InvalidDataException("Ollama trả ID trùng hoặc tiêu đề rỗng.");
        HashSet<Guid> expected = input.Select(item => item.Id).ToHashSet();
        if (output.Count != expected.Count || output.Any(item => !expected.Remove(item.Id)) || expected.Count != 0)
            throw new InvalidDataException("Ollama trả thiếu, thừa hoặc sai ID; batch chưa được áp dụng.");
    }
}

public sealed class TraditionalChapterTitleTranslationBackend(TraditionalTranslationService service, bool hanViet)
    : IChapterTitleTranslationBackend
{
    public async Task<IReadOnlyList<ChapterTitleBackendOutput>> TranslateBatchAsync(
        IReadOnlyList<ChapterTitleBackendInput> titles, CancellationToken cancellationToken = default)
    {
        var result = new List<ChapterTitleBackendOutput>(titles.Count);
        foreach (ChapterTitleBackendInput title in titles)
        {
            TraditionalTranslationResult translated = await service.TranslateAsync(title.ChineseName, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            result.Add(new(title.Id, hanViet ? translated.HanVietText : translated.VietPhraseText));
        }
        return result;
    }
}

public sealed class KeepChineseChapterTitleTranslationBackend : IChapterTitleTranslationBackend
{
    public Task<IReadOnlyList<ChapterTitleBackendOutput>> TranslateBatchAsync(
        IReadOnlyList<ChapterTitleBackendInput> titles, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ChapterTitleBackendOutput>>(
            titles.Select(title => new ChapterTitleBackendOutput(title.Id, title.ChineseName)).ToArray());
    }
}

public static class ChapterTitleFormatter
{
    public static string Compose(int? number, string translatedName, string fallbackChinese = "")
    {
        string name = string.IsNullOrWhiteSpace(translatedName) ? fallbackChinese.Trim() : translatedName.Trim();
        return number is > 0 ? name.Length == 0 ? $"Chương {number}" : $"Chương {number}: {name}" : name;
    }

    public static string ExtractName(string heading)
    {
        string text = heading?.Trim() ?? "";
        if (text.Length < 3 || text[0] != '第') return text;
        int marker = -1;
        for (int index = 1; index < Math.Min(text.Length, 24); index++)
            if (text[index] is '章' or '回' or '节' or '節') { marker = index; break; }
        if (marker < 0) return text;
        int start = marker + 1;
        while (start < text.Length && (char.IsWhiteSpace(text[start]) || text[start] is ':' or '：' or '-' or '—')) start++;
        return text[start..].Trim();
    }
}

public sealed class ChapterTitleTranslationDraftStore(string path)
{
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public Task SaveAsync(ChapterTitleTranslationDraft draft, CancellationToken cancellationToken = default)
    {
        draft.Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(draft, DocumentJson.Options);
        return SaveCoreAsync(bytes, cancellationToken);
    }

    public async Task<ChapterTitleTranslationDraft?> LoadAsync(CancellationToken cancellationToken = default)
    {
        return await SimpleFileStorage.ReadOptionalAsync(Path, Decode,
            () => null, cancellationToken).ConfigureAwait(false);
    }

    private Task SaveCoreAsync(byte[] bytes, CancellationToken cancellationToken) =>
        SimpleFileStorage.WriteAsync(Path, bytes, Decode, cancellationToken);

    private static ChapterTitleTranslationDraft? Decode(byte[] bytes)
    {
        ChapterTitleTranslationDraft draft = JsonSerializer.Deserialize<ChapterTitleTranslationDraft>(bytes, DocumentJson.Options)
            ?? throw new InvalidDataException("Bản nháp dịch tiêu đề trống.");
        draft.Validate();
        return draft;
    }
}
