namespace MmapCacheApp.Config;

/// <summary>All settings needed to build and reload one named mmap cache.</summary>
public sealed class MmapCacheDefinition<TValue>
{
    /// <summary>Logical name — also used as the sub-directory under BasePath.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Produces the full dataset on every reload.
    /// Yielding lazily (yield return) avoids loading everything into a list upfront.
    /// </summary>
    public required Func<IEnumerable<(string Key, TValue Value)>> Supplier { get; init; }

    /// <summary>Serialize TValue → bytes written into the shard file.</summary>
    public required Func<TValue, byte[]> Serializer { get; init; }

    /// <summary>Deserialize bytes read from the shard file → TValue.</summary>
    public required Func<byte[], TValue> Deserializer { get; init; }

    /// <summary>Background refresh interval. After this age a reload is triggered.</summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// When true, the supplier is iterated once to measure max key/value lengths
    /// before allocating shards (tighter disk layout).
    /// When false, MaxKeyBytes / MaxValueBytes must be set manually.
    /// </summary>
    public bool DynamicSizing { get; init; } = true;

    public int MaxKeyBytes { get; init; } = 256;
    public int MaxValueBytes { get; init; } = 4096;

    /// <summary>Max records per shard file.</summary>
    public long ShardCapacity { get; init; } = 200_000;

    /// <summary>Number of FasterKV index shard files.</summary>
    public int IndexShardCount { get; init; } = 16;

    /// <summary>Max entries in L1 MemoryCache. 0 = L1 disabled.</summary>
    public int L1MaxSize { get; init; } = 10_000;

    /// <summary>Per-entry TTL inside L1. null = LRU-only eviction.</summary>
    public TimeSpan? L1Ttl { get; init; }
}
