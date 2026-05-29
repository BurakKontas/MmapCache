using MmapCache.Cache;

namespace MmapCache.Config;

/// <summary>
/// Zero-allocation deserializer for reading directly from the memory-mapped file.
/// </summary>
public delegate TValue SpanDeserializer<out TValue>(ReadOnlySpan<byte> span);

/// <summary>All settings needed to build and reload one named mmap cache.</summary>
public sealed class MmapCacheDefinition<TValue> : ICacheDefinition
{
    /// <summary>Logical name — also used as the sub-directory under BasePath.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Produces the full dataset on every reload.
    /// Yielding lazily (yield return) avoids loading everything into a list upfront.
    /// </summary>
    public required Func<CancellationToken, IEnumerable<(string Key, TValue Value)>> Supplier { get; init; }
    
    /// <summary>Serialize TValue → bytes written into the shard file.</summary>
    public required Func<TValue, byte[]> Serializer { get; init; }

    /// <summary>Deserialize bytes read from the shard file → TValue.</summary>
    public required SpanDeserializer<TValue> Deserializer { get; init; }

    /// <summary>Background refresh interval. After this age a reload is triggered.</summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// When true, the supplier is iterated once to measure max key/value lengths
    /// before allocating shards (tighter disk layout).
    /// When false, MaxKeyBytes / MaxValueBytes must be set manually.
    /// </summary>
    public bool DynamicSizing { get; init; } = true;

    /// <summary>Max entries in L1 MemoryCache. 0 = L1 disabled.</summary>
    public int L1MaxSize { get; init; } = 10_000;

    /// <summary>Per-entry TTL inside L1. null = LRU-only eviction.</summary>
    public TimeSpan? L1Ttl { get; init; }

    /// <summary>Max entries in the MemTable's off-heap radix tree index. This should be sized to hold the expected number of keys in the MemTable before flushing to disk, to avoid costly resizing operations during heavy write phases.</summary>
    public int RadixTreeCapacity { get; init; } = 1_000_000;
    public int MemTableFlushThresholdBytes { get; init; } = 64 * 1024 * 1024;
    public bool ReloadOnInit { get; init; } = true;
}
