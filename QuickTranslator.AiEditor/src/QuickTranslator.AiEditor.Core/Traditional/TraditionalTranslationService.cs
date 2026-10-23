using System.Security.Cryptography;
using System.Text;

namespace QuickTranslator.AiEditor.Core.Traditional;

public sealed class TraditionalTranslationService
{
    private readonly TraditionalDataLoader _loader;
    private readonly TraditionalTranslator _translator;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, TraditionalTranslationResult Value)>> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, TraditionalTranslationResult Value)> _lru = [];
    private TraditionalDictionarySnapshot? _snapshot;
    private int _loadGeneration;

    public TraditionalTranslationService(TraditionalDataLoader? loader = null, TraditionalTranslator? translator = null, int cacheCapacity = 64)
    {
        _loader = loader ?? new();
        _translator = translator ?? new();
        CacheCapacity = Math.Clamp(cacheCapacity, 1, 512);
    }

    public int CacheCapacity { get; }
    public TraditionalDictionarySnapshot? CurrentSnapshot { get { lock (_gate) return _snapshot; } }

    public async Task<TraditionalDataManifest> LoadDataAsync(string directory, CancellationToken cancellationToken = default)
    {
        int generation = Interlocked.Increment(ref _loadGeneration);
        var loaded = await _loader.LoadAsync(directory, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (generation != _loadGeneration) throw new OperationCanceledException("Một lần nạp Data mới hơn đã thay thế lần này.");
            _snapshot = loaded;
            _cache.Clear();
            _lru.Clear();
        }
        return loaded.Manifest;
    }

    public Task<TraditionalTranslationResult> TranslateAsync(string source, TraditionalTranslationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        TraditionalDictionarySnapshot snapshot;
        lock (_gate) snapshot = _snapshot ?? throw new InvalidOperationException("Chưa nạp thư mục Data QuickTranslator.");
        options ??= new();
        string key = snapshot.Manifest.Fingerprint + ":" + options.PrioritizeNames + ":" + options.TranslationAlgorithm + ":" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _lru.Remove(node); _lru.AddFirst(node);
                return Task.FromResult(node.Value.Value);
            }
        }
        return Task.Run(() => Store(key, snapshot, _translator.Translate(source, snapshot, options, cancellationToken)), cancellationToken);
    }

    internal int CachedNodeCount { get { lock (_gate) return _lru.Count; } }

    // Two concurrent misses for one key both reach here: the second reuses the first node instead of adding a
    // duplicate whose eviction would later drop the live dictionary entry.
    internal TraditionalTranslationResult Store(string key, TraditionalDictionarySnapshot? snapshot, TraditionalTranslationResult result)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(snapshot, _snapshot)) return result;
            if (_cache.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing); _lru.AddFirst(existing);
                return existing.Value.Value;
            }
            var node = new LinkedListNode<(string, TraditionalTranslationResult)>((key, result));
            _lru.AddFirst(node); _cache[key] = node;
            while (_cache.Count > CacheCapacity)
            {
                var last = _lru.Last!; _lru.RemoveLast(); _cache.Remove(last.Value.Key);
            }
        }
        return result;
    }
}
