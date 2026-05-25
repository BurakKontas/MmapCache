using Microsoft.Extensions.Caching.Memory;
using MmapCacheApp.Cache;
using MmapCacheApp.Index;
using MmapCacheApp.Shards;

namespace MmapCacheApp.Cache;

/// <summary>
/// Immutable snapshot produced by one reload cycle.
/// Once built and published, it is never mutated — reads are lock-free.
///
/// Reader counter: allows safe disposal after all in-flight reads finish.
///
/// On retire the version directory is deleted from disk — but only AFTER
/// all in-flight reads have completed, so readers are never left with a
/// dangling memory-mapped file.
/// </summary>
internal sealed class CacheVersion : IDisposable
{
    private readonly CacheShard[] _shards;
    private readonly CacheIndex _index;
    private readonly MemoryCache? _l1;
    private readonly int _maxKeyBytes;
    private readonly TimeSpan? _l1Ttl;

    private int _readers;
    private volatile bool _dead;

    public string Id { get; }
    public string VersionDir { get; }   // e.g. basePath/products/v1716681234567
    public DateTime CreatedAt { get; }
    public long Size => _index.Count;

    public CacheVersion(
        string id,
        string versionDir,
        CacheShard[] shards,
        CacheIndex index,
        int maxKeyBytes,
        int l1MaxSize,
        TimeSpan? l1Ttl,
        DateTime? createdAt = null)   // null → now (normal build path)
    {
        Id = id;
        VersionDir = versionDir;
        CreatedAt = createdAt ?? DateTime.UtcNow;
        _shards = shards;
        _index = index;
        _maxKeyBytes = maxKeyBytes;
        _l1Ttl = l1Ttl;

        if (l1MaxSize > 0)
            _l1 = new MemoryCache(new MemoryCacheOptions { SizeLimit = l1MaxSize });
    }

    // ── Read path ─────────────────────────────────────────────────────────────

    public bool TryGet<T>(string key, Func<byte[], T> deserializer, out T? value)
    {
        value = default;
        if (_dead) return false;

        Interlocked.Increment(ref _readers);
        try
        {
            // L1 hit — zero index/shard overhead
            if (_l1 is not null && _l1.TryGetValue(key, out T? cached))
            {
                value = cached;
                return true;
            }

            // FasterKV index lookup → mmap shard read
            if (!_index.TryGet(key, out var loc)) return false;

            byte[]? bytes = _shards[loc.ShardId].ReadValue(loc.Offset, _maxKeyBytes);
            if (bytes is null) return false;

            value = deserializer(bytes);

            // Promote to L1
            if (_l1 is not null)
            {
                var opts = new MemoryCacheEntryOptions { Size = 1 };
                if (_l1Ttl.HasValue) opts.AbsoluteExpirationRelativeToNow = _l1Ttl;
                _l1.Set(key, value, opts);
            }

            return true;
        }
        finally
        {
            Interlocked.Decrement(ref _readers);
        }
    }

    public bool Exists(string key) => !_dead && _index.Contains(key);

    // ── Write path (only called during build, before version is published) ────

    internal void WriteRecord(string key, CacheShard shard, long offset, byte[] record)
    {
        shard.WriteRecord(offset, record);
        _index.Set(key, new CacheLocation(shard.Id, offset));
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Marks version dead, waits for every in-flight read to finish,
    /// disposes all resources, then deletes the version directory from disk.
    ///
    /// Called off the hot path (background Task.Run) so the await loop
    /// does not block any reader or the reload pipeline.
    /// </summary>
    public async Task RetireAsync(CancellationToken ct = default)
    {
        // 1. Stop new readers from entering
        _dead = true;

        // 2. Wait until every reader that slipped past the _dead check has exited
        while (Volatile.Read(ref _readers) > 0)
            await Task.Delay(1, ct);

        // 3. Release OS handles (mmaps, FasterKV log devices)
        Dispose();

        // 4. Now it is safe to remove the directory — no handle is open
        if (Directory.Exists(VersionDir))
        {
            try
            {
                Directory.Delete(VersionDir, recursive: true);
                Console.WriteLine($"[Retire] Deleted old version directory: {Path.GetFileName(VersionDir)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Retire] Warning — could not delete {VersionDir}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        foreach (var s in _shards) s.Dispose();
        _index.Dispose();
        _l1?.Dispose();
    }
}
