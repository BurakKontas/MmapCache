namespace MmapCacheApp.Cache;

/// <summary>
/// Written as meta.json inside every version directory.
/// Allows the manager to re-open existing shard files on restart
/// without calling the Supplier again, and to decide whether the
/// cached data is still within TTL.
/// </summary>
internal sealed class VersionMeta
{
    /// <summary>UTC instant the version was fully written to disk.</summary>
    public DateTime CreatedAt { get; set; }

    public int MaxKeyBytes { get; set; }
    public int MaxValueBytes { get; set; }

    /// <summary>Pre-computed: CacheRecord.RecordSize(MaxKeyBytes, MaxValueBytes)</summary>
    public int RecordSize { get; set; }

    public int ShardCount { get; set; }
    public long ShardCapacity { get; set; }
    public int IndexShardCount { get; set; }
}
