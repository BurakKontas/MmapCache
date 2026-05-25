using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MmapCacheApp.Config;
using MmapCacheApp.Index;
using MmapCacheApp.Shards;

namespace MmapCacheApp.Cache;

/// <summary>
/// Singleton coordinator for all mmap caches.
///
///  • One CacheVersion (shards + FasterKV index + L1) per registered cache.
///  • Background PeriodicTimer checks TTL every TtlCheckInterval and triggers async reload.
///  • Reload builds a brand-new version, does an atomic volatile swap, then
///    retires the old version only after all in-flight reads finish.
///  • Readers never block — they always see a complete version.
///
/// Restart behaviour
/// ─────────────────
///  On Register(), the manager checks whether a version directory already exists
///  on disk whose meta.json shows it is still within TTL.  If so, it re-opens
///  the mmap shard files and re-indexes them locally (no Supplier call) and
///  resumes from there.  Only when no fresh version exists is a full reload
///  triggered.
///
/// Old-version cleanup
/// ───────────────────
///  After a reload the old CacheVersion is retired: first marked dead (no new
///  readers), then we spin until the reader count reaches zero, then OS handles
///  are released, and finally the version directory is deleted from disk.
///  This guarantees no reader is left with a dangling mmap handle.
/// </summary>
public sealed class MmapCacheManager : IAsyncDisposable
{
    // ── Singleton ─────────────────────────────────────────────────────────────

    private static MmapCacheManager? _instance;

    public static MmapCacheManager Initialize(
        string basePath,
        int shardCapacity = 200_000,
        int memoryCacheSize = 10_000,
        int indexShardCount = 16,
        TimeSpan? ttlCheck = null)
    {
        if (_instance is not null)
            throw new InvalidOperationException("Already initialized.");

        _instance = new MmapCacheManager(basePath, shardCapacity, memoryCacheSize,
                                          indexShardCount, ttlCheck ?? TimeSpan.FromSeconds(10));
        return _instance;
    }

    public static MmapCacheManager Instance
        => _instance ?? throw new InvalidOperationException("Call Initialize() first.");

    // ── Slot: stable named handle with volatile active version ────────────────

    private sealed class CacheSlot
    {
        private volatile CacheVersion? _active;
        public CacheVersion? Active => _active;
        public void Swap(CacheVersion next) => Interlocked.Exchange(ref _active!, next);
    }

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly string _basePath;
    private readonly int _defaultShardCapacity;
    private readonly int _defaultMemCacheSize;
    private readonly int _defaultIndexShards;

    private readonly ConcurrentDictionary<string, object> _defs = new();
    private readonly ConcurrentDictionary<string, CacheSlot> _slots = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _ttlTask;

    private MmapCacheManager(string basePath, int shardCapacity, int memCacheSize,
                               int indexShards, TimeSpan ttlCheck)
    {
        _basePath = basePath;
        _defaultShardCapacity = shardCapacity;
        _defaultMemCacheSize = memCacheSize;
        _defaultIndexShards = indexShards;

        Directory.CreateDirectory(basePath);
        _timer = new PeriodicTimer(ttlCheck);
        _ttlTask = RunTtlLoopAsync(_cts.Token);
    }

    // ── Registration ──────────────────────────────────────────────────────────

    public void Register<TValue>(MmapCacheDefinition<TValue> def)
    {
        _defs[def.Name] = def;
        _slots[def.Name] = new CacheSlot();
        _gates[def.Name] = new SemaphoreSlim(1, 1);

        // Try to resume from a still-fresh version that exists on disk.
        // This avoids an expensive Supplier call on every process restart.
        var existing = TryLoadExistingVersionAsync(def, CancellationToken.None)
                           .GetAwaiter().GetResult();

        if (existing is not null)
        {
            _slots[def.Name].Swap(existing);
            var age = DateTime.UtcNow - existing.CreatedAt;
            Console.WriteLine($"[{def.Name}] Resumed from disk " +
                              $"(age={age.TotalSeconds:F0}s, ttl={def.Ttl.TotalSeconds:F0}s).");
            return;
        }

        // No fresh version on disk → full build.
        BuildAndSwapAsync(def, CancellationToken.None).GetAwaiter().GetResult();
    }

