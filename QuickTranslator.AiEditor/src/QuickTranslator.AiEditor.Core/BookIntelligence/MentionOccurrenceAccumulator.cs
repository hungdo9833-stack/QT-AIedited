using System.Security.Cryptography;
using System.Text;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

/// <summary>Counts and fingerprints every occurrence, retaining bounded navigation samples only.</summary>
internal sealed class MentionOccurrenceAccumulator(int maximumSamples = CharacterMentionCensus.MaximumStoredMentions) : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private Guid? _chapter;
    public List<CharacterMention> Samples { get; } = [];
    public List<Guid> Chapters { get; } = [];
    public int Count { get; private set; }
    public int FirstChapter { get; private set; } = int.MaxValue;
    public int LastChapter { get; private set; }

    public void Add(Guid chapterId, int number, int start, int length, string sourceHash)
    {
        Span<byte> data = stackalloc byte[20];
        if (_chapter != chapterId)
        {
            _chapter = chapterId; Chapters.Add(chapterId);
            chapterId.TryWriteBytes(data); BitConverter.TryWriteBytes(data[16..], number);
            _hash.AppendData(data); _hash.AppendData(Encoding.UTF8.GetBytes(sourceHash));
        }
        BitConverter.TryWriteBytes(data, start); BitConverter.TryWriteBytes(data[4..], length);
        _hash.AppendData(data[..8]);
        Count++; FirstChapter = Math.Min(FirstChapter, number); LastChapter = Math.Max(LastChapter, number);
        if (Samples.Count < maximumSamples)
            Samples.Add(new(chapterId, number, start, length, sourceHash));
    }
    public string Finish() => Convert.ToHexString(_hash.GetHashAndReset());
    public void Dispose() => _hash.Dispose();
}
