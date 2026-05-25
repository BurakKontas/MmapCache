namespace MmapCacheApp.Cache;

/// <summary>
/// Physical location of a record: which shard file, and which slot within it.
/// Stored as 12 bytes in the FasterKV index (4-byte shardId + 8-byte offset).
/// </summary>
internal readonly struct CacheLocation
{
    public readonly int ShardId;
    public readonly long Offset;

    public CacheLocation(int shardId, long offset)
    {
        ShardId = shardId;
        Offset = offset;
    }

    public override string ToString() => $"shard={ShardId} offset={Offset}";
}