    // ── Public read API ───────────────────────────────────────────────────────

    public TValue? Get<TValue>(string cache, string key)
    {
        TryGet<TValue>(cache, key, out var v);
        return v;
    }

    public bool TryGet<TValue>(string cache, string key, out TValue? value)
    {
        value = default;
        var version = _slots.TryGetValue(cache, out var slot) ? slot.Active : null;
        if (version is null) return false;
        return version.TryGet(key, GetDef<TValue>(cache).Deserializer, out value);
    }

    public bool Exists(string cache, string key)
        => _slots.TryGetValue(cache, out var s) && (s.Active?.Exists(key) ?? false);

    public long Size(string cache)
        => _slots.TryGetValue(cache, out var s) ? s.Active?.Size ?? 0 : 0;

    public DateTime LastReload(string cache)
        => _slots.TryGetValue(cache, out var s) ? s.Active?.CreatedAt ?? DateTime.MinValue : DateTime.MinValue;

    // ── Manual reload ─────────────────────────────────────────────────────────

    public Task ReloadAsync<TValue>(string cache, CancellationToken ct = default)
        => BuildAndSwapAsync(GetDef<TValue>(cache), ct);

    // ── Build pipeline ────────────────────────────────────────────────────────

    private async Task BuildAndSwapAsync<TValue>(MmapCacheDefinition<TValue> def, CancellationToken ct)
    {
        var gate = _gates[def.Name];
        if (!await gate.WaitAsync(0, ct)) return; // reload already running

        try
        {
            var newVersion = await BuildVersionAsync(def, ct);
            var slot = _slots[def.Name];
            var oldVersion = slot.Active;

            slot.Swap(newVersion);

            if (oldVersion is not null)
            {
                // Fire-and-forget: waits for readers → disposes handles → deletes directory.
                _ = Task.Run(() => oldVersion.RetireAsync(CancellationToken.None),
                             CancellationToken.None);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CacheVersion> BuildVersionAsync<TValue>(
        MmapCacheDefinition<TValue> def, CancellationToken ct)
    {
        string vid = $"v{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        string versionDir = Path.Combine(_basePath, def.Name, vid);
        string indexDir = Path.Combine(versionDir, "index");
        Directory.CreateDirectory(versionDir);
        Directory.CreateDirectory(indexDir);

        // ── Step 1: collect ───────────────────────────────────────────────────
        Console.WriteLine($"  [{def.Name}] collecting rows...");
        var rows = await Task.Run(() => def.Supplier().ToList(), ct);
        Console.WriteLine($"  [{def.Name}] {rows.Count:N0} rows collected.");

        // ── Step 2: sizing ────────────────────────────────────────────────────
        int maxKeyBytes, maxValueBytes;

        if (def.DynamicSizing)
        {
            maxKeyBytes = maxValueBytes = 8;
            foreach (var (key, value) in rows)
            {
                int kb = Encoding.UTF8.GetByteCount(key);
                int vb = def.Serializer(value).Length;
                if (kb > maxKeyBytes) maxKeyBytes = kb;
                if (vb > maxValueBytes) maxValueBytes = vb;
            }
            Console.WriteLine($"  [{def.Name}] dynamic sizing → maxKey={maxKeyBytes}B  maxVal={maxValueBytes}B");
        }
        else
        {
            maxKeyBytes = def.MaxKeyBytes;
            maxValueBytes = def.MaxValueBytes;
        }

        int recordSize = CacheRecord.RecordSize(maxKeyBytes, maxValueBytes);
        long shardCapacity = def.ShardCapacity > 0 ? def.ShardCapacity : _defaultShardCapacity;
        int shardCount = Math.Max(1, (int)Math.Ceiling((double)rows.Count / shardCapacity));

        Console.WriteLine($"  [{def.Name}] recordSize={recordSize}B  shards={shardCount}");

        // ── Step 3: allocate shards & index ───────────────────────────────────
        var shards = new CacheShard[shardCount];
        for (int i = 0; i < shardCount; i++)
            shards[i] = new CacheShard(i, Path.Combine(versionDir, $"shard_{i:D4}.bin"),
                                       recordSize, shardCapacity);

        int idxShards = def.IndexShardCount > 0 ? def.IndexShardCount : _defaultIndexShards;
        var index = new CacheIndex(indexDir, idxShards);
        var version = new CacheVersion(vid, versionDir, shards, index, maxKeyBytes,
                                          def.L1MaxSize > 0 ? def.L1MaxSize : _defaultMemCacheSize,
                                          def.L1Ttl);

        // ── Step 4: write records ─────────────────────────────────────────────
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await Task.Run(() =>
        {
            var buf = new byte[recordSize];
            long globalOffset = 0;

            foreach (var (key, value) in rows)
            {
                int sid = (int)(globalOffset / shardCapacity);
                long offset = globalOffset % shardCapacity;

                var keyBytes = Encoding.UTF8.GetBytes(key);
                var valueBytes = def.Serializer(value);
                long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                CacheRecord.Write(buf, globalOffset, keyBytes, valueBytes,
                                  maxKeyBytes, maxValueBytes, ts);
                version.WriteRecord(key, shards[sid], offset, buf);
                globalOffset++;
            }

            foreach (var s in shards) s.Flush();
        }, ct);

        sw.Stop();
        Console.WriteLine($"  [{def.Name}] wrote {rows.Count:N0} records in {sw.ElapsedMilliseconds}ms  " +
                          $"({rows.Count / sw.Elapsed.TotalSeconds:N0} rec/s)");

        // ── Step 5: write meta.json ───────────────────────────────────────────
        // Written last so an interrupted build (no meta.json) is never mistaken
        // for a complete version on the next startup.
        var meta = new VersionMeta
        {
            CreatedAt = version.CreatedAt,
            MaxKeyBytes = maxKeyBytes,
            MaxValueBytes = maxValueBytes,
            RecordSize = recordSize,
            ShardCount = shardCount,
            ShardCapacity = shardCapacity,
            IndexShardCount = idxShards,
        };

        await File.WriteAllTextAsync(
            Path.Combine(versionDir, "meta.json"),
            JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }),
            ct);

        return version;
    }

    // ── Startup: try to resume from existing on-disk version ──────────────────

    /// <summary>
    /// Looks for the newest version directory that has a valid meta.json and
    /// whose age is still within <paramref name="def"/>.Ttl.
    ///
    /// When a usable version is found:
    ///   • All other (older or stale) version directories are deleted.
    ///   • The shard files are opened via memory-map (no Supplier call).
    ///   • The FasterKV index is rebuilt by scanning the shard files — this is
    ///     a local I/O operation, typically 10-100× faster than a Supplier call.
    ///
    /// Returns null when no usable version exists (triggers a full build).
    /// </summary>
    private async Task<CacheVersion?> TryLoadExistingVersionAsync<TValue>(
        MmapCacheDefinition<TValue> def, CancellationToken ct)
    {
        string cacheDir = Path.Combine(_basePath, def.Name);
        if (!Directory.Exists(cacheDir)) return null;

        // Newest first (version dirs are named v{UnixTimeMs}).
        var allDirs = Directory.GetDirectories(cacheDir)
            .Where(d => Path.GetFileName(d).StartsWith('v'))
            .OrderByDescending(d => d)
            .ToList();

        if (allDirs.Count == 0) return null;

        string? usableDir = null;
        VersionMeta? usableMeta = null;

        foreach (var dir in allDirs)
        {
            string metaPath = Path.Combine(dir, "meta.json");
            if (!File.Exists(metaPath))
            {
                // Incomplete build (process died mid-write) — clean up.
                TryDeleteDir(dir, "incomplete build");
                continue;
            }

            VersionMeta? m;
            try { m = JsonSerializer.Deserialize<VersionMeta>(File.ReadAllText(metaPath)); }
            catch { m = null; }

            if (m is null)
            {
                TryDeleteDir(dir, "corrupt meta.json");
                continue;
            }

            if (DateTime.UtcNow - m.CreatedAt >= def.Ttl)
            {
                TryDeleteDir(dir, "TTL expired");
                continue;
            }

            // First directory that passes all checks is the one to use.
            usableDir = dir;
            usableMeta = m;
            break;
        }

        if (usableDir is null || usableMeta is null)
            return null;

        // Delete any older directories we did not already delete above
        // (they were behind usableDir in the sorted list).
        foreach (var dir in allDirs.Where(d => d != usableDir))
            TryDeleteDir(dir, "superseded by resumed version");

        Console.WriteLine($"  [{def.Name}] Found fresh version {Path.GetFileName(usableDir)} on disk. " +
                          $"Re-indexing (skipping Supplier call)...");

        // Open existing shard files (read-write mmap so Flush/Dispose work normally).
        var shards = new CacheShard[usableMeta.ShardCount];
        for (int i = 0; i < usableMeta.ShardCount; i++)
        {
            string path = Path.Combine(usableDir, $"shard_{i:D4}.bin");
            shards[i] = CacheShard.OpenExisting(i, path, usableMeta.RecordSize, usableMeta.ShardCapacity);
        }

        // Rebuild FasterKV index by scanning shards.
        // We always start with a fresh index directory so we don't rely on
        // FasterKV's recovery mechanism (which requires explicit checkpoints).
        string indexDir = Path.Combine(usableDir, "index");
        if (Directory.Exists(indexDir)) Directory.Delete(indexDir, recursive: true);
        Directory.CreateDirectory(indexDir);

        var index = new CacheIndex(indexDir, usableMeta.IndexShardCount);

        long total = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await Task.Run(() =>
        {
            for (int si = 0; si < shards.Length; si++)
            {
                for (long slot = 0; slot < usableMeta.ShardCapacity; slot++)
                {
                    // kLen == 0 means zero-padded empty slot → end of this shard.
                    if (!shards[si].TryReadKey(slot, usableMeta.MaxKeyBytes, out string? key))
                        break;

                    index.Set(key, new CacheLocation(si, slot));
                    total++;
                }
            }
        }, ct);

        sw.Stop();
        Console.WriteLine($"  [{def.Name}] Re-indexed {total:N0} records in {sw.ElapsedMilliseconds}ms.");

        int l1Size = def.L1MaxSize > 0 ? def.L1MaxSize : _defaultMemCacheSize;

        return new CacheVersion(
            Path.GetFileName(usableDir),
            usableDir,
            shards,
            index,
            usableMeta.MaxKeyBytes,
            l1Size,
            def.L1Ttl,
            createdAt: usableMeta.CreatedAt);   // preserve original timestamp for TTL math
    }

    // ── TTL background loop ───────────────────────────────────────────────────

    private async Task RunTtlLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(ct))
            {
                foreach (var (name, slot) in _slots)
                {
                    if (slot.Active is null) continue;
                    if (!_defs.TryGetValue(name, out var defObj)) continue;

                    var ttl = (TimeSpan)defObj.GetType()
                        .GetProperty(nameof(MmapCacheDefinition<object>.Ttl))!
                        .GetValue(defObj)!;

                    if (DateTime.UtcNow - slot.Active.CreatedAt >= ttl)
                    {
                        Console.WriteLine($"[TTL] '{name}' expired → triggering reload");
                        _ = Task.Run(() => ReloadByNameAsync(name, defObj, ct), ct);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private Task ReloadByNameAsync(string name, object defObj, CancellationToken ct)
    {
        var valueType = defObj.GetType().GetGenericArguments()[0];
        var method = typeof(MmapCacheManager)
            .GetMethod(nameof(ReloadAsync),
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!
            .MakeGenericMethod(valueType);
        return (Task)method.Invoke(this, [name, ct])!;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private MmapCacheDefinition<TValue> GetDef<TValue>(string name)
        => _defs.TryGetValue(name, out var obj) && obj is MmapCacheDefinition<TValue> def
            ? def
            : throw new KeyNotFoundException($"Cache '{name}' not registered or TValue mismatch.");

    private static void TryDeleteDir(string dir, string reason)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
                Console.WriteLine($"  [Cleanup] Deleted {Path.GetFileName(dir)} ({reason}).");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [Cleanup] Warning — could not delete {dir}: {ex.Message}");
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _ttlTask; } catch (OperationCanceledException) { }
        _cts.Dispose();
        _timer.Dispose();

        foreach (var slot in _slots.Values)
            slot.Active?.Dispose();
    }
}
