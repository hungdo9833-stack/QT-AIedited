using QuickTranslator.AiEditor.Core.Traditional;

namespace QuickTranslator.AiEditor.Tests;

public sealed class TraditionalTranslationCacheTests
{
    [Fact]
    public void Concurrent_misses_for_one_key_share_a_single_node_and_eviction_stays_consistent()
    {
        var service = new TraditionalTranslationService(cacheCapacity: 2);
        var first = Result("a1");
        // Both concurrent misses finish: the second must reuse the first node, not add a duplicate.
        Assert.Same(first, service.Store("a", null, first));
        Assert.Same(first, service.Store("a", null, Result("a2")));
        Assert.Equal(1, service.CachedNodeCount);

        service.Store("b", null, Result("b"));
        service.Store("c", null, Result("c"));
        Assert.Equal(2, service.CachedNodeCount);
        // "a" was evicted; storing it again is a fresh entry, not a resurrected orphan.
        var again = Result("a3");
        Assert.Same(again, service.Store("a", null, again));
        Assert.Equal(2, service.CachedNodeCount);
    }

    private static TraditionalTranslationResult Result(string text) => new()
    {
        SourceText = text, SourceVersion = "v", HanVietText = text, VietPhraseText = text,
        HanVietAlignment = [], VietPhraseAlignment = [], DictionaryFingerprint = "f"
    };
}
