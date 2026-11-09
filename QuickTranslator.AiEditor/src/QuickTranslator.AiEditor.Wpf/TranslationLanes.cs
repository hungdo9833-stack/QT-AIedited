using System.ComponentModel;

namespace QuickTranslator.AiEditor.Wpf;

/// <summary>A choice in the automatic queue's "Luồng dịch" column. Kept by id when the list refreshes, so a row's choice
/// survives a GPU being renamed.</summary>
public sealed class TranslationLaneOption(string id, string label) : INotifyPropertyChanged
{
    private string _label = label;
    public string Id { get; } = id;
    public string Label
    {
        get => _label;
        set
        {
            if (_label == value) return;
            _label = value;
            PropertyChanged?.Invoke(this, new(nameof(Label)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => Label;
}

/// <summary>A worker offered in the "+ Worker" window, with what it runs and why it cannot be chosen (if so).</summary>
public sealed record WorkerChoice(string Id, string Name, string Detail, bool Available, string? Reason = null);

/// <summary>One chosen worker of a book, shown as "Worker 1 · GPU máy này".</summary>
public sealed class BookWorker(string id, string name, int number)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public int Number { get; } = number;
    public string Label => $"Worker {Number} · {Name}";
}

/// <summary>A book's workers, numbered in the order they were added.</summary>
public sealed class BookWorkerList : System.Collections.ObjectModel.ObservableCollection<BookWorker>
{
    public void Reset(IEnumerable<TranslationLaneOption> workers)
    {
        Clear();
        foreach (TranslationLaneOption worker in workers.DistinctBy(worker => worker.Id))
            Add(new BookWorker(worker.Id, worker.Label, Count + 1));
    }

    public IReadOnlyList<TranslationLaneOption> Options() => this.Select(worker => new TranslationLaneOption(worker.Id, worker.Name)).ToList();
}

/// <summary>
/// Lane ids of the automatic queue. A lane is one GPU, a group of GPUs (they share each book's chapters) or the Gemini
/// key pool; books on lanes that share no GPU or pool translate at the same time.
/// </summary>
internal static class TranslationLanes
{
    /// <summary>Whatever QT Studio's translation source and GPU mode say when the book starts translating.</summary>
    public const string FollowAppId = "app";
    public const string LocalId = "local";
    public const string GeminiId = "gemini";
    /// <summary>Codex (ChatGPT plan) through the Codex CLI.</summary>
    public const string CodexId = "codex";
    public const string AllRemoteId = "all-remote";
    public const string AllGpuId = "all-gpu";
    private const string RemotePrefix = "remote:";
    private const string GeminiKeyPrefix = "gemini-key:";

    public static string Remote(Guid profileId) => RemotePrefix + profileId.ToString("N");

    /// <summary>One Gemini API key as its own worker, by the key's fingerprint.</summary>
    public static string GeminiKey(string fingerprint) => GeminiKeyPrefix + fingerprint;

    public static string? GeminiKeyFingerprint(string laneId) =>
        laneId.StartsWith(GeminiKeyPrefix, StringComparison.Ordinal) ? laneId[GeminiKeyPrefix.Length..] : null;

    public static Guid? RemoteProfile(string laneId) =>
        laneId.StartsWith(RemotePrefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(laneId[RemotePrefix.Length..], "N", out Guid id) ? id : null;

    /// <summary>Brings <paramref name="target"/> to <paramref name="fresh"/> in place: options keep their object while
    /// their id stays, so combo boxes bound to them keep their selection.</summary>
    public static void Synchronize(IList<TranslationLaneOption> target, IReadOnlyList<TranslationLaneOption> fresh)
    {
        for (int i = target.Count - 1; i >= 0; i--)
            if (fresh.All(option => option.Id != target[i].Id)) target.RemoveAt(i);
        for (int i = 0; i < fresh.Count; i++)
        {
            int at = -1;
            for (int j = 0; j < target.Count; j++) if (target[j].Id == fresh[i].Id) { at = j; break; }
            if (at < 0) { target.Insert(Math.Min(i, target.Count), fresh[i]); continue; }
            target[at].Label = fresh[i].Label;
            if (at != i && target is System.Collections.ObjectModel.ObservableCollection<TranslationLaneOption> observable)
                observable.Move(at, i);
        }
    }
}
